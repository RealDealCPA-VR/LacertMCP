using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-029 — <c>lacerte_diff_clients</c>. Structured field-by-field
    /// comparison of two clients in the SAME active context (datapath, year,
    /// module). v1 deliberately rejects cross-context diffs: temporarily
    /// swapping the session manager's active context for the duration of one
    /// fan-out would violate Boundary Invariant 3 (one session manager per
    /// process) and the stable-active-context contract every other tool
    /// depends on. Cross-context diff is a v2 once a scoped read path lands
    /// on the session manager.
    ///
    /// <para>
    /// Returns only the rows where the two clients differ — DATA1 field
    /// diffs as <c>{ field, a, b }</c>, subclients/states/detail as
    /// <c>{ added, removed, changed }</c> buckets matched on stable composite
    /// keys (SubClientId / StateAbbrev / (Series, Code, StateAbbrev, Description)
    /// respectively). The same WireLog Wire-scope redaction pass that runs
    /// over the final envelope masks SSN/EIN/bank symmetrically — if both
    /// sides hold the same redacted-shape value the equality check still
    /// fires and no row is emitted.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class ClientDiffTools
    {
        private readonly LacerteSessionManager _session;

        public ClientDiffTools(LacerteSessionManager session) => _session = session;

        /// <summary>
        /// Default ignore-set for the DATA1 field comparison. The wire-key
        /// <c>clientNumber</c> is injected by CloneRow and would otherwise
        /// always diff when the two clients have different ClientNumbers
        /// (which they always do — that's the precondition for calling
        /// the tool). Operators can extend via the <c>ignoreFields</c>
        /// parameter.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultIgnoreFields = new[]
        {
            "clientNumber",
        };

        [McpServerTool]
        [Description("Compare two clients in the active (datapath, year, module) field-by-field across DATA1 (identity), DATA2 (subclients), DATA3 (states), and optionally DATA6 (detail rows). Returns ONLY the rows where they differ — { field, a, b } per DATA1 column; { added[], removed[], changed[] } buckets per row-collection. Same-context only in v1 (cross-context diff is rejected with 9017 InvalidArgument). Use cases: 'did I import the right one?', 'spot-check a proforma rollover', 'how does this couple differ from their parents?'. The wire-scope WireLog redaction pass masks SSN/EIN/bank symmetrically — fields that agree under redaction are suppressed, so the envelope does not leak PII on agreement. Rejects A==B with 9017.")]
        public Task<string> LacerteDiffClients(
            [Description("First ClientNumber to compare (sim-assigned monotonic identifier; e.g. \"1001\").")] string client1Number,
            [Description("Second ClientNumber to compare. Must differ from client1Number.")] string client2Number,
            [Description("When true, also diff DATA2 subclient rows (partners / dependents / shareholders). Default true.")] bool includeSubclients = true,
            [Description("When true, also diff DATA3 state rows (per-state overrides). Default true.")] bool includeStates = true,
            [Description("When true, also diff DATA6 detail rows (W-2 / 1099 / state apportionment line items). Default false — detail-row reads can be heavy.")] bool includeDetail = false,
            [Description("Optional additional field-name list to suppress from the DATA1 diff (case-sensitive on the wire key). Useful for hiding audit timestamps the operator does not care about. The built-in ignore-set (clientNumber) is always applied.")] string[]? ignoreFields = null,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                ValidateArgs(client1Number, client2Number);

                // Both reads gated by ReadClient — 9009 ContextNotSet etc.
                // propagate naturally before we touch the fan-out.
                var rowA = await _session.ReadClient(client1Number, cancellationToken).ConfigureAwait(false);
                if (rowA == null)
                {
                    return (object)NotFoundEnvelope(client1Number, "client1Number");
                }
                var rowB = await _session.ReadClient(client2Number, cancellationToken).ConfigureAwait(false);
                if (rowB == null)
                {
                    return (object)NotFoundEnvelope(client2Number, "client2Number");
                }

                var ignored = BuildIgnoreSet(ignoreFields);
                var dataOneDiff = DiffDataOne(rowA, rowB, ignored);

                object? subclientDiff = null;
                if (includeSubclients)
                {
                    var subA = await _session.ReadSubclients(client1Number, cancellationToken).ConfigureAwait(false);
                    var subB = await _session.ReadSubclients(client2Number, cancellationToken).ConfigureAwait(false);
                    subclientDiff = DiffRowsByKey(subA, subB, "subClientId", ignored);
                }

                object? stateDiff = null;
                if (includeStates)
                {
                    var stA = await _session.ReadStates(client1Number, cancellationToken).ConfigureAwait(false);
                    var stB = await _session.ReadStates(client2Number, cancellationToken).ConfigureAwait(false);
                    stateDiff = DiffRowsByKey(stA, stB, "stateAbbrev", ignored);
                }

                object? detailDiff = null;
                if (includeDetail)
                {
                    var dA = await _session.ReadDetail(client1Number, cancellationToken).ConfigureAwait(false);
                    var dB = await _session.ReadDetail(client2Number, cancellationToken).ConfigureAwait(false);
                    detailDiff = DiffDetailRows(dA, dB);
                }

                return new
                {
                    client1Number,
                    client2Number,
                    summary = new
                    {
                        matchedFieldsCount = dataOneDiff.MatchedFieldsCount,
                        differingFieldsCount = dataOneDiff.Diffs.Count,
                        ignoredCount = ignored.Count,
                    },
                    dataOne = new
                    {
                        fields = dataOneDiff.Diffs,
                    },
                    subclients = subclientDiff,
                    states = stateDiff,
                    detail = detailDiff,
                    ignoredFields = ignored.ToArray(),
                };
            });

        // ===================================================================
        // Argument validation
        // ===================================================================

        private static void ValidateArgs(string client1Number, string client2Number)
        {
            if (string.IsNullOrWhiteSpace(client1Number))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidArgument,
                    new Dictionary<string, object?>
                    {
                        ["argument"] = "client1Number",
                        ["value"] = client1Number,
                        ["reason"] = "client1Number must be a non-empty string.",
                    });
            }
            if (string.IsNullOrWhiteSpace(client2Number))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidArgument,
                    new Dictionary<string, object?>
                    {
                        ["argument"] = "client2Number",
                        ["value"] = client2Number,
                        ["reason"] = "client2Number must be a non-empty string.",
                    });
            }
            if (string.Equals(client1Number, client2Number, StringComparison.OrdinalIgnoreCase))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidArgument,
                    new Dictionary<string, object?>
                    {
                        ["argument"] = "client2Number",
                        ["value"] = client2Number,
                        ["reason"] = "client1Number and client2Number must differ — comparing a client to itself is meaningless.",
                    });
            }
        }

        private static object NotFoundEnvelope(string clientNumber, string which)
        {
            return new
            {
                clientNumber,
                found = false,
                hint = $"{which} '{clientNumber}' not present in the active (datapath, year, module). Call lacerte_client_list to enumerate available ClientNumbers.",
            };
        }

        // ===================================================================
        // DATA1 diff
        // ===================================================================

        public sealed class DataOneDiff
        {
            public int MatchedFieldsCount { get; set; }
            public List<object> Diffs { get; set; } = new List<object>();
        }

        /// <summary>
        /// Compare two flat row dicts. For every key that appears in EITHER
        /// row and is not in the ignore-set, emit <c>{ field, a, b }</c> when
        /// the stringified values differ. Public so tests can drive the pure
        /// helper without spinning up the WrapToolHandler envelope.
        /// </summary>
        public static DataOneDiff DiffDataOne(
            IReadOnlyDictionary<string, object?> a,
            IReadOnlyDictionary<string, object?> b,
            HashSet<string> ignored)
        {
            var result = new DataOneDiff();
            var allKeys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in a.Keys) allKeys.Add(k);
            foreach (var k in b.Keys) allKeys.Add(k);

            foreach (var key in allKeys)
            {
                if (ignored.Contains(key)) continue;
                a.TryGetValue(key, out var av);
                b.TryGetValue(key, out var bv);
                if (ScalarEquals(av, bv))
                {
                    result.MatchedFieldsCount++;
                }
                else
                {
                    result.Diffs.Add(new
                    {
                        field = key,
                        a = av,
                        b = bv,
                    });
                }
            }
            return result;
        }

        // ===================================================================
        // Row-collection diff (matched by stable key)
        // ===================================================================

        /// <summary>
        /// Diff two row collections matched on the supplied wire key. Rows
        /// with a key value that exists only in B surface in <c>added</c>
        /// (read the diff as "what you would have to add to A to make it
        /// match B"). Rows with a key value that exists only in A surface
        /// symmetrically in <c>removed</c>. Matched-key rows that differ
        /// surface in <c>changed</c> as <c>{ key, fields:[{field,a,b}] }</c>.
        /// Rows whose key value is missing/null cannot be matched and are
        /// surfaced via the <c>rowsWithoutKey</c> counters (R6-06d) instead
        /// of being silently dropped.
        /// </summary>
        public static object DiffRowsByKey(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> a,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> b,
            string keyName,
            HashSet<string> ignored)
        {
            // Match by stringified key value — sim/live both surface
            // SubClientId as int and StateAbbrev as string; ToString()
            // canonicalizes both.
            string? KeyOf(IReadOnlyDictionary<string, object?> r)
                => r.TryGetValue(keyName, out var v) && v != null ? v.ToString() : null;

            var aByKey = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var bByKey = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            // R6-06d — rows with a missing/null key cannot participate in
            // keyed matching; count them so the envelope surfaces their
            // existence instead of silently dropping them from all buckets.
            int aWithoutKey = 0;
            int bWithoutKey = 0;
            foreach (var r in a)
            {
                var k = KeyOf(r);
                if (k != null) aByKey[k] = r;
                else aWithoutKey++;
            }
            foreach (var r in b)
            {
                var k = KeyOf(r);
                if (k != null) bByKey[k] = r;
                else bWithoutKey++;
            }

            var addedList = new List<object>();
            var removedList = new List<object>();
            var changedList = new List<object>();

            // Rows present in B but not A — "added" from A's perspective
            // (i.e. you would have to add these to A to make it match B).
            foreach (var kv in bByKey)
            {
                if (!aByKey.ContainsKey(kv.Key))
                {
                    addedList.Add(new { key = kv.Key, row = kv.Value });
                }
            }
            // Rows present in A but not B — "removed" from A's perspective.
            foreach (var kv in aByKey)
            {
                if (!bByKey.ContainsKey(kv.Key))
                {
                    removedList.Add(new { key = kv.Key, row = kv.Value });
                }
            }
            // Matched-key rows — emit a per-field changed envelope.
            foreach (var kv in aByKey)
            {
                if (!bByKey.TryGetValue(kv.Key, out var bRow)) continue;
                var sub = DiffDataOne(kv.Value, bRow, ignored);
                if (sub.Diffs.Count > 0)
                {
                    changedList.Add(new { key = kv.Key, fields = sub.Diffs });
                }
            }

            return new
            {
                added = addedList,
                removed = removedList,
                changed = changedList,
                // R6-06d — rows that could not be matched because their key
                // value was missing/null. Always emitted (zeros on the happy
                // path) so the LLM never has to guess whether the field
                // exists.
                rowsWithoutKey = new { a = aWithoutKey, b = bWithoutKey },
            };
        }

        // ===================================================================
        // Detail-row diff (composite key)
        // ===================================================================

        /// <summary>
        /// Diff two <see cref="DetailRow"/> collections matched on the
        /// composite key (Series, Code, StateAbbrev, Description) per the
        /// audit's refinedDesign. The composite is a deliberate trade-off:
        /// Description is included so two W-2 rows for different employers
        /// don't collide on the (Series, Code) prefix, at the cost of
        /// surfacing every typo-corrected description as add+remove.
        ///
        /// <para>
        /// R6-06c — duplicate-key multiplicity is honored: when a key group
        /// exists on both sides, rows are compared position-by-position
        /// within the group (detail files are row-ordered with no primary
        /// key, so position is the only stable tiebreaker). A count
        /// difference surfaces the surplus rows as <c>added</c> (extra in
        /// B) or <c>removed</c> (extra in A) — two identical W-2 rows vs
        /// one is a real diff, not a no-op.
        /// </para>
        /// </summary>
        public static object DiffDetailRows(
            IReadOnlyList<DetailRow> a,
            IReadOnlyList<DetailRow> b)
        {
            static string KeyOf(DetailRow r)
                => $"{r.Series}|{r.Code}|{r.StateAbbr}|{r.Description}";

            var aByKey = a.ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);
            var bByKey = b.ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);

            var added = bByKey
                .Where(g => !aByKey.Contains(g.Key))
                .SelectMany(g => g)
                .Select(r => (object)ShapeDetailRow(r))
                .ToList();

            var removed = aByKey
                .Where(g => !bByKey.Contains(g.Key))
                .SelectMany(g => g)
                .Select(r => (object)ShapeDetailRow(r))
                .ToList();

            var changed = new List<object>();
            foreach (var g in aByKey)
            {
                if (!bByKey.Contains(g.Key)) continue;

                // R6-06c — compare the whole key group position-by-position
                // instead of First()-vs-First(), so duplicate rows under the
                // same composite key are no longer invisible.
                var aRows = g.ToList();
                var bRows = bByKey[g.Key].ToList();
                int paired = Math.Min(aRows.Count, bRows.Count);
                for (int i = 0; i < paired; i++)
                {
                    if (!DetailRowsEqual(aRows[i], bRows[i]))
                    {
                        changed.Add(new
                        {
                            key = g.Key,
                            position = i,
                            a = ShapeDetailRow(aRows[i]),
                            b = ShapeDetailRow(bRows[i]),
                        });
                    }
                }
                // Surplus rows beyond the paired prefix surface as
                // added/removed so a 2-vs-1 duplicate is a visible diff.
                for (int i = paired; i < bRows.Count; i++)
                {
                    added.Add(ShapeDetailRow(bRows[i]));
                }
                for (int i = paired; i < aRows.Count; i++)
                {
                    removed.Add(ShapeDetailRow(aRows[i]));
                }
            }

            return new { added, removed, changed };
        }

        /// <summary>
        /// R6-06a — full-fidelity equality across ALL 11 DetailRow fields.
        /// Rows differing only in TSJ (taxpayer vs spouse), Source, Prefix,
        /// Suffix, CityAbbr, or ImportSrc are real diffs.
        /// </summary>
        private static bool DetailRowsEqual(DetailRow a, DetailRow b)
        {
            return string.Equals(a.Series ?? "", b.Series ?? "", StringComparison.Ordinal)
                && string.Equals(a.Prefix ?? "", b.Prefix ?? "", StringComparison.Ordinal)
                && string.Equals(a.Code ?? "", b.Code ?? "", StringComparison.Ordinal)
                && string.Equals(a.Suffix ?? "", b.Suffix ?? "", StringComparison.Ordinal)
                && string.Equals(a.StateAbbr ?? "", b.StateAbbr ?? "", StringComparison.Ordinal)
                && string.Equals(a.CityAbbr ?? "", b.CityAbbr ?? "", StringComparison.Ordinal)
                && string.Equals(a.Source ?? "", b.Source ?? "", StringComparison.Ordinal)
                && string.Equals(a.TSJ ?? "", b.TSJ ?? "", StringComparison.Ordinal)
                && string.Equals(a.ImportSrc ?? "", b.ImportSrc ?? "", StringComparison.Ordinal)
                && Nullable.Equals(a.Amount, b.Amount)
                && string.Equals(a.Description ?? "", b.Description ?? "", StringComparison.Ordinal);
        }

        // R6-06b — emit ALL 11 DetailRow fields (camelCase wire keys) so a
        // diff caused by any field is visible in the envelope, not just
        // detectable.
        private static object ShapeDetailRow(DetailRow r) => new
        {
            series = r.Series,
            prefix = r.Prefix,
            code = r.Code,
            suffix = r.Suffix,
            stateAbbrev = r.StateAbbr,
            cityAbbr = r.CityAbbr,
            source = r.Source,
            tsj = r.TSJ,
            importSrc = r.ImportSrc,
            amount = r.Amount,
            description = r.Description,
        };

        // ===================================================================
        // Helpers
        // ===================================================================

        private static HashSet<string> BuildIgnoreSet(string[]? extra)
        {
            var set = new HashSet<string>(DefaultIgnoreFields, StringComparer.Ordinal);
            if (extra != null)
            {
                foreach (var f in extra)
                {
                    if (!string.IsNullOrEmpty(f)) set.Add(f);
                }
            }
            return set;
        }

        /// <summary>
        /// v1 string-equality after <c>.ToString()</c> per the audit verdict
        /// scope-note. Two nulls equal; one-null vs non-null does not equal.
        /// Numeric coercion (12345 vs "12345") is deferred to a later round.
        /// </summary>
        public static bool ScalarEquals(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            var aStr = a.ToString() ?? string.Empty;
            var bStr = b.ToString() ?? string.Empty;
            return string.Equals(aStr, bStr, StringComparison.Ordinal);
        }
    }
}
