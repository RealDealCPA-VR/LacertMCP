using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #44 — `lacerte_subclient_read` (DATA2).</summary>
    public class SubclientsToolsTests
    {
        [Fact]
        public async Task SubclientRead_NoContext_Returns9009()
        {
            var tools = new SubclientsTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteSubclientRead("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SubclientRead_Partnership_Returns3PartnerRows()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tools = new SubclientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteSubclientRead("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            Assert.Equal(3, result.GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task SubclientRead_UnknownClient_Returns0Rows()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tools = new SubclientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteSubclientRead("NONESUCH"));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(0, root.GetProperty("result").GetProperty("count").GetInt32());
        }
    }
}
