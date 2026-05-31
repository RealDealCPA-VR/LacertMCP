using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Linq;
using System.Reflection;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using Xunit;

namespace LacertMCP.Tests.Odbc
{
    /// <summary>
    /// P15-045 — connection recovery + Ping tests for
    /// <see cref="LacerteDsnReader"/>.
    ///
    /// <para>
    /// Uses an internal <see cref="DbConnection"/> factory seam to inject a
    /// fake that throws <see cref="OdbcException"/>-shaped errors without
    /// going through the real ODBC driver. The <see cref="OdbcException"/>
    /// ctor is internal, so we mint one via reflection — mirrors the
    /// note in <c>OdbcErrorMapperTests</c>.
    /// </para>
    /// </summary>
    public class LacerteDsnReaderRecoveryTests
    {
        // -------------------------------------------------------------------
        // IsConnectionFatal predicate
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("08001")]
        [InlineData("08003")]
        [InlineData("08004")]
        [InlineData("08S01")]
        [InlineData("IM002")]
        [InlineData("HY000")]
        [InlineData("HY010")]
        [InlineData("S1000")]
        public void IsConnectionFatal_TrueForConnectionFamilies(string sqlState)
        {
            Assert.True(OdbcErrorMapper.IsConnectionFatal(sqlState));
        }

        [Theory]
        [InlineData("42S02")]
        [InlineData("42S22")]
        [InlineData("42000")]
        [InlineData("")]
        [InlineData(null)]
        public void IsConnectionFatal_FalseForNonConnectionStates(string? sqlState)
        {
            Assert.False(OdbcErrorMapper.IsConnectionFatal(sqlState));
        }

        // -------------------------------------------------------------------
        // Connection recycling — fatal SQLSTATE should dispose+null the
        // cached connection so the NEXT Query() opens fresh. Non-fatal
        // SQLSTATE leaves the cached connection in place.
        // -------------------------------------------------------------------

        [Fact]
        public void Query_OnConnectionFatalSqlState_RecyclesConnection()
        {
            var factory = new FakeConnectionFactory();
            // First connection throws on Open? No — Open succeeds, but
            // ExecuteReader on the first Query throws a HY000 (driver-side error).
            factory.NextConnection.CommandThrowsOnExecute = MakeOdbc("HY000", "driver dead");

            using var reader = new LacerteDsnReader("Driver={X}", factory.Create);

            Assert.Throws<LacerteSDKError>(() => reader.Query("select 1"));

            // After the throw the cached connection must be nulled so a
            // subsequent call goes through the factory again.
            Assert.False(reader.HasOpenConnection,
                "cached connection should be recycled on connection-fatal SQLSTATE");

            // Second Query opens a fresh connection — assert factory was
            // called twice total.
            factory.NextConnection.CommandThrowsOnExecute = null; // succeed now
            var rows = reader.Query("select 2");
            Assert.Empty(rows);
            Assert.Equal(2, factory.ConnectionsCreated);
        }

        [Fact]
        public void Query_OnNonFatalSqlState_KeepsConnection()
        {
            var factory = new FakeConnectionFactory();
            // 42S02 = table not found — diagnostic-level, NOT connection fatal.
            factory.NextConnection.CommandThrowsOnExecute = MakeOdbc("42S02", "no such table");

            using var reader = new LacerteDsnReader("Driver={X}", factory.Create);

            Assert.Throws<LacerteSDKError>(() => reader.Query("select 1"));

            // Connection stays cached — operator's TABLE typo shouldn't tear
            // down a fresh link to the data store.
            Assert.True(reader.HasOpenConnection,
                "cached connection should NOT be recycled on diagnostic SQLSTATE");
            Assert.Equal(1, factory.ConnectionsCreated);
        }

        // -------------------------------------------------------------------
        // Ping helper
        // -------------------------------------------------------------------

        [Fact]
        public void Ping_ReturnsTrue_OnSuccessfulProbe()
        {
            var factory = new FakeConnectionFactory();
            // Default fake command returns an empty reader — Ping just needs
            // the query to round-trip without throwing.
            using var reader = new LacerteDsnReader("Driver={X}", factory.Create);

            Assert.True(reader.Ping());
        }

        [Fact]
        public void Ping_ReturnsFalse_OnConnectionFatalProbeFailure()
        {
            var factory = new FakeConnectionFactory();
            factory.NextConnection.CommandThrowsOnExecute = MakeOdbc("08S01", "link down");

            using var reader = new LacerteDsnReader("Driver={X}", factory.Create);

            Assert.False(reader.Ping());
            Assert.False(reader.HasOpenConnection,
                "Ping must recycle on connection-fatal SQLSTATE so next call opens fresh");
        }

        [Fact]
        public void Ping_ReturnsFalse_ButLeavesConnection_OnDiagnosticFailure()
        {
            // 42S22 = column not found. If somehow the Lacerte schema changes
            // and C1_0 disappears, Ping returns false but doesn't tear down
            // the connection — diagnostic vs fatal distinction holds.
            var factory = new FakeConnectionFactory();
            factory.NextConnection.CommandThrowsOnExecute = MakeOdbc("42S22", "no such column");

            using var reader = new LacerteDsnReader("Driver={X}", factory.Create);

            Assert.False(reader.Ping());
            Assert.True(reader.HasOpenConnection,
                "non-fatal probe failure should leave the connection cached");
        }

        [Fact]
        public void Ping_AfterDispose_ReturnsFalse()
        {
            var factory = new FakeConnectionFactory();
            var reader = new LacerteDsnReader("Driver={X}", factory.Create);
            reader.Dispose();
            Assert.False(reader.Ping());
        }

        // -------------------------------------------------------------------
        // Helpers — fake DbConnection chain + OdbcException reflection ctor
        // -------------------------------------------------------------------

        private static OdbcException MakeOdbc(string sqlState, string message)
        {
            // OdbcException + OdbcError + OdbcErrorCollection all have
            // internal ctors with shifting signatures across .NET Framework
            // patch releases. Bypass the ctors entirely via
            // FormatterServices.GetUninitializedObject and inject field
            // values directly — the only thing LacerteDsnReader.TryRecycle
            // needs is `ex.Errors[0].SQLState` to round-trip.

            // 1) Construct an uninitialized OdbcError and set its private
            //    fields. The SQLState backing field name varies across .NET
            //    FX patch releases ("_state" / "state" / "_sqlState"). We
            //    only NEED state to round-trip — message/source/nativeerror
            //    are nice-to-have. Try common names; fall back to probing
            //    by FieldType=string and matching the SQLState property.
            var errorType = typeof(OdbcError);
            var odbcError = (OdbcError)System.Runtime.Serialization
                .FormatterServices.GetUninitializedObject(errorType);
            TrySetStringField(odbcError, message, new[] { "_message", "message" });
            TrySetStringField(odbcError, "test", new[] { "_source", "source" });
            // SQLState is the load-bearing one — verify after set.
            TrySetStringField(odbcError, sqlState, new[] { "_state", "state", "_sqlState" });
            // Defensive: if the property still doesn't read back, brute-force
            // scan all string-typed instance fields and set them all to the
            // SQLState. The mapper only consults SQLState.
            if (odbcError.SQLState != sqlState)
            {
                foreach (var f in errorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(f => f.FieldType == typeof(string)))
                {
                    if (f.GetValue(odbcError) == null) f.SetValue(odbcError, sqlState);
                }
            }
            Assert.Equal(sqlState, odbcError.SQLState);

            // 2) Construct an uninitialized OdbcErrorCollection and stuff
            //    the single OdbcError into its private ArrayList. Field
            //    name varies (typically "_items"); probe by FieldType.
            var collectionType = typeof(OdbcErrorCollection);
            var coll = System.Runtime.Serialization
                .FormatterServices.GetUninitializedObject(collectionType);
            var itemsField = collectionType.GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(System.Collections.ArrayList));
            Assert.NotNull(itemsField);
            var list = new System.Collections.ArrayList { odbcError };
            itemsField!.SetValue(coll, list);

            // 3) Construct an uninitialized OdbcException and set its
            //    errors collection. Field name varies across .NET FX patch
            //    releases (sometimes "_errors", sometimes "errors", and on
            //    some builds the collection lives on the inherited
            //    DbException). Probe to find the FieldInfo whose type is
            //    OdbcErrorCollection regardless of name.
            var exType = typeof(OdbcException);
            var ex = (OdbcException)System.Runtime.Serialization
                .FormatterServices.GetUninitializedObject(exType);
            FieldInfo? errorsField = null;
            for (var t = exType; t != null && errorsField == null; t = t.BaseType)
            {
                errorsField = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(f => f.FieldType == collectionType);
            }
            Assert.NotNull(errorsField);
            errorsField!.SetValue(ex, coll);
            // Exception._message is the backing field for Exception.Message.
            var messageField = typeof(Exception).GetField("_message",
                BindingFlags.Instance | BindingFlags.NonPublic);
            messageField?.SetValue(ex, message);
            return ex;
        }

        private static void SetField(object target, string name, object? value)
        {
            var f = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f!.SetValue(target, value);
        }

        private static void TrySetStringField(object target, string value, string[] candidateNames)
        {
            var type = target.GetType();
            foreach (var name in candidateNames)
            {
                var f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(string))
                {
                    f.SetValue(target, value);
                    return;
                }
            }
        }

        private sealed class FakeConnectionFactory
        {
            public int ConnectionsCreated { get; private set; }
            public FakeDbConnection NextConnection { get; set; } = new FakeDbConnection();

            public DbConnection Create(string cs)
            {
                ConnectionsCreated++;
                var conn = NextConnection;
                // Provision a fresh next-connection so the second factory call
                // gets a default behavior (empty reader, no command throw).
                NextConnection = new FakeDbConnection();
                return conn;
            }
        }

        private sealed class FakeDbConnection : DbConnection
        {
            public OdbcException? CommandThrowsOnExecute { get; set; }
            private ConnectionState _state = ConnectionState.Closed;

            public override string ConnectionString { get; set; } = string.Empty;
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "0";
            public override ConnectionState State => _state;

            public override void ChangeDatabase(string databaseName) { }
            public override void Close() { _state = ConnectionState.Closed; }
            public override void Open() { _state = ConnectionState.Open; }

            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
                => throw new NotSupportedException();

            protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);
        }

        private sealed class FakeDbCommand : DbCommand
        {
            private readonly FakeDbConnection _connection;

            public FakeDbCommand(FakeDbConnection connection)
            {
                _connection = connection;
                DbConnection = connection;
            }

            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new FakeParamCollection();
            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel() { }
            public override int ExecuteNonQuery() => 0;
            public override object? ExecuteScalar() => null;
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (_connection.CommandThrowsOnExecute != null)
                {
                    throw _connection.CommandThrowsOnExecute;
                }
                return new EmptyDbDataReader();
            }
        }

        private sealed class FakeParamCollection : DbParameterCollection
        {
            private readonly List<DbParameter> _items = new List<DbParameter>();
            public override int Count => _items.Count;
            public override object SyncRoot => this;
            public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
            public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains((DbParameter)value);
            public override bool Contains(string value) => false;
            public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
            public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
            public override int IndexOf(string parameterName) => -1;
            public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _items.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string parameterName) { }
            protected override DbParameter GetParameter(int index) => _items[index];
            protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) { }
        }

        private sealed class EmptyDbDataReader : DbDataReader
        {
            public override int FieldCount => 0;
            public override bool HasRows => false;
            public override bool IsClosed => false;
            public override int RecordsAffected => 0;
            public override int Depth => 0;
            public override object this[int ordinal] => throw new InvalidOperationException();
            public override object this[string name] => throw new InvalidOperationException();

            public override bool GetBoolean(int ordinal) => false;
            public override byte GetByte(int ordinal) => 0;
            public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
            public override char GetChar(int ordinal) => '\0';
            public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
            public override string GetDataTypeName(int ordinal) => string.Empty;
            public override DateTime GetDateTime(int ordinal) => default;
            public override decimal GetDecimal(int ordinal) => 0m;
            public override double GetDouble(int ordinal) => 0;
            public override System.Collections.IEnumerator GetEnumerator() => new List<object>().GetEnumerator();
            public override Type GetFieldType(int ordinal) => typeof(object);
            public override float GetFloat(int ordinal) => 0;
            public override Guid GetGuid(int ordinal) => Guid.Empty;
            public override short GetInt16(int ordinal) => 0;
            public override int GetInt32(int ordinal) => 0;
            public override long GetInt64(int ordinal) => 0;
            public override string GetName(int ordinal) => string.Empty;
            public override int GetOrdinal(string name) => -1;
            public override string GetString(int ordinal) => string.Empty;
            public override object GetValue(int ordinal) => null!;
            public override int GetValues(object[] values) => 0;
            public override bool IsDBNull(int ordinal) => true;
            public override bool NextResult() => false;
            public override bool Read() => false;
        }
    }
}
