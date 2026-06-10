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
    /// R6 gap #4: <c>lacerte_detail_write_bulk</c> — bulk intake of
    /// W-2/1099/dependent rows. Covers: happy path multi-row append, cap
    /// rejection (9020 via the SHARED LACERTE_BULK_WRITE_MAX), dryRun
    /// (no mutation), continueOnError both ways, per-row 9008 validation
    /// (bad code / bad stateAbbrev / bad tsj), replaceIndex pass-through
    /// (sim honors it), positional idempotency replay (no duplicate
    /// appends — the silent-corruption case for keyless retries),
    /// same-logical-row-twice positional keys, cancellation (stoppedEarly),
    /// context preflight, and the empty no-op envelope.
    ///
    /// <para>
    /// Joined to <see cref="LacertMCP.Tests.AccessMapMutationCollection"/>
    /// because the cap tests mutate the process-wide LACERTE_BULK_WRITE_MAX
    /// env var that <see cref="FieldWriteBulkToolsTests"/> (same collection)
    /// also mutates — serializing the two classes prevents a parallel-run
    /// race on the shared variable.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class DetailWriteBulkToolsTests
    {
        private static (LacerteSessionManager mgr, DetailWriteBulkTools tool) NewToolWithContext(bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return (mgr, new DetailWriteBulkTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        private static DetailWriteBulkTools.BulkDetailEntry Entry(
            string clientNumber, string code, double? amount, string description) =>
            new DetailWriteBulkTools.BulkDetailEntry
            {
                ClientNumber = clientNumber,
                Series = SeedData.SeriesIds.Wages, // numeric "11" per P14-055
                Code = code,
                Amount = amount,
                Description = description,
                Source = "W2",
                Tsj = "T",
            };

        // (a) Happy path — 3 rows append to the seeded client 1001 (BROW);
        //     summary counts + per-row shape + store grew by 3.
        [Fact]
        public async Task DetailBulk_HappyPath_ThreeRows_AllAppend()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var json = await tool.LacerteDetailWriteBulk(new[]
            {
                Entry("1001", "1", 100.0, "bulk row 0"),
                Entry("1001", "2", 200.0, "bulk row 1"),
                Entry("1001", "3", 300.0, "bulk row 2"),
            });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(3, result.GetProperty("processedCount").GetInt32());
            Assert.Equal(3, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("skippedCount").GetInt32());
            Assert.False(result.GetProperty("stoppedEarly").GetBoolean());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.Equal(i, rows[i].GetProperty("index").GetInt32());
                Assert.True(rows[i].GetProperty("succeeded").GetBoolean());
                Assert.Equal(StatusCodes.Success, rows[i].GetProperty("statusCode").GetInt32());
                Assert.Equal("Success", rows[i].GetProperty("statusName").GetString());
                Assert.Equal("1001", rows[i].GetProperty("clientNumber").GetString());
            }

            Assert.Equal(before + 3, (await mgr.ReadDetail("1001")).Count);
        }

        // (b) Cap rejection — uses the SHARED LACERTE_BULK_WRITE_MAX (same
        //     weight class as field writes) and the same 9020 fail-closed
        //     shape; context.envOverride names the shared var.
        [Fact]
        public async Task DetailBulk_ExceedsCap_Throws9020_SharedEnvVar_NothingAppended()
        {
            var prior = Environment.GetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, "2");

                var (mgr, tool) = NewToolWithContext();
                var before = (await mgr.ReadDetail("1001")).Count;

                var json = await tool.LacerteDetailWriteBulk(new[]
                {
                    Entry("1001", "1", 1.0, "cap 0"),
                    Entry("1001", "2", 2.0, "cap 1"),
                    Entry("1001", "3", 3.0, "cap 2"),
                });

                var root = Parse(json);
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(SyntheticStatusCodes.BulkBatchTooLarge,
                    root.GetProperty("statusCode").GetInt32());
                Assert.Equal("BulkBatchTooLarge",
                    root.GetProperty("statusName").GetString());

                var ctx = root.GetProperty("context");
                Assert.Equal(3, ctx.GetProperty("entryCount").GetInt32());
                Assert.Equal(2, ctx.GetProperty("maxAllowed").GetInt32());
                Assert.Equal(FieldWriteBulkTools.BulkWriteMaxEnvName,
                    ctx.GetProperty("envOverride").GetString());

                // Fail-closed: no row ran.
                Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
            }
            finally
            {
                Environment.SetEnvironmentVariable(FieldWriteBulkTools.BulkWriteMaxEnvName, prior);
            }
        }

        // (c) dryRun — every row validates + simulates then rolls back; no
        //     row persists.
        [Fact]
        public async Task DetailBulk_DryRun_DoesNotPersist()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var json = await tool.LacerteDetailWriteBulk(new[]
                {
                    Entry("1001", "1", 11.0, "dry 0"),
                    Entry("1001", "2", 22.0, "dry 1"),
                },
                dryRun: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("dryRun").GetBoolean());
            Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());

            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
        }

        // (d) Stop-on-error (default) — row 1 fails the mandatory 9008
        //     validation (non-numeric code); row 2 is skipped and NOT
        //     appended; only row 0 persists.
        [Fact]
        public async Task DetailBulk_StopOnError_HaltsAtFirstFailureAndSkipsRemainder()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var json = await tool.LacerteDetailWriteBulk(new[]
            {
                Entry("1001", "1", 100.0, "ok 0"),
                Entry("1001", "abc", 200.0, "bad code"), // fails ^-?\d+$
                Entry("1001", "3", 300.0, "should skip"),
            });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(1, result.GetProperty("skippedCount").GetInt32());
            Assert.Equal(1, result.GetProperty("firstFailureIndex").GetInt32());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.False(rows[1].GetProperty("succeeded").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                rows[1].GetProperty("statusCode").GetInt32());
            Assert.False(rows[2].GetProperty("succeeded").GetBoolean());
            Assert.True(rows[2].GetProperty("skipped").GetBoolean());

            // Only row 0 landed.
            Assert.Equal(before + 1, (await mgr.ReadDetail("1001")).Count);
        }

        // (e) continueOnError=true — keeps going past the bad row; rows 0
        //     and 2 persist.
        [Fact]
        public async Task DetailBulk_ContinueOnError_KeepsGoingPastFailure()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var json = await tool.LacerteDetailWriteBulk(new[]
                {
                    Entry("1001", "1", 100.0, "ok 0"),
                    Entry("1001", "abc", 200.0, "bad code"),
                    Entry("1001", "3", 300.0, "ok 2"),
                },
                continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("skippedCount").GetInt32());

            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        // (f) Per-row validation parity with DetailsTools — bad stateAbbrev
        //     fails 9008 with context.field=stateAbbrev.
        [Fact]
        public async Task DetailBulk_BadStateAbbrev_RowFails9008()
        {
            var (mgr, tool) = NewToolWithContext();

            var bad = Entry("1001", "1", 100.0, "bad state");
            bad.StateAbbrev = "California"; // must be 2-letter

            var json = await tool.LacerteDetailWriteBulk(
                new[] { bad }, continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var rows = root.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.False(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                rows[0].GetProperty("statusCode").GetInt32());
            Assert.Equal("stateAbbrev",
                rows[0].GetProperty("context").GetProperty("field").GetString());
        }

        // (g) Per-row validation parity — bad TSJ fails 9008 with
        //     context.field=tsj; empty series fails with field=series.
        [Fact]
        public async Task DetailBulk_BadTsjAndEmptySeries_RowsFail9008()
        {
            var (mgr, tool) = NewToolWithContext();

            var badTsj = Entry("1001", "1", 100.0, "bad tsj");
            badTsj.Tsj = "X";
            var badSeries = Entry("1001", "1", 100.0, "no series");
            badSeries.Series = "";

            var json = await tool.LacerteDetailWriteBulk(
                new[] { badTsj, badSeries }, continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(2, result.GetProperty("failedCount").GetInt32());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal("tsj",
                rows[0].GetProperty("context").GetProperty("field").GetString());
            Assert.Equal("series",
                rows[1].GetProperty("context").GetProperty("field").GetString());
        }

        // (h) replaceIndex pass-through — sim honors per-entry replaceIndex
        //     (P17-001: live rejects with 9019; sim is the parity surface).
        //     Row count is unchanged and row 0's description is replaced.
        [Fact]
        public async Task DetailBulk_ReplaceIndex_ReplacesRowInSim()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = await mgr.ReadDetail("1001");
            var beforeCount = before.Count;
            Assert.True(beforeCount >= 1, "Seed must have at least one detail row.");

            var replace = Entry("1001", "1", 999.0, "replaced via bulk");
            replace.ReplaceIndex = 0;

            var json = await tool.LacerteDetailWriteBulk(new[] { replace });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var rows = root.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal(0, rows[0].GetProperty("replaceIndex").GetInt32());

            var after = await mgr.ReadDetail("1001");
            Assert.Equal(beforeCount, after.Count);
            Assert.Equal("replaced via bulk", after[0].Description);
        }

        // (i) Idempotency replay — resubmitting the SAME batch with the SAME
        //     prefix replays every row from cache: NO duplicate appends.
        //     This is the headline safety property for detail bulk (detail
        //     files have no primary key; a keyless retry silently doubles
        //     W-2 amounts).
        [Fact]
        public async Task DetailBulk_IdempotencyReplay_SameBatch_NoDuplicateAppends()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var entries = new[]
            {
                Entry("1001", "1", 500.0, "replay 0"),
                Entry("1001", "2", 600.0, "replay 1"),
            };

            var json1 = await tool.LacerteDetailWriteBulk(
                entries, idempotencyKey: "dwb-replay");
            Assert.True(Parse(json1).GetProperty("success").GetBoolean(), json1);
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);

            // Replay: same entries, same order, same prefix.
            var json2 = await tool.LacerteDetailWriteBulk(
                entries, idempotencyKey: "dwb-replay");
            var root2 = Parse(json2);
            Assert.True(root2.GetProperty("success").GetBoolean(), json2);
            var result2 = root2.GetProperty("result");
            Assert.Equal(2, result2.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, result2.GetProperty("failedCount").GetInt32());

            // Still +2, not +4 — cached replay, no re-append.
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        // (j) Same prefix + DIFFERENT per-row payload → 9102
        //     IdempotencyConflict for that row.
        [Fact]
        public async Task DetailBulk_IdempotencyConflict_DifferentPayloadSameKey_Returns9102Rows()
        {
            var (mgr, tool) = NewToolWithContext();

            var json1 = await tool.LacerteDetailWriteBulk(
                new[] { Entry("1001", "7", 1000.0, "conflict seed") },
                idempotencyKey: "dwb-conflict");
            Assert.True(Parse(json1).GetProperty("success").GetBoolean(), json1);

            // Same key/position/client/series/code — different amount.
            var json2 = await tool.LacerteDetailWriteBulk(
                new[] { Entry("1001", "7", 2000.0, "conflict seed") },
                continueOnError: true,
                idempotencyKey: "dwb-conflict");

            var root2 = Parse(json2);
            Assert.True(root2.GetProperty("success").GetBoolean(), json2);
            var rows = root2.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.False(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                rows[0].GetProperty("statusCode").GetInt32());
        }

        // (k) R6-20a parity — the SAME logical row twice in one keyed batch
        //     does NOT alias to one key (index component): both rows append.
        //     Legitimate for detail files (e.g. two W-2s, same series+code).
        [Fact]
        public async Task DetailBulk_SameLogicalRowTwiceInBatch_BothAppend()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var json = await tool.LacerteDetailWriteBulk(new[]
                {
                    Entry("1001", "5", 750.0, "first W-2"),
                    Entry("1001", "5", 750.0, "first W-2"),
                },
                idempotencyKey: "dwb-dup-pair");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(2, root.GetProperty("result").GetProperty("succeededCount").GetInt32());
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        // (l) Pre-cancelled token — stops BEFORE any mutation; partial
        //     envelope carries stoppedEarly=true + processedCount=0.
        [Fact]
        public async Task DetailBulk_PreCancelledToken_StopsEarly_NothingAppended()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            var json = await tool.LacerteDetailWriteBulk(new[]
                {
                    Entry("1001", "1", 1.0, "never lands 0"),
                    Entry("1001", "2", 2.0, "never lands 1"),
                },
                cancellationToken: cts.Token);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("stoppedEarly").GetBoolean());
            Assert.Equal(0, result.GetProperty("processedCount").GetInt32());
            Assert.Equal(2, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());

            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
        }

        // (m) Missing context preflight → 9009 ContextNotSet before any row.
        [Fact]
        public async Task DetailBulk_MissingContext_Returns9009()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var tool = new DetailWriteBulkTools(mgr);

            var json = await tool.LacerteDetailWriteBulk(
                new[] { Entry("1001", "1", 1.0, "no ctx") });

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (n) Empty array → no-op envelope.
        [Fact]
        public async Task DetailBulk_EmptyArray_ReturnsZeroCountEnvelope()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteDetailWriteBulk(
                Array.Empty<DetailWriteBulkTools.BulkDetailEntry>());

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("processedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("recoveryHint").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("firstFailureIndex").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("firstFailure").ValueKind);
        }

        // (o) Read-only session — every attempted row fails 9001 through
        //     the session chokepoint (RejectIfReadOnly is per row, not
        //     bulk-level), confirming the bulk tool inherits the gate.
        [Fact]
        public async Task DetailBulk_ReadOnlySession_RowFails9001()
        {
            var (mgr, tool) = NewToolWithContext(readOnly: true);

            var json = await tool.LacerteDetailWriteBulk(
                new[] { Entry("1001", "1", 1.0, "should not persist") });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var rows = root.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.False(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly,
                rows[0].GetProperty("statusCode").GetInt32());
        }

        // (p) recoveryHint — failures with an idempotencyKey emit
        //     canResumeViaSameBatch=true and the detail-specific positional
        //     key shape.
        [Fact]
        public async Task DetailBulk_FailureWithKey_RecoveryHintNamesPositionalKeyShape()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteDetailWriteBulk(new[]
                {
                    Entry("1001", "1", 1.0, "ok"),
                    Entry("1001", "abc", 2.0, "bad code"),
                },
                idempotencyKey: "dwb-recovery");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var hint = root.GetProperty("result").GetProperty("recoveryHint");
            Assert.True(hint.GetProperty("canResumeViaSameBatch").GetBoolean());
            Assert.Equal("dwb-recovery", hint.GetProperty("idempotencyKeyPrefix").GetString());
            Assert.Equal("{prefix}-{index}-{clientNumber}-{series}-{code}",
                hint.GetProperty("perRowKeyShape").GetString());
        }
    }
}
