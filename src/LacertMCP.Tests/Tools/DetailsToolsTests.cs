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
        public async Task DetailWrite_BadStateAbbr_Returns9008()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteDetailWrite(
                clientNumber: "1001",
                series: "State",
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbr: "California", // INVALID — must be 2-letter
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
                series: "Wages",
                prefix: "",
                code: "abc", // INVALID — must be numeric
                suffix: "",
                stateAbbr: "",
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
                stateAbbr: "",
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
                series: "Wages",
                prefix: "",
                code: "9",
                suffix: "",
                stateAbbr: "CA",
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
                series: "Wages",
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbr: "",
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
                series: "Wages",
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbr: "",
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
                series: "Wages",
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbr: "",
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
                series: "Wages",
                prefix: "",
                code: "1",
                suffix: "",
                stateAbbr: "",
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
    }
}
