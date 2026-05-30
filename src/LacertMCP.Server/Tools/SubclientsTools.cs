using System.ComponentModel;
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
    /// </summary>
    [McpServerToolType]
    public sealed class SubclientsTools
    {
        private readonly LacerteSessionManager _session;

        public SubclientsTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA2 subclient rows for one client. Subclients are dependents, partners, shareholders, or beneficiaries depending on the active module. Keyed by (ClientNumber, SubClientID).")]
        public Task<string> LacerteSubclientRead(
            [Description("The ClientNumber whose subclient rows to read.")] string clientNumber)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadSubclients(clientNumber).ConfigureAwait(false);
                return new
                {
                    clientNumber,
                    count = rows.Count,
                    subclients = rows,
                };
            });
    }
}
