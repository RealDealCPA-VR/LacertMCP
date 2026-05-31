using System;
using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
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
    ///
    /// P15-054: when the periodic heartbeat writer is enabled (default 30s;
    /// disable by setting <c>LACERTE_HEARTBEAT_SECONDS=0</c>), the envelope
    /// also surfaces <c>heartbeatPath</c> so an operator can point a Task
    /// Scheduler / NSSM / PowerShell watcher at the file without grepping the
    /// docs for the canonical path.
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
        [Description("Health probe. Returns a structured { mode, status, heartbeatPath? } envelope reporting the active session mode (simulation/live) and reachability. Used to confirm the MCP stdio plumbing + the Lacerte SDK runtime shim are both functional.")]
        public Task<string> LacerteHealth()
            // Live SDK HRESULT surfacing via _session.HealthProbe() is Windows-gated —
            // tracked as a follow-up in Todo.md. The { mode, status } shape is the
            // observational contract so sim/live stay identical at the seam.
            => WrapToolHandler.Run(() =>
            {
                var heartbeatSeconds = HeartbeatWriter.IntervalSeconds(
                    Environment.GetEnvironmentVariable(HeartbeatWriter.IntervalEnvName));
                if (heartbeatSeconds > 0)
                {
                    return Task.FromResult<object>(new
                    {
                        mode = _session.Mode.ToString().ToLowerInvariant(),
                        status = "reachable",
                        heartbeatPath = HeartbeatWriter.DefaultPath(),
                        heartbeatSeconds,
                    });
                }
                return Task.FromResult<object>(new
                {
                    mode = _session.Mode.ToString().ToLowerInvariant(),
                    status = "reachable",
                });
            });
    }
}
