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
    /// Phase 6 #51 + #54-56: field-write tool happy path + read-only / dry-run /
    /// idempotency acceptance criteria. The tools wrap WrapToolHandler so each
    /// test deserializes the envelope and asserts on the structured shape.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldWriteToolsTests
    {
        private static (LacerteSessionManager mgr, FieldWriteTools tool) NewToolWithContext(bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            // P15-050 — auto-Register so RequireRegistered gate passes.
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return (mgr, new FieldWriteTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public async Task FieldWrite_HappyPath_WritesReadWriteField_ReturnsSuccess()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            // BROW (ClientNumber 1001) is seeded in TY24 IData.
            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "NewValue");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task FieldWrite_ReadOnlyField_ReturnsExposedForRead()
        {
            var (mgr, tool) = NewToolWithContext();
            // C1_0 is universally ReadOnly — no fixture needed.
            var json = await tool.LacerteFieldWrite(
                table: 1, clientNumber: "1001", field: "C1_0", value: "should-fail");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.ExposedForRead,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task FieldWrite_ReadOnlySession_ReturnsFieldIsReadOnly()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext(readOnly: true);

            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "should-not-persist");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task FieldWrite_MissingContext_ReturnsContextNotSet()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var tool = new FieldWriteTools(mgr);

            var json = await tool.LacerteFieldWrite(
                table: 1, clientNumber: "1001", field: "C1_15", value: "x");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task FieldWrite_DryRun_DoesNotPersist()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            // P15-064: prior version asserted `Assert.Equal(origValue, afterValue)`
            // when both were null (C1_15 is not in the default seed), so the test
            // passed via null==null regardless of whether dry-run actually
            // short-circuited the write. Establish a known sentinel via a real
            // write first, then exercise the dry-run with a DIFFERENT value, and
            // assert the post-state still matches the sentinel. This catches
            // (a) silent-persist (afterValue == "dry-run-only"),
            // (b) row-loss (afterValue == null), AND
            // (c) mis-routing to a different field/row.
            var seedJson = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "seed-value-before-dryrun");
            Assert.True(Parse(seedJson).GetProperty("success").GetBoolean());

            var rowBefore = await mgr.ReadClient("1001");
            rowBefore!.TryGetValue(AccessMapTestFixture.WritableField, out var origValue);
            Assert.Equal("seed-value-before-dryrun", origValue);

            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "dry-run-only",
                dryRun: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());

            // The value must remain the sentinel — the dry-run snapshot rolled back.
            var rowAfter = await mgr.ReadClient("1001");
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var afterValue);
            Assert.Equal("seed-value-before-dryrun", afterValue);
        }

        [Fact]
        public async Task FieldWrite_IdempotencyConflict_ThrowsOn9102()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var json1 = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "first",
                idempotencyKey: "fw-conflict-1");

            Assert.True(Parse(json1).GetProperty("success").GetBoolean());

            // Same key, different value → IdempotencyConflict (9102).
            var json2 = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "second",
                idempotencyKey: "fw-conflict-1");

            var root2 = Parse(json2);
            Assert.False(root2.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                root2.GetProperty("statusCode").GetInt32());
        }

        // ----------------------------------------------------------------
        // P15-051 — value type whitelist (NormalizeFieldWriteValue)
        // ----------------------------------------------------------------

        [Fact]
        public async Task FieldWrite_RejectsJsonElementArray_WithFieldWriteInvalid()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            using var doc = JsonDocument.Parse("[1,2,3]");
            var arrayValue = doc.RootElement.Clone();

            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: arrayValue);

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldWriteInvalid,
                root.GetProperty("statusCode").GetInt32());
            var ctx = root.GetProperty("context");
            Assert.Equal(AccessMapTestFixture.WritableField,
                ctx.GetProperty("field").GetString());
            Assert.Contains("Array", ctx.GetProperty("clrType").GetString());
        }

        [Fact]
        public async Task FieldWrite_RejectsJsonElementObject_WithFieldWriteInvalid()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            using var doc = JsonDocument.Parse("{\"nested\":1}");
            var objValue = doc.RootElement.Clone();

            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: objValue);

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldWriteInvalid,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task FieldWrite_AcceptsJsonElementString_RoundTrips()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            using var doc = JsonDocument.Parse("\"hello\"");
            var strValue = doc.RootElement.Clone();

            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: strValue);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());

            var rowAfter = await mgr.ReadClient("1001");
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var v);
            Assert.Equal("hello", v);
        }

        [Fact]
        public async Task FieldWrite_AcceptsJsonElementNumber_AsLong()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            using var doc = JsonDocument.Parse("42");
            var numValue = doc.RootElement.Clone();

            var json = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: numValue);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            var rowAfter = await mgr.ReadClient("1001");
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var v);
            Assert.Equal(42L, v);
        }

        [Fact]
        public async Task FieldWrite_NormalizationIsIdempotencyStable_LongAndJsonElementHashEqual()
        {
            // P15-051 — the normalizer runs BEFORE BuildPayload, so int 42
            // and JsonElement(42) MUST fingerprint to the same hash. If a
            // future refactor moves the normalizer below BuildPayload, this
            // test fails because the second call would hit the cache as a
            // CONFLICTING payload (9102) instead of a HIT.
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            using var doc = JsonDocument.Parse("42");
            var jsonEle = doc.RootElement.Clone();

            // First call: plain long.
            var first = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: 42L,
                idempotencyKey: "p15-051-stable");
            Assert.True(Parse(first).GetProperty("success").GetBoolean());

            // Second call: JsonElement-wrapped 42, same idempotency key.
            // Must REPLAY (success) — not conflict.
            var second = await tool.LacerteFieldWrite(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: jsonEle,
                idempotencyKey: "p15-051-stable");
            var root2 = Parse(second);
            Assert.True(root2.GetProperty("success").GetBoolean(),
                "JsonElement(42) and 42L must produce identical idempotency fingerprints — see P15-051 NormalizeFieldWriteValue.");
        }
    }
}
