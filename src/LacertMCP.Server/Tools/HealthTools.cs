using System.ComponentModel;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Phase-0 spike tool. Proves the MCP stdio transport, attribute-based tool
    /// discovery, and DI plumbing all work end-to-end from <c>dotnet run</c>.
    ///
    /// Per Todo.md Phase 0 #5: returns "reachable (sim)" in simulation mode and
    /// (later, when Stream 4 + Stream 9 land) the COM HRESULT of
    /// <c>new LacerteSDK_Access.LacerteAccess()</c> in live mode.
    /// </summary>
    [McpServerToolType]
    public class HealthTools
    {
        private readonly LacerteSessionManager _session;

        public HealthTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Health probe. Returns 'reachable (sim)' in simulation mode, or the COM HRESULT of `new LacerteSDK_Access.LacerteAccess()` in live mode. Used to confirm the MCP stdio plumbing + the Lacerte SDK runtime shim are both functional.")]
        public string LacerteHealth()
        {
            // TODO (Phase 9): when the COM bridge lands, branch on _session.Mode and
            // return the HRESULT from instantiating LacerteSDK_Access.LacerteAccess()
            // in live mode. For now we exercise the sim path only.
            return "reachable (sim)";
        }
    }
}
