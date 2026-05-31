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
    /// P14-025: direct tests for <c>lacerte_update_return</c>. Mirrors
    /// <see cref="FieldWriteToolsTests"/> structure (both tools funnel
    /// through the same session chokepoint so the gate behaviour matches).
    /// Pinned response shape catches envelope regressions — in particular
    /// the deliberate removal of the misleading <c>api</c> field
    /// (finding P14-026).
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class UpdateReturnToolsTests
    {
        private static (LacerteSessionManager mgr, UpdateReturnTools tool) NewToolWithContext(bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            // P15-050 — auto-Register so RequireRegistered gate passes.
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return (mgr, new UpdateReturnTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        // (1) Happy path — assert success + pin the response shape exactly
        //     so a regression that adds/removes/renames fields fails loudly.
        //     The 'api' field is deliberately ABSENT (P14-026 fix).
        [Fact]
        public async Task UpdateReturn_HappyPath_WritesReadWriteField_ReturnsSuccess()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            // BROW (ClientNumber 1001) is seeded in TY24 IData.
            var json = await tool.LacerteUpdateReturn(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "NewValue");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());

            var result = root.GetProperty("result");
            Assert.Equal(StatusCodes.Success, result.GetProperty("statusCode").GetInt32());
            Assert.Equal("Success", result.GetProperty("statusName").GetString());
            Assert.Equal(AccessMapTestFixture.Table, result.GetProperty("table").GetInt32());
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            Assert.Equal(AccessMapTestFixture.WritableField, result.GetProperty("field").GetString());
            Assert.False(result.GetProperty("dryRun").GetBoolean());

            // P14-026: the response MUST NOT carry an `api` field — the live
            // wire choice belongs to the session manager (Boundary Invariant
            // #5), so the tool can't honestly label it from this layer. Lock
            // both keys (positive list) AND the absence of `api` so a future
            // hand-edit can't silently reintroduce the wrong claim.
            var keys = new HashSet<string>();
            foreach (var prop in result.EnumerateObject()) keys.Add(prop.Name);
            Assert.Equal(
                new HashSet<string> { "statusCode", "statusName", "table", "clientNumber", "field", "dryRun" },
                keys);
        }

        // (2) ReadOnly session rejection — synthetic 9001 fires synchronously
        //     in the session manager before any wire call.
        [Fact]
        public async Task UpdateReturn_ReadOnlySession_ReturnsFieldIsReadOnly()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext(readOnly: true);

            var json = await tool.LacerteUpdateReturn(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "should-not-persist");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly,
                root.GetProperty("statusCode").GetInt32());
        }

        // (3) Universal C{t}_0 ReadOnly rule — StatusCodes.ExposedForRead (8).
        [Fact]
        public async Task UpdateReturn_ClientNumberField_ReturnsExposedForRead()
        {
            var (mgr, tool) = NewToolWithContext();
            // C1_0 is universally ReadOnly — no fixture needed.
            var json = await tool.LacerteUpdateReturn(
                table: 1, clientNumber: "1001", field: "C1_0", value: "should-fail");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.ExposedForRead,
                root.GetProperty("statusCode").GetInt32());
        }

        // (4) Missing context — synthetic 9009 ContextNotSet.
        [Fact]
        public async Task UpdateReturn_MissingContext_ReturnsContextNotSet()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var tool = new UpdateReturnTools(mgr);

            var json = await tool.LacerteUpdateReturn(
                table: 1, clientNumber: "1001", field: "C1_15", value: "x");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (5) Dry-run — Success status but no persisted change.
        //     P15-064: pre-seed a sentinel via a real write so the post-dry-run
        //     read asserts against a known value rather than null==null (the
        //     unwritten C1_15 default). Catches silent-persist, row-loss, and
        //     field/row mis-routing — none of which would fail the old
        //     null==null assertion.
        [Fact]
        public async Task UpdateReturn_DryRun_DoesNotPersist()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var seedJson = await tool.LacerteUpdateReturn(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "seed-value-before-dryrun");
            Assert.True(Parse(seedJson).GetProperty("success").GetBoolean());

            var rowBefore = await mgr.ReadClient("1001");
            rowBefore!.TryGetValue(AccessMapTestFixture.WritableField, out var origValue);
            Assert.Equal("seed-value-before-dryrun", origValue);

            var json = await tool.LacerteUpdateReturn(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "dry-run-only",
                dryRun: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());

            var rowAfter = await mgr.ReadClient("1001");
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var afterValue);
            Assert.Equal("seed-value-before-dryrun", afterValue);
        }

        // (6) Idempotency conflict — same key + different payload → 9102.
        [Fact]
        public async Task UpdateReturn_IdempotencyConflict_Returns9102()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var json1 = await tool.LacerteUpdateReturn(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "first",
                idempotencyKey: "ur-conflict-1");

            Assert.True(Parse(json1).GetProperty("success").GetBoolean());

            var json2 = await tool.LacerteUpdateReturn(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "second",
                idempotencyKey: "ur-conflict-1");

            var root2 = Parse(json2);
            Assert.False(root2.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                root2.GetProperty("statusCode").GetInt32());
        }
    }
}
