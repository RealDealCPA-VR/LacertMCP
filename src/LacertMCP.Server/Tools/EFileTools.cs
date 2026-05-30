using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// E-file status read tool. TY19+ — joins the EFilings and EFStatuses
    /// tables to produce a flat per-filing status row. Per <c>Todo.md</c>
    /// Phase 5 task #49.
    /// </summary>
    [McpServerToolType]
    public sealed class EFileTools
    {
        private readonly LacerteSessionManager _session;

        public EFileTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read the per-client e-file status by joining EFilings and EFStatuses in the active context. TY19+ only. Returns flat rows with EFilingId, ClientId, EFId, InstanceId, Status, StatusDate.")]
        public Task<string> LacerteEfStatus()
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadEFStatus().ConfigureAwait(false);
                return new { count = rows.Count, efStatuses = rows };
            });
    }
}
