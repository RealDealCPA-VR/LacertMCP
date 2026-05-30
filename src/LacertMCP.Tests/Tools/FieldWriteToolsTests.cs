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

            // Read the original value (if any).
            var rowBefore = await mgr.ReadClient("1001");
            rowBefore!.TryGetValue(AccessMapTestFixture.WritableField, out var origValue);

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

            // The value must remain whatever it was before — the dry-run
            // snapshot rolled back.
            var rowAfter = await mgr.ReadClient("1001");
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var afterValue);
            Assert.Equal(origValue, afterValue);
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
    }
}
