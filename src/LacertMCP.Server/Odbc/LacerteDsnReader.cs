// Design note (lifted from the deleted ConnectionPool.cs):
// We DO NOT implement an MCP-level pool for ODBC connections. The .NET
// System.Data.Odbc provider already maintains an internal connection pool keyed on
// the literal connection string (same scheme our pool would have used) and that
// pool is correctly torn down on AppDomain shutdown. Adding a second layer would
// duplicate the bookkeeping, complicate lifetime semantics, and undermine the
// "single-threaded; one reader per worker" model documented on LacerteDsnReader.
//
// Per agent 03 §3: "Pool with the connection string verbatim — the ODBC provider
// keys its pool on the literal connection string, so consistent casing/ordering
// matters." We rely on that built-in pool. Callers should construct
// LacerteDsnReader directly (or inject it) rather than going through a factory.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// Reads-only ODBC client for the Lacerte <c>LacerteDSIIDriver</c>. Wraps
    /// <see cref="OdbcConnection"/> + <see cref="OdbcCommand"/> + <see cref="OdbcDataReader"/>
    /// with normalized error handling (every <see cref="OdbcException"/> is translated through
    /// <see cref="OdbcErrorMapper.Map"/> to a <see cref="LacerteSDKError"/>).
    ///
    /// <para>
    /// <b>Reads only.</b> Per <c>ARCHITECTURE.md</c> Boundary Invariant #6 and
    /// <c>docs/research/agents/03-odbc-dbf-access.md</c> §6, this class exposes no
    /// <c>ExecuteNonQuery</c> surface. All mutations go through the COM bridge.
    /// </para>
    ///
    /// <para>
    /// <b>Threading model.</b> Single-threaded. Create one reader per worker — do not share
    /// instances across threads. The .NET ODBC provider runs its own internal connection
    /// pool keyed on the literal connection string, so creating new readers is cheap.
    /// </para>
    ///
    /// <para>
    /// <b>Lifetime.</b> The underlying <see cref="DbConnection"/> is opened lazily on the
    /// first query and reused for subsequent queries; <see cref="Dispose"/> closes the
    /// connection and returns it to the pool.
    /// </para>
    ///
    /// <para>
    /// <b>Disconnect recovery (P15-045).</b> A long-running server can outlive
    /// transient ODBC link failures (DBF share unreachable, DSN reconfigured,
    /// driver process death). When <see cref="Query{T}"/> catches an
    /// <see cref="OdbcException"/> whose SQLSTATE is connection-fatal (08*, IM002,
    /// HY*, S1*; classified by <see cref="OdbcErrorMapper.IsConnectionFatal"/>),
    /// the cached connection is disposed+nulled before rethrowing so the next
    /// query opens fresh. Non-fatal SQLSTATEs (42S02 table-not-found, 42S22
    /// column-not-found, 42000 syntax) keep the connection. <see cref="Ping"/>
    /// runs a cheap schema-aware probe (<c>SELECT TOP 1 C1_0 FROM
    /// [DATA1-Client Info]</c> — <c>SELECT 1</c> doesn't work against the
    /// Lacerte DBF driver) and returns false on connection-fatal failures,
    /// recycling the connection along the way so subsequent reads recover.
    /// </para>
    /// </summary>
    public sealed class LacerteDsnReader : IDisposable
    {
        private readonly string _connectionString;
        private readonly Func<string, DbConnection> _connectionFactory;
        private DbConnection? _connection;
        private bool _disposed;

        public LacerteDsnReader(string connectionString)
            : this(connectionString, cs => new OdbcConnection(cs))
        {
        }

        /// <summary>
        /// Test-only constructor — accepts a <see cref="DbConnection"/> factory so
        /// tests can inject a fake that throws <see cref="OdbcException"/>-shaped
        /// errors without going through the real ODBC driver. Production callers
        /// use the single-arg constructor which defaults the factory to
        /// <c>cs =&gt; new OdbcConnection(cs)</c>.
        /// </summary>
        internal LacerteDsnReader(string connectionString, Func<string, DbConnection> connectionFactory)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("connectionString must be non-empty.", nameof(connectionString));
            }
            _connectionString = connectionString;
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        /// <summary>
        /// Test-only inspection: true when an underlying connection is cached
        /// (i.e. <see cref="EnsureOpen"/> has run and the cached handle has not
        /// been recycled by the connection-fatal recovery path).
        /// </summary>
        internal bool HasOpenConnection => _connection != null;

        /// <summary>
        /// Convenience overload that materializes every row as a loose
        /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> keyed by column name. Useful for
        /// diagnostic / ad-hoc reads where a strong row type isn't worth defining.
        /// Strongly-typed callers should prefer the <see cref="Query{T}(string, Func{IDataRecord, T})"/>
        /// overload.
        /// </summary>
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
        {
            return Query(sql, record =>
            {
                var dict = new Dictionary<string, object?>(record.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < record.FieldCount; i++)
                {
                    var name = record.GetName(i);
                    var value = record.IsDBNull(i) ? null : record.GetValue(i);
                    dict[name] = value;
                }
                return (IReadOnlyDictionary<string, object?>)dict;
            });
        }

        /// <summary>
        /// Executes <paramref name="sql"/> and maps each row through <paramref name="mapRow"/>.
        /// Throws <see cref="LacerteSDKError"/> on any <see cref="OdbcException"/> (mapped via
        /// <see cref="OdbcErrorMapper.Map"/>).
        /// </summary>
        public IReadOnlyList<T> Query<T>(string sql, Func<IDataRecord, T> mapRow)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (mapRow == null) throw new ArgumentNullException(nameof(mapRow));
            ThrowIfDisposed();

            // F11 — wrap with WireLog.RecordSync. Args carry the SQL text so
            // the operator can replay diagnostic queries from the log.
            return WireLog.RecordSync<IReadOnlyList<T>>("Odbc.Query", new { sql }, () =>
            {
                var rows = new List<T>();
                try
                {
                    var conn = EnsureOpen();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                rows.Add(mapRow(reader));
                            }
                        }
                    }
                }
                catch (OdbcException ex)
                {
                    // P15-045 — recycle the cached connection on connection-fatal
                    // SQLSTATEs so the next Query() re-opens fresh. EnsureOpen's
                    // `State != Open` guard does not fire for many server-side
                    // disconnects (driver leaves the handle in Open while the
                    // socket is dead), so without this branch a single network
                    // blip pins ODBC reads broken until process restart.
                    TryRecycleConnectionOnFatal(ex);
                    throw OdbcErrorMapper.Map(ex, new Dictionary<string, object?>
                    {
                        ["sql"] = sql,
                    });
                }

                return (IReadOnlyList<T>)rows;
            });
        }

        /// <summary>
        /// P15-045 — cheap schema-aware health probe. Runs
        /// <c>SELECT TOP 1 C1_0 FROM [DATA1-Client Info]</c> (DATA1-Individual is
        /// universally present per the Lacerte SDK Structure Spec; <c>SELECT 1</c>
        /// doesn't work against the Lacerte DBF driver which has no virtual rowset).
        /// Returns true when the query round-trips successfully. On any
        /// <see cref="OdbcException"/> the connection is recycled when the SQLSTATE
        /// is connection-fatal (so the next caller opens a fresh handle) and false
        /// is returned. Wire this into <c>lacerte_session_status</c> so the
        /// operator can surface ODBC health and trigger recycle before a tool
        /// call hangs on a dead handle.
        /// </summary>
        public bool Ping()
        {
            if (_disposed) return false;
            try
            {
                Query("SELECT TOP 1 C1_0 FROM [DATA1-Client Info]");
                return true;
            }
            catch
            {
                // Query already handled fatal-state recycling; any throw here
                // (LacerteSDKError or otherwise) means the probe failed.
                return false;
            }
        }

        private void TryRecycleConnectionOnFatal(OdbcException ex)
        {
            string? sqlState = null;
            try
            {
                if (ex.Errors != null && ex.Errors.Count > 0)
                {
                    sqlState = ex.Errors[0]?.SQLState;
                }
            }
            catch
            {
                // Defensive — touching .Errors should never throw, but if it does
                // we conservatively skip the recycle (no worse than pre-P15-045
                // behavior). Falls through to leave _connection cached.
            }

            if (!OdbcErrorMapper.IsConnectionFatal(sqlState)) return;

            var stale = _connection;
            _connection = null;
            if (stale != null)
            {
                try { stale.Dispose(); }
                catch { /* swallow — connection is already in an undefined state */ }
            }
        }

        private DbConnection EnsureOpen()
        {
            if (_connection == null)
            {
                _connection = _connectionFactory(_connectionString);
            }
            // R6-36 — a Broken connection must be Close()d before re-Open().
            // ADO.NET contract: Open() on a Broken connection throws
            // InvalidOperationException; only Closed → Open is legal. Close()
            // on a Broken handle is documented as safe, but swallow anyway —
            // the handle is already in an undefined state.
            if (_connection.State == ConnectionState.Broken)
            {
                try { _connection.Close(); }
                catch { /* swallow — proceed to re-open */ }
            }
            if (_connection.State != ConnectionState.Open)
            {
                _connection.Open();
            }
            return _connection;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LacerteDsnReader));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_connection != null)
            {
                try
                {
                    if (_connection.State != ConnectionState.Closed)
                    {
                        _connection.Close();
                    }
                }
                catch
                {
                    // Closing should never throw, but swallow defensively — Dispose contract.
                }
                _connection.Dispose();
                _connection = null;
            }
        }
    }
}
