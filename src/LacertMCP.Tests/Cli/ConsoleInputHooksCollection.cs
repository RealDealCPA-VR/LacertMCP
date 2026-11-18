using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Tests that set the CLI's static ConsoleInput hooks (PIN / line readers,
    /// redirected-input override) run one at a time.
    /// </summary>
    [CollectionDefinition(Name)]
    public class ConsoleInputHooksCollection
    {
        public const string Name = "ConsoleInputHooks";
    }
}
