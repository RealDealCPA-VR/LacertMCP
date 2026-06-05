using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-034 — `lacerte_pre_filing_check` (X_feat_accel cluster).
    /// </summary>
    [Xunit.Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class PreFilingCheckToolsTests
    {
        [Fact]
        public async Task PreFilingCheck_NoContext_Returns9009()
        {
            var tools = new PreFilingCheckTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacertePreFilingCheck("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task PreFilingCheck_MissingClient_ReturnsOutOfIndex()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck("9999"));
            Assert.False(root.GetProperty("success").GetBoolean());
            // OutOfIndex is StatusCodes.OutOfIndex (SDK code).
            Assert.True(root.GetProperty("statusCode").GetInt32() > 0);
        }

        [Fact]
        public async Task PreFilingCheck_HappyPath_ReturnsReadyOrWarn()
        {
            // BROW is a fully-populated TY24 individual: name, email,
            // forms with 1040 row, DATA5 status row present. Should be
            // "ready" on the v1 catalog.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            var readiness = result.GetProperty("readiness").GetString();
            Assert.True(readiness == "ready" || readiness == "warn",
                "Expected ready or warn for fully-populated BROW; got " + readiness);
            Assert.True(result.GetProperty("checkedRules").GetArrayLength() >= 6);
        }

        [Fact]
        public async Task PreFilingCheck_NonIndividualModule_ReturnsUnsupported()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.ACME.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("unsupported-module",
                root.GetProperty("result").GetProperty("readiness").GetString());
            Assert.False(root.GetProperty("result").GetProperty("passed").GetBoolean());
        }

        [Fact]
        public async Task PreFilingCheck_StrictPromotesWarningsToBlockers()
        {
            // Normal vs strict on the same client — strict path may have
            // a different readiness if any warnings exist.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);

            var normal = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber, severity: "normal"));
            var strict = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber, severity: "strict"));
            Assert.True(normal.GetProperty("success").GetBoolean());
            Assert.True(strict.GetProperty("success").GetBoolean());

            // If normal produced warnings, strict must produce blockers
            // of at least equal count.
            int normalWarnings = normal.GetProperty("result").GetProperty("warnings").GetArrayLength();
            int normalBlockers = normal.GetProperty("result").GetProperty("blockers").GetArrayLength();
            int strictBlockers = strict.GetProperty("result").GetProperty("blockers").GetArrayLength();
            int strictWarnings = strict.GetProperty("result").GetProperty("warnings").GetArrayLength();

            Assert.Equal(0, strictWarnings); // strict has no warnings (all promoted)
            Assert.Equal(normalWarnings + normalBlockers, strictBlockers);
        }

        [Fact]
        public async Task PreFilingCheck_SkippedRules_CarryF5Deferral()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var skipped = root.GetProperty("result").GetProperty("skippedRules");
            Assert.True(skipped.GetArrayLength() >= 2);
        }

        [Fact]
        public async Task PreFilingCheck_InvalidSeverity_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber, severity: "MEDIUM"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("severity",
                root.GetProperty("context").GetProperty("argument").GetString());
        }

        [Fact]
        public async Task PreFilingCheck_EmptyClientNumber_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck("  "));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }
    }
}
