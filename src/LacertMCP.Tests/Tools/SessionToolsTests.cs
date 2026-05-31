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
            // P15-050 — use the explicit unregistered helper so this test
            // continues to assert the fresh-manager-pre-Register snapshot
            // shape (the default NewSimManager auto-registers now to keep
            // legacy chokepoint tests green).
            var tools = new SessionTools(ToolTestHelpers.NewUnregisteredSimManager());
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
            // P15-050 — NewSimManager already auto-Registers; no need to call
            // Register again. The original double-Register was harmless but
            // confusing post-P15-050.
            var mgr = ToolTestHelpers.NewSimManager();
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

        // -----------------------------------------------------------------
        // P14-049 — dataPath PathValidation gate (9013 InvalidDataPath)
        // -----------------------------------------------------------------

        [Fact]
        public async Task SetContext_RelativePath_Returns9013()
        {
            // "foo\\bar" is not rooted — must be rejected before any sim
            // backend write happens.
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(@"foo\bar", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_DotDotTraversal_Returns9013()
        {
            // '..' as a separator-bounded segment is the textbook traversal
            // primitive — Path.GetFullPath would silently collapse it; the
            // PathValidation gate catches it BEFORE canonicalization.
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(@"C:\Lacerte\24Tax\..\Windows", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_UncPath_RejectedWithout_AllowUnc()
        {
            // UNC paths are rejected by default; the escape hatch is
            // LACERTE_ALLOW_UNC=1 (tested separately at the helper level so
            // we don't have to mutate process env in xUnit).
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(@"\\server\share\IData", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_NullByte_Returns9013()
        {
            // Control characters (< 0x20 OR 0x7F) are highest-risk parser
            // confusion strings — null in particular has been used to slice
            // strings in C-FFI marshalling boundaries.
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext("C:\\Lacerte\\24Tax\\IData\0evil", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_CrLf_Returns9013()
        {
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext("C:\\Lacerte\\24Tax\\IData\r\n", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        // -----------------------------------------------------------------
        // P14-049 — PathValidation.ValidateDataPath helper (direct)
        // -----------------------------------------------------------------
        // These exercise the helper with explicit env dictionaries so we can
        // cover the allowlist + escape-hatch branches without mutating the
        // process-wide environment (xUnit runs tests in parallel — env
        // mutation would race).

        [Fact]
        public void ValidateDataPath_AllowsUnc_WhenEscapeHatchSet()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_ALLOW_UNC"] = "1",
            };
            // Must not throw; UNC paths pass when the escape hatch is set.
            // Don't assert on the canonical return — GetFullPath behavior on
            // UNC is host-dependent.
            LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"\\server\share\IData", env);
        }

        [Fact]
        public void ValidateDataPath_DataRoot_RejectsOutsideRoot()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_DATA_ROOT"] = @"C:\Lacerte",
            };
            var ex = Assert.Throws<LacertMCP.Server.Errors.LacerteSDKError>(
                () => LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"C:\Windows\System32", env));
            Assert.Equal(9013, ex.StatusCode);
        }

        [Fact]
        public void ValidateDataPath_DataRoot_AcceptsUnderRoot()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_DATA_ROOT"] = @"C:\Lacerte",
            };
            // Must not throw; happy path under the allowlist root.
            LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"C:\Lacerte\24Tax\IData", env);
        }

        [Fact]
        public void ValidateDataPath_DataRoot_NoFalseMatch_OnSiblingPrefix()
        {
            // Trailing-separator normalization: "C:\Lacerte2\foo" must NOT
            // match root "C:\Lacerte" via naive StartsWith.
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_DATA_ROOT"] = @"C:\Lacerte",
            };
            var ex = Assert.Throws<LacertMCP.Server.Errors.LacerteSDKError>(
                () => LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"C:\Lacerte2\foo", env));
            Assert.Equal(9013, ex.StatusCode);
        }

        [Fact]
        public void ValidateLogDir_RejectsRelative()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>();
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"relative\logs", env);
            Assert.Null(result);
        }

        [Fact]
        public void ValidateLogDir_RejectsUnc_WithoutEscapeHatch()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>();
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"\\evil\share\logs", env);
            Assert.Null(result);
        }

        [Fact]
        public void ValidateLogDir_AcceptsUnderUserprofile()
        {
            // Allowlist test — when USERPROFILE is set and the log dir is
            // under it, the canonical path is returned.
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["USERPROFILE"] = @"C:\Users\tester",
            };
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"C:\Users\tester\AppData\logs", env);
            Assert.NotNull(result);
        }

        [Fact]
        public void ValidateLogDir_NoEnvAllowlistRoots_ReturnsCanonical()
        {
            // When the env dictionary has no allowlist roots (test env, no
            // USERPROFILE / TEMP / LACERTE_DATA_ROOT), the helper returns the
            // canonical path. Production env always has USERPROFILE on
            // Windows so the allowlist is real there.
            var env = new System.Collections.Generic.Dictionary<string, string?>();
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"C:\anywhere\logs", env);
            Assert.NotNull(result);
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
