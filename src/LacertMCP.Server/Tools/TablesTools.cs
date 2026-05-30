using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Generic DBF table reader for DATA1..DATA7. TY21+ branching is the
    /// session manager's job (Boundary Invariant 5) — the tool just passes
    /// the table number through. Per <c>Todo.md</c> Phase 5 task #43.
    /// </summary>
    [McpServerToolType]
    public sealed class TablesTools
    {
        private readonly LacerteSessionManager _session;

        public TablesTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read rows from one of the DATA1..DATA7 DBF tables in the active context. Optional clientNumber/stateAbbrev filters narrow the result. Supports limit/offset pagination. TY21+ branching is handled by the session manager — callers should not branch on year/module themselves.")]
        public Task<string> LacerteTableRead(
            [Description("Table number, 1-7. 1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status (F4), 6=Display, 7=Forms. DATA8 is reserved (rejected).")] int table,
            [Description("Optional ClientNumber filter (case-insensitive equality).")] string? clientNumber = null,
            [Description("Optional 2-letter state abbreviation filter (case-insensitive equality). Required for DATA3/DATA4 in some live SQL templates; the session manager handles the SQL shape.")] string? stateAbbrev = null,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the filtered result set. Default 0.")] int offset = 0)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadTable(table, clientNumber, stateAbbrev).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);

                // NOTE: `count` and `rows` are kept; `rows` previously held the full
                // (post-filter) result and now holds the paginated slice — same key,
                // additive new sibling fields (total/limit/offset/hasMore/nextOffset).
                // The legacy `count` field will be removed in v0.2; new consumers
                // should read `total` for the full-set size.
                return new
                {
                    table,
                    clientNumber,
                    stateAbbrev,
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                };
            });
    }
}
