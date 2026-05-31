using System;
using Microsoft.Extensions.Logging;

namespace LacertMCP.Server.Startup
{
    /// <summary>
    /// P15-053 — parses the <c>LACERTE_LOG_LEVEL</c> environment variable into a
    /// <see cref="LogLevel"/> for the Microsoft.Extensions.Logging pipeline.
    /// Default is <see cref="LogLevel.Information"/>; unparseable values
    /// silently fall back to the default (the value still appears in the
    /// startup banner so the operator can spot the typo).
    /// </summary>
    /// <remarks>
    /// Pure function with no env access of its own — callers pass the raw
    /// value so this is trivially unit-testable. Mirrors the existing
    /// <c>ComputeExitCode</c> seam pattern from <c>DoctorCommand</c> (P14-071).
    /// </remarks>
    public static class LogLevelEnv
    {
        public const string EnvName = "LACERTE_LOG_LEVEL";

        public const LogLevel Default = LogLevel.Information;

        /// <summary>Parse a raw env-var value into a <see cref="LogLevel"/>.
        /// Returns <see cref="Default"/> for null/empty/unparseable input.</summary>
        public static LogLevel Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Default;
            // Enum.TryParse accepts numeric strings ("9") even when no enum
            // member is defined for that value, so explicitly reject digits
            // and require the parsed value to be a defined LogLevel.
            var trimmed = raw!.Trim();
            if (trimmed.Length > 0 && (char.IsDigit(trimmed[0]) || trimmed[0] == '-' || trimmed[0] == '+'))
                return Default;
            return Enum.TryParse<LogLevel>(trimmed, ignoreCase: true, out var lvl) && Enum.IsDefined(typeof(LogLevel), lvl)
                ? lvl
                : Default;
        }
    }
}
