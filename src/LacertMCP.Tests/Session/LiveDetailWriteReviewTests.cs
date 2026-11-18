using System;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using Xunit;
using static LacertMCP.Tests.Session.LiveDetailWriteHarness;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Regressions from the independent review of the live detail-write path:
    /// no duplicate rows on retry, required Suffix, ANSI-only text, TSJ
    /// blank == Joint, single-statement worker SQL, strict snapshot folder.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class LiveDetailWriteReviewTests : IDisposable
    {
        private readonly string _dir = NewTempDir();

        public void Dispose() => TryDelete(_dir);

        // The worker failed AFTER the row landed (re-read finds it): a same-key
        // retry must replay the error instead of appending a second row.
        [Fact]
        public async Task WorkerFailure_RowLanded_SameKeyRetry_DoesNotAppendAgain()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = _ =>
            {
                db.For("SMIT").Add(W2());   // committed...
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Crashed, ExitCode = -1 }; // ...then crashed
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var first = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "rv-landed"));
            Assert.Equal("yes", first.Context["rowWritten"]);

            var retry = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "rv-landed"));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, retry.StatusCode);
            Assert.Equal(true, retry.Context["idempotencyReplay"]);
            Assert.Single(runner.Calls);
            Assert.Single(db.For("SMIT"));
        }

        // A worker failure whose re-read shows the row is NOT there leaves the
        // key free, so the retry really writes it.
        [Fact]
        public async Task WorkerFailure_RowNotWritten_SameKeyRetry_Writes()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.Failed, SqlState = "08001", Error = "file in use", ExitCode = 1 },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var first = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "rv-free"));
            Assert.Equal("no", first.Context["rowWritten"]);

            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "rv-free");
            Assert.True(outcome.Verified);
        }

        // A timed-out worker may still commit later: never "not written".
        [Fact]
        public async Task WorkerTimeout_IsUnknown_NotNo()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.TimedOut },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "rv-timeout"));
            Assert.Equal("unknown", ex.Context["rowWritten"]);
            Assert.True(mgr.TryGetIdempotencyEntryForTest("rv-timeout", out _));
        }

        [Fact]
        public async Task LiveRow_WithoutSuffix_IsRefusedBeforeAnything()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);
            var row = new DetailRow("11", "", "1", "", "", "", "Source", "Taxpayer", "", 100, "no suffix");

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", row));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
            Assert.Equal("Suffix", ex.Context["field"]);
            Assert.Empty(runner.Calls);
        }

        [Theory]
        [InlineData("Payer \U0001F600")]   // emoji
        [InlineData("Работодатель")]        // Cyrillic
        public void InsertSql_RejectsTextOutsideWindows1252(string description)
        {
            var row = new DetailRow("11", "", "1", "1000", "", "", "Source", "Taxpayer", "", 100, description);
            var ex = Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", row));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public void InsertSql_AcceptsWindows1252Accents()
        {
            var row = new DetailRow("11", "", "1", "1000", "", "", "Source", "Taxpayer", "", 100, "Café Müller");
            Assert.Contains("'Café Müller'", DetailRowWriter.BuildInsertSql("SMIT", row));
        }

        [Fact]
        public void ReadBack_BlankTsj_MatchesStoredJoint()
        {
            var written = new DetailRow("11", "", "1", "1000", "", "", "Source", "", "", 100, "x");
            var stored = new DetailRow("11", "", "1", "1000", "", "", "Source", "Joint", "", 100, "x");
            Assert.True(DetailRowWriter.RowMatches(written, stored));
        }

        [Theory]
        [InlineData("INSERT INTO [SMIT-Detail] (Series) VALUES (11)", true)]
        [InlineData("INSERT INTO [SMIT-Detail] (Description) VALUES ('a;b -- c')", true)]
        [InlineData("INSERT INTO [SMIT-Detail] (Series) VALUES (11); DELETE FROM [SMIT-Detail]", false)]
        [InlineData("INSERT INTO [SMIT-Detail] (Series) VALUES (11) -- trailing", false)]
        [InlineData("INSERT INTO [SMIT-Detail] (Description) VALUES ('unterminated)", false)]
        public void Worker_AcceptsOnlyOneStatement(string sql, bool ok)
            => Assert.Equal(ok, DetailWriteWorker.IsSingleStatement(sql));

        [Theory]
        [InlineData(@"C:\snapshots", true)]
        [InlineData(@"\\server\share\snaps", true)]
        [InlineData(@"C:snapshots", false)]
        [InlineData(@"\snapshots", false)]
        [InlineData("snapshots", false)]
        public void SnapshotDir_MustBeFullyQualified(string path, bool ok)
            => Assert.Equal(ok, LacerteSessionManager.IsFullyQualifiedPath(path));

        [Fact]
        public async Task Sim_DetailDescription_WithLineBreak_IsRefused_LikeLive()
        {
            var mgr = new LacerteSessionManager(new System.Collections.Generic.Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(LacertMCP.Server.Session.Fixtures.SeedData.DataPathIData2024, 2024, 'I');
            using var _ = mgr;
            var row = new DetailRow("11", "", "1", "1000", "", "", "Source", "Taxpayer", "", 100, "line1\nline2");
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("1001", row));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }
    }
}
