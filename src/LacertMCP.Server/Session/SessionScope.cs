using System.Threading;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// Per-connection state for the HTTP transport. The HTTP host sets
    /// <see cref="Current"/> before handing each request to the MCP transport
    /// (with FlowExecutionContextFromRequests on, the tool call inherits it), so
    /// <see cref="LacerteSessionManager.Context"/> resolves to this connection's
    /// own tuple. Under stdio <see cref="Current"/> is null and the manager uses
    /// its single process-wide context, exactly as before.
    /// </summary>
    public sealed class SessionScope
    {
        private static readonly AsyncLocal<SessionScope?> s_current = new AsyncLocal<SessionScope?>();

        public static SessionScope? Current
        {
            get => s_current.Value;
            set => s_current.Value = value;
        }

        /// <summary>This connection's active context (null until lacerte_set_context).</summary>
        public SessionContext? Context { get; set; }

        /// <summary>True when the calling device only has read access: every write is refused with 9001.</summary>
        public bool ReadOnlyAccess { get; set; }

        /// <summary>Who is on the other end, for activity records ("desktop-0ngsgb4", "this computer").</summary>
        public string DeviceName { get; set; } = string.Empty;
    }
}
