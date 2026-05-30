using System;
using LacertMCP.Server.Bridge;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// Point-in-time snapshot of the session manager's auth + context state.
    /// Returned by <see cref="LacerteSessionManager.SessionStatus"/>. NO wire
    /// I/O — pure read of in-memory fields, safe to call before
    /// <see cref="LacerteSessionManager.Register"/>.
    ///
    /// <para>
    /// Shape mirrors the QB MCP <c>qb_session_status</c> tool surface per
    /// agent 02 §6: <c>{ state, mode, registeredAt, expiresAt,
    /// minutesUntilExpiry, appName, appId, ...context }</c>. <c>AppId</c> is
    /// returned as the literal string <c>"***"</c> for security — the real
    /// GUID lives in the Windows Credential Manager and is never echoed.
    /// </para>
    /// </summary>
    public sealed record SessionStatusSnapshot(
        SessionMode Mode,
        bool IsRegistered,
        DateTimeOffset? RegisteredAt,
        DateTimeOffset? ExpiresAt,
        double? MinutesUntilExpiry,
        SessionContext? Context,
        string? AppName,
        string AppId,
        EditionInfo? Edition = null,
        // P14-040: build stamp for bug reports — the operator's dev box (sim) and
        // Windows operator box (live) are necessarily different machines and the
        // operator will hand-copy binaries between them. The version comes from
        // Directory.Build.props -> AssemblyInformationalVersion via BuildInfo.
        string? BuildVersion = null);
}
