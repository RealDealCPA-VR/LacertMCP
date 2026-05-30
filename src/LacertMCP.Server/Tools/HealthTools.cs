using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Phase-0 spike tool. Proves the MCP stdio transport, attribute-based tool
    /// discovery, and DI plumbing all work end-to-end from <c>dotnet run</c>.
    ///
    /// Per Todo.md Phase 0 #5 and audit task P14-001: returns a structured
    /// envelope <c>{ mode, status }</c> in both modes — the live HRESULT probe
    /// remains a Phase-9 follow-up via <c>_session.HealthProbe()</c>, but the
    /// shape commitment is locked in now so live and sim stay observationally
    /// identical (Boundary Invariant #2) and the response always rides the
    /// canonical WrapToolHandler envelope (Invariant #9).
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
        [Description("Health probe. Returns a structured { mode, status } envelope reporting the active session mode (simulation/live) and reachability. Used to confirm the MCP stdio plumbing + the Lacerte SDK runtime shim are both functional.")]
        public Task<string> LacerteHealth()
            // TODO (Phase 9): surface live SDK HRESULT via _session.HealthProbe()
            // — keep the { mode, status } shape so sim/live stay observationally
            // identical at the seam.
            => WrapToolHandler.Run(() => Task.FromResult<object>(new
            {
                mode = _session.Mode.ToString().ToLowerInvariant(),
                status = "reachable",
            }));
    }
}
