using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA2 (SubClient Info) read tool. Per <c>Todo.md</c> Phase 5 task #44.
    /// SubClients are dependents (individual), partners (partnership),
    /// shareholders (S-corp), beneficiaries (fiduciary), etc.
    ///
    /// <para>
    /// Audit P16-047 — paginated envelope (limit/offset/total/hasMore/
    /// nextOffset) so a 200-partner partnership doesn't return all rows in
    /// one unbounded envelope. Audit P16-076 — accepts CancellationToken so
    /// `notifications/cancelled` on a slow read aborts cleanly.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class SubclientsTools
    {
        private readonly LacerteSessionManager _session;

        public SubclientsTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA2 subclient rows for one client. Subclients are dependents, partners, shareholders, or beneficiaries depending on the active module. Keyed by (ClientNumber, SubClientID). Supports limit/offset pagination — large partnerships / S-corps return many rows.")]
        public Task<string> LacerteSubclientRead(
            [Description("The ClientNumber whose subclient rows to read.")] string clientNumber,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadSubclients(clientNumber, cancellationToken).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);
                // P16-024 — canonical `rows` key for cross-tool consistency.
                // `subclients` retained as a v0.1 alias for back-compat (removed
                // in v0.2). New consumers should target `rows`.
                // P16-047 — paginated envelope; legacy `count` reflects the
                // slice size; new consumers should read `total`.
                return new
                {
                    clientNumber,
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
                    subclients = page.Rows,
                };
            });
    }
}
