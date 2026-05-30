namespace LacertMCP.Server.Errors
{
    /// <summary>
    /// Classification of a Lacerte SDK status return.
    ///
    /// Ported from <c>docs/research/agents/05-error-mapping.md</c> §3:
    /// <list type="bullet">
    ///   <item><description><c>Success</c> — non-error terminals (status 1, 201).</description></item>
    ///   <item><description><c>Warning</c> — access-map informational answers
    ///     (<c>ExposedForRead</c>/<c>ExposedForWrite</c>/<c>ExposedForReadWrite</c>/<c>NotExposed</c>).
    ///     Escalated to error by the caller when returned from a data call.</description></item>
    ///   <item><description><c>Error</c> — everything else.</description></item>
    /// </list>
    /// </summary>
    public enum Severity
    {
        Success,
        Warning,
        Error,
    }
}
