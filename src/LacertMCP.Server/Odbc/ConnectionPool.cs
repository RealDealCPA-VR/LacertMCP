// Design note:
// We DO NOT implement an MCP-level pool for ODBC connections. The .NET
// System.Data.Odbc provider already maintains an internal connection pool keyed on
// the literal connection string (same scheme our pool would have used) and that
// pool is correctly torn down on AppDomain shutdown. Adding a second layer would
// duplicate the bookkeeping, complicate lifetime semantics, and undermine the
// "single-threaded; one reader per worker" model documented on LacerteDsnReader.
//
// Per agent 03 §3: "Pool with the connection string verbatim — the ODBC provider
// keys its pool on the literal connection string, so consistent casing/ordering
// matters." We rely on that built-in pool. The factory method below exists as a
// stable allocation point so callers don't construct LacerteDsnReader directly —
// future changes (e.g. instrumentation, reader recycling) land here without
// touching call sites.

using System;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// Factory for <see cref="LacerteDsnReader"/> instances. There is no application-level
    /// pool — see the file header for rationale. Callers should <see cref="IDisposable.Dispose"/>
    /// the returned reader when done (the underlying connection goes back to the .NET ODBC
    /// provider's internal pool, keyed on the connection string).
    ///
    /// <para>Threading: thread-safe (stateless).</para>
    /// </summary>
    public static class ConnectionPool
    {
        /// <summary>
        /// Returns a fresh <see cref="LacerteDsnReader"/> for the given connection string.
        /// Name is "GetOrCreate" rather than "Create" for forward compatibility with a
        /// future pooling layer — callers should not assume a fresh instance.
        /// </summary>
        public static LacerteDsnReader GetOrCreate(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("connectionString must be non-empty.", nameof(connectionString));
            }
            return new LacerteDsnReader(connectionString);
        }
    }
}
