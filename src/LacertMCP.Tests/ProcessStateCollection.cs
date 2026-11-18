using Xunit;

namespace LacertMCP.Tests
{
    /// <summary>
    /// Runs alone, after the parallel collections: for tests that flip
    /// process-wide statics every other test reads (e.g.
    /// <c>SessionScope.Required</c>, which makes every scope-less session
    /// manager report no context while it is on).
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class ProcessStateCollection
    {
        public const string Name = "ProcessState";
    }
}
