using LacertMCP.Cli.Commands;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// register/login <c>--app-name</c> default must follow LACERTE_APP_NAME (the
    /// variable the server reads) so the CLI registers under the same name the
    /// server later presents; falls back to the server's hard default. Pure seam —
    /// no env mutation.
    /// </summary>
    public class AppNameDefaultTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankEnv_FallsBackToOperatorLacerteMcp(string? env)
        {
            Assert.Equal("Operator Lacerte MCP", RegisterCommand.ResolveDefaultAppName(env));
        }

        [Fact]
        public void SetEnv_IsUsedVerbatim()
        {
            Assert.Equal("Smith CPA MCP", RegisterCommand.ResolveDefaultAppName("Smith CPA MCP"));
        }

        [Fact]
        public void FallbackConstant_MatchesServerDefault()
        {
            // LacerteSessionManager.DefaultAppName is internal to the server; the test
            // assembly has InternalsVisibleTo, so pin the two literals together.
            Assert.Equal(LacertMCP.Server.Session.LacerteSessionManager.DefaultAppName, RegisterCommand.FallbackAppName);
        }
    }
}
