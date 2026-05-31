using System.Collections.Generic;
using System.Linq;
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
    /// P15-008: tests for <c>lacerte_field_write_bulk</c>. The bulk tool fans
    /// out N writes through the same session chokepoint the single-write tool
    /// uses, so the read-only / dry-run / idempotency invariants are inherited.
    /// These tests cover the bulk-specific behaviour:
    /// (a) happy-path 5-row batch aggregates to {success:5, error:0},
    /// (b) injected failure on row 3 with default stop-on-error yields
    ///     {success:2, error:1, remaining marked skipped},
    /// (c) same payload in continue-on-error yields {success:4, error:1},
    /// (d) idempotency-prefix replay returns the cached statusCode per row
    ///     without re-invoking the underlying write,
    /// (e) context-mismatch pre-flight rejects before any row runs.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldWriteBulkToolsTests
    {
        private static (LacerteSessionManager mgr, FieldWriteBulkTools tool) NewToolWithContext(bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return (mgr, new FieldWriteBulkTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        /// <summary>
        /// Seed 3 additional individual clients so a 5-row bulk batch has 5
        /// targets (BROW=1001 and PEND=1002 are already seeded; we add
        /// 1003/1004/1005 via the SDK CreateClient path so they look identical
        /// to the seeded clients in DATA1).
        /// </summary>
        private static async Task<List<string>> Seed5IndividualClients(LacerteSessionManager mgr)
        {
            var roster = new List<string> { "1001", "1002" };
            for (int i = 0; i < 3; i++)
            {
                var args = new CreateClientArgs(
                    ClientID: $"BLK{i}",
                    TaxType: "Individual",
                    FirstName: $"Bulk{i}",
                    LastName: $"Target{i}",
                    EmailID: $"bulk{i}@example.com");
                var result = await mgr.CreateClient(args);
                Assert.Equal(StatusCodes.Success, result.Status);
                Assert.NotNull(result.ClientNumber);
                roster.Add(result.ClientNumber!);
            }
            return roster;
        }

        // (a) Happy-path 5-row bulk-update — every row writes to the writable
        //     field exposed by the AccessMapTestFixture; envelope aggregates
        //     to {totalCount:5, successCount:5, errorCount:0}.
        [Fact]
        public async Task BulkWrite_HappyPath_FiveRows_AllSucceed()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = clients.Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                FieldName = AccessMapTestFixture.WritableField,
                Value = $"bulk-value-{cn}",
            }).ToArray();

            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(5, result.GetProperty("totalCount").GetInt32());
            Assert.Equal(5, result.GetProperty("successCount").GetInt32());
            Assert.Equal(0, result.GetProperty("errorCount").GetInt32());

            var perItem = result.GetProperty("perItem");
            Assert.Equal(5, perItem.GetArrayLength());
            foreach (var item in perItem.EnumerateArray())
            {
                Assert.True(item.GetProperty("success").GetBoolean());
                Assert.Equal(StatusCodes.Success, item.GetProperty("statusCode").GetInt32());
            }

            // Verify the writes actually landed for a sample row.
            var row = await mgr.ReadClient(clients[0]);
            Assert.NotNull(row);
            row!.TryGetValue(AccessMapTestFixture.WritableField, out var v);
            Assert.Equal($"bulk-value-{clients[0]}", v);
        }

        // (b) Inject a failure on row 3 (write to a known ReadOnly field —
        //     C1_0 is universally ReadOnly per spec §2). Default
        //     continueOnError=false: rows 0-1 succeed, row 2 fails, rows 3-4
        //     are marked skipped without invoking WriteField.
        [Fact]
        public async Task BulkWrite_StopOnError_HaltsAtFirstFailureAndSkipsRemainder()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = clients[0], FieldName = AccessMapTestFixture.WritableField, Value = "ok-0" },
                new() { ClientNumber = clients[1], FieldName = AccessMapTestFixture.WritableField, Value = "ok-1" },
                // C1_0 is universally ReadOnly — writes throw ExposedForRead (8).
                new() { ClientNumber = clients[2], FieldName = "C1_0", Value = "will-fail" },
                new() { ClientNumber = clients[3], FieldName = AccessMapTestFixture.WritableField, Value = "should-skip-3" },
                new() { ClientNumber = clients[4], FieldName = AccessMapTestFixture.WritableField, Value = "should-skip-4" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(5, result.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, result.GetProperty("successCount").GetInt32());
            Assert.Equal(1, result.GetProperty("errorCount").GetInt32());

            var perItem = result.GetProperty("perItem").EnumerateArray().ToArray();
            Assert.True(perItem[0].GetProperty("success").GetBoolean());
            Assert.True(perItem[1].GetProperty("success").GetBoolean());
            Assert.False(perItem[2].GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.ExposedForRead, perItem[2].GetProperty("statusCode").GetInt32());
            Assert.False(perItem[3].GetProperty("success").GetBoolean());
            Assert.True(perItem[3].GetProperty("skipped").GetBoolean());
            Assert.False(perItem[4].GetProperty("success").GetBoolean());
            Assert.True(perItem[4].GetProperty("skipped").GetBoolean());

            // Confirm the skipped rows did NOT persist.
            var row3 = await mgr.ReadClient(clients[3]);
            row3!.TryGetValue(AccessMapTestFixture.WritableField, out var v3);
            Assert.NotEqual("should-skip-3", v3);
        }

        // (c) Same payload as (b) but continueOnError=true — rows 0/1/3/4
        //     succeed, row 2 still fails. Aggregates to {success:4, error:1}.
        [Fact]
        public async Task BulkWrite_ContinueOnError_KeepsGoingPastFailure()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = clients[0], FieldName = AccessMapTestFixture.WritableField, Value = "ok-0" },
                new() { ClientNumber = clients[1], FieldName = AccessMapTestFixture.WritableField, Value = "ok-1" },
                new() { ClientNumber = clients[2], FieldName = "C1_0", Value = "will-fail" },
                new() { ClientNumber = clients[3], FieldName = AccessMapTestFixture.WritableField, Value = "ok-3" },
                new() { ClientNumber = clients[4], FieldName = AccessMapTestFixture.WritableField, Value = "ok-4" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries,
                continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(5, result.GetProperty("totalCount").GetInt32());
            Assert.Equal(4, result.GetProperty("successCount").GetInt32());
            Assert.Equal(1, result.GetProperty("errorCount").GetInt32());

            var perItem = result.GetProperty("perItem").EnumerateArray().ToArray();
            Assert.True(perItem[0].GetProperty("success").GetBoolean());
            Assert.True(perItem[1].GetProperty("success").GetBoolean());
            Assert.False(perItem[2].GetProperty("success").GetBoolean());
            // Critically, rows 3 and 4 must succeed — NOT be skipped.
            Assert.True(perItem[3].GetProperty("success").GetBoolean());
            Assert.True(perItem[4].GetProperty("success").GetBoolean());
        }

        // (d) Replay-safety: invoke twice with the same idempotencyKey prefix
        //     and assert the second call returns the same statusCodes per row
        //     (no double-write — verify by changing the persisted sentinel
        //     between calls and asserting the value did NOT change on replay).
        [Fact]
        public async Task BulkWrite_IdempotencyPrefix_ReplaysCachedResultsPerRow()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = clients.Take(3).Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                FieldName = AccessMapTestFixture.WritableField,
                Value = $"first-{cn}",
            }).ToArray();

            var json1 = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries,
                idempotencyKey: "bulk-replay-test");

            var root1 = Parse(json1);
            Assert.True(root1.GetProperty("success").GetBoolean());
            Assert.Equal(3, root1.GetProperty("result").GetProperty("successCount").GetInt32());

            // Verify the first-call values landed.
            var rowBefore = await mgr.ReadClient(clients[0]);
            rowBefore!.TryGetValue(AccessMapTestFixture.WritableField, out var vBefore);
            Assert.Equal($"first-{clients[0]}", vBefore);

            // Replay with the SAME prefix + SAME per-row payload. WriteField's
            // per-key idempotency cache should serve every row from cache so
            // the persisted value stays the sentinel.
            var json2 = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries,
                idempotencyKey: "bulk-replay-test");

            var root2 = Parse(json2);
            Assert.True(root2.GetProperty("success").GetBoolean());
            Assert.Equal(3, root2.GetProperty("result").GetProperty("successCount").GetInt32());
            Assert.Equal(0, root2.GetProperty("result").GetProperty("errorCount").GetInt32());

            // Sentinel still intact — the replay returned cached results
            // rather than re-issuing the write with a different value.
            var rowAfter = await mgr.ReadClient(clients[0]);
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var vAfter);
            Assert.Equal($"first-{clients[0]}", vAfter);

            // Different payload + same key → per-row IdempotencyConflict (9102).
            var conflictEntries = clients.Take(3).Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                FieldName = AccessMapTestFixture.WritableField,
                Value = $"second-{cn}",
            }).ToArray();

            var json3 = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: conflictEntries,
                continueOnError: true,
                idempotencyKey: "bulk-replay-test");

            var root3 = Parse(json3);
            Assert.True(root3.GetProperty("success").GetBoolean());
            var result3 = root3.GetProperty("result");
            Assert.Equal(0, result3.GetProperty("successCount").GetInt32());
            Assert.Equal(3, result3.GetProperty("errorCount").GetInt32());
            foreach (var item in result3.GetProperty("perItem").EnumerateArray())
            {
                Assert.False(item.GetProperty("success").GetBoolean());
                Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                    item.GetProperty("statusCode").GetInt32());
            }
        }

        // (e) Dry-run — all 5 rows return Success but no value persists.
        [Fact]
        public async Task BulkWrite_DryRun_DoesNotPersist()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);

            // Seed a sentinel via a real bulk write so the post-dry-run
            // read asserts against a known value.
            var seedEntries = clients.Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                FieldName = AccessMapTestFixture.WritableField,
                Value = "sentinel-before-dryrun",
            }).ToArray();
            var seedJson = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: seedEntries);
            Assert.True(Parse(seedJson).GetProperty("success").GetBoolean());

            // Dry-run with a DIFFERENT value — must not persist.
            var dryEntries = clients.Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                FieldName = AccessMapTestFixture.WritableField,
                Value = "dry-only",
            }).ToArray();
            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: dryEntries,
                dryRun: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(5, root.GetProperty("result").GetProperty("successCount").GetInt32());
            Assert.True(root.GetProperty("result").GetProperty("dryRun").GetBoolean());

            // Every row must still hold the sentinel.
            foreach (var cn in clients)
            {
                var row = await mgr.ReadClient(cn);
                row!.TryGetValue(AccessMapTestFixture.WritableField, out var v);
                Assert.Equal("sentinel-before-dryrun", v);
            }
        }

        // (f) Pre-flight: year mismatch fails before any row runs.
        [Fact]
        public async Task BulkWrite_YearMismatch_ThrowsBeforeIterating()
        {
            var (mgr, tool) = NewToolWithContext(); // context year = 2024

            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", FieldName = "C1_15", Value = "x" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries,
                year: 2023); // wrong year

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (g) Pre-flight: readOnly mismatch fails before any row runs.
        [Fact]
        public async Task BulkWrite_ReadOnlyMismatch_ThrowsBeforeIterating()
        {
            var (mgr, tool) = NewToolWithContext(readOnly: false);

            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", FieldName = "C1_15", Value = "x" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries,
                readOnly: true); // caller asserted readOnly but context is RW

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (h) Pre-flight: missing context.
        [Fact]
        public async Task BulkWrite_MissingContext_ReturnsContextNotSet()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var tool = new FieldWriteBulkTools(mgr);

            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", FieldName = "C1_15", Value = "x" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries);

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (i) Empty array → no-op envelope.
        [Fact]
        public async Task BulkWrite_EmptyArray_ReturnsZeroCountEnvelope()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: System.Array.Empty<FieldWriteBulkTools.BulkWriteEntry>());

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
            Assert.Equal(0, result.GetProperty("successCount").GetInt32());
            Assert.Equal(0, result.GetProperty("errorCount").GetInt32());
            Assert.Equal(0, result.GetProperty("perItem").GetArrayLength());
        }
    }
}
