using System.Linq;
using LacertMCP.Server.Web;
using Xunit;

namespace LacertMCP.Tests.Web
{
    /// <summary>
    /// The workstation one-liner (GET /setup.ps1) and the stdio bridge
    /// (GET /bridge.ps1). Behaviour was exercised end to end under pwsh against
    /// the HTTP server; these pin the properties Windows PowerShell 5.1 needs.
    /// </summary>
    public class WorkstationSetupTests
    {
        private const string Base = "http://100.88.12.7:8767";

        [Fact]
        public void SetupScript_PointsAtTheServerItWasFetchedFrom()
        {
            var s = WorkstationSetup.SetupScript(Base);
            Assert.Contains("$Base = '" + Base + "'", s);
            Assert.Contains("irm " + Base + "/setup.ps1 | iex", s);
            Assert.DoesNotContain("__BASE__", s);
            Assert.DoesNotContain("__DIR__", s);
            Assert.DoesNotContain("__NAME__", s);
        }

        [Fact]
        public void Scripts_ArePureAscii_WithCrlfLineEndings()
        {
            foreach (var s in new[] { WorkstationSetup.SetupScript(Base), WorkstationSetup.BridgeScript() })
            {
                // PowerShell 5.1 reads BOM-less files as ANSI: any non-ASCII byte would be mangled.
                Assert.True(s.All(c => c < 128), "script contains a non-ASCII character");
                Assert.DoesNotContain("\r\r", s);
                Assert.Equal(s.Count(c => c == '\n'), s.Split(new[] { "\r\n" }, System.StringSplitOptions.None).Length - 1);
            }
        }

        [Fact]
        public void SetupScript_SupportsUninstall_AndNeverRewritesUnreadableConfigs()
        {
            var s = WorkstationSetup.SetupScript(Base);
            Assert.Contains("LACERTE_MCP_UNINSTALL", s);
            Assert.Contains("isn't valid JSON", s);
            Assert.Contains("/access-check", s);
        }

        [Fact]
        public void Bridge_AnswersEveryRequest_EvenOnFailure()
        {
            var b = WorkstationSetup.BridgeScript();
            Assert.Contains("Send-Error", b);
            Assert.Contains("returned no answer", b);
            Assert.Contains("KeepAlive = $false", b);
            // Status must be read before the error response is closed (it reads 0 afterwards).
            Assert.True(b.IndexOf("$code = [int]$r.StatusCode") < b.IndexOf("$r.Close()"));
        }
    }
}
