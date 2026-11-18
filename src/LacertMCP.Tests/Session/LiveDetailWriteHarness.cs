using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Reflection;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// FP-2 test harness: a fake ODBC detail table (answers the read path's
    /// <c>select ... from [X-Detail]</c> from an in-memory list) plus a fake
    /// out-of-process worker runner, so live detail writes are exercised end
    /// to end without the Lacerte driver and without spawning a process.
    /// </summary>
    internal sealed class FakeDetailDb
    {
        /// <summary>clientKey -> rows. A client missing from the map has no detail table (42S02-like failure).</summary>
        public readonly Dictionary<string, List<DetailRow>> Tables =
            new Dictionary<string, List<DetailRow>>(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> Sql = new List<string>();
        public int ConnectionsOpened;

        /// <summary>When set, every query throws this (after recording the SQL).</summary>
        public Func<Exception?>? FailReads;

        public List<DetailRow> For(string client)
        {
            if (!Tables.TryGetValue(client, out var l))
            {
                l = new List<DetailRow>();
                Tables[client] = l;
            }
            return l;
        }

        public LacerteDsnReader Factory(string cs)
        {
            ConnectionsOpened++;
            var ctor = typeof(LacerteDsnReader).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(Func<string, DbConnection>) }, null);
            return (LacerteDsnReader)ctor!.Invoke(new object[] { cs, (Func<string, DbConnection>)(_ => new Conn(this)) });
        }

        private DataTable Answer(string sql)
        {
            Sql.Add(sql);
            var fail = FailReads?.Invoke();
            if (fail != null) throw fail;
            var start = sql.IndexOf('[');
            var end = sql.IndexOf("-Detail]", StringComparison.Ordinal);
            if (start < 0 || end < 0) return new DataTable();
            var client = sql.Substring(start + 1, end - start - 1);
            if (!Tables.TryGetValue(client, out var rows))
            {
                throw new LacerteSDKError(StatusCodes.DBNotSupported,
                    new Dictionary<string, object?> { ["sql"] = sql });
            }
            var t = new DataTable();
            foreach (var c in new[] { "Series", "Prefix", "Code", "Suffix", "StateAbbr", "CityAbbr", "Source", "TSJ", "ImportSrc", "Amount", "Description" })
            {
                t.Columns.Add(c, typeof(object));
            }
            foreach (var r in rows)
            {
                // Driver-like shapes: numeric columns as decimal, CHAR padded.
                t.Rows.Add(
                    Num(r.Series), Num(r.Prefix), Num(r.Code), Num(r.Suffix),
                    Pad(r.StateAbbr, 2), Pad(r.CityAbbr, 3), Pad(r.Source, 10), Pad(r.TSJ, 10), Pad(r.ImportSrc, 10),
                    r.Amount.HasValue ? (object)(decimal)r.Amount.Value : DBNull.Value,
                    Pad(r.Description, 40));
            }
            return t;
        }

        private static object Num(string s)
            => string.IsNullOrWhiteSpace(s) ? (object)0m : decimal.Parse(s, CultureInfo.InvariantCulture);

        private static object Pad(string s, int n) => (s ?? string.Empty).PadRight(Math.Max(n, (s ?? string.Empty).Length));

        private sealed class Conn : DbConnection
        {
            private readonly FakeDetailDb _db;
            private ConnectionState _state = ConnectionState.Closed;
            public Conn(FakeDetailDb db) { _db = db; }
            public override string ConnectionString { get; set; } = string.Empty;
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "0";
            public override ConnectionState State => _state;
            public override void ChangeDatabase(string databaseName) { }
            public override void Close() { _state = ConnectionState.Closed; }
            public override void Open() { _state = ConnectionState.Open; }
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
            protected override DbCommand CreateDbCommand() => new Cmd(_db);
        }

        private sealed class Cmd : DbCommand
        {
            private readonly FakeDetailDb _db;
            public Cmd(FakeDetailDb db) { _db = db; }
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery() => throw new NotSupportedException("the server never executes non-queries in-process");
            public override object? ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _db.Answer(CommandText).CreateDataReader();
        }
    }

    /// <summary>Fake worker runner: records calls; behavior scripted per test.</summary>
    internal sealed class FakeDetailWriteRunner : IDetailWriteRunner
    {
        public readonly List<(string ConnectionString, string Sql, TimeSpan Timeout)> Calls =
            new List<(string, string, TimeSpan)>();

        /// <summary>Default: report success without changing anything.</summary>
        public Func<string, DetailWriteRunResult> Behavior =
            _ => new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1, ExitCode = 0 };

        public DetailWriteRunResult Run(string connectionString, string sql, TimeSpan timeout)
        {
            Calls.Add((connectionString, sql, timeout));
            return Behavior(sql);
        }
    }

    /// <summary>Minimal live bridge for the session's test ctor; detail writes never reach it.</summary>
    internal sealed class DetailTestBridge : ILacerteBridge
    {
        public int WriteDetailCalls;
        public int Register(string appName, string appId, int taxYear) => StatusCodes.AccessGranted;
        public void Dispose() { }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(string dataPath, int year, char module) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, object?>? ReadClient(string dataPath, int year, char module, string clientNumber) => throw new NotSupportedException();
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(string dataPath, int year, char module, int table, string? clientNumber = null, string? stateAbbrev = null) => throw new NotSupportedException();
        public bool CanUseNewApis(string appId, int year, char module) => true;
        public int WriteField(string appId, string dataPath, int year, char module, int table, int rowNum, string field, object? value) => StatusCodes.Success;
        public int UpdateTaxReturn(string appId, string dataPath, int year, char module, int table, string clientNumber, string field, object? value, int? subClientId, string? stateAbbrev) => StatusCodes.Success;
        public int WriteDetail(string appId, string dataPath, int year, char module, string clientNumber, DetailRow row, int? replaceIndex)
        {
            WriteDetailCalls++;
            return StatusCodes.Success;
        }
        public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module, string clientId, string taxType, string? firstName, string? lastName, string? emailId)
            => new BridgeCreateClientResult(StatusCodes.Success, clientId);
    }

    internal static class LiveDetailWriteHarness
    {
        public const string DataPath = @"C:\Lacerte\24Tax\IData";

        public static LacerteSessionManager NewLive(IDictionary<string, string?>? env = null, ILacerteBridge? bridge = null)
        {
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) }, null);
            var mgr = (LacerteSessionManager)ctor!.Invoke(new object?[] { bridge ?? new DetailTestBridge(), env });
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(DataPath, 2024, 'I');
            return mgr;
        }

        /// <summary>Live manager with detail writes ON, the fake DB, the fake runner and a temp backup dir.</summary>
        public static LacerteSessionManager NewEnabled(FakeDetailDb db, FakeDetailWriteRunner runner, string snapshotDir)
        {
            var mgr = NewLive();
            mgr.LiveDetailWritesEnabled = true;
            mgr.LiveReaderFactory = db.Factory;
            mgr.DetailWriteRunner = runner;
            mgr.DetailSnapshotDirectory = snapshotDir;
            SeedVerifiedDetailMapping(snapshotDir);
            return mgr;
        }

        /// <summary>
        /// Live detail writes only target Series/Code the verified field
        /// dictionary lists (R8-review gate). Point the loader (AsyncLocal test
        /// seam — this runs synchronously in the test body, so it flows into the
        /// awaited calls) at a dictionary that maps the harness's W-2 row
        /// (Series 11, Code 1) for module I. Lives under the snapshot dir's
        /// "dict" subfolder so the test's cleanup removes it.
        /// </summary>
        public static void SeedVerifiedDetailMapping(string snapshotDir)
        {
            // Some tests deliberately pass a FILE as the snapshot dir; keep the
            // dictionary beside it then.
            var root = System.IO.File.Exists(snapshotDir) ? snapshotDir + ".dict" : System.IO.Path.Combine(snapshotDir, "dict");
            System.IO.Directory.CreateDirectory(root);
            var entry = new LacertMCP.Server.Schema.FieldDictionaryEntry("1040.w2_wages", "W-2 wages", "1040", "1a",
                LacertMCP.Server.Schema.FieldDictionaryLoader.KindDetail, null, null, 11, 1, null, null, false, false,
                "amount", null, null, "TY2024 on TEST-PC", LacertMCP.Server.Schema.FieldDictionaryLoader.SourceManual);
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "individual.json"),
                LacertMCP.Server.Schema.FieldDictionaryLoader.Serialize('I', new[] { entry }));
            LacertMCP.Server.Schema.FieldDictionaryLoader.RootOverrideForTests.Value = root;
            LacertMCP.Server.Schema.FieldDictionaryLoader.ReloadCaches();
        }

        /// <summary>Runner behavior that "inserts" <paramref name="row"/> into the fake table, then reports success.</summary>
        public static Func<string, DetailWriteRunResult> AppendOnRun(FakeDetailDb db, string client, DetailRow row)
            => _ =>
            {
                db.For(client).Add(row);
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1, ExitCode = 0 };
            };

        public static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lacerte-fp2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static DetailRow W2(double amount = 52000.50, string description = "ACME W-2", string tsj = "Taxpayer")
            => new DetailRow("11", "", "1", "1000", "", "", "Source", tsj, "", amount, description);
    }
}
