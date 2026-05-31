using System.ComponentModel;
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
        [Description("List clients in the active (datapath, year, module) context. Optional case-insensitive prefix filter on last-name (individual modules) or entity name (corporate/partnership/etc.). Supports limit/offset pagination.")]
        public Task<string> LacerteClientList(
            [Description("Optional case-insensitive prefix to match against the conventional name field (C1_2 for individual modules, C1_1 for entity modules).")] string? nameFilter = null,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the filtered result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // Pull the full filtered set from the session, then page in-tool via
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
                var rows = await _session.ListClients(nameFilter, int.MaxValue, cancellationToken).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);

                // NOTE: `count` and `clients` are legacy fields kept for additive
                // backwards-compat with pre-pagination consumers. They will be
                // removed in v0.2 — new code should read `rows` + `total`.
                return new
                {
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    clients = page.Rows,
                };
            });

        [McpServerTool]
        [Description("Read one client by ClientNumber from DATA1 in the active context. Returns null if no such client exists.")]
        public Task<string> LacerteClientRead(
            [Description("The ClientNumber (sim-assigned monotonic identifier; e.g. \"1001\").")] string clientNumber,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var row = await _session.ReadClient(clientNumber, cancellationToken).ConfigureAwait(false);
                return new { clientNumber, found = row != null, client = row };
            });
    }
}
