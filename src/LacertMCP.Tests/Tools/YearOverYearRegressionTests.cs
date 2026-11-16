using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// R7 — `lacerte_year_over_year` regressions: PII masking of the
    /// demographics `{field, fromValue, toValue}` rows on the wire, and
    /// exact (decimal) detail-total arithmetic.
    /// </summary>
    [Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class YearOverYearRegressionTests
    {
        [Fact]
        public async Task YearOverYear_DemographicsPiiPair_MaskedOnWire_NonPiiVisible_R7()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new YearOverYearTools(mgr);

            // ClientNumber 1001 is SMIT on TY20 and BROW on TY24, so C1_3
            // (email, a PII code) and C1_2 (last name) both differ.
            var json = await tools.LacerteYearOverYear(
                SeedData.BROW.ClientNumber, 2020, 2024, scope: "demographics");
            Assert.DoesNotContain(SeedData.SMIT.Email, json);
            Assert.DoesNotContain(SeedData.BROW.Email, json);

            var rows = ToolTestHelpers.Parse(json).GetProperty("result")
                .GetProperty("demographicsDiff").EnumerateArray().ToArray();

            var email = rows.Single(r => r.GetProperty("field").GetString() == "C1_3");
            Assert.Equal("***", email.GetProperty("fromValue").GetString());
            Assert.Equal("***", email.GetProperty("toValue").GetString());
            // Non-value metadata on the same row survives.
            Assert.True(email.GetProperty("changed").GetBoolean());

            var lastName = rows.Single(r => r.GetProperty("field").GetString() == "C1_2");
            Assert.Equal(SeedData.SMIT.LastName, lastName.GetProperty("fromValue").GetString());
            Assert.Equal(SeedData.BROW.LastName, lastName.GetProperty("toValue").GetString());
        }

        private static DetailRow Row(string series, double amount, string description)
            => new DetailRow(series, "", "1", "", "", "", "", "T", "", amount, description);

        [Fact]
        public async Task YearOverYear_DetailTotals_SumExactly_NoPhantomDelta_R7()
        {
            // Precondition that makes this a real regression test: the
            // double sum is NOT 1545.63.
            double a = 1200.10, b = 300.20, c = 45.33;
            Assert.NotEqual(1545.63, a + b + c);

            var mgr = ToolTestHelpers.NewSimManager();
            // TY20: one row of 1545.63 under an otherwise-unused series.
            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("97", 1545.63, "single")));
            // TY24: the same total split across three rows.
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("97", 1200.10, "part 1")));
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("97", 300.20, "part 2")));
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("97", 45.33, "part 3")));

            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(await tools.LacerteYearOverYear(
                "1001", 2020, 2024, scope: "detailTotals", seriesIds: new[] { 97 }));
            Assert.True(root.GetProperty("success").GetBoolean());
            // Equal totals -> no row at all (was a phantom delta 2.27e-13).
            Assert.Equal(0, root.GetProperty("result").GetProperty("detailTotalsDiff").GetArrayLength());
        }

        [Fact]
        public async Task YearOverYear_DetailTotals_ChangedRow_HasCleanValues_R7()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("98", 100.10, "prior")));
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("98", 1200.10, "part 1")));
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("98", 300.20, "part 2")));
            Assert.Equal(StatusCodes.Success, await mgr.WriteDetail("1001", Row("98", 45.33, "part 3")));

            var tools = new YearOverYearTools(mgr);
            var root = ToolTestHelpers.Parse(await tools.LacerteYearOverYear(
                "1001", 2020, 2024, scope: "detailTotals", seriesIds: new[] { 98 }));
            var rows = root.GetProperty("result").GetProperty("detailTotalsDiff").EnumerateArray().ToArray();
            var row = Assert.Single(rows);
            Assert.Equal("100.1", row.GetProperty("fromTotal").GetRawText());
            Assert.Equal("1545.63", row.GetProperty("toTotal").GetRawText());
            Assert.Equal("1445.53", row.GetProperty("delta").GetRawText());
        }

        [Fact]
        public void ToMoney_RecoversDbfLiteral_AndRejectsNonFinite_R7()
        {
            Assert.Equal(1200.1m, YearOverYearTools.ToMoney(1200.10));
            Assert.Equal(45.33m, YearOverYearTools.ToMoney(45.33));
            Assert.Equal(0m, YearOverYearTools.ToMoney(null));
            Assert.Equal(0m, YearOverYearTools.ToMoney(double.NaN));
            Assert.Equal(0m, YearOverYearTools.ToMoney(double.PositiveInfinity));
            Assert.Equal(0m, YearOverYearTools.ToMoney(1e30));
            Assert.Equal(1545.63m,
                YearOverYearTools.ToMoney(1200.10) + YearOverYearTools.ToMoney(300.20) + YearOverYearTools.ToMoney(45.33));
        }
    }
}
