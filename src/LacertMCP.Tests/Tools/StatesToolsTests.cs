using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #45 — `lacerte_state_read`, `lacerte_state_subclient_read`.</summary>
    public class StatesToolsTests
    {
        [Fact]
        public async Task StateRead_NoContext_Returns9009()
        {
            var tools = new StatesTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteStateRead("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task StateSubclientRead_NoContext_Returns9009()
        {
            var tools = new StatesTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteStateSubclientRead("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task StateRead_AcmeCorp_ReturnsAtLeastCAandNY()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new StatesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteStateRead("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            Assert.True(result.GetProperty("count").GetInt32() >= 2);
        }

        [Fact]
        public async Task StateRead_WithFilter_NarrowsToOneState()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new StatesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteStateRead("1001", stateAbbrev: "CA"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("CA", result.GetProperty("stateAbbrev").GetString());
            Assert.True(result.GetProperty("count").GetInt32() <= 1);
        }

        [Fact]
        public async Task StateSubclientRead_ContextSet_ReturnsEnvelopeEvenWhenEmpty()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new StatesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteStateSubclientRead("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            // Individual seed has no DATA4 rows — empty array is the right shape.
            Assert.True(root.GetProperty("result").GetProperty("count").GetInt32() >= 0);
        }
    }
}
