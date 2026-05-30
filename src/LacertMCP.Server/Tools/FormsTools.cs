using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA7 (Form Info) read tool. Per <c>Todo.md</c> Phase 5 task #47.
    /// Returns the form list in the return for one client. Read-only.
    /// </summary>
    [McpServerToolType]
    public sealed class FormsTools
    {
        private readonly LacerteSessionManager _session;

        public FormsTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA7 form rows for one client. Returns the list of forms attached to the return. Read-only.")]
        public Task<string> LacerteFormsRead(
            [Description("The ClientNumber whose form rows to read.")] string clientNumber)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadForms(clientNumber).ConfigureAwait(false);
                return new
                {
                    clientNumber,
                    count = rows.Count,
                    forms = rows,
                };
            });
    }
}
