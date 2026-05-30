namespace LacertMCP.Server.Errors
{
    /// <summary>
    /// Describes a single Lacerte SDK status code: the canonical name, severity classification,
    /// human-readable user message, and (optionally) remediation guidance.
    ///
    /// Ported from <c>docs/research/agents/05-error-mapping.md</c> §2 (TS <c>StatusInfo</c> interface).
    /// </summary>
    public sealed class StatusInfo
    {
        public string Name { get; }
        public Severity Severity { get; }
        public string UserMessage { get; }
        public string? Remediation { get; }

        public StatusInfo(string name, Severity severity, string userMessage, string? remediation = null)
        {
            Name = name;
            Severity = severity;
            UserMessage = userMessage;
            Remediation = remediation;
        }
    }
}
