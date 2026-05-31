using System.Collections.Generic;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-011 — detail-row write→read round-trip. Detail writes don't go
    /// through the access-map gate (P14-002 — the
    /// <c>(module|table|field)</c> key shape can't address detail rows),
    /// so no <see cref="LacertMCP.Tests.Tools.AccessMapTestFixture"/> is
    /// required.
    ///
    /// <para>
    /// Covers both detail-write paths: append (no <c>replaceIndex</c>) and
    /// replace-at-index. A bug in <c>SimulationStore</c>'s detail-list
    /// mutation that double-appends, replaces the wrong index, or drops
    /// payload fields (e.g. <c>Amount</c>, <c>ImportSrc</c>) would be
    /// invisible to every other test today; this round-trip catches it.
    /// </para>
    /// </summary>
    public class DetailWriteRoundTripTests
    {
        [Fact]
        public async Task WriteDetail_Append_RowVisibleInReadDetail()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var regStatus = await mgr.Register();
            Assert.Equal(StatusCodes.AccessGranted, regStatus);
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            // BROW (ClientNumber 1001) starts with 3 details in TY24 IData.
            var before = await mgr.ReadDetail("1001");
            var beforeCount = before.Count;
            Assert.True(beforeCount > 0, "seed should populate detail rows for BROW");

            // Append a fresh Wages row. Amounts + Description must survive
            // the write→read round-trip byte-identically.
            var newRow = new DetailRow(
                Series: SeedData.SeriesIds.Wages,
                Prefix: "",
                Code: "1",
                Suffix: "",
                StateAbbr: "",
                CityAbbr: "",
                Source: "W2",
                TSJ: "T",
                ImportSrc: "round-trip-tag",
                Amount: 12345.67,
                Description: "RoundTripEmployer");

            var status = await mgr.WriteDetail("1001", newRow);
            Assert.Equal(StatusCodes.Success, status);

            var after = await mgr.ReadDetail("1001");
            Assert.Equal(beforeCount + 1, after.Count);

            // New row lands at the tail per append semantics.
            var appended = after[after.Count - 1];
            Assert.Equal("RoundTripEmployer", appended.Description);
            Assert.Equal(12345.67, appended.Amount);
            Assert.Equal("round-trip-tag", appended.ImportSrc);
            Assert.Equal(SeedData.SeriesIds.Wages, appended.Series);
            Assert.Equal("1", appended.Code);
            Assert.Equal("W2", appended.Source);
            Assert.Equal("T", appended.TSJ);
        }

        [Fact]
        public async Task WriteDetail_ReplaceIndex_OverwritesRowInPlace()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var regStatus = await mgr.Register();
            Assert.Equal(StatusCodes.AccessGranted, regStatus);
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var before = await mgr.ReadDetail("1001");
            var beforeCount = before.Count;
            Assert.True(beforeCount > 0, "seed should populate detail rows for BROW");

            // Capture row[1] so we can confirm replace-at-0 doesn't bleed into it.
            var keepRow1 = before[1];

            var replacement = new DetailRow(
                Series: SeedData.SeriesIds.Wages,
                Prefix: "",
                Code: "1",
                Suffix: "",
                StateAbbr: "",
                CityAbbr: "",
                Source: "W2",
                TSJ: "T",
                ImportSrc: "replaced",
                Amount: 1.0,
                Description: "Replaced");

            var status = await mgr.WriteDetail("1001", replacement, replaceIndex: 0);
            Assert.Equal(StatusCodes.Success, status);

            var after = await mgr.ReadDetail("1001");
            // Replace-in-place must NOT change the row count.
            Assert.Equal(beforeCount, after.Count);

            Assert.Equal("Replaced", after[0].Description);
            Assert.Equal(1.0, after[0].Amount);
            Assert.Equal("replaced", after[0].ImportSrc);

            // Adjacent row untouched — proves the mutation didn't smear.
            Assert.Equal(keepRow1.Description, after[1].Description);
            Assert.Equal(keepRow1.Amount, after[1].Amount);
        }
    }
}
