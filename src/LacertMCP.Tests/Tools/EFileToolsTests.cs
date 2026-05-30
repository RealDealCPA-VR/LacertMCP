using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #49 — `lacerte_ef_status` (TY19+ EFilings JOIN EFStatuses).</summary>
    public class EFileToolsTests
    {
        [Fact]
        public async Task EfStatus_NoContext_Returns9009()
        {
            var tools = new EFileTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task EfStatus_ContextSet_ReturnsSuccessEnvelope()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("count").GetInt32() >= 0);
            Assert.True(result.TryGetProperty("efStatuses", out _));
        }
    }
}
