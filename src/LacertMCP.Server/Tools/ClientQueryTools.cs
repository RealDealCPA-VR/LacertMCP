using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// R6 gap item #3 — `lacerte_client_query`. Server-side filtered
    /// cross-client search so an LLM can answer roster-wide questions
    /// ("all clients missing an email", "all CA clients", "everyone whose
    /// C1_5 is empty") WITHOUT paging the full roster through its context
    /// window. Before this tool, bulk-target selection meant pulling every
    /// DATA1 row via lacerte_table_read / lacerte_client_list and filtering
    /// client-side — hundreds of rows of token spend per question.
    ///
    /// <para>The predicate runs over the SAME row dictionaries that
    /// <c>lacerte_table_read</c> returns (FoxPro column codes like
    /// <c>C1_3</c> verbatim, plus the camelCase SDK-meta keys
    /// <c>clientNumber</c> / <c>clientId</c> / <c>subClientId</c> /
    /// <c>stateAbbrev</c> injected by the session's CloneRow), so a field
    /// name observed in a table_read response works here unchanged.</para>
    ///
    /// <para>Pagination follows the standard contract (P16-074/R6-19):
    /// rows / total / limit / offset / returnedCount / hasMore /
    /// nextOffset — where <c>total</c> is the MATCHED count (post-filter),
    /// not the table size. The envelope echoes the applied
    /// <c>filter</c> so an LLM can self-check what was actually run.</para>
    /// </summary>
    [McpServerToolType]
    public sealed class ClientQueryTools
    {
        private readonly LacerteSessionManager _session;

        public ClientQueryTools(LacerteSessionManager session) => _session = session;

        /// <summary>
        /// Canonical operator set. Matching is case-insensitive on the way
        /// in ("STARTSWITH" → "startsWith"); the canonical spelling is
        /// echoed back in the envelope's <c>filter.op</c>.
        /// </summary>
        private static readonly string[] AllowedOps =
        {
            "eq", "ne", "contains", "startsWith", "empty", "notEmpty",
        };

        [McpServerTool]
        [Description("Filtered cross-client search over one DATA1..DATA7 table in the active (datapath, taxYear, module) — the token-economical way to answer roster-wide questions (\"all clients missing an email\", \"all clients whose C1_4 is 'Joint'\", \"every DATA1 row where C1_5 is empty\"). The filter runs SERVER-SIDE and only matching rows are returned, so prefer this over paging lacerte_client_list / lacerte_table_read through your context window whenever you need a subset defined by a field predicate; use lacerte_client_list for plain enumeration and lacerte_client_find for name-to-ClientNumber resolution. fieldName must match the row keys exactly as lacerte_table_read returns them: FoxPro DBF column codes verbatim (e.g. 'C1_3' = email, 'C1_4' = filing status on DATA1) plus the injected camelCase meta keys ('clientNumber', 'clientId', 'subClientId', 'stateAbbrev'). Ops (case-insensitive): eq / ne / contains / startsWith (string comparison, OrdinalIgnoreCase; non-string cell values are compared via their invariant-culture string form) and empty / notEmpty (empty = field missing OR null OR \"\"; value is ignored). Returns { filter: {table, fieldName, op, value}, rows, total, limit, offset, returnedCount, hasMore, nextOffset } where rows carry the same shape as lacerte_table_read and total is the MATCHED count across the full table (not the page size, not the table size). Pagination (limit/offset) applies to the FILTERED set.")]
        public Task<string> LacerteClientQuery(
            [Description("Row key to test — a FoxPro DBF column code exactly as lacerte_table_read returns it (e.g. 'C1_3', 'C2_2', 'C5_1') or one of the injected camelCase meta keys ('clientNumber', 'clientId', 'subClientId', 'stateAbbrev'). Case-sensitive, matching the row dictionary keys verbatim. Required.")] string fieldName,
            [Description("Predicate operator — one of: 'eq', 'ne', 'contains', 'startsWith', 'empty', 'notEmpty' (case-insensitive). eq/ne/contains/startsWith compare the cell's string form against `value` with OrdinalIgnoreCase; empty matches rows where the field is missing, null, or \"\" — notEmpty is its inverse (both ignore `value`). Required.")] string op,
            [Description("Comparison value for eq/ne/contains/startsWith. Ignored (and may be omitted) for empty/notEmpty. Default \"\".")] string value = "",
            [Description("Table number to query, 1-7. 1=Client (DATA1, default), 2=SubClient, 3=State, 4=StateSubClient, 5=Status (F4), 6=Display, 7=Forms. DATA8 is reserved (rejected).")] int table = 1,
            [Description("Maximum matched rows to return per page. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the FILTERED (matched) result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // Gate order per R6-13 convention: input validation first,
                // then context (the session's ReadTable raises 9009), then
                // table support (session raises DBNotSupported for tables
                // outside 1..7 — same code lacerte_table_read emits).
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "fieldName",
                            ["value"] = fieldName,
                            ["reason"] = "fieldName is required — pass a row key exactly as lacerte_table_read returns it (e.g. 'C1_3', 'clientNumber').",
                        });
                }

                var normalizedOp = AllowedOps.FirstOrDefault(
                    a => string.Equals(a, op?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (normalizedOp == null)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "op",
                            ["value"] = op,
                            ["reason"] = "op must be one of: eq, ne, contains, startsWith, empty, notEmpty (case-insensitive).",
                        });
                }

                var key = fieldName.Trim();
                var effectiveValue = value ?? string.Empty;

                // Same session call lacerte_table_read uses — sim reads the
                // in-memory module store; live mode inherits whatever ODBC
                // shape the session manager dispatches. No clientNumber /
                // stateAbbrev push-down: the predicate below IS the filter.
                var rows = await _session.ReadTable(table, null, null, cancellationToken)
                    .ConfigureAwait(false);

                var matched = rows
                    .Where(r => Matches(r, key, normalizedOp, effectiveValue))
                    .ToList();

                var page = Pagination.Pagination.Page(matched, limit, offset);

                return new
                {
                    // Echo the applied filter so an LLM can self-check that
                    // what ran matches what it intended (op is echoed in
                    // canonical casing).
                    filter = new
                    {
                        table,
                        fieldName = key,
                        op = normalizedOp,
                        value = effectiveValue,
                    },
                    rows = page.Rows,
                    // `total` = MATCHED rows across the whole table (the
                    // filtered-set size), NOT the unfiltered table size.
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    // R6-19 — rows-on-this-page count (Pagination P16-074).
                    returnedCount = page.ReturnedCount,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                };
            });

        // ---------------------------------------------------------------
        // Predicate
        // ---------------------------------------------------------------

        /// <summary>
        /// Evaluate one row against the (fieldName, op, value) predicate.
        /// Non-string cell values (the seed stores ints for C1_5, doubles
        /// for amounts) are compared via their invariant-culture string
        /// form so "2" matches the int 2 — the LLM only ever sees the
        /// JSON-serialized string form anyway. A missing or null field
        /// behaves as "" for eq/ne/contains/startsWith and counts as
        /// empty for empty/notEmpty.
        /// </summary>
        internal static bool Matches(
            IReadOnlyDictionary<string, object?> row,
            string fieldName,
            string op,
            string value)
        {
            string? str = null;
            if (row.TryGetValue(fieldName, out var raw) && raw != null)
            {
                str = Convert.ToString(raw, CultureInfo.InvariantCulture);
            }

            switch (op)
            {
                case "empty":
                    return string.IsNullOrEmpty(str);
                case "notEmpty":
                    return !string.IsNullOrEmpty(str);
                case "eq":
                    return string.Equals(str ?? string.Empty, value, StringComparison.OrdinalIgnoreCase);
                case "ne":
                    return !string.Equals(str ?? string.Empty, value, StringComparison.OrdinalIgnoreCase);
                case "contains":
                    return (str ?? string.Empty).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "startsWith":
                    return (str ?? string.Empty).StartsWith(value, StringComparison.OrdinalIgnoreCase);
                default:
                    // Unreachable — op is canonicalized against AllowedOps
                    // before this helper runs. Fail-closed (no match) if a
                    // future op lands in AllowedOps without a case here.
                    return false;
            }
        }
    }
}
