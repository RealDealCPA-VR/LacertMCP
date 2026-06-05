using System;
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
                    ClientId: $"BLK{i}",
                    TaxType: "Individual",
                    FirstName: $"Bulk{i}",
                    LastName: $"Target{i}",
                    EmailId: $"bulk{i}@example.com");
                var result = await mgr.CreateClient(args);
                Assert.Equal(StatusCodes.Success, result.Status);
                Assert.NotNull(result.ClientNumber);
                roster.Add(result.ClientNumber!);
            }
            return roster;
        }

        // (a) Happy-path 5-row bulk-update â€” every row writes to the writable
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
                Field = AccessMapTestFixture.WritableField,
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

        // (b) Inject a failure on row 3 (write to a known ReadOnly field â€”
        //     C1_0 is universally ReadOnly per spec Â§2). Default
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
                new() { ClientNumber = clients[0], Field = AccessMapTestFixture.WritableField, Value = "ok-0" },
                new() { ClientNumber = clients[1], Field = AccessMapTestFixture.WritableField, Value = "ok-1" },
                // C1_0 is universally ReadOnly â€” writes throw ExposedForRead (8).
                new() { ClientNumber = clients[2], Field = "C1_0", Value = "will-fail" },
                new() { ClientNumber = clients[3], Field = AccessMapTestFixture.WritableField, Value = "should-skip-3" },
                new() { ClientNumber = clients[4], Field = AccessMapTestFixture.WritableField, Value = "should-skip-4" },
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

        // (c) Same payload as (b) but continueOnError=true â€” rows 0/1/3/4
        //     succeed, row 2 still fails. Aggregates to {success:4, error:1}.
        [Fact]
        public async Task BulkWrite_ContinueOnError_KeepsGoingPastFailure()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = clients[0], Field = AccessMapTestFixture.WritableField, Value = "ok-0" },
                new() { ClientNumber = clients[1], Field = AccessMapTestFixture.WritableField, Value = "ok-1" },
                new() { ClientNumber = clients[2], Field = "C1_0", Value = "will-fail" },
                new() { ClientNumber = clients[3], Field = AccessMapTestFixture.WritableField, Value = "ok-3" },
                new() { ClientNumber = clients[4], Field = AccessMapTestFixture.WritableField, Value = "ok-4" },
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
            // Critically, rows 3 and 4 must succeed â€” NOT be skipped.
            Assert.True(perItem[3].GetProperty("success").GetBoolean());
            Assert.True(perItem[4].GetProperty("success").GetBoolean());
        }

        // (d) Replay-safety: invoke twice with the same idempotencyKey prefix
        //     and assert the second call returns the same statusCodes per row
        //     (no double-write â€” verify by changing the persisted sentinel
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
                Field = AccessMapTestFixture.WritableField,
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

            // Sentinel still intact â€” the replay returned cached results
            // rather than re-issuing the write with a different value.
            var rowAfter = await mgr.ReadClient(clients[0]);
            rowAfter!.TryGetValue(AccessMapTestFixture.WritableField, out var vAfter);
            Assert.Equal($"first-{clients[0]}", vAfter);

            // Different payload + same key â†’ per-row IdempotencyConflict (9102).
            var conflictEntries = clients.Take(3).Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                Field = AccessMapTestFixture.WritableField,
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

        // (e) Dry-run â€” all 5 rows return Success but no value persists.
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
                Field = AccessMapTestFixture.WritableField,
                Value = "sentinel-before-dryrun",
            }).ToArray();
            var seedJson = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: seedEntries);
            Assert.True(Parse(seedJson).GetProperty("success").GetBoolean());

            // Dry-run with a DIFFERENT value â€” must not persist.
            var dryEntries = clients.Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                Field = AccessMapTestFixture.WritableField,
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
        // P16-063 — the mismatch case throws 9016 ContextMismatch (NOT 9009
        // ContextNotSet) so the operator-facing remediation tells the truth.
        [Fact]
        public async Task BulkWrite_YearMismatch_ThrowsBeforeIterating()
        {
            var (mgr, tool) = NewToolWithContext(); // context year = 2024

            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", Field = "C1_15", Value = "x" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries,
                taxYear: 2023); // wrong year

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextMismatch,
                root.GetProperty("statusCode").GetInt32());
            Assert.Equal("ContextMismatch",
                root.GetProperty("statusName").GetString());
            // Remediation must NOT lie by telling the operator to call
            // lacerte_set_context "first" — context IS set.
            var remediation = root.GetProperty("remediation").GetString();
            Assert.DoesNotContain("Call lacerte_set_context first", remediation);
            // Carried context must surface the expected/actual values.
            var ctx = root.GetProperty("context");
            Assert.Equal(2023, ctx.GetProperty("expectedTaxYear").GetInt32());
            Assert.Equal(2024, ctx.GetProperty("actualTaxYear").GetInt32());
        }

        // (g) Pre-flight: readOnly mismatch fails before any row runs.
        // P16-063 — same 9016 ContextMismatch treatment as the year branch.
        [Fact]
        public async Task BulkWrite_ReadOnlyMismatch_ThrowsBeforeIterating()
        {
            var (mgr, tool) = NewToolWithContext(readOnly: false);

            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", Field = "C1_15", Value = "x" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries,
                readOnly: true); // caller asserted readOnly but context is RW

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextMismatch,
                root.GetProperty("statusCode").GetInt32());
            var ctx = root.GetProperty("context");
            Assert.True(ctx.GetProperty("expectedReadOnly").GetBoolean());
            Assert.False(ctx.GetProperty("actualReadOnly").GetBoolean());
        }

        // (h) Pre-flight: missing context.
        [Fact]
        public async Task BulkWrite_MissingContext_ReturnsContextNotSet()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var tool = new FieldWriteBulkTools(mgr);

            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", Field = "C1_15", Value = "x" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries);

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (i) Empty array â†’ no-op envelope.
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
            // P16-064 — successful envelopes do NOT carry a recoveryHint
            // (errorCount==0 means there is nothing to recover from).
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                result.GetProperty("recoveryHint").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                result.GetProperty("firstFailureIndex").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                result.GetProperty("firstFailure").ValueKind);
        }

        // (j) P16-064 — envelope hoists firstFailureIndex + firstFailure and
        //     emits a recoveryHint with canResumeViaSameBatch=true when an
        //     idempotencyKey was supplied. Recovery story tells the LLM to
        //     resubmit the SAME entries with the SAME key.
        [Fact]
        public async Task BulkWrite_StopOnError_HoistsFirstFailureAndEmitsRecoveryHint()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = clients[0], Field = AccessMapTestFixture.WritableField, Value = "ok-0" },
                new() { ClientNumber = clients[1], Field = AccessMapTestFixture.WritableField, Value = "ok-1" },
                new() { ClientNumber = clients[2], Field = "C1_0", Value = "will-fail" },
                new() { ClientNumber = clients[3], Field = AccessMapTestFixture.WritableField, Value = "skip-3" },
                new() { ClientNumber = clients[4], Field = AccessMapTestFixture.WritableField, Value = "skip-4" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries,
                idempotencyKey: "recovery-test-batch-A");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");

            // firstFailureIndex points to row 2 (the first non-success row).
            Assert.Equal(2, result.GetProperty("firstFailureIndex").GetInt32());

            // firstFailure carries a hoisted copy of perItem[2].
            var firstFailure = result.GetProperty("firstFailure");
            Assert.Equal(2, firstFailure.GetProperty("index").GetInt32());
            Assert.Equal(clients[2], firstFailure.GetProperty("clientNumber").GetString());
            Assert.False(firstFailure.GetProperty("success").GetBoolean());

            // skippedCount tracks the trailing skipped rows.
            Assert.Equal(2, result.GetProperty("skippedCount").GetInt32());

            // recoveryHint says safe-resume IS possible (idempotencyKey + !dryRun).
            var hint = result.GetProperty("recoveryHint");
            Assert.True(hint.GetProperty("canResumeViaSameBatch").GetBoolean());
            Assert.Equal("recovery-test-batch-A",
                hint.GetProperty("idempotencyKeyPrefix").GetString());
            Assert.Equal("{prefix}-{clientNumber}-{field}",
                hint.GetProperty("perRowKeyShape").GetString());
            // replayedRowsCount = successCount (rows that already landed).
            Assert.Equal(2, hint.GetProperty("replayedRowsCount").GetInt32());
            // retryRowsCount = errorCount + skippedCount (rows to re-execute).
            Assert.Equal(3, hint.GetProperty("retryRowsCount").GetInt32());
        }

        // (k) P16-064 — without an idempotencyKey, the recoveryHint correctly
        //     reports canResumeViaSameBatch=false (a blind resubmit would
        //     re-execute already-succeeded rows).
        [Fact]
        public async Task BulkWrite_FailureWithoutIdempotencyKey_RecoveryHintFlagsNoResume()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = clients[0], Field = AccessMapTestFixture.WritableField, Value = "ok-0" },
                new() { ClientNumber = clients[1], Field = "C1_0", Value = "will-fail" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries);

            var root = Parse(json);
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("firstFailureIndex").GetInt32());

            var hint = result.GetProperty("recoveryHint");
            Assert.False(hint.GetProperty("canResumeViaSameBatch").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                hint.GetProperty("idempotencyKeyPrefix").ValueKind);
        }

        // ---------------------------------------------------------------
        // P17-008 — batch-size cap + expectedCount cross-check tests.
        //
        // The bulk write tool fans out N writes through the live SDK from
        // a single MCP call. Without a cap a fuzzy LLM-constructed payload
        // (e.g. "every client in NY" emitted as every client in the firm)
        // can mutate thousands of rows with no per-row inspection. The
        // hard cap (DefaultMaxBulkEntries=500, override via
        // LACERTE_BULK_WRITE_MAX) is enforced at the tool chokepoint
        // BEFORE any row runs — same posture as the existing 9016
        // ContextMismatch pre-flight. expectedCount is the symmetric
        // operator-intent cross-check that catches LLM-vs-prompt drift.
        // ---------------------------------------------------------------

        // (l) entries.Length > maxAllowed throws 9020 BulkBatchTooLarge
        //     with {entryCount, maxAllowed, table} context, BEFORE any row
        //     runs. Use the env var to lower the cap to 3 so the test
        //     doesn't have to materialize 501 entries.
        [Fact]
        public async Task BulkWrite_ExceedsCap_Throws9020WithStructuredContext()
        {
            var prior = Environment.GetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, "3");

                var (mgr, tool) = NewToolWithContext();

                // 4 entries with cap=3 → reject.
                var entries = new FieldWriteBulkTools.BulkWriteEntry[]
                {
                    new() { ClientNumber = "1001", Field = "C1_15", Value = "a" },
                    new() { ClientNumber = "1002", Field = "C1_15", Value = "b" },
                    new() { ClientNumber = "1003", Field = "C1_15", Value = "c" },
                    new() { ClientNumber = "1004", Field = "C1_15", Value = "d" },
                };

                var json = await tool.LacerteFieldWriteBulk(
                    table: 1,
                    entries: entries);

                var root = Parse(json);
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(SyntheticStatusCodes.BulkBatchTooLarge,
                    root.GetProperty("statusCode").GetInt32());
                Assert.Equal("BulkBatchTooLarge",
                    root.GetProperty("statusName").GetString());

                // Carried context must surface the entry count, the cap,
                // and the env-var override name so the LLM can suggest a
                // structured remediation.
                var ctx = root.GetProperty("context");
                Assert.Equal(4, ctx.GetProperty("entryCount").GetInt32());
                Assert.Equal(3, ctx.GetProperty("maxAllowed").GetInt32());
                Assert.Equal(1, ctx.GetProperty("table").GetInt32());
                Assert.Equal(FieldWriteBulkTools.BulkWriteMaxEnvName,
                    ctx.GetProperty("envOverride").GetString());

                // Remediation must name the splitting strategy and the env
                // override so the LLM/operator has a path forward.
                var remediation = root.GetProperty("remediation").GetString();
                Assert.Contains("Split", remediation);
                Assert.Contains(FieldWriteBulkTools.BulkWriteMaxEnvName, remediation);
            }
            finally
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, prior);
            }
        }

        // (m) entries.Length == maxAllowed PASSES the cap (the cap is an
        //     UPPER bound, not a strict inequality). Verifies the boundary
        //     condition doesn't reject a deliberately-sized batch.
        [Fact]
        public async Task BulkWrite_AtCapBoundary_Succeeds()
        {
            var prior = Environment.GetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, "5");

                using var fixture = new AccessMapTestFixture();
                var (mgr, tool) = NewToolWithContext();

                var clients = await Seed5IndividualClients(mgr);
                // Exactly 5 entries with cap=5 → must NOT trip 9020.
                var entries = clients.Select(cn => new FieldWriteBulkTools.BulkWriteEntry
                {
                    ClientNumber = cn,
                    Field = AccessMapTestFixture.WritableField,
                    Value = $"boundary-{cn}",
                }).ToArray();

                var json = await tool.LacerteFieldWriteBulk(
                    table: AccessMapTestFixture.Table,
                    entries: entries);

                var root = Parse(json);
                Assert.True(root.GetProperty("success").GetBoolean());
                Assert.Equal(5, root.GetProperty("result").GetProperty("successCount").GetInt32());
            }
            finally
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, prior);
            }
        }

        // (n) expectedCount=10 but entries.Length=11 throws 9021
        //     BulkBatchCountMismatch with {expected, actual, table} context
        //     BEFORE any row runs. Symmetric with the year/readOnly
        //     mismatch pre-flights at (f)/(g).
        [Fact]
        public async Task BulkWrite_ExpectedCountMismatch_Throws9021WithStructuredContext()
        {
            var (mgr, tool) = NewToolWithContext();

            // 2 entries supplied but caller asserted 1 → reject.
            var entries = new FieldWriteBulkTools.BulkWriteEntry[]
            {
                new() { ClientNumber = "1001", Field = "C1_15", Value = "x" },
                new() { ClientNumber = "1002", Field = "C1_15", Value = "y" },
            };

            var json = await tool.LacerteFieldWriteBulk(
                table: 1,
                entries: entries,
                expectedCount: 1);

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.BulkBatchCountMismatch,
                root.GetProperty("statusCode").GetInt32());
            Assert.Equal("BulkBatchCountMismatch",
                root.GetProperty("statusName").GetString());

            var ctx = root.GetProperty("context");
            Assert.Equal(1, ctx.GetProperty("expected").GetInt32());
            Assert.Equal(2, ctx.GetProperty("actual").GetInt32());
            Assert.Equal(1, ctx.GetProperty("table").GetInt32());
        }

        // (o) expectedCount==entries.Length PASSES the cross-check (sanity:
        //     the happy path through the new gate must not reject a
        //     correctly-asserted batch).
        [Fact]
        public async Task BulkWrite_ExpectedCountMatches_PassesPreflight()
        {
            using var fixture = new AccessMapTestFixture();
            var (mgr, tool) = NewToolWithContext();

            var clients = await Seed5IndividualClients(mgr);
            var entries = clients.Select(cn => new FieldWriteBulkTools.BulkWriteEntry
            {
                ClientNumber = cn,
                Field = AccessMapTestFixture.WritableField,
                Value = $"match-{cn}",
            }).ToArray();

            var json = await tool.LacerteFieldWriteBulk(
                table: AccessMapTestFixture.Table,
                entries: entries,
                expectedCount: 5);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(5, root.GetProperty("result").GetProperty("successCount").GetInt32());
        }

        // (p) ResolveMaxBulkEntries env-var parsing — null/empty/non-numeric
        //     /negative/zero all fall back to the default. Matches the
        //     project's never-throw-from-env-parse convention.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-number")]
        [InlineData("-1")]
        [InlineData("0")]
        public void ResolveMaxBulkEntries_InvalidEnvVar_FallsBackToDefault(string? raw)
        {
            var prior = Environment.GetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, raw);
                Assert.Equal(FieldWriteBulkTools.DefaultMaxBulkEntries,
                    FieldWriteBulkTools.ResolveMaxBulkEntries());
            }
            finally
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, prior);
            }
        }

        // (q) ResolveMaxBulkEntries with a valid positive integer replaces
        //     the default — the env-var escape hatch the remediation names.
        [Fact]
        public void ResolveMaxBulkEntries_ValidEnvVar_ReplacesDefault()
        {
            var prior = Environment.GetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, "1234");
                Assert.Equal(1234, FieldWriteBulkTools.ResolveMaxBulkEntries());
            }
            finally
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, prior);
            }
        }
    }
}
