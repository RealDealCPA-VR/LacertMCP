using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Phase 5 task #41 — `lacerte_session_status`, `lacerte_set_context`,
    /// `lacerte_can_use_new_apis`, `lacerte_list_modules`,
    /// `lacerte_list_datapaths`.
    /// </summary>
    public class SessionToolsTests
    {
        private static SessionTools NewTools() => new SessionTools(ToolTestHelpers.NewSimManager());

        // -----------------------------------------------------------------
        // lacerte_session_status
        // -----------------------------------------------------------------

        [Fact]
        public async Task SessionStatus_FreshManager_ReturnsSimMode_NotRegistered_NoContext()
        {
            var tools = NewTools();
            var json = await tools.LacerteSessionStatus();
            var root = ToolTestHelpers.Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("Simulation", result.GetProperty("mode").GetString());
            Assert.False(result.GetProperty("isRegistered").GetBoolean());
            // No context set yet — should be JSON null.
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("context").ValueKind);
        }

        [Fact]
        public async Task SessionStatus_AfterRegister_ReflectsRegistered()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            await mgr.Register();
            var tools = new SessionTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteSessionStatus());
            var result = root.GetProperty("result");

            Assert.True(result.GetProperty("isRegistered").GetBoolean());
            Assert.True(result.GetProperty("minutesUntilExpiry").GetDouble() > 0);
        }

        // -----------------------------------------------------------------
        // lacerte_set_context
        // -----------------------------------------------------------------

        [Fact]
        public async Task SetContext_HappyPath_ReturnsContextEcho()
        {
            var tools = NewTools();
            var json = await tools.LacerteSetContext(SeedData.DataPathIData2024, 2024, "I");
            var root = ToolTestHelpers.Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean());
            var ctx = root.GetProperty("result");
            Assert.Equal(SeedData.DataPathIData2024, ctx.GetProperty("dataPath").GetString());
            Assert.Equal(2024, ctx.GetProperty("year").GetInt32());
            Assert.Equal("I", ctx.GetProperty("module").GetString());
            Assert.False(ctx.GetProperty("readOnly").GetBoolean());
        }

        [Fact]
        public async Task SetContext_YearBelowFloor_Returns9003()
        {
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(SeedData.DataPathIData2024, 2017, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9003, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_EmptyModule_RejectsCleanly()
        {
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(SeedData.DataPathIData2024, 2024, ""));
            Assert.False(root.GetProperty("success").GetBoolean());
            // Tool-level validation maps to InValidClientName (24) per the convention used by SetContext.
            Assert.Equal(24, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_ReadOnlyFlag_PreservedThroughSessionStatus()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new SessionTools(mgr);
            await tools.LacerteSetContext(SeedData.DataPathIData2024, 2024, "I", readOnly: true);

            var statusRoot = ToolTestHelpers.Parse(await tools.LacerteSessionStatus());
            var ctx = statusRoot.GetProperty("result").GetProperty("context");
            Assert.True(ctx.GetProperty("readOnly").GetBoolean());
        }

        // -----------------------------------------------------------------
        // lacerte_can_use_new_apis
        // -----------------------------------------------------------------

        [Fact]
        public async Task CanUseNewApis_TY24_ReturnsTrue_WithUpdateTaxReturnPath()
        {
            var root = ToolTestHelpers.Parse(await NewTools().LacerteCanUseNewApis(2024, "I"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("canUseNewApis").GetBoolean());
            Assert.Equal("UpdateTaxReturn", result.GetProperty("apiPath").GetString());
        }

        [Fact]
        public async Task CanUseNewApis_TY20_ReturnsFalse_WithLegacyPath()
        {
            var root = ToolTestHelpers.Parse(await NewTools().LacerteCanUseNewApis(2020, "I"));
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("canUseNewApis").GetBoolean());
            Assert.Equal("WriteToDBFDatabase", result.GetProperty("apiPath").GetString());
        }

        // -----------------------------------------------------------------
        // lacerte_list_modules
        // -----------------------------------------------------------------

        [Fact]
        public async Task ListModules_Returns9Modules_AllChars()
        {
            var root = ToolTestHelpers.Parse(await NewTools().LacerteListModules());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(9, result.GetProperty("count").GetInt32());

            var chars = result.GetProperty("modules").EnumerateArray()
                .Select(m => m.GetProperty("module").GetString())
                .ToArray();
            foreach (var expected in new[] { "I", "C", "P", "S", "F", "R", "T", "N", "B" })
            {
                Assert.Contains(expected, chars);
            }
        }

        // -----------------------------------------------------------------
        // lacerte_list_datapaths
        // -----------------------------------------------------------------

        [Fact]
        public async Task ListDatapaths_DefaultRoot_ReturnsEnvelope_NoCrash()
        {
            // Off-Windows / no Lacerte install: rootExists may be false, but the
            // tool must still return a structured success envelope (empty array).
            var root = ToolTestHelpers.Parse(await NewTools().LacerteListDatapaths());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.TryGetProperty("root", out _));
            Assert.True(result.TryGetProperty("datapaths", out var arr));
            Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
        }

        [Fact]
        public void DiscoverDatapaths_NonExistentRoot_ReturnsEmpty()
        {
            var entries = SessionTools.DiscoverDatapaths(@"Q:\definitely\not\a\real\path");
            Assert.Empty(entries);
        }

        [Fact]
        public void DiscoverDatapaths_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Empty(SessionTools.DiscoverDatapaths(""));
            Assert.Empty(SessionTools.DiscoverDatapaths(null!));
        }

        [Fact]
        public void DiscoverDatapaths_RealLayout_FindsYYTaxModuleData()
        {
            // Build a temp tree: <tmp>/20Tax/IData + <tmp>/24Tax/CData + a couple of decoys.
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lacert-mcp-discover-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "20Tax", "IData"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "24Tax", "CData"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "NotTax", "IData")); // decoy
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "20Tax", "XData"));  // unknown module
            try
            {
                var entries = SessionTools.DiscoverDatapaths(root);
                Assert.Equal(2, entries.Count);
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* swallow */ }
            }
        }
    }
}
