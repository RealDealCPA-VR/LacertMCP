using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-035 — `lacerte_year_over_year` (X_feat_accel cluster).
    /// </summary>
    [Xunit.Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class YearOverYearToolsTests
    {
        [Fact]
        public async Task YearOverYear_NoContext_Returns9009()
        {
            var tools = new YearOverYearTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear("1001", 2023, 2024));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task YearOverYear_FromYearGteToYear_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear("1001", 2024, 2023));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task YearOverYear_DeltaTooLarge_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            // (toYear - fromYear) = 6 exceeds MaxYearDelta = 5.
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear("1001", 2018, 2024));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("toYear",
                root.GetProperty("context").GetProperty("argument").GetString());
        }

        [Fact]
        public async Task YearOverYear_TaxYearTooOld_Returns9003()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear("1001", 2010, 2014));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9003, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public void YearOverYear_AlternateFlatLayout_RewriteHelperReturnsNull()
        {
            // The full set_context + SetContext-of-flat-path flow is
            // unreachable from a unit test because SetContext's
            // PathValidation rejects non-canonical paths before YoY ever
            // sees them. Assert the contract at the helper boundary
            // instead: RewriteYearPath returns null for a flat-share
            // layout, which is what surfaces the 9013 to the caller in
            // the production path.
            Assert.Null(YearOverYearTools.RewriteYearPath(@"C:\Lacerte\24data\IData", 2023));
            Assert.Null(YearOverYearTools.RewriteYearPath(@"C:\Lacerte\flat\IData", 2023));
        }

        [Fact]
        public async Task YearOverYear_HappyPath_ReturnsDiffEnvelope()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            // BROW exists on IData 2024; sim auto-creates the 2020 cell
            // empty so the fromYear leg surfaces the "all new" diff.
            // Note: BROW.ClientNumber = "1001" — matches SMIT on 2020,
            // which is a different client. So demographicsDiff will
            // surface the SMIT-vs-BROW row diffs.
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear(SeedData.BROW.ClientNumber, 2020, 2024));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(SeedData.BROW.ClientNumber, result.GetProperty("clientNumber").GetString());
            Assert.Equal(2020, result.GetProperty("fromYear").GetInt32());
            Assert.Equal(2024, result.GetProperty("toYear").GetInt32());
            Assert.Equal("all", result.GetProperty("scope").GetString());
            Assert.True(result.TryGetProperty("demographicsDiff", out _));
            Assert.True(result.TryGetProperty("detailTotalsDiff", out _));
            Assert.True(result.TryGetProperty("fromDataPath", out var fromPath));
            Assert.Contains("20Tax", fromPath.GetString());
            Assert.True(result.TryGetProperty("toDataPath", out var toPath));
            Assert.Contains("24Tax", toPath.GetString());
        }

        [Fact]
        public async Task YearOverYear_RestoresContextOnSuccess()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly: true);
            var beforeCtx = mgr.SessionStatus().Context!;
            var tools = new YearOverYearTools(mgr);
            _ = await tools.LacerteYearOverYear(SeedData.BROW.ClientNumber, 2020, 2024);

            var afterCtx = mgr.SessionStatus().Context!;
            Assert.Equal(beforeCtx.DataPath, afterCtx.DataPath);
            Assert.Equal(beforeCtx.Year, afterCtx.Year);
            Assert.Equal(beforeCtx.Module, afterCtx.Module);
            Assert.Equal(beforeCtx.ReadOnly, afterCtx.ReadOnly);

            // R6-09 — the context-scan gate must be back to its single free
            // slot after the save-set-restore span completes.
            Assert.Equal(1, LacertMCP.Server.Tools.Internal.ContextScanGate.Gate.CurrentCount);
        }

        [Fact]
        public async Task YearOverYear_DemographicsScope_OmitsDetailTotals()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear(
                    SeedData.BROW.ClientNumber, 2020, 2024, scope: "demographics"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("detailTotalsDiff").GetArrayLength());
        }

        [Fact]
        public async Task YearOverYear_InvalidScope_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear("1001", 2020, 2024, scope: "everything"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        // R6-07d — scope matching is case-insensitive; the envelope echoes
        // the canonical camelCase spelling regardless of input casing.
        [Theory]
        [InlineData("ALL", "all")]
        [InlineData("detailtotals", "detailTotals")]
        [InlineData("DetailTotals", "detailTotals")]
        [InlineData("DEMOGRAPHICS", "demographics")]
        public async Task YearOverYear_ScopeCaseInsensitive_NormalizesToCanonical_R6_07(
            string input, string canonical)
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear(
                    SeedData.BROW.ClientNumber, 2020, 2024, scope: input));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(canonical,
                root.GetProperty("result").GetProperty("scope").GetString());
        }

        // R6-07a — a client present in both years sets both found flags and
        // emits no hint.
        [Fact]
        public async Task YearOverYear_FoundFlags_TrueWhenClientExists_R6_07()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear(SeedData.BROW.ClientNumber, 2020, 2024));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("foundInFromYear").GetBoolean());
            Assert.True(result.GetProperty("foundInToYear").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                result.GetProperty("hint").ValueKind);
        }

        // R6-07a — a client missing in BOTH years must not read as a
        // confident "no changes": both flags false + an explicit hint.
        [Fact]
        public async Task YearOverYear_ClientMissingInBothYears_SetsFlagsAndHint_R6_07()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear("424242", 2020, 2024));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("foundInFromYear").GetBoolean());
            Assert.False(result.GetProperty("foundInToYear").GetBoolean());
            var hint = result.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("424242", hint);
            Assert.Contains("no such client", hint!);
        }

        [Fact]
        public async Task YearOverYear_NegativeSeriesId_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacerteYearOverYear(
                    "1001", 2020, 2024, seriesIds: new[] { 11, -5 }));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public void RewriteYearPath_CanonicalLayout_RewritesYY()
        {
            Assert.Equal(
                @"C:\Lacerte\23Tax\IData",
                YearOverYearTools.RewriteYearPath(@"C:\Lacerte\24Tax\IData", 2023));
            Assert.Equal(
                @"C:\Lacerte\22Tax\IData",
                YearOverYearTools.RewriteYearPath(@"C:\Lacerte\24Tax\IData", 2022));
        }

        [Fact]
        public void RewriteYearPath_FlatLayout_ReturnsNull()
        {
            Assert.Null(YearOverYearTools.RewriteYearPath(@"C:\Lacerte\24data\IData", 2023));
            Assert.Null(YearOverYearTools.RewriteYearPath(@"C:\some\other\path", 2023));
        }
    }
}
