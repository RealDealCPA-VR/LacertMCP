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
    /// DATA1 (Client Info) read tools. Per <c>Todo.md</c> Phase 5 task #42.
    /// Delegates to <see cref="LacerteSessionManager.ListClients"/> and
    /// <see cref="LacerteSessionManager.ReadClient"/>.
    /// </summary>
    [McpServerToolType]
    public sealed class ClientsTools
    {
        private readonly LacerteSessionManager _session;

        public ClientsTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("List clients in the active (datapath, year, module) context. Optional case-insensitive SUBSTRING filter on the conventional name fields: Individual modules match against BOTH first name (C1_1) and last name (C1_2) so 'John Smith' resolves whether the operator typed the first or the last name; non-Individual modules match against the entity name (C1_1). Supports limit/offset pagination — limit semantics: omit (null) for the default page size (100); pass a positive int up to Pagination.MaxLimit (1000) for a custom page; pass 0 for a count-only response (rows=[], total=N); negative values are rejected. NOTE: for name-to-ClientNumber resolution prefer `lacerte_client_find` (returns a pre-shaped disambiguation envelope with didYouMean fallback).")]
        public Task<string> LacerteClientList(
            [Description("Optional case-insensitive substring matched against first OR last name (Individual modules) or the entity name (other modules). 'bro' matches 'Brown' and 'Bronstein'; 'mit' matches 'Smith'. Empty / null = no filter.")] string? nameFilter = null,
            [Description("Maximum rows to return. Omit (null) for the default page size (100). Pass a positive int up to Pagination.MaxLimit (1000) for a custom page. Pass 0 to get a count-only response (rows=[], total=N). Negative values are rejected.")] int? limit = null,
            [Description("Zero-based row offset into the filtered result set. Omit (null) for offset=0.")] int? offset = null,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // P16-075 — surface a safe, four-state limit contract on the wire:
                //   null     -> default page (Pagination.DefaultLimit = 100)
                //   0        -> count-only (rows=[], total=N) — no MaxLimit bypass
                //   positive -> clamped to Pagination.MaxLimit (handled by helper)
                //   negative -> ArgumentOutOfRangeException
                //
                // We normalize BEFORE calling Pagination.Page so the helper's
                // current "non-positive = unbounded" branch (which bypasses
                // MaxLimit) is never reached from this tool. The four-state
                // contract is locked in here; an LLM that hallucinates
                // `limit: 0` gets a safe empty-rows envelope with the total,
                // not the full firm-history.
                if (limit.HasValue && limit.Value < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(limit),
                        "limit must be null (default), 0 (count-only), or a positive int up to Pagination.MaxLimit (1000).");
                }
                int effectiveLimit = !limit.HasValue
                    ? Pagination.Pagination.DefaultLimit
                    : limit.Value; // 0 or positive — helper handles positive; we special-case 0 below.

                // Pull the full set from the session, then filter + page in-tool via
                // Pagination.Page<T> so 'total' is exact.
                //
                // PAGINATION LIMITATION (audit P14-014, cluster N_sql): the Lacerte
                // ODBC driver (FoxPro-style) does NOT support OFFSET / FETCH NEXT /
                // ROW_NUMBER(), so true server-side OFFSET-based paging is not
                // available. The live-mode push-down templates SqlTemplates.Data1Top
                // and Data1TopWhereName cap rows via TOP n but cannot skip; until the
                // session exposes a (rows, total) overload that does an in-memory
                // slice while preserving an exact total, this tool overfetches via
                // int.MaxValue and slices here. Sim is in-memory so the overfetch is
                // free; live mode will eat the full-table scan cost per paged request
                // — slated for a session-level fix in a future phase.
                //
                // P18-005 / cluster E_tools_session — `nameFilter` is now a
                // SUBSTRING match over BOTH the first name (C1_1) and last name
                // (C1_2) for Individual modules, and the entity name (C1_1) for
                // non-Individual modules. The session-manager filter is
                // prefix-only on a single column, so we deliberately pass
                // `null` to the session call and apply the broader filter here.
                // This unblocks the common LLM/operator pattern of typing
                // "smith" and expecting both "John Smith" (last) and
                // "Smith Holdings" (entity) to match. The same overfetch +
                // tool-side slice path used for pagination keeps the
                // overhead identical to the previous shape.
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                    await _session.ListClients(null, int.MaxValue, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(nameFilter))
                {
                    rows = FilterByName(rows, nameFilter!).ToList();
                }

                if (limit.HasValue && limit.Value == 0)
                {
                    // Count-only: surface total without materializing rows on the
                    // wire. Page through the helper with limit=1 just to get exact
                    // total + correct hasMore/nextOffset semantics relative to the
                    // requested offset, then blank the rows.
                    var probe = Pagination.Pagination.Page(rows, 1, offset);
                    return new
                    {
                        rows = System.Array.Empty<object>(),
                        total = probe.Total,
                        limit = 0,
                        offset = probe.Offset,
                        // R6-19 — rows-on-this-page count per the Pagination
                        // contract (P16-074); 0 on the count-only probe.
                        returnedCount = 0,
                        hasMore = probe.Total > probe.Offset,
                        nextOffset = probe.Total > probe.Offset ? (int?)probe.Offset : null,
                        // legacy (v0.1) — remove in v0.2:
                        count = 0,
                        clients = System.Array.Empty<object>(),
                    };
                }

                var page = Pagination.Pagination.Page(rows, effectiveLimit, offset);

                // NOTE: `count` and `clients` are legacy fields kept for additive
                // backwards-compat with pre-pagination consumers. They will be
                // removed in v0.2 — new code should read `rows` + `total`.
                return new
                {
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    // R6-19 — rows-on-this-page count per the Pagination
                    // contract (P16-074).
                    returnedCount = page.ReturnedCount,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    clients = page.Rows,
                };
            });

        [McpServerTool]
        [Description("Read one client by ClientNumber from DATA1 in the active context. Returns an envelope { clientNumber, found, client, hint? } — when no such client exists in the active context, `found` is false, `client` is null, and `hint` describes the recovery path (call lacerte_client_list to enumerate available ClientNumbers). The envelope itself is never null; check `found` (or `client != null`), not the top-level result.")]
        public Task<string> LacerteClientRead(
            [Description("The ClientNumber (sim-assigned monotonic identifier; e.g. \"1001\"). NOTE: ClientNumber is the SDK-minted identifier (sim's monotonic counter), distinct from the operator-chosen ClientID — these are not interchangeable.")] string clientNumber,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var row = await _session.ReadClient(clientNumber, cancellationToken).ConfigureAwait(false);
                if (row == null)
                {
                    // P16-081 — add a `hint` field so an LLM scanning the envelope
                    // cannot mis-summarize `client: null` as "the record exists but
                    // is empty". The hint also disambiguates the ClientNumber vs
                    // ClientID confusion, which is the second most common cause of
                    // this failure.
                    return (object)new
                    {
                        clientNumber,
                        found = false,
                        client = (object?)null,
                        hint = $"ClientNumber '{clientNumber}' not present in the active (datapath, year, module). Call lacerte_client_list to enumerate available ClientNumbers, or lacerte_create_client to create one. Note: ClientNumber is the SDK-minted identifier (sim's monotonic counter, e.g. \"1001\"), distinct from the operator-chosen ClientID (e.g. \"BROW\") — these are not interchangeable.",
                    };
                }
                return new { clientNumber, found = true, client = row };
            });

        // ---------------------------------------------------------------
        // P18-005 — name-to-ClientNumber resolver tool.
        //
        // Before this tool, every name-driven operator workflow ("change
        // John's email", "what's Smith's address?") had to bounce through
        // lacerte_client_list with a prefix filter, pick a row by index, and
        // then call lacerte_client_read or lacerte_field_write — three
        // round-trips and a per-step failure point (prefix gap, index pick
        // error, ClientNumber-vs-ClientID confusion).
        //
        // LacerteClientFind collapses that into one call shaped for an LLM
        // disambiguator: substring across BOTH C1_1 + C1_2 in Individual
        // modules (so "john" hits a row where C1_1="John" and "smith" hits
        // C1_2="Smith"), substring on C1_1 alone for entity modules. The
        // returned envelope is pre-shaped for an LLM picker — clientNumber
        // first so the next-call ID is unambiguous, with firstName /
        // lastName / clientId / email as disambiguators the operator can
        // confirm at-a-glance.
        //
        // Cross-cluster note (cluster A_preflight owns LacerteSessionManager
        // per cluster instructions): the conceptually-correct home for the
        // substring scan is LacerteSessionManager.FindClients — which would
        // let live mode push a LIKE '%name%' down to ODBC via a new
        // SqlTemplates.Data1Fuzzy template. Until that lands, this tool
        // overfetches via _session.ListClients(null, int.MaxValue) and
        // filters in-memory. Sim is already in-memory so the overfetch is
        // free; live mode eats a full-table scan per call — same cost
        // shape as the existing LacerteClientList pagination loop, so no
        // new regression. Note logged in StructuredOutput.notes.
        // ---------------------------------------------------------------

        [McpServerTool]
        [Description("Resolve a plain-English client name to ClientNumber(s) in the active (datapath, year, module). Call BEFORE lacerte_client_read / lacerte_field_write when the operator gave you a name instead of a ClientNumber. Matches case-insensitive SUBSTRING on BOTH first name (C1_1) AND last name (C1_2) for Individual modules — 'john' hits 'John Smith', 'smith' hits the same row — or on the entity name (C1_1) for non-Individual modules. Returns { matches: [{clientNumber, clientId, firstName, lastName, email}], total, didYouMean? }. When total==1 the LLM proceeds straight to the next call; when total>1 it surfaces the disambiguators to the operator (\"did you mean John Smith or John Smyth?\"); when total==0 the `didYouMean` field carries up to 3 Levenshtein-closest names so the LLM can recover from a typo without bouncing back to the user.")]
        public Task<string> LacerteClientFind(
            [Description("Plain-English client name — first OR last for Individual modules, full or partial entity name for non-Individual. Case-insensitive substring match.")] string name,
            [Description("Maximum matches to return. Omit (null) for the default 20 (enough for an LLM picker without overwhelming the context). Pass a positive int up to 100; values <= 0 are rejected. NOTE: the total field always reflects the unfiltered match count even when truncated, so the LLM can detect overflow.")] int? limit = null,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException(
                        "name must be a non-empty substring; pass at least one printable character. To enumerate all clients use lacerte_client_list.",
                        nameof(name));
                }
                int effectiveLimit;
                if (!limit.HasValue)
                {
                    effectiveLimit = DefaultFindLimit;
                }
                else if (limit.Value <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(limit),
                        "limit must be null (default 20) or a positive int up to MaxFindLimit (100).");
                }
                else
                {
                    effectiveLimit = Math.Min(limit.Value, MaxFindLimit);
                }

                // Overfetch the full client roster from the session — see
                // cross-cluster note above. The session-mgr filter is
                // prefix-only on a single column and would mis-match the
                // first-name-vs-last-name pivot we need here.
                var all = await _session.ListClients(null, int.MaxValue, cancellationToken).ConfigureAwait(false);

                var matched = FilterByName(all, name).ToList();
                var truncated = matched.Count > effectiveLimit
                    ? matched.Take(effectiveLimit).ToList()
                    : matched;

                string? didYouMean = null;
                if (matched.Count == 0 && all.Count > 0)
                {
                    didYouMean = BuildDidYouMeanHint(all, name);
                }

                // Shape each match as a tight disambiguator envelope. The
                // FoxPro DBF column codes (C1_*) are intentionally NOT
                // surfaced verbatim — the LLM consumer wants stable
                // semantic field names, not ODBC column ordinals. The
                // pass-through row stays available via lacerte_client_read
                // for callers who need the full DBF shape.
                var matchObjs = truncated.Select(r => ShapeMatchEnvelope(r)).ToList();

                return new
                {
                    matches = matchObjs,
                    total = matched.Count,
                    truncated = matched.Count > matchObjs.Count,
                    didYouMean,
                };
            });

        // ---------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------

        /// <summary>
        /// Default cap on returned matches when the operator omits the limit
        /// parameter. Sized for an LLM-driven disambiguator: enough headroom
        /// to surface a handful of John-Smiths without overwhelming the host
        /// context window.
        /// </summary>
        public const int DefaultFindLimit = 20;

        /// <summary>
        /// Hard ceiling on the returned matches per call. The `total` field
        /// remains exact above this cap so an LLM can detect overflow and
        /// re-call lacerte_client_list with pagination if needed.
        /// </summary>
        public const int MaxFindLimit = 100;

        /// <summary>
        /// P18-005 — case-insensitive SUBSTRING filter over the conventional
        /// name columns. Individual modules (rows that carry both C1_1 and
        /// C1_2): match against EITHER column so "john" or "smith" both find
        /// "John Smith". Non-Individual rows (only C1_1 — corp/partnership
        /// entity name): match against C1_1 only.
        ///
        /// <para>Public so tests can drive the pure helper directly without
        /// spinning up the WrapToolHandler envelope.</para>
        /// </summary>
        public static IEnumerable<IReadOnlyDictionary<string, object?>> FilterByName(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string substring)
        {
            if (rows == null) yield break;
            if (string.IsNullOrEmpty(substring))
            {
                foreach (var r in rows) yield return r;
                yield break;
            }
            foreach (var row in rows)
            {
                if (RowMatches(row, substring)) yield return row;
            }
        }

        private static bool RowMatches(IReadOnlyDictionary<string, object?> row, string substring)
        {
            // Both columns are checked when present; either hit accepts the
            // row. Missing columns (entity modules don't carry C1_2) are
            // silently skipped so the helper works across module shapes.
            if (TryGetStringField(row, "C1_1", out var first) &&
                first.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (TryGetStringField(row, "C1_2", out var last) &&
                last.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }

        private static bool TryGetStringField(
            IReadOnlyDictionary<string, object?> row, string key, out string value)
        {
            if (row.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrEmpty(s))
            {
                value = s;
                return true;
            }
            value = string.Empty;
            return false;
        }

        private static object ShapeMatchEnvelope(IReadOnlyDictionary<string, object?> row)
        {
            // clientNumber is injected by CloneRow (camelCase per P17-011),
            // clientId is the normalized form of the seed's ClientID alias,
            // firstName/lastName project from C1_1/C1_2, email from C1_3.
            // Missing fields surface as null rather than throwing so the
            // envelope shape stays stable across module shapes.
            row.TryGetValue("clientNumber", out var clientNumber);
            row.TryGetValue("clientId", out var clientId);
            row.TryGetValue("C1_1", out var c1_1);
            row.TryGetValue("C1_2", out var c1_2);
            row.TryGetValue("C1_3", out var c1_3);
            return new
            {
                clientNumber,
                clientId,
                firstName = c1_1,
                lastName = c1_2,
                email = c1_3,
            };
        }

        /// <summary>
        /// P18-005 — build a "did you mean ...?" hint when a find returned 0
        /// rows. Computes the Levenshtein distance from the supplied
        /// substring to every C1_1 + C1_2 value present in the roster,
        /// surfaces the top 3 closest as a single hint string. Kept simple
        /// (no normalized-score threshold) because the typical roster is
        /// small (low hundreds of clients) and a 3-name surface is cheap
        /// for the LLM to interpret. Returns null when the roster is empty.
        /// </summary>
        public static string? BuildDidYouMeanHint(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string query)
        {
            if (rows == null || rows.Count == 0 || string.IsNullOrEmpty(query)) return null;
            var candidates = new List<string>();
            foreach (var row in rows)
            {
                if (TryGetStringField(row, "C1_1", out var first)) candidates.Add(first);
                if (TryGetStringField(row, "C1_2", out var last)) candidates.Add(last);
            }
            if (candidates.Count == 0) return null;

            var queryLower = query.ToLowerInvariant();
            var ranked = candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => new { Name = c, Dist = Levenshtein(c.ToLowerInvariant(), queryLower) })
                .OrderBy(x => x.Dist)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Name)
                .ToList();
            if (ranked.Count == 0) return null;
            return $"No client name contains '{query}'. Closest matches: {string.Join(", ", ranked)}. If one of these is what you meant, re-call lacerte_client_find with that string.";
        }

        // Iterative Levenshtein with two-row optimization. Sized for the
        // typical Lacerte roster (hundreds of clients, names <= 32 chars),
        // not for high-frequency hot-path use.
        private static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                Array.Copy(curr, prev, b.Length + 1);
            }
            return prev[b.Length];
        }
    }
}
