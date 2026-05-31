using System;
using System.Collections.Generic;

namespace LacertMCP.Server.Errors
{
    /// <summary>
    /// Structured exception carrying a Lacerte SDK status code plus all the metadata needed to
    /// render a useful MCP error response. Ported from agent 05 §2 (TS <c>LacerteSDKError</c>
    /// class) — C# idiom: extends <see cref="Exception"/>, exposes the structured fields as
    /// readonly properties, looks up <see cref="StatusInfo"/> from <see cref="StatusCodes"/>
    /// (or <see cref="SyntheticStatusCodes"/> when applicable) at construction time.
    /// </summary>
    public class LacerteSDKError : Exception
    {
        /// <summary>
        /// Numeric threshold separating SDK-origin codes (TAPIStatus 0-28 + TPSDKStatus
        /// 200-204) from MCP-minted synthetic codes (9001-9299). Codes &gt;= this value
        /// are synthetic, &lt; this are SDK. See DECISIONS.md 2026-05-30 "Synthetic
        /// statusCode range" + 2026-05-30 "Error envelope carries origin discriminator"
        /// (P15-037).
        /// </summary>
        public const int SyntheticCodeThreshold = 9000;

        public int StatusCode { get; }
        public string StatusName { get; }
        public Severity Severity { get; }
        public string UserMessage { get; }
        public string? Remediation { get; }
        public IReadOnlyDictionary<string, object?> Context { get; }

        /// <summary>
        /// P15-037: surface whether this error came from the SDK or was MCP-minted, so
        /// wire-envelope consumers (LLMs, operators) don't have to memorize the 9000+
        /// range to know where the remediation lives. Derived from <see cref="StatusCode"/>
        /// at construction.
        /// <list type="bullet">
        ///   <item><description><c>"sdk"</c> — real Lacerte TAPIStatus/TPSDKStatus state;
        ///   look up the code in Intuit's SDK documentation; remediation is operator-side.</description></item>
        ///   <item><description><c>"synthetic"</c> — minted by this MCP before any SDK call;
        ///   pre-flight validation, auth gate, or bridge runtime; remediation is usually
        ///   MCP-side config.</description></item>
        /// </list>
        /// </summary>
        public string Origin => StatusCode >= SyntheticCodeThreshold ? "synthetic" : "sdk";

        public LacerteSDKError(int code)
            : this(code, EmptyContext)
        {
        }

        public LacerteSDKError(int code, IReadOnlyDictionary<string, object?> context)
            : base(BuildMessage(code))
        {
            var info = LookupStatusInfo(code);
            StatusCode = code;
            StatusName = info.Name;
            Severity = info.Severity;
            UserMessage = info.UserMessage;
            Remediation = info.Remediation;
            Context = context ?? EmptyContext;
        }

        public LacerteSDKError(int code, IReadOnlyDictionary<string, object?> context, Exception innerException)
            : base(BuildMessage(code), innerException)
        {
            var info = LookupStatusInfo(code);
            StatusCode = code;
            StatusName = info.Name;
            Severity = info.Severity;
            UserMessage = info.UserMessage;
            Remediation = info.Remediation;
            Context = context ?? EmptyContext;
        }

        private static StatusInfo LookupStatusInfo(int code)
        {
            // Synthetic 9xxx codes take priority over the unknown-fallback in StatusCodes.
            if (SyntheticStatusCodes.StatusTable.TryGetValue(code, out var syntheticInfo))
            {
                return syntheticInfo;
            }

            return StatusCodes.GetStatusInfo(code);
        }

        private static string BuildMessage(int code)
        {
            var info = LookupStatusInfo(code);
            return $"[{info.Name}] {info.UserMessage}";
        }

        private static readonly IReadOnlyDictionary<string, object?> EmptyContext =
            new Dictionary<string, object?>();
    }
}
