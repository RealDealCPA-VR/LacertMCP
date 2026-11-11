using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Live-mode read path (LacerteSessionManager.Live.cs) and write read-back
    /// verification (LacerteSessionManager.Verify.cs), driven through a fake
    /// ODBC connection that answers SQL with canned DataTables. These pin the
    /// shape contract (trimmed DBF strings, camelCase meta keys, sim-identical
    /// filters) — the real driver's column names still need a live-box pass.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class LiveReadPathTests
    {
        private const string DataPath = @"C:\Lacerte\24Tax\IData";

        // ---------------------------------------------------------------
        // Fake ODBC: SQL text -> DataTable
        // ---------------------------------------------------------------

        private sealed class FakeDb
        {
            public readonly List<string> Sql = new List<string>();
            public int ConnectionsOpened;
            public Func<string, DataTable> Answer = _ => new DataTable();

            public LacerteDsnReader Factory(string cs)
            {
                ConnectionsOpened++;
                var ctor = typeof(LacerteDsnReader).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(Func<string, DbConnection>) }, null);
                return (LacerteDsnReader)ctor!.Invoke(new object[] { cs, (Func<string, DbConnection>)(_ => new Conn(this)) });
            }

            private sealed class Conn : DbConnection
            {
                private readonly FakeDb _db;
                private ConnectionState _state = ConnectionState.Closed;
                public Conn(FakeDb db) { _db = db; }
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
                private readonly FakeDb _db;
                public Cmd(FakeDb db) { _db = db; }
                public override string CommandText { get; set; } = string.Empty;
                public override int CommandTimeout { get; set; }
                public override CommandType CommandType { get; set; }
                public override bool DesignTimeVisible { get; set; }
                public override UpdateRowSource UpdatedRowSource { get; set; }
                protected override DbConnection? DbConnection { get; set; }
                protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
                protected override DbTransaction? DbTransaction { get; set; }
                public override void Cancel() { }
                public override int ExecuteNonQuery() => throw new NotSupportedException();
                public override object? ExecuteScalar() => throw new NotSupportedException();
                public override void Prepare() { }
                protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
                protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
                {
                    _db.Sql.Add(CommandText);
                    return _db.Answer(CommandText).CreateDataReader();
                }
            }
        }

        private sealed class StubBridge : ILacerteBridge
        {
            public bool NewApis = true;
            public int Register(string appName, string appId, int taxYear) => StatusCodes.AccessGranted;
            public void Dispose() { }
            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(string dataPath, int year, char module) => throw new NotSupportedException("reads go through ODBC");
            public IReadOnlyDictionary<string, object?>? ReadClient(string dataPath, int year, char module, string clientNumber) => throw new NotSupportedException("reads go through ODBC");
            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(string dataPath, int year, char module, int table, string? clientNumber = null, string? stateAbbrev = null) => throw new NotSupportedException("reads go through ODBC");
            public bool CanUseNewApis(string appId, int year, char module) => NewApis;
            public int WriteField(string appId, string dataPath, int year, char module, int table, int rowNum, string field, object? value) => StatusCodes.Success;
            public int UpdateTaxReturn(string appId, string dataPath, int year, char module, int table, string clientNumber, string field, object? value, int? subClientId, string? stateAbbrev) => StatusCodes.Success;
            public int WriteDetail(string appId, string dataPath, int year, char module, string clientNumber, DetailRow row, int? replaceIndex) => StatusCodes.Success;
            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module, string clientId, string taxType, string? firstName, string? lastName, string? emailId)
                => new BridgeCreateClientResult(StatusCodes.Success, clientId);
        }

        private static LacerteSessionManager NewLive(FakeDb db, StubBridge? bridge = null)
        {
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) }, null);
            var mgr = (LacerteSessionManager)ctor!.Invoke(new object?[] { bridge ?? new StubBridge(), null });
            mgr.SetRegisteredForTest(true);
            mgr.LiveReaderFactory = db.Factory;
            mgr.SetContext(DataPath, 2024, 'I');
            return mgr;
        }

        private static DataTable Table(string[] cols, params object?[][] rows)
        {
            var t = new DataTable();
            foreach (var c in cols) t.Columns.Add(c, typeof(object));
            foreach (var r in rows) t.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
            return t;
        }

        // ---------------------------------------------------------------
        // Reads
        // ---------------------------------------------------------------

        [Fact]
        public async Task ListClients_Live_TrimsDbfPadding_AndAddsClientNumber()
        {
            var db = new FakeDb
            {
                Answer = _ => Table(new[] { "C1_0", "C1_1", "C1_2" },
                    new object?[] { "SMIT    ", "John      ", "Smith     " },
                    new object?[] { "JONE    ", "Ann       ", "Jones     " }),
            };
            using var mgr = NewLive(db);

            var rows = await mgr.ListClients(limit: 10);

            Assert.Equal(2, rows.Count);
            Assert.Equal("SMIT", rows[0]["clientNumber"]);
            Assert.Equal("Smith", rows[0]["C1_2"]);
            Assert.Contains("top 10", db.Sql.Single());
        }

        [Fact]
        public async Task ListClients_Live_NameFilter_IsCaseInsensitive_InMemory_LikeSim()
        {
            var db = new FakeDb
            {
                Answer = _ => Table(new[] { "C1_0", "C1_2" },
                    new object?[] { "SMIT", "SMITH" },
                    new object?[] { "JONE", "Jones" },
                    new object?[] { "SMI2", "smithers" }),
            };
            using var mgr = NewLive(db);

            var rows = await mgr.ListClients(lastNameFilter: "smi", limit: 10);

            Assert.Equal(new[] { "SMIT", "SMI2" }, rows.Select(r => (string)r["clientNumber"]!).ToArray());
            Assert.DoesNotContain(db.Sql, s => s.Contains("like"));
        }

        [Fact]
        public async Task ReadClient_Live_NormalizesKey_AndReturnsNullWhenAbsent()
        {
            var db = new FakeDb { Answer = _ => Table(new[] { "C1_0" }) };
            using var mgr = NewLive(db);

            var row = await mgr.ReadClient(" smit ");

            Assert.Null(row);
            Assert.Contains("C1_0 = 'SMIT'", db.Sql.Single());
        }

        [Fact]
        public async Task ReadClient_Live_RejectsInjectionShapedClientNumber()
        {
            using var mgr = NewLive(new FakeDb());
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.ReadClient("X' OR '1'='1"));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        [Fact]
        public async Task ReadTable_Live_State_NewForm_FiltersAndStampsMeta()
        {
            var db = new FakeDb
            {
                Answer = _ => Table(new[] { "C3_G1", "C3_5" },
                    new object?[] { "CA", "x" },
                    new object?[] { "NY", "y" }),
            };
            using var mgr = NewLive(db);

            var rows = await mgr.ReadTable(3, "ACME", "ny");

            var r = Assert.Single(rows);
            Assert.Equal("NY", r["stateAbbrev"]);
            Assert.Equal("ACME", r["clientNumber"]);
            Assert.Contains("[ACME-StateClientInfo]", db.Sql.Single());
        }

        [Fact]
        public async Task ReadTable_Live_PerClientTable_WithoutClient_IsStructuredError()
        {
            using var mgr = NewLive(new FakeDb());
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.ReadTable(2));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        [Fact]
        public async Task ReadDetail_Live_MapsNumericColumnsToSimShape()
        {
            var db = new FakeDb
            {
                Answer = _ => Table(
                    new[] { "Series", "Prefix", "Code", "Suffix", "StateAbbr", "CityAbbr", "Source", "TSJ", "ImportSrc", "Amount", "Description" },
                    new object?[] { 11m, "  ", 1m, "", "", "", "", "T ", "", 52000.50m, "Wages      " }),
            };
            using var mgr = NewLive(db);

            var rows = await mgr.ReadDetail("SMIT", seriesFilter: 11);

            var d = Assert.Single(rows);
            Assert.Equal("11", d.Series);
            Assert.Equal("1", d.Code);
            Assert.Equal("T", d.TSJ);
            Assert.Equal(52000.50, d.Amount);
            Assert.Equal("Wages", d.Description);
            Assert.Contains("Series = 11", db.Sql.Single());
        }

        [Fact]
        public async Task ReadEFStatus_Live_ProjectsSimKeys()
        {
            var db = new FakeDb
            {
                Answer = _ => Table(new[] { "ClientId", "EFilingId", "EFId", "InstanceId", "Status", "StatusDate" },
                    new object?[] { "SMIT  ", 1, "E1", "I1", "Accepted ", new DateTime(2025, 3, 1) }),
            };
            using var mgr = NewLive(db);

            var rows = await mgr.ReadEFStatus();

            var r = Assert.Single(rows);
            Assert.Equal(new[] { "ClientId", "EFilingId", "EFId", "InstanceId", "Status", "StatusDate" }, r.Keys.ToArray());
            Assert.Equal("SMIT", r["ClientId"]);
            Assert.Equal("Accepted", r["Status"]);
            Assert.Equal("2025-03-01", r["StatusDate"]);
        }

        // ---------------------------------------------------------------
        // Write read-back verification
        // ---------------------------------------------------------------

        [Fact]
        public async Task VerifyFieldWrite_Live_Match_UsesFreshConnection()
        {
            var db = new FakeDb { Answer = _ => Table(new[] { "C1_0", "C1_5" }, new object?[] { "SMIT", "555-1234     " }) };
            using var mgr = NewLive(db);
            await mgr.ListClients(limit: 1); // warms the cached reader
            var before = db.ConnectionsOpened;

            var v = await mgr.VerifyFieldWrite(1, "SMIT", "C1_5", "555-1234", null, null);

            Assert.True(v.Verified);
            Assert.Equal(before + 1, db.ConnectionsOpened);
        }

        [Fact]
        public async Task VerifyFieldWrite_Live_Mismatch_ReportsReadBack()
        {
            var db = new FakeDb { Answer = _ => Table(new[] { "C1_0", "C1_5" }, new object?[] { "SMIT", "OLD" }) };
            using var mgr = NewLive(db);

            var v = await mgr.VerifyFieldWrite(1, "SMIT", "C1_5", "NEW", null, null);

            Assert.False(v.Verified);
            Assert.Equal("OLD", v.ReadBack);
        }

        [Fact]
        public async Task VerifyFieldWrite_Live_MissingRow_IsNotVerified()
        {
            using var mgr = NewLive(new FakeDb { Answer = _ => Table(new[] { "C1_0" }) });
            var v = await mgr.VerifyFieldWrite(1, "SMIT", "C1_5", "x", null, null);
            Assert.False(v.Verified);
        }

        [Fact]
        public async Task VerifyFieldWrite_Live_AmbiguousSubclientRows_MakesNoClaim()
        {
            var db = new FakeDb { Answer = _ => Table(new[] { "C2_3" }, new object?[] { "a" }, new object?[] { "b" }) };
            using var mgr = NewLive(db);

            var v = await mgr.VerifyFieldWrite(2, "SMIT", "C2_3", "a", 1, null);

            Assert.Null(v.Verified);
            Assert.NotNull(v.Note);
        }

        [Theory]
        [InlineData("Smith", "Smith", true)]
        [InlineData("smith", "SMITH", true)]          // Lacerte upper-cases some fields
        [InlineData("1000", "1,000.00", true)]
        [InlineData("07001", "7001", false)]          // ZIP leading zero is significant
        [InlineData("123-45-6789", "123-45-6788", false)]
        [InlineData("01/02/1980", "1980-01-02", true)]
        [InlineData("01/02/1980", "1980-02-01", false)]
        [InlineData("", "", true)]
        [InlineData("x", "", false)]
        public void ValuesMatch_Cases(string written, string stored, bool expected)
        {
            Assert.Equal(expected, LacerteSessionManager.ValuesMatch(written, stored, out _));
        }

        [Fact]
        public void ValuesMatch_NumericWrite_AgainstDecimalColumn()
        {
            Assert.True(LacerteSessionManager.ValuesMatch(52000.5, 52000.50m, out _));
            Assert.False(LacerteSessionManager.ValuesMatch(52000.5, 52000.49m, out _));
        }

        [Fact]
        public void ValuesMatch_BoolWrite_AgainstCheckboxStorage()
        {
            Assert.True(LacerteSessionManager.ValuesMatch(true, "X", out _));
            Assert.True(LacerteSessionManager.ValuesMatch(false, "", out _));
            Assert.False(LacerteSessionManager.ValuesMatch(true, "", out _));
        }
    }
}
