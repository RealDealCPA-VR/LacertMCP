using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Phase 5 #48 + Phase 6 #53-56: detail read + write with mandatory
    /// client-side validation, dry-run, idempotency.
    /// </summary>
    public class DetailsToolsTests
    {
        private static (LacerteSessionManager mgr, DetailsTools tool) NewToolWithContext(bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            // P15-050 — auto-Register so RequireRegistered gate passes.
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return (mgr, new DetailsTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public async Task DetailRead_KnownClient_ReturnsSeededRows()
        {
            var (mgr, tool) = NewToolWithContext();
            var json = await tool.LacerteDetailRead("1001"); // BROW seed
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("result").GetProperty("count").GetInt32() >= 1);
        }

        [Fact]
        public async Task DetailRead_KnownClient_PreservesSeededDescription()
        {
            // P15-001 — wire-scope redaction must NOT scrub detail-row
            // Description. Pre-P15-001, the broad keyset turned every
            // Description into "***" on the wire, leaving the operator with
            // {Series:"11",Amount:95000,Description:"***"} — useless. This
            // test pins the seed values from SeedData.BrownDetails() so the
            // regression is visible immediately if the wire keyset ever
            // re-widens to include "Description".
            var (mgr, tool) = NewToolWithContext();
            var json = await tool.LacerteDetailRead("1001"); // BROW seed
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            // BrownDetails seeds three rows whose Description text is known.
            // Spot-check the literal substrings survive end-to-end through
            // WrapToolHandler.Redact(..., Wire).
            Assert.Contains("Bill primary wages", json);
            Assert.Contains("Spouse wages", json);
            Assert.Contains("Dependent: Tim Brown, age 12", json);
            Assert.DoesNotContain("\"Description\":\"***\"", json);
        }

        [Fact]
        public async Task DetailRead_DefaultPagination_ReturnsAllRowsAndPagingFields()
        {
            // P15-040 / P15-047 — paginated envelope. Default limit (100) is
            // larger than the seed (3 rows for client 1001), so the slice is
            // the full set and hasMore is false.
            var (mgr, tool) = NewToolWithContext();
            var json = await tool.LacerteDetailRead("1001");
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            var result = root.GetProperty("result");
            var total = result.GetProperty("total").GetInt32();
            Assert.True(total >= 1);
            Assert.Equal(100, result.GetProperty("limit").GetInt32());
            Assert.Equal(0, result.GetProperty("offset").GetInt32());
            Assert.False(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("nextOffset").ValueKind);
            // Legacy v0.1 back-compat fields still present.
            Assert.Equal(total, result.GetProperty("count").GetInt32());
            Assert.Equal(total, result.GetProperty("rows").GetArrayLength());
        }

        [Fact]
        public async Task DetailRead_LimitOne_SetsHasMoreAndNextOffset()
        {
            // P15-040 / P15-047 — limit slice + hasMore semantic.
            var (mgr, tool) = NewToolWithContext();
            var json = await tool.LacerteDetailRead("1001", limit: 1, offset: 0);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            var result = root.GetProperty("result");
            var total = result.GetProperty("total").GetInt32();
            Assert.True(total > 1, "Seed must have >1 row for this test to be meaningful.");
            Assert.Equal(1, result.GetProperty("limit").GetInt32());
            Assert.Equal(0, result.GetProperty("offset").GetInt32());
            Assert.True(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(1, result.GetProperty("nextOffset").GetInt32());
            Assert.Equal(1, result.GetProperty("rows").GetArrayLength());
        }

        [Fact]
        public async Task DetailRead_OffsetPastTotal_ReturnsEmptySlice()
        {
            // P15-040 / P15-047 — offset past total clamps to total, returns
            // empty rows with hasMore=false.
            var (mgr, tool) = NewToolWithContext();
            var json = await tool.LacerteDetailRead("1001", limit: 10, offset: 9999);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
            Assert.False(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("nextOffset").ValueKind);
        }

        [Fact]
        public async Task DetailWrite_BadStateAbbr_Returns9008()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.State, // numeric "30" per P14-055
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbrev: "California", // INVALID — must be 2-letter
                cityAbbr: "",
                source: "",
                tsj: "",
                importSrc: "",
                amount: 1000.0,
                description: "should be rejected by validator");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DetailWrite_NonNumericCode_Returns9008()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                prefix: "",
                code: "abc", // INVALID — must be numeric
                suffix: "",
                stateAbbrev: "",
                cityAbbr: "",
                source: "W2",
                tsj: "T",
                importSrc: "",
                amount: 5000.0,
                description: "bad code");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DetailWrite_EmptySeries_Returns9008()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: "",
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbrev: "",
                cityAbbr: "",
                source: "",
                tsj: "",
                importSrc: "",
                amount: null,
                description: "no series");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DetailWrite_HappyPath_MutatesStoreAndReadable()
        {
            var (mgr, tool) = NewToolWithContext();
            var beforeJson = await tool.LacerteDetailRead("1001");
            var beforeCount = Parse(beforeJson).GetProperty("result").GetProperty("count").GetInt32();

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                prefix: "",
                code: "9",
                suffix: "",
                stateAbbrev: "CA",
                cityAbbr: "",
                source: "W2",
                tsj: "T",
                importSrc: "",
                amount: 12345.67,
                description: "happy-path test row");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());

            var afterJson = await tool.LacerteDetailRead("1001");
            var afterCount = Parse(afterJson).GetProperty("result").GetProperty("count").GetInt32();
            Assert.Equal(beforeCount + 1, afterCount);
        }

        [Fact]
        public async Task DetailWrite_ReadOnlySession_Returns9001()
        {
            var (mgr, tool) = NewToolWithContext(readOnly: true);

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbrev: "",
                cityAbbr: "",
                source: "W2",
                tsj: "T",
                importSrc: "",
                amount: 1000.0,
                description: "should not persist");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DetailWrite_DryRun_DoesNotPersist()
        {
            var (mgr, tool) = NewToolWithContext();
            var beforeRows = await mgr.ReadDetail("1001");
            var beforeCount = beforeRows.Count;

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbrev: "",
                cityAbbr: "",
                source: "W2",
                tsj: "T",
                importSrc: "",
                amount: 9999.0,
                description: "dry-run only",
                dryRun: true);

            Assert.True(Parse(json).GetProperty("success").GetBoolean());
            var afterRows = await mgr.ReadDetail("1001");
            Assert.Equal(beforeCount, afterRows.Count);
        }

        [Fact]
        public async Task DetailWrite_IdempotencyConflict_Returns9102()
        {
            var (mgr, tool) = NewToolWithContext();

            var json1 = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbrev: "",
                cityAbbr: "",
                source: "W2",
                tsj: "T",
                importSrc: "",
                amount: 1000.0,
                description: "first",
                idempotencyKey: "dw-1");
            Assert.True(Parse(json1).GetProperty("success").GetBoolean());

            // Same key, different amount.
            var json2 = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbrev: "",
                cityAbbr: "",
                source: "W2",
                tsj: "T",
                importSrc: "",
                amount: 2000.0,
                description: "second",
                idempotencyKey: "dw-1");

            var root2 = Parse(json2);
            Assert.False(root2.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                root2.GetProperty("statusCode").GetInt32());
        }

        // ----------------------------------------------------------------
        // P15-009 — series push-down filter
        // ----------------------------------------------------------------

        [Fact]
        public async Task DetailRead_SeriesFilter_ReturnsOnlyMatchingRows()
        {
            // BROW (Bill Brown, ClientNumber 1001) is seeded with two Wages
            // (Series=11) and one Dependent (Series=14). Filter on 11 → must
            // return only the two Wages rows.
            var (mgr, tool) = NewToolWithContext();
            var json = await tool.LacerteDetailRead("1001", series: 11);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            var rows = root.GetProperty("result").GetProperty("rows");
            Assert.Equal(2, rows.GetArrayLength());
            foreach (var r in rows.EnumerateArray())
            {
                // DetailRow is a record; JsonSerializer keeps PascalCase.
                Assert.Equal("11", r.GetProperty("Series").GetString());
            }
        }

        [Fact]
        public async Task DetailRead_SeriesFilter_NoMatches_ReturnsEmptyButSuccess()
        {
            var (mgr, tool) = NewToolWithContext();
            // Series 99 has no seeded rows in any client.
            var json = await tool.LacerteDetailRead("1001", series: 99);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(0, root.GetProperty("result").GetProperty("total").GetInt32());
            Assert.Equal(0, root.GetProperty("result").GetProperty("rows").GetArrayLength());
        }

        [Fact]
        public async Task DetailRead_NoSeriesFilter_ReturnsAllRows()
        {
            // Sanity — when series is unset the full row count is returned
            // (no regression vs the pre-P15-009 unfiltered shape).
            var (mgr, tool) = NewToolWithContext();
            var unfiltered = await tool.LacerteDetailRead("1001");
            var rootU = Parse(unfiltered);
            var totalUnfiltered = rootU.GetProperty("result").GetProperty("total").GetInt32();
            Assert.Equal(3, totalUnfiltered); // 2 Wages + 1 Dependent

            var filtered = await tool.LacerteDetailRead("1001", series: 11);
            var rootF = Parse(filtered);
            Assert.Equal(2, rootF.GetProperty("result").GetProperty("total").GetInt32());
            Assert.NotEqual(
                totalUnfiltered,
                rootF.GetProperty("result").GetProperty("total").GetInt32());
        }
    }
}
