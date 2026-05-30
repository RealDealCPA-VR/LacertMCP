using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA5 (F4 Status Info) read tool. Per <c>Todo.md</c> Phase 5 task #46.
    /// DATA5 surfaces the per-client F4 status indicators (e.g. completed,
    /// e-file ready, locked); read-only outside a few global toggles.
    /// </summary>
    [McpServerToolType]
    public sealed class StatusTools
    {
        private readonly LacerteSessionManager _session;

        public StatusTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA5 F4-status rows for one client. Surfaces per-client status indicators (completed, e-file ready, locked, etc.). DATA5 is read-only outside a few global flags.")]
        public Task<string> LacerteStatusRead(
            [Description("The ClientNumber whose status rows to read.")] string clientNumber)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadStatuses(clientNumber).ConfigureAwait(false);
                return new
                {
                    clientNumber,
                    count = rows.Count,
                    statuses = rows,
                };
            });
    }
}
