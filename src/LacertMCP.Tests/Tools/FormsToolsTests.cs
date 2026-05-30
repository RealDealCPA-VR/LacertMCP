using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #47 — `lacerte_forms_read` (DATA7).</summary>
    public class FormsToolsTests
    {
        [Fact]
        public async Task FormsRead_NoContext_Returns9009()
        {
            var tools = new FormsTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteFormsRead("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task FormsRead_ContextSet_ReturnsSuccessEnvelope()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new FormsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteFormsRead("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            Assert.True(result.GetProperty("count").GetInt32() >= 0);
            Assert.True(result.TryGetProperty("forms", out _));
        }
    }
}
