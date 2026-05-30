using System;
using System.Collections.Generic;
using System.Data;
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
    /// <b>Lifetime.</b> The underlying <see cref="OdbcConnection"/> is opened lazily on the
    /// first query and reused for subsequent queries; <see cref="Dispose"/> closes the
    /// connection and returns it to the pool.
    /// </para>
    /// </summary>
    public sealed class LacerteDsnReader : IDisposable
    {
        private readonly string _connectionString;
        private OdbcConnection? _connection;
        private bool _disposed;

        public LacerteDsnReader(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("connectionString must be non-empty.", nameof(connectionString));
            }
            _connectionString = connectionString;
        }

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
                    using (var cmd = new OdbcCommand(sql, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            rows.Add(mapRow(reader));
                        }
                    }
                }
                catch (OdbcException ex)
                {
                    throw OdbcErrorMapper.Map(ex, new Dictionary<string, object?>
                    {
                        ["sql"] = sql,
                    });
                }

                return (IReadOnlyList<T>)rows;
            });
        }

        private OdbcConnection EnsureOpen()
        {
            if (_connection == null)
            {
                _connection = new OdbcConnection(_connectionString);
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
