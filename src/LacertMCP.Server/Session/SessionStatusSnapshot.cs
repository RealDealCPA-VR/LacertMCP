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
        string? BuildVersion = null,
        // P15-030: when mode resolution overrode an operator's env preference
        // (both LACERTE_LIVE=1 and LACERTE_SIMULATION=1, OR LACERTE_LIVE=1 off-
        // Windows), this carries the human-readable reason. Null when the
        // resolution was unambiguous so the snapshot stays compact in the
        // common case. Surfaced via `lacerte_session_status` so the operator
        // who expected Live can immediately see WHY Sim engaged.
        string? ModeResolutionReason = null,
        // P15-004: surfaces the parsed LACERTE_DEFAULT_TAX_YEAR (validated
        // >= 2018 in the ctor; null when unset). Operator-facing confirmation
        // that the documented env var was actually picked up — before P15-004
        // the var was advertised in README/.env.example but never read.
        int? DefaultTaxYear = null,
        // P16-066: surfaces the LACERTE_DEBUG_WIRE state + resolved log path so
        // an operator hitting the 9201/9203 remediation ("inspect logs/
        // lacerte-YYYYMMDD.log") can immediately see whether logging is on AND
        // where the log file lives — without having to read source or rely on
        // the startup banner (which Claude Desktop swallows). DebugWireEnabled
        // mirrors WireLog.IsEnabled; WireLogPath is the path WireLog would
        // write to today (always non-null — when disabled it's the would-be
        // path so the operator can enable + restart + grep without guessing).
        bool DebugWireEnabled = false,
        string? WireLogPath = null);
}
