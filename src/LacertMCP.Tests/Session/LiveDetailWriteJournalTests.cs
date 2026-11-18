using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using Xunit;
using static LacertMCP.Tests.Session.LiveDetailWriteHarness;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// FP-2 restore — the journal the server appends next to its pre-write
    /// backup for every row it INSERTED: verified, unverified and
    /// landed-after-error outcomes are journaled; refused / not-written ones
    /// aren't; a journal failure never fails the write.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class LiveDetailWriteJournalTests : IDisposable
    {
        private readonly string _dir = NewTempDir();

        public void Dispose() => TryDelete(_dir);

        private static List<DetailJournalEntry> ReadJournal(string snapshot)
        {
            var path = DetailWriteJournal.JournalPathFor(snapshot);
            Assert.True(File.Exists(path), "journal missing: " + path);
            var problems = new List<string>();
            var entries = DetailWriteJournal.Read(path, problems);
            Assert.Empty(problems);
            return entries;
        }

        private string[] JournalFiles() => Directory.GetFiles(_dir, "*" + DetailWriteJournal.JournalSuffix);

        [Fact]
        public async Task VerifiedWrite_AppendsOneLine_AndSnapshotLinksTheJournal()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            using var mgr = NewEnabled(db, runner, _dir);

            var outcome = await mgr.WriteDetailWithOutcome("smit", W2(), idempotencyKey: "fp2-j1");

            Assert.True(outcome.Verified);
            Assert.Null(outcome.VerificationNote);
            var e = Assert.Single(ReadJournal(outcome.Snapshot!));
            Assert.Equal(DetailWriteJournal.OutcomeVerified, e.Outcome);
            Assert.Equal("SMIT", e.ClientNumber);
            Assert.Equal(DataPath, e.DataPath);
            Assert.Equal(2024, e.TaxYear);
            Assert.Equal("I", e.Module);
            Assert.Equal("fp2-j1", e.IdempotencyKey);
            Assert.Equal(Path.GetFileName(outcome.Snapshot), e.Snapshot);
            Assert.Equal(W2(), e.Row);
            Assert.Equal(DetailRowWriter.BuildInsertSql("SMIT", W2()), e.InsertSql);
            Assert.False(string.IsNullOrEmpty(e.EntryId));

            using var doc = JsonDocument.Parse(File.ReadAllText(outcome.Snapshot!));
            Assert.Equal(DetailWriteJournal.JournalFileNameFor(outcome.Snapshot!), doc.RootElement.GetProperty("journal").GetString());
            // Version-1 fields unchanged.
            Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("rowCount").GetInt32());

            // A second write to the same client appends to the same journal.
            var row2 = W2(100, "SECOND");
            runner.Behavior = AppendOnRun(db, "SMIT", row2);
            var outcome2 = await mgr.WriteDetailWithOutcome("SMIT", row2);
            Assert.Equal(outcome.Snapshot, outcome2.Snapshot);
            var both = ReadJournal(outcome.Snapshot!);
            Assert.Equal(2, both.Count);
            Assert.Null(both[1].IdempotencyKey);
            Assert.NotEqual(both[0].EntryId, both[1].EntryId);
            Assert.Single(JournalFiles());
        }

        [Fact]
        public async Task ReadBackCannotRun_JournalsUnverified()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);
            runner.Behavior = _ =>
            {
                db.For("SMIT").Add(W2());
                db.FailReads = () => new LacerteSDKError(StatusCodes.DBFNotAccessable);
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1 };
            };

            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2());

            Assert.Null(outcome.Verified);
            var e = Assert.Single(ReadJournal(outcome.Snapshot!));
            Assert.Equal(DetailWriteJournal.OutcomeUnverified, e.Outcome);
            Assert.Contains("read-back failed", e.Note);
        }

        [Fact]
        public async Task ReadBackMismatch9022_JournalsUnverified_ErrorStillThrown()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            // The worker says OK but the stored row differs (truncated description).
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2(description: "ACME"));
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, ex.StatusCode);
            Assert.False(ex.Context.ContainsKey("journal")); // journal append succeeded

            var e = Assert.Single(ReadJournal((string)ex.Context["snapshot"]!));
            Assert.Equal(DetailWriteJournal.OutcomeUnverified, e.Outcome);
            Assert.Contains("0 -> 0", e.Note);
        }

        [Fact]
        public async Task WorkerFailure_RowLanded_JournalsLandedAfterError()
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

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2(), idempotencyKey: "fp2-landed"));
            Assert.Equal("yes", ex.Context["rowWritten"]);

            var e = Assert.Single(ReadJournal((string)ex.Context["snapshot"]!));
            Assert.Equal(DetailWriteJournal.OutcomeLandedAfterError, e.Outcome);
            Assert.Equal("fp2-landed", e.IdempotencyKey);
            Assert.Contains("TimedOut", e.Note);
        }

        [Theory]
        [InlineData(DetailWriteRunStatus.Crashed)]
        [InlineData(DetailWriteRunStatus.Failed)]
        [InlineData(DetailWriteRunStatus.NotStarted)]
        [InlineData(DetailWriteRunStatus.TimedOut)]
        public async Task WorkerFailure_RowNotWritten_NoJournalLine(DetailWriteRunStatus status)
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner
            {
                Behavior = _ => new DetailWriteRunResult { Status = status, Error = "x", ExitCode = 1 },
            };
            using var mgr = NewEnabled(db, runner, _dir);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.NotEqual("yes", ex.Context["rowWritten"]);
            Assert.Empty(JournalFiles());
        }

        [Fact]
        public async Task RefusedWrites_AndDryRun_NoJournal()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            using var mgr = NewEnabled(db, runner, _dir);

            // Not in the verified dictionary → refused before anything runs.
            await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2() with { Series = "12" }));
            // Missing Suffix → refused.
            await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2() with { Suffix = "" }));
            // Dry run → validates only.
            var dry = await mgr.WriteDetailWithOutcome("SMIT", W2(), dryRun: true);
            Assert.Equal(StatusCodes.Success, dry.Status);
            // Baseline read fails → refused, nothing written.
            db.FailReads = () => new LacerteSDKError(StatusCodes.DBFNotAccessable);
            await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));

            Assert.Empty(runner.Calls);
            Assert.Empty(JournalFiles());
        }

        [Fact]
        public async Task JournalAppendFailure_DoesNotFailTheWrite_ReturnsNote_AndLogs()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2());
            using var mgr = NewEnabled(db, runner, _dir);
            var logged = new List<string>();
            mgr.DetailJournalAppender = (_, __) => throw new IOException("disk full");
            mgr.DetailJournalLog = logged.Add;

            var outcome = await mgr.WriteDetailWithOutcome("SMIT", W2(), idempotencyKey: "fp2-jfail");

            Assert.Equal(StatusCodes.Success, outcome.Status);
            Assert.True(outcome.Verified);
            Assert.Contains("restore journal", outcome.VerificationNote);
            Assert.Contains("disk full", outcome.VerificationNote);
            Assert.Contains(logged, l => l.Contains("journal append failed") && l.Contains("SMIT"));
            Assert.True(mgr.TryGetIdempotencyEntryForTest("fp2-jfail", out _));
            Assert.Single(db.For("SMIT"));
        }

        [Fact]
        public async Task JournalAppendFailure_On9022_AddsJournalNoteToContext()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = AppendOnRun(db, "SMIT", W2(description: "ACME"));
            using var mgr = NewEnabled(db, runner, _dir);
            mgr.DetailJournalAppender = (_, __) => throw new UnauthorizedAccessException("denied");
            mgr.DetailJournalLog = _ => { };

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteDetail("SMIT", W2()));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, ex.StatusCode);
            Assert.Contains("denied", (string)ex.Context["journal"]!);
        }

        [Fact]
        public void JournalNaming_RoundTrips()
        {
            var snap = Path.Combine(_dir, "20261118-101500-I24-SMIT.json");
            var journal = DetailWriteJournal.JournalPathFor(snap);
            Assert.Equal(Path.Combine(_dir, "20261118-101500-I24-SMIT.journal.jsonl"), journal);
            Assert.Equal(snap, DetailWriteJournal.SnapshotPathFor(journal));
            Assert.Equal(Path.Combine(_dir, "20261118-101500-I24-SMIT.journal.restored.jsonl"), DetailWriteJournal.RestoredPathFor(journal));
        }

        [Fact]
        public void JournalRead_SkipsTornLines_AndReportsThem()
        {
            var path = Path.Combine(_dir, "x.journal.jsonl");
            var good = new DetailJournalEntry
            {
                EntryId = "a1", ClientNumber = "SMIT", DataPath = DataPath, TaxYear = 2024, Module = "I",
                Row = W2(), Outcome = DetailWriteJournal.OutcomeVerified,
            };
            DetailWriteJournal.AppendLine(path, DetailWriteJournal.SerializeLine(good));
            File.AppendAllText(path, "{\"kind\":\"lacerte-mcp detail-write jour");
            var problems = new List<string>();
            var entries = DetailWriteJournal.Read(path, problems);
            Assert.Single(entries);
            Assert.Equal(W2(), entries[0].Row);
            Assert.Single(problems);
        }
    }
}
