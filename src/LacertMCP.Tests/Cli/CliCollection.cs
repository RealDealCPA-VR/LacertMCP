using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Forces every CLI test class into one xUnit collection so they run sequentially.
    ///
    /// <para>
    /// The CLI tests mutate process-global state: the <c>APPDATA</c> and
    /// <c>LACERTE_MCP_FORCE_FILE_STORE</c> environment variables, plus the
    /// <see cref="LacertMCP.Cli.Storage.AppIdStore"/> file-backed config. Running
    /// them in parallel — xUnit's default for separate classes — produces flaky
    /// failures where one class's fixture wipes the other's APPDATA mid-test.
    /// Sharing one collection forces serial execution.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name)]
    public class CliCollection
    {
        public const string Name = "Cli (serial)";
    }
}
