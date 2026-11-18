using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using Xunit;
using static LacertMCP.Tests.Session.LiveDetailWriteHarness;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// FP-2 — live detail-row writes over ODBC: the off-by-default gate, the
    /// INSERT handed to the out-of-process worker, the per-client backup, the
    /// read-back, and the failure mapping. Uses the fake ODBC detail table and
    /// an injected worker runner, so no process is spawned and no driver is
    /// needed.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class LiveDetailWriteTests : IDisposable
    {
        private readonly string _dir = NewTempDir();

        public void Dispose() => TryDelete(_dir);

        // ---------------------------------------------------------------
        // Gate
        // ---------------------------------------------------------------

        [Fact]
        public async Task Disabled_ByDefault_Returns9202_WithEnableHint_AndTouchesNothing()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewLive();
            mgr.LiveReaderFactory = db.Factory;
            mgr.DetailWriteRunner = runner;
            mgr.DetailSnapshotDirectory = _dir;
            Assert.False(mgr.LiveDetailWritesEnabled);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.SimulationOnly, ex.StatusCode);
            var reason = (string)ex.Context["reason"]!;
            Assert.Contains("LACERTE_LIVE_DETAIL_WRITES=1", reason);
            Assert.Contains("REGRESSION_CHECKLIST", reason);
            Assert.Equal("LACERTE_LIVE_DETAIL_WRITES=1", ex.Context["enableWith"]);

            // The dry run refuses exactly where the commit does.
            var dry = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), dryRun: true));
            Assert.Equal(SyntheticStatusCodes.SimulationOnly, dry.StatusCode);

            Assert.Empty(runner.Calls);
            Assert.Empty(db.Sql);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("true", false)]
        [InlineData(" 1", false)]
        public void EnvFlag_StrictOne_EnablesLiveDetailWrites(string value, bool expected)
        {
            using var mgr = NewLive(new Dictionary<string, string?> { ["LACERTE_LIVE_DETAIL_WRITES"] = value });
            Assert.Equal(expected, mgr.LiveDetailWritesEnabled);
        }

        [Fact]
        public void SnapshotDirectory_EnvOverride_ElseAppData_ElseNone()
        {
            Assert.Equal(@"D:\backups",
                LacerteSessionManager.ResolveDetailSnapshotDirectory(new Dictionary<string, string?>
                {
                    ["LACERTE_DETAIL_SNAPSHOT_DIR"] = @"D:\backups",
                    ["APPDATA"] = @"C:\Users\x\AppData\Roaming",
                }));
            // A relative override is ignored (falls back to APPDATA).
            Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Roaming", "lacerte-mcp", "detail-snapshots"),
                LacerteSessionManager.ResolveDetailSnapshotDirectory(new Dictionary<string, string?>
                {
                    ["LACERTE_DETAIL_SNAPSHOT_DIR"] = "relative\\dir",
                    ["APPDATA"] = @"C:\Users\x\AppData\Roaming",
                }));
            Assert.Null(LacerteSessionManager.ResolveDetailSnapshotDirectory(new Dictionary<string, string?>()));
        }

        // ---------------------------------------------------------------
        // Commit: SQL, backup, read-back
        // ---------------------------------------------------------------

        [Fact]
        public async Task Enabled_Commit_HandsExactInsertAndConnectionString_ToWorker_AndVerifies()
        {
            var db = new FakeDetailDb();
            db.For("SMIT").Add(new DetailRow("12", "", "1", "", "", "", "Source", "Taxpayer", "", 320.5, "Bank interest"));
            var runner = new FakeDetailWriteRunner();
            var row = new DetailRow("11", "", "1", "1000", "", "", "source", "T", "", 52000.5, "O'Brien Co W-2");
            runner.Behavior = AppendOnRun(db, "SMIT", row with { Source = "Source", TSJ = "Taxpayer" });
            using var mgr = NewEnabled(db, runner, _dir);

            var outcome = await mgr.WriteDetailWithOutcome(" smit ", row);

            var call = Assert.Single(runner.Calls);
            Assert.Equal(
                "INSERT INTO [SMIT-Detail] (Series, Code, Suffix, StateAbbr, CityAbbr, Source, TSJ, Amount, Description) " +
                "VALUES (11, 1, 1000, '', '', 'Source', 'Taxpayer', 52000.5, 'O''Brien Co W-2')",
                call.Sql);
            Assert.Equal(LacerteSessionManager.LiveConnectionString(mgr.Context!), call.ConnectionString);
            Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);

            Assert.Equal(StatusCodes.Success, outcome.Status);
            Assert.True(outcome.Verified);
            Assert.Null(outcome.VerificationNote);
            Assert.NotNull(outcome.Snapshot);
            Assert.True(File.Exists(outcome.Snapshot));
            // Baseline + read-back, both over fresh connections.
            Assert.Equal(2, db.Sql.Count(s => s.Contains("[SMIT-Detail]")));
            Assert.Equal(2, db.ConnectionsOpened);
        }

        [Fact]
        public async Task Snapshot_TakenOncePerClient_BeforeTheFirstWrite_WithPreWriteRows()
        {
            var db = new FakeDetailDb();
            db.For("SMIT").Add(new DetailRow("12", "", "1", "", "", "", "Source", "Taxpayer", "", 320.5, "Bank interest"));
            db.For("JONE");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);

            runner.Behavior = AppendOnRun(db, "SMIT", W2(100));
            var a = await mgr.WriteDetailWithOutcome("SMIT", W2(100));
            runner.Behavior = AppendOnRun(db, "SMIT", W2(200));
            var b = await mgr.WriteDetailWithOutcome("SMIT", W2(200));
            runner.Behavior = AppendOnRun(db, "JONE", W2(300));
            var c = await mgr.WriteDetailWithOutcome("JONE", W2(300));

            Assert.Equal(a.Snapshot, b.Snapshot);
            Assert.NotEqual(a.Snapshot, c.Snapshot);
            var files = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
            Assert.Equal(2, files.Length);
            Assert.Contains(files, f => Regex.IsMatch(f!, @"^\d{8}-\d{6}-I24-SMIT\.json$"));
            Assert.Contains(files, f => Regex.IsMatch(f!, @"^\d{8}-\d{6}-I24-JONE\.json$"));

            using var doc = JsonDocument.Parse(File.ReadAllText(a.Snapshot!));
            var root = doc.RootElement;
            Assert.Equal("SMIT", root.GetProperty("clientNumber").GetString());
            Assert.Equal(2024, root.GetProperty("taxYear").GetInt32());
            Assert.Equal("I", root.GetProperty("module").GetString());
            Assert.Equal(1, root.GetProperty("rowCount").GetInt32()); // only the pre-existing row
            var only = root.GetProperty("rows")[0];
            Assert.Equal("12", only.GetProperty("Series").GetString());
            Assert.Equal("Bank interest", only.GetProperty("Description").GetString());
        }

        [Fact]
        public async Task Snapshot_CannotBeSaved_RefusesTheWrite_WorkerNeverRuns()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            // A FILE where the backup directory should be: CreateDirectory fails.
            var blocker = Path.Combine(_dir, "not-a-dir");
            File.WriteAllText(blocker, "x");
            using var mgr = NewEnabled(db, runner, blocker);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.InternalServerError, ex.StatusCode);
            Assert.Contains("Nothing was written", (string)ex.Context["reason"]!);
            Assert.Empty(runner.Calls);

            mgr.DetailSnapshotDirectory = null;
            var ex2 = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Contains("LACERTE_DETAIL_SNAPSHOT_DIR", (string)ex2.Context["reason"]!);
            Assert.Empty(runner.Calls);
        }

        // R8-review — a client with no detail table yet (nothing entered) gets
        // its first row: the missing table is an empty baseline, like the read
        // path treats it, and the INSERT decides.
        [Fact]
        public async Task NoDetailTableYet_EmptyBaseline_FirstRowIsWritten()
        {
            var db = new FakeDetailDb(); // no SMIT table → DBNotSupported on read
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            using var mgr = NewEnabled(db, runner, _dir);

            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2());
            Assert.True(outcome.Verified);
            Assert.Single(runner.Calls);
            Assert.Single(Directory.GetFiles(_dir)); // the (empty) backup was still taken
        }

        [Fact]
        public async Task ReadBack_NoNewMatchingRow_Throws9022_AndPinsTheIdempotencyKey()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner(); // reports Ok, changes nothing
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "fp2-mismatch"));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, ex.StatusCode);
            Assert.Equal(0, ex.Context["matchingRowsBefore"]);
            Assert.Equal(0, ex.Context["matchingRowsAfter"]);
            Assert.NotNull(ex.Context["snapshot"]);
            // R8-review — detail rows are append-only: a row may now be in the
            // file, so the key is pinned and a same-key retry must NOT append.
            Assert.True(mgr.TryGetIdempotencyEntryForTest("fp2-mismatch", out _));
            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            var replay = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "fp2-mismatch"));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, replay.StatusCode);
            Assert.Equal(true, replay.Context["idempotencyReplay"]);
            Assert.Single(runner.Calls);

            // A NEW key writes it (after the operator checked it is missing).
            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "fp2-mismatch-2");
            Assert.True(outcome.Verified);
            Assert.Equal(2, runner.Calls.Count);
        }

        [Fact]
        public async Task ReadBack_DriverTruncatedDescription_Is9022()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2(description: "A very long employer na"));
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("SMIT", W2(description: "A very long employer name that the field cuts")));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, ex.StatusCode);
        }

        [Fact]
        public async Task ReadBack_TwoNewMatchingRows_Is9022()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = _ =>
            {
                db.For("SMIT").Add(W2());
                db.For("SMIT").Add(W2()); // someone else added the same row meanwhile
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1 };
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, ex.StatusCode);
            Assert.Equal(2, ex.Context["matchingRowsAfter"]);
        }

        [Fact]
        public async Task ReadBack_ExistingIdenticalRow_StillVerifiesByCountIncrease()
        {
            var db = new FakeDetailDb();
            db.For("SMIT").Add(W2()); // an identical W-2 is already there
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            using var mgr = NewEnabled(db, runner, _dir);

            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2());
            Assert.True(outcome.Verified);
        }

        [Fact]
        public async Task ReadBack_CannotRun_IsSuccessWithVerifiedNullAndNote()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);
            runner.Behavior = _ =>
            {
                db.FailReads = () => new LacerteSDKError(StatusCodes.DBFNotAccessable);
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1 };
            };

            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "fp2-noread");
            Assert.Equal(StatusCodes.Success, outcome.Status);
            Assert.Null(outcome.Verified);
            Assert.Contains("Not verified", outcome.VerificationNote);
            Assert.NotNull(outcome.Snapshot);
            Assert.True(mgr.TryGetIdempotencyEntryForTest("fp2-noread", out _));
        }

        // ---------------------------------------------------------------
        // Worker failures
        // ---------------------------------------------------------------

        [Fact]
        public async Task WorkerCrash_IsCleanBridgeUnavailable_ServerUnaffected_NoWriteClaim()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.Crashed, ExitCode = -1073741819, Error = "exited without a result" },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "fp2-crash"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            var reason = (string)ex.Context["reason"]!;
            Assert.Contains("crashed during the INSERT", reason);
            Assert.Contains("unaffected", reason);
            Assert.Equal("no", ex.Context["rowWritten"]);
            Assert.Equal(-1073741819, ex.Context["workerExitCode"]);
            Assert.False(ex.Context.ContainsKey("verified"));
            Assert.False(mgr.TryGetIdempotencyEntryForTest("fp2-crash", out _));

            // The session is still usable afterwards.
            var rows = await mgr.ReadDetail("SMIT");
            Assert.Empty(rows);
        }

        [Fact]
        public async Task WorkerTimeout_RowActuallyLanded_SaysSo()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = _ =>
            {
                db.For("SMIT").Add(W2());
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.TimedOut, Error = "timed out" };
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal("yes", ex.Context["rowWritten"]);
            Assert.Contains("do not write it again", (string)ex.Context["reason"]!);
        }

        [Fact]
        public async Task WorkerSqlError_MapsThroughOdbcErrorMapper()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.Failed, SqlState = "08001", Error = "unable to connect", ExitCode = 1 },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(StatusCodes.DBFNotAccessable, ex.StatusCode);
            Assert.Equal("08001", ex.Context["sqlState"]);
            Assert.Equal("no", ex.Context["rowWritten"]);
        }

        [Fact]
        public async Task WorkerNotStarted_IsBridgeUnavailable_NothingWritten()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = "Worker executable not found" },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal("no", ex.Context["rowWritten"]);
            Assert.Contains("Nothing was written", (string)ex.Context["reason"]!);
        }

        [Fact]
        public async Task FailedWrite_NotCached_RetryReachesTheWorkerAgain()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.Failed, SqlState = "HY000", Error = "file locked", ExitCode = 1 },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "fp2-retry"));
            Assert.False(mgr.TryGetIdempotencyEntryForTest("fp2-retry", out _));

            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            var status = await mgr.WriteDetail("SMIT", W2(), idempotencyKey: "fp2-retry");
            Assert.Equal(StatusCodes.Success, status);
            Assert.Equal(2, runner.Calls.Count);
            Assert.True(mgr.TryGetIdempotencyEntryForTest("fp2-retry", out _));
        }

        // ---------------------------------------------------------------
        // Dry run, idempotency, replaceIndex
        // ---------------------------------------------------------------

        [Fact]
        public async Task DryRun_Enabled_ValidatesOnly_NeverSpawns_NoReadNoBackup()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);

            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2(), dryRun: true);

            Assert.Equal(StatusCodes.Success, outcome.Status);
            Assert.Null(outcome.Verified);
            Assert.Null(outcome.Snapshot);
            Assert.Contains("Dry run", outcome.VerificationNote);
            Assert.Empty(runner.Calls);
            Assert.Empty(db.Sql);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public async Task DryRun_Enabled_RejectsWhatTheInsertWouldReject()
        {
            using var mgr = NewEnabled(new FakeDetailDb(), new FakeDetailWriteRunner(), _dir);
            // A tab passes the both-modes content check but can't go into a live INSERT.
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("SMIT", W2(description: "W-2\tACME"), dryRun: true));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public async Task SameKeySamePayload_ReplaysFromCache_WorkerRunsOnce()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            using var mgr = NewEnabled(db, runner, _dir);

            var first = await mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "fp2-replay");
            var second = await mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "fp2-replay");

            Assert.True(first.Verified);
            Assert.Single(runner.Calls);
            Assert.Equal(StatusCodes.Success, second.Status);
            Assert.Null(second.Verified);
            Assert.Contains("Replayed", second.VerificationNote);
            Assert.Equal(first.Snapshot, second.Snapshot);
            Assert.Single(db.For("SMIT"));
        }

        [Fact]
        public async Task ReplaceIndex_Live_Enabled_Still9019_NothingRuns()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), replaceIndex: 0));
            Assert.Equal(SyntheticStatusCodes.LiveReplaceIndexUnsupported, ex.StatusCode);
            Assert.Empty(runner.Calls);
            Assert.Empty(db.Sql);
        }

        [Fact]
        public async Task Simulation_Outcome_HasSameShape_NoBackupNoReadBack()
        {
            using var sim = new LacerteSessionManager(new Dictionary<string, string?>());
            await sim.Register();
            sim.SetContext(LacertMCP.Server.Session.Fixtures.SeedData.DataPathIData2024, 2024, 'I');
            var outcome = await sim.WriteDetailWithOutcome(LacertMCP.Server.Session.Fixtures.SeedData.BROW.ClientNumber, W2());
            Assert.Equal(StatusCodes.Success, outcome.Status);
            Assert.Null(outcome.Snapshot);
            Assert.Null(outcome.Verified);
            Assert.Contains("Simulation", outcome.VerificationNote);
        }
    }
}
