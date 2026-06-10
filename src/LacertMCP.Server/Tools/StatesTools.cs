using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA3 (State Client Info) and DATA4 (State SubClient Info) read tools.
    /// Per <c>Todo.md</c> Phase 5 task #45.
    ///
    /// <para>
    /// Audit P16-047 — paginated envelopes (limit/offset/total/hasMore/
    /// nextOffset) so a multistate partnership (200 partners × 50 states =
    /// 10,000 DATA4 rows) doesn't return everything in one envelope. Audit
    /// P16-076 — both reads accept CancellationToken.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class StatesTools
    {
        private readonly LacerteSessionManager _session;

        public StatesTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA3 state rows for one client. Each row carries state-level overrides (apportionment, addbacks, residency) keyed by (ClientNumber, StateAbbrev). Supports limit/offset pagination.")]
        public Task<string> LacerteStateRead(
            [Description("The ClientNumber whose state rows to read.")] string clientNumber,
            [Description("Optional 2-letter state abbreviation filter (e.g. \"CA\"). If omitted, returns all state rows for the client.")] string? stateAbbrev = null,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the filtered result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadTable(3, clientNumber, stateAbbrev, cancellationToken).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);
                // P16-024 — canonical `rows` key for cross-tool consistency.
                // `states` retained as a v0.1 alias (removed in v0.2).
                // P16-047 — paginated envelope.
                return new
                {
                    clientNumber,
                    stateAbbrev,
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    // R6-19 — rows-on-this-page count (Pagination P16-074).
                    returnedCount = page.ReturnedCount,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    states = page.Rows,
                };
            });

        [McpServerTool]
        [Description("Read DATA4 state-subclient rows for one client. Each row is a per-state override on a subclient (e.g. partner-level state apportionment) keyed by (ClientNumber, SubClientId, StateAbbrev). Supports limit/offset pagination — multistate partnerships (200 partners × 50 states) generate the largest result sets in the API.")]
        public Task<string> LacerteStateSubclientRead(
            [Description("The ClientNumber whose state-subclient rows to read.")] string clientNumber,
            [Description("Optional 2-letter state abbreviation filter. If omitted, returns all states.")] string? stateAbbrev = null,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the filtered result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadTable(4, clientNumber, stateAbbrev, cancellationToken).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);
                // P16-024 — canonical `rows` key for cross-tool consistency.
                // `stateSubclients` retained as a v0.1 alias (removed in v0.2).
                // P16-047 — paginated envelope.
                return new
                {
                    clientNumber,
                    stateAbbrev,
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    // R6-19 — rows-on-this-page count (Pagination P16-074).
                    returnedCount = page.ReturnedCount,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    stateSubclients = page.Rows,
                };
            });
    }
}
