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
        public int StatusCode { get; }
        public string StatusName { get; }
        public Severity Severity { get; }
        public string UserMessage { get; }
        public string? Remediation { get; }
        public IReadOnlyDictionary<string, object?> Context { get; }

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
