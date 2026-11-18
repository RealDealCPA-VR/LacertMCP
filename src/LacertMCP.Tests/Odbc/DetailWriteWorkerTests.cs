using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Text;
using LacertMCP.Server.Odbc;
using Xunit;

namespace LacertMCP.Tests.Odbc
{
    /// <summary>
    /// FP-2 — the out-of-process detail-write worker: its stdin/stdout
    /// protocol (driven in-process with a fake connection) and one real spawn
    /// of the built server exe.
    /// </summary>
    public class DetailWriteWorkerTests
    {
        private const string GoodSql =
            "INSERT INTO [SMIT-Detail] (Series, Code, StateAbbr, CityAbbr, Source, TSJ, Amount, Description) " +
            "VALUES (11, 1, '', '', 'Source', 'Taxpayer', 52000.5, 'Ren\u00e9e''s W-2')";

        private sealed class FakeConn : DbConnection
        {
            public readonly List<string> Executed = new List<string>();
            public Func<int> NonQuery = () => 1;
            private ConnectionState _state = ConnectionState.Closed;
            public override string ConnectionString { get; set; } = string.Empty;
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "0";
            public override ConnectionState State => _state;
            public override void ChangeDatabase(string databaseName) { }
            public override void Close() { _state = ConnectionState.Closed; }
            public override void Open() { _state = ConnectionState.Open; }
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
            protected override DbCommand CreateDbCommand() => new FakeCmd(this);
        }

        private sealed class FakeCmd : DbCommand
        {
            private readonly FakeConn _conn;
            public FakeCmd(FakeConn conn) { _conn = conn; }
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery()
            {
                _conn.Executed.Add(CommandText);
                return _conn.NonQuery();
            }
            public override object? ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        }

        private static (int exit, DetailWriteResponse? response, string raw) RunWorker(byte[] stdin, Func<string, DbConnection> factory)
        {
            using var input = new MemoryStream(stdin);
            using var output = new MemoryStream();
            int exit = DetailWriteWorker.Run(input, output, factory);
            var raw = Encoding.UTF8.GetString(output.ToArray());
            return (exit, DetailWriteWorker.ParseResponse(raw), raw);
        }

        private static byte[] Request(string cs, string sql)
            => DetailWriteWorker.Serialize(new DetailWriteRequest { ConnectionString = cs, Sql = sql });

        [Fact]
        public void Worker_RunsTheInsert_WritesOneResultLine_Exit0()
        {
            var conn = new FakeConn();
            string? seenCs = null;
            var (exit, response, raw) = RunWorker(Request("Driver={X};DataDir={C:\\d}", GoodSql), cs => { seenCs = cs; return conn; });

            Assert.Equal(DetailWriteWorker.ExitOk, exit);
            Assert.NotNull(response);
            Assert.True(response!.Ok);
            Assert.Equal(1, response.RowsAffected);
            Assert.Equal("Driver={X};DataDir={C:\\d}", seenCs);
            Assert.Equal(GoodSql, Assert.Single(conn.Executed)); // non-ASCII + quotes survive the round trip
            Assert.Single(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        [Theory]
        [InlineData("DELETE FROM [SMIT-Detail]")]
        [InlineData("select * from [SMIT-Detail]")]
        [InlineData("INSERT INTO [DATA1-Client Info] (C1_0) VALUES ('X')")]
        [InlineData("UPDATE [SMIT-Detail] SET Amount = 0")]
        public void Worker_RefusesAnythingButADetailInsert_Exit2(string sql)
        {
            var conn = new FakeConn();
            var (exit, response, _) = RunWorker(Request("Driver={X}", sql), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitBadRequest, exit);
            Assert.False(response!.Ok);
            Assert.Empty(conn.Executed);
        }

        // ---- restore DELETE request type ---------------------------------------------------

        private const string GoodDelete =
            "DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' " +
            "AND Source = 'Source' AND TSJ = 'Taxpayer' AND Amount = 52000.5 AND Description = 'Renée''s W-2'";

        private static byte[] DeleteRequest(string cs, string sql, string? op = DetailWriteWorker.OpDeleteDetailRow)
            => DetailWriteWorker.Serialize(new DetailWriteRequest { ConnectionString = cs, Sql = sql, Op = op });

        [Fact]
        public void Worker_DeleteOp_RunsTheExactRestoreDelete_Exit0()
        {
            var conn = new FakeConn { NonQuery = () => 2 };
            var (exit, response, raw) = RunWorker(DeleteRequest("Driver={X}", GoodDelete), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitOk, exit);
            Assert.True(response!.Ok);
            Assert.Equal(2, response.RowsAffected);
            Assert.Equal(GoodDelete, Assert.Single(conn.Executed));
            Assert.Single(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        [Theory]
        // Not the exact BuildDeleteSql shape.
        [InlineData("DELETE FROM [SMIT-Detail]")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x'")] // no Suffix
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Code = 1 AND Series = 11 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x'")] // order
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 011 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x'")] // literal
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x' OR 1 = 1")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x'; DELETE FROM [SMIT-Detail]")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x' -- c")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x' AND ImportSrc = ''")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'T' AND Description = 'x'")] // TSJ not canonical
        [InlineData("delete from [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x'")]
        [InlineData("DELETE FROM [DATA1-Client Info] WHERE C1_0 = 'X'")]
        [InlineData("DELETE FROM [smit-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = 'Source' AND TSJ = 'Taxpayer' AND Description = 'x'")]
        // Not a DELETE at all.
        [InlineData(GoodSql)]
        [InlineData("UPDATE [SMIT-Detail] SET Amount = 0")]
        [InlineData("select * from [SMIT-Detail]")]
        public void Worker_DeleteOp_RefusesAnythingButTheExactShape_Exit2(string sql)
        {
            var conn = new FakeConn();
            var (exit, response, _) = RunWorker(DeleteRequest("Driver={X}", sql), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitBadRequest, exit);
            Assert.False(response!.Ok);
            Assert.Empty(conn.Executed);
        }

        [Fact]
        public void Worker_InsertPath_StillRefusesTheRestoreDelete_AndUnknownOps()
        {
            var conn = new FakeConn();
            // No op = the server's INSERT request: a DELETE is refused there.
            var (exit1, _, _) = RunWorker(Request("Driver={X}", GoodDelete), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitBadRequest, exit1);
            // Unknown op.
            var (exit2, r2, _) = RunWorker(DeleteRequest("Driver={X}", GoodSql, op: "insert"), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitBadRequest, exit2);
            Assert.Equal("Unknown request op.", r2!.Error);
            var (exit3, _, _) = RunWorker(DeleteRequest("Driver={X}", GoodDelete, op: "DELETEDETAILROW"), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitBadRequest, exit3);
            Assert.Empty(conn.Executed);
            // The INSERT still runs without an op.
            var (exit4, r4, _) = RunWorker(Request("Driver={X}", GoodSql), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitOk, exit4);
            Assert.True(r4!.Ok);
        }

        [Fact]
        public void Request_WithoutOp_SerializesWithoutOpKey()
        {
            var json = Encoding.UTF8.GetString(DetailWriteWorker.Serialize(new DetailWriteRequest { ConnectionString = "c", Sql = "s" }));
            Assert.DoesNotContain("\"op\"", json);
        }

        [Fact]
        public void ProcessRunner_RunDelete_MissingExe_IsNotStarted()
        {
            var r = new ProcessDetailWriteRunner(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".exe"))
                .RunDelete("Driver={X}", GoodDelete, TimeSpan.FromSeconds(5));
            Assert.Equal(DetailWriteRunStatus.NotStarted, r.Status);
        }

        [Fact]
        public void Worker_UnreadableRequest_Exit2()
        {
            var (exit, response, _) = RunWorker(Encoding.UTF8.GetBytes("not json"), _ => new FakeConn());
            Assert.Equal(DetailWriteWorker.ExitBadRequest, exit);
            Assert.False(response!.Ok);
        }

        [Fact]
        public void Worker_DriverThrows_ReportsError_Exit1()
        {
            var conn = new FakeConn { NonQuery = () => throw new InvalidOperationException("driver said no") };
            var (exit, response, _) = RunWorker(Request("Driver={X}", GoodSql), _ => conn);
            Assert.Equal(DetailWriteWorker.ExitFailed, exit);
            Assert.False(response!.Ok);
            Assert.Equal("driver said no", response.Error);
            Assert.Equal("InvalidOperationException", response.ErrorType);
        }

        [Fact]
        public void ParseResponse_IgnoresNoise_TakesLastResultLine()
        {
            var r = DetailWriteWorker.ParseResponse("driver banner\r\n{\"ok\":false}\n{\"ok\":true,\"rowsAffected\":1}\n");
            Assert.True(r!.Ok);
            Assert.Null(DetailWriteWorker.ParseResponse(""));
            Assert.Null(DetailWriteWorker.ParseResponse("Unhandled Exception: System.AccessViolationException"));
            Assert.Null(DetailWriteWorker.ParseResponse("{\"ok\":tru"));
        }

        [Fact]
        public void IsWorkerInvocation_OnlyTheExactFirstArg()
        {
            Assert.True(DetailWriteWorker.IsWorkerInvocation(new[] { "--detail-write-worker" }));
            Assert.False(DetailWriteWorker.IsWorkerInvocation(new[] { "--http", "--detail-write-worker" }));
            Assert.False(DetailWriteWorker.IsWorkerInvocation(Array.Empty<string>()));
            Assert.False(DetailWriteWorker.IsWorkerInvocation(null));
        }

        [Fact]
        public void ProcessRunner_MissingExe_IsNotStarted()
        {
            var r = new ProcessDetailWriteRunner(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".exe"))
                .Run("Driver={X}", GoodSql, TimeSpan.FromSeconds(5));
            Assert.Equal(DetailWriteRunStatus.NotStarted, r.Status);
        }

        /// <summary>
        /// Spawns the real built server exe in worker mode with a connection
        /// string naming a driver that doesn't exist. The worker must exit with
        /// a clean result (the ODBC driver manager's error), not crash, not
        /// hang, and not start the MCP host. Returns early (counts as passed)
        /// when the exe isn't next to the test assembly.
        /// </summary>
        [Fact]
        public void ProcessRunner_RealWorkerExe_InvalidConnectionString_CleanError()
        {
            var exe = ProcessDetailWriteRunner.DefaultExePath();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)
                || !exe.EndsWith("LacertMCP.Server.exe", StringComparison.OrdinalIgnoreCase))
            {
                return; // worker exe not available in this test layout
            }

            var runner = new ProcessDetailWriteRunner(exe);
            var r = runner.Run("Driver={No Such Lacerte Test Driver};DataDir={C:\\nowhere};TaxYear=2024;ReturnType=Individual",
                GoodSql, TimeSpan.FromSeconds(60));

            Assert.Equal(DetailWriteRunStatus.Failed, r.Status);
            Assert.Equal(DetailWriteWorker.ExitFailed, r.ExitCode);
            Assert.False(string.IsNullOrEmpty(r.Error));
            Assert.Equal("IM002", r.SqlState); // driver manager: data source / driver not found
        }

        /// <summary>Same as above for the restore DELETE: accepted by the real worker, fails cleanly at the driver manager.</summary>
        [Fact]
        public void ProcessRunner_RealWorkerExe_Delete_InvalidConnectionString_CleanError()
        {
            var exe = ProcessDetailWriteRunner.DefaultExePath();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)
                || !exe.EndsWith("LacertMCP.Server.exe", StringComparison.OrdinalIgnoreCase))
            {
                return; // worker exe not available in this test layout
            }

            var runner = new ProcessDetailWriteRunner(exe);
            var r = runner.RunDelete("Driver={No Such Lacerte Test Driver};DataDir={C:\\nowhere};TaxYear=2024;ReturnType=Individual",
                GoodDelete, TimeSpan.FromSeconds(60));
            Assert.Equal(DetailWriteRunStatus.Failed, r.Status);
            Assert.Equal("IM002", r.SqlState);

            var bad = runner.RunDelete("Driver={No Such Lacerte Test Driver}", "DELETE FROM [SMIT-Detail]", TimeSpan.FromSeconds(60));
            Assert.Equal(DetailWriteRunStatus.Failed, bad.Status);
            Assert.Equal(DetailWriteWorker.ExitBadRequest, bad.ExitCode);
            Assert.Equal("BadRequest", bad.ErrorType);
        }
    }
}
