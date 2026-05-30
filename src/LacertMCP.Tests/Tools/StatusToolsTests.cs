using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #46 — `lacerte_status_read` (DATA5 F4 status).</summary>
    public class StatusToolsTests
    {
        [Fact]
        public async Task StatusRead_NoContext_Returns9009()
        {
            var tools = new StatusTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteStatusRead("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task StatusRead_ContextSet_ReturnsSuccessEnvelope()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new StatusTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteStatusRead("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            // Sim seed may have 0 or more F4-status rows; the envelope shape is the assertion.
            Assert.True(result.GetProperty("count").GetInt32() >= 0);
            Assert.True(result.TryGetProperty("statuses", out _));
        }
    }
}
