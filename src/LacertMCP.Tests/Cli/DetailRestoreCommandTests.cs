using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Cli.Commands;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using LacertMCP.Tests.Session;
using Xunit;
using static LacertMCP.Tests.Session.LiveDetailWriteHarness;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// FP-2 restore — <c>lacerte-mcp detail-restore list | plan | apply</c>. Rows
    /// are written through the real live write path (fake ODBC + fake worker),
    /// so the backup and journal under test are exactly what the server writes;
    /// the restore then reads the same fake detail table and runs its DELETE /
    /// re-INSERTs through fake workers that act on it.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class DetailRestoreCommandTests : IDisposable
    {
        private const string Client = "SMIT";
        private readonly string _dir = NewTempDir();
        private readonly FakeDetailDb _db = new FakeDetailDb();
        private readonly FakeDetailWriteRunner _serverRunner = new FakeDetailWriteRunner();
        private readonly LacerteSessionManager _mgr;

        // Restore-side fakes.
        private readonly List<string> _deletes = new List<string>();
        private readonly List<string> _inserts = new List<string>();
        private Func<string, DetailWriteRunResult>? _deleteBehavior;
        private Func<string, int, DetailWriteRunResult>? _insertBehavior;

        public DetailRestoreCommandTests()
        {
            _db.For(Client);
            _mgr = NewEnabled(_db, _serverRunner, _dir);
        }

        public void Dispose()
        {
            _mgr.Dispose();
            TryDelete(_dir);
        }

        private static DetailRow A => W2(52000.5, "ACME W-2");
        private static DetailRow B => W2(10, "B CO");
        private static DetailRow Oper => W2(100, "ENTERED IN LACERTE");

        private List<DetailRow> Rows => _db.For(Client);

        /// <summary>Writes <paramref name="row"/> through the server's live path (journaled).</summary>
        private async Task<DetailWriteOutcome> ServerWrite(DetailRow row)
        {
            _serverRunner.Behavior = AppendOnRun(_db, Client, row);
            return await _mgr.WriteDetailWithOutcome(Client, row);
        }

        private string JournalOf(DetailWriteOutcome o) => DetailWriteJournal.JournalPathFor(o.Snapshot!);

        private sealed class ReaderSource : ISnapshotQuerySource
        {
            private readonly LacerteDsnReader _r;
            public ReaderSource(LacerteDsnReader r) { _r = r; }
            public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql) => _r.Query(sql);
            public void Dispose() => _r.Dispose();
        }

        private sealed class DelegateDeleteRunner : IDetailDeleteRunner
        {
            private readonly Func<string, DetailWriteRunResult> _f;
            public DelegateDeleteRunner(Func<string, DetailWriteRunResult> f) { _f = f; }
            public DetailWriteRunResult RunDelete(string connectionString, string deleteSql, TimeSpan timeout) => _f(deleteSql);
        }

        private sealed class DelegateInsertRunner : IDetailWriteRunner
        {
            private readonly Func<string, DetailWriteRunResult> _f;
            public DelegateInsertRunner(Func<string, DetailWriteRunResult> f) { _f = f; }
            public DetailWriteRunResult Run(string connectionString, string sql, TimeSpan timeout) => _f(sql);
        }

        private static DetailWriteRunResult Ok(int n = 1) => new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = n, ExitCode = 0 };

        /// <summary>Default driver: the worker must accept the DELETE; it removes every row equal on the written columns.</summary>
        private DetailWriteRunResult DriverDelete(string sql)
        {
            Assert.True(DetailWriteWorker.IsAllowedDelete(sql), "the worker would refuse: " + sql);
            Assert.True(DetailRowWriter.TryParseDeleteSql(sql, out var key, out var row));
            int n = _db.For(key).RemoveAll(r => DetailRowWriter.RowMatches(row!, r));
            return Ok(n);
        }

        /// <summary>Default driver INSERT: appends the row the SQL names (looked up among the known rows).</summary>
        private DetailWriteRunResult DriverInsert(string sql)
        {
            var row = new[] { A, B, Oper }.First(r => DetailRowWriter.BuildInsertSql(Client, r) == sql);
            Rows.Add(row);
            return Ok();
        }

        private DetailRestoreCommand.Deps Deps() => new DetailRestoreCommand.Deps
        {
            SourceFactory = cs => new ReaderSource(_db.Factory(cs)),
            DeleteRunner = new DelegateDeleteRunner(sql =>
            {
                _deletes.Add(sql);
                return (_deleteBehavior ?? DriverDelete)(sql);
            }),
            InsertRunner = new DelegateInsertRunner(sql =>
            {
                _inserts.Add(sql);
                return _insertBehavior != null ? _insertBehavior(sql, _inserts.Count) : DriverInsert(sql);
            }),
            Now = () => new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
        };

        private (int Exit, string Out, string Err) Plan(string journal)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = DetailRestoreCommand.RunPlan(journal, Deps(), o, e);
            return (exit, o.ToString(), e.ToString());
        }

        private (int Exit, string Out, string Err) Apply(string journal, bool confirm = true)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = DetailRestoreCommand.RunApply(journal, confirm, Deps(), o, e);
            return (exit, o.ToString(), e.ToString());
        }

        private static int Count(IEnumerable<DetailRow> rows, DetailRow r) => DetailRowWriter.CountMatching(rows, r);

        private static List<DetailRestoreCommand.RestoreRecord> Records(string journal)
            => File.Exists(DetailWriteJournal.RestoredPathFor(journal))
                ? File.ReadAllLines(DetailWriteJournal.RestoredPathFor(journal))
                    .Select(l => System.Text.Json.JsonSerializer.Deserialize<DetailRestoreCommand.RestoreRecord>(l)!).ToList()
                : new List<DetailRestoreCommand.RestoreRecord>();

        // ---- apply: happy path ------------------------------------------------------------------

        [Fact]
        public async Task Apply_RemovesOnlyJournaledRows_OneCopyEach_Verified_ThenSecondApplyIsNoOp()
        {
            Rows.Add(Oper);                       // in the backup: never removed
            var o = await ServerWrite(A);
            await ServerWrite(B);
            await ServerWrite(A);                 // a second copy of A, journaled twice
            Rows.Add(W2(77, "LATER IN LACERTE"));  // entered after the backup: not journaled
            var journal = JournalOf(o);

            var (exit, output, err) = Apply(journal);

            Assert.True(exit == 0, output + err);
            Assert.Equal(0, Count(Rows, A));
            Assert.Equal(0, Count(Rows, B));
            Assert.Equal(1, Count(Rows, Oper));
            Assert.Equal(1, Count(Rows, W2(77, "LATER IN LACERTE")));
            Assert.Equal(2, Rows.Count);

            // A had 2 copies: DELETE + 1 re-insert, then DELETE + 0; B: DELETE.
            Assert.Equal(new[]
            {
                DetailRowWriter.BuildDeleteSql(Client, A),
                DetailRowWriter.BuildDeleteSql(Client, B),
                DetailRowWriter.BuildDeleteSql(Client, A),
            }, _deletes);
            Assert.Equal(new[] { DetailRowWriter.BuildInsertSql(Client, A) }, _inserts);
            Assert.Contains("removed and verified", output);
            Assert.Contains("UNVERIFIED", output);
            Assert.Contains("Stop every agent", output);

            var recs = Records(journal);
            Assert.Equal(3, recs.Count(r => r.Result == DetailRestoreCommand.ResultRemoved));

            // Second apply: nothing to do, no worker runs.
            _deletes.Clear();
            _inserts.Clear();
            var (exit2, out2, err2) = Apply(journal);
            Assert.True(exit2 == 0, out2 + err2);
            Assert.Empty(_deletes);
            Assert.Empty(_inserts);
            Assert.Contains("Removed 0 journaled row(s)", out2);
            Assert.Equal(2, Rows.Count);
        }

        [Fact]
        public async Task Apply_RemovesExactlyOneOfNIdenticalCopies_KeepsTheBackupsCopies()
        {
            Rows.Add(A);
            Rows.Add(A);                          // two identical copies already in the backup
            var o = await ServerWrite(A);         // the MCP adds a third
            Assert.Equal(3, Count(Rows, A));

            var (exit, output, err) = Apply(JournalOf(o));

            Assert.True(exit == 0, output + err);
            Assert.Equal(2, Count(Rows, A));
            Assert.Single(_deletes);
            Assert.Equal(2, _inserts.Count);      // n = 3 → DELETE all, re-insert 2
            Assert.Contains("removing one of 3 copies", output);
            var rec = Assert.Single(Records(JournalOf(o)).Where(r => r.Result == DetailRestoreCommand.ResultRemoved));
            Assert.Equal(3, rec.CopiesBefore);
        }

        [Fact]
        public async Task Apply_WithoutConfirm_PrintsPlan_ChangesNothing()
        {
            var o = await ServerWrite(A);
            var (exit, output, _) = Apply(JournalOf(o), confirm: false);

            Assert.Equal(1, exit);
            Assert.Contains("Still present - would be removed (one copy each): 1", output);
            Assert.Contains("pass --confirm", output);
            Assert.Empty(_deletes);
            Assert.Empty(_inserts);
            Assert.Equal(1, Count(Rows, A));
            Assert.False(File.Exists(DetailWriteJournal.RestoredPathFor(JournalOf(o))));
        }

        // ---- plan ---------------------------------------------------------------------------------

        [Fact]
        public async Task Plan_ClassifiesPresentGoneDuplicatesAndEdited_ChangesNothing()
        {
            Rows.Add(B);                          // backup already has one B
            var o = await ServerWrite(A);
            await ServerWrite(B);                 // MCP adds a second B → present
            var c = W2(5, "C CO");
            await ServerWrite(c);
            await ServerWrite(W2(9, "D CO"));
            // Operator deletes C and edits D's amount in Lacerte.
            Rows.RemoveAll(r => DetailRowWriter.RowMatches(c, r));
            var di = Rows.FindIndex(r => DetailRowWriter.RowMatches(W2(9, "D CO"), r));
            Rows[di] = W2(19, "D CO");
            var before = Rows.ToList();

            var (exit, output, err) = Plan(JournalOf(o));

            Assert.True(exit == 0, err);
            Assert.Contains("Still present - would be removed (one copy each): 2", output);
            Assert.Contains("Already gone - nothing to do: 2", output);
            Assert.Contains("the backup already had 1 identical copy of this row; it stays.", output);
            Assert.Contains("2 identical copies now; apply leaves 1.", output);
            Assert.Contains("looks edited in Lacerte?", output);
            Assert.Contains("Amount 19", output);
            Assert.Contains("never removed", output);
            Assert.Empty(_deletes);
            Assert.Empty(_inserts);
            Assert.Equal(before, Rows);
        }

        [Fact]
        public async Task Plan_RowRemovedByOperator_ButBackupCopyRemains_IsGone()
        {
            Rows.Add(A);                          // backup copy
            var o = await ServerWrite(A);
            Rows.Remove(Rows.First(r => DetailRowWriter.RowMatches(A, r))); // operator removed one copy
            var (_, output, _) = Plan(JournalOf(o));
            Assert.Contains("Still present - would be removed (one copy each): 0", output);
            Assert.Contains("Already gone - nothing to do: 1", output);

            var (exit, _, _) = Apply(JournalOf(o));
            Assert.Equal(0, exit);
            Assert.Empty(_deletes);
            Assert.Equal(1, Count(Rows, A));
            Assert.Equal(DetailRestoreCommand.ResultAlreadyGone, Assert.Single(Records(JournalOf(o))).Result);
        }

        [Fact]
        public async Task Plan_RowTheDeleteCouldOverMatch_IsNotRemovable_ApplySkipsIt()
        {
            var o = await ServerWrite(W2(500, "ACME"));
            Rows.Add(W2(500, "ACME CORP"));       // operator row the driver could also match (prefix compare)

            var (_, plan, _) = Plan(JournalOf(o));
            Assert.Contains("NOT REMOVABLE", plan);

            var (exit, output, _) = Apply(JournalOf(o));
            Assert.Equal(1, exit);
            Assert.Contains("SKIPPED (not changed)", output);
            Assert.Empty(_deletes);
            Assert.Equal(2, Rows.Count);
            Assert.Empty(Records(JournalOf(o)));
        }

        [Fact]
        public async Task Apply_MissingBackup_Refuses_PlanWarns()
        {
            var o = await ServerWrite(A);
            File.Delete(o.Snapshot!);

            var (pExit, plan, _) = Plan(JournalOf(o));
            Assert.Equal(0, pExit);
            Assert.Contains("is missing - apply refuses without it", plan);

            var (exit, _, err) = Apply(JournalOf(o));
            Assert.Equal(1, exit);
            Assert.Contains("Refusing", err);
            Assert.Empty(_deletes);
        }

        [Fact]
        public void Plan_MissingJournal_Fails()
        {
            var (exit, _, err) = Plan(Path.Combine(_dir, "nope.journal.jsonl"));
            Assert.Equal(1, exit);
            Assert.Contains("Journal not found", err);
        }

        // ---- apply: failures ---------------------------------------------------------------------

        [Fact]
        public async Task Apply_DeleteFails_StopsSafely_NothingChanged_LaterRowsUntouched()
        {
            var o = await ServerWrite(A);
            await ServerWrite(B);
            _deleteBehavior = _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.Failed, SqlState = "HY000", Error = "driver said no" };

            var (exit, _, err) = Apply(JournalOf(o));

            Assert.Equal(1, exit);
            Assert.Contains("STOPPED", err);
            Assert.Contains("nothing was changed", err);
            Assert.Contains("driver said no", err);
            Assert.Single(_deletes);              // B never attempted
            Assert.Empty(_inserts);
            Assert.Equal(1, Count(Rows, A));
            Assert.Equal(1, Count(Rows, B));
            Assert.Equal(DetailRestoreCommand.ResultAborted, Records(JournalOf(o)).Last().Result);

            // The aborted entry is retried by the next apply.
            _deleteBehavior = null;
            var (exit2, out2, err2) = Apply(JournalOf(o));
            Assert.True(exit2 == 0, out2 + err2);
            Assert.Empty(Rows);
        }

        [Fact]
        public async Task Apply_DeleteRunsButMatchesNothing_StopsSafely()
        {
            var o = await ServerWrite(A);
            _deleteBehavior = _ => Ok(0);

            var (exit, _, err) = Apply(JournalOf(o));

            Assert.Equal(1, exit);
            Assert.Contains("removed nothing", err);
            Assert.Empty(_inserts);
            Assert.Equal(1, Count(Rows, A));
        }

        [Fact]
        public async Task Apply_DeleteRemovesAnotherRow_StopsAndSaysWhatToReEnter()
        {
            Rows.Add(Oper);
            var o = await ServerWrite(A);
            _deleteBehavior = sql =>
            {
                var r = DriverDelete(sql);
                Rows.RemoveAll(x => DetailRowWriter.RowMatches(Oper, x)); // driver over-matched
                return r;
            };

            var (exit, _, err) = Apply(JournalOf(o));

            Assert.Equal(1, exit);
            Assert.Contains("RE-ENTER in Lacerte (another row the DELETE removed)", err);
            Assert.Contains("ENTERED IN LACERTE", err);
            Assert.Empty(_inserts);
            Assert.Equal(DetailRestoreCommand.ResultStarted, Records(JournalOf(o)).Last().Result);
        }

        [Fact]
        public async Task Apply_ReinsertFails_StopsAndSaysHowManyCopiesToReEnter_ThenResumesAfterFix()
        {
            Rows.Add(A);                          // backup copy
            var o = await ServerWrite(A);          // n = 2
            await ServerWrite(B);
            _insertBehavior = (_, __) => new DetailWriteRunResult { Status = DetailWriteRunStatus.Crashed, ExitCode = -1 };

            var (exit, _, err) = Apply(JournalOf(o));

            Assert.Equal(1, exit);
            Assert.Contains("re-inserting copy 1 of 1 crashed", err);
            Assert.Contains("RE-ENTER in Lacerte: 1 copy of that row.", err);
            Assert.Equal(0, Count(Rows, A));
            Assert.Equal(1, Count(Rows, B));       // stopped before B
            Assert.Single(_deletes);
            Assert.Equal(DetailRestoreCommand.ResultStarted, Records(JournalOf(o)).Last().Result);

            // Plan flags the interrupted removal; apply refuses until the copy is re-entered.
            var (_, plan, _) = Plan(JournalOf(o));
            Assert.Contains("RE-ENTER 1 copy", plan);
            var (exitBlocked, _, errBlocked) = Apply(JournalOf(o));
            Assert.Equal(1, exitBlocked);
            Assert.Contains("interrupted", errBlocked);
            Assert.Single(_deletes);

            // Operator re-enters the backup's copy; the next apply marks A removed and goes on to B.
            Rows.Add(A);
            _insertBehavior = null;
            var (exit2, out2, err2) = Apply(JournalOf(o));
            Assert.True(exit2 == 0, out2 + err2);
            Assert.Contains("earlier interrupted apply had finished", out2);
            Assert.Equal(1, Count(Rows, A));
            Assert.Equal(0, Count(Rows, B));
        }

        [Fact]
        public async Task Apply_InterruptedBeforeTheDelete_IsRetried()
        {
            var o = await ServerWrite(A);
            var journal = JournalOf(o);
            var entry = DetailWriteJournal.Read(journal, new List<string>()).Single();
            DetailWriteJournal.AppendLine(DetailWriteJournal.RestoredPathFor(journal), DetailWriteJournal.SerializeLine(
                new DetailRestoreCommand.RestoreRecord { EntryId = entry.EntryId, Result = DetailRestoreCommand.ResultStarted, CopiesBefore = 1 }));

            var (exit, output, err) = Apply(journal);

            Assert.True(exit == 0, output + err);
            Assert.Equal(0, Count(Rows, A));
            Assert.Single(_deletes);
        }

        [Fact]
        public async Task Apply_ReadFailsBeforeAnything_ChangesNothing()
        {
            var o = await ServerWrite(A);
            _db.FailReads = () => new LacerteSDKError(StatusCodes.DBFNotAccessable);

            var (exit, _, err) = Apply(JournalOf(o));

            Assert.Equal(1, exit);
            Assert.Contains("Nothing was changed", err);
            Assert.Empty(_deletes);
        }

        // ---- list ---------------------------------------------------------------------------------

        [Fact]
        public async Task List_ShowsBackupsAndJournals_IgnoresOtherJson()
        {
            Rows.Add(Oper);
            var o = await ServerWrite(A);
            await ServerWrite(B);
            File.WriteAllText(Path.Combine(_dir, "other.json"), "{\"kind\":\"something else\"}");

            var outW = new StringWriter();
            var exit = DetailRestoreCommand.RunList(_dir, new Dictionary<string, string?>(), outW, new StringWriter());
            var output = outW.ToString();

            Assert.Equal(0, exit);
            Assert.Contains(Path.GetFileNameWithoutExtension(o.Snapshot!), output);
            Assert.Contains("client SMIT, module I, TY2024", output);
            Assert.Contains(DataPath, output);
            Assert.Contains("1 row(s)", output);
            Assert.Contains("journal: 2 row(s) written by the MCP, 0 already restored/gone", output);
            Assert.DoesNotContain("other", output);
        }

        [Fact]
        public void List_DefaultDirFromEnv_AndMissingFolder()
        {
            var missing = Path.Combine(_dir, "none");
            var outW = new StringWriter();
            var exit = DetailRestoreCommand.RunList(null,
                new Dictionary<string, string?> { ["LACERTE_DETAIL_SNAPSHOT_DIR"] = missing }, outW, new StringWriter());
            Assert.Equal(0, exit);
            Assert.Contains(missing, outW.ToString());
            Assert.Contains("does not exist", outW.ToString());

            var errW = new StringWriter();
            Assert.Equal(1, DetailRestoreCommand.RunList(null, new Dictionary<string, string?>(), new StringWriter(), errW));
            Assert.Contains("No backup folder", errW.ToString());
        }

        [Fact]
        public void Command_IsRegistered_WithListPlanApply()
        {
            var cmd = DetailRestoreCommand.Build();
            Assert.Equal("detail-restore", cmd.Name);
            Assert.Equal(new[] { "list", "plan", "apply" }, cmd.Subcommands.Select(s => s.Name).ToArray());
        }
    }
}
