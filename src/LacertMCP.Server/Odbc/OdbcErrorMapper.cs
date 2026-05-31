using System;
using System.Collections.Generic;
using System.Data.Odbc;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// Normalizes raw <see cref="OdbcException"/>s into <see cref="LacerteSDKError"/>
    /// using the SQLSTATE → TAPIStatus table from
    /// <c>docs/research/agents/03-odbc-dbf-access.md</c> §7.
    ///
    /// <para>
    /// The original SQLSTATE is preserved in <see cref="LacerteSDKError.Context"/> under
    /// the <c>sqlState</c> key so diagnostics retain the driver-specific code even after
    /// translation to TAPIStatus.
    /// </para>
    ///
    /// <para>Threading: pure functions; safe to call from any thread.</para>
    /// </summary>
    public static class OdbcErrorMapper
    {
        /// <summary>
        /// Maps an <see cref="OdbcException"/> to a <see cref="LacerteSDKError"/>. The first
        /// error in <see cref="OdbcException.Errors"/> is consulted (drivers typically chain
        /// SQLSTATEs back-to-front and the first is the most specific).
        /// </summary>
        public static LacerteSDKError Map(OdbcException ex, IReadOnlyDictionary<string, object?>? context = null)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));

            // OdbcException.Errors is never null but can be empty if the exception was
            // constructed manually (rare); fall back to the message in that case.
            string sqlState = "";
            string message = ex.Message;
            if (ex.Errors != null && ex.Errors.Count > 0)
            {
                var first = ex.Errors[0];
                sqlState = first?.SQLState ?? "";
                message = first?.Message ?? ex.Message;
            }

            var mapped = MapSqlState(sqlState, message);

            // Merge caller-supplied context with the SQLSTATE breadcrumb.
            var ctx = new Dictionary<string, object?>
            {
                ["sqlState"] = sqlState,
                ["odbcMessage"] = message,
            };
            if (context != null)
            {
                foreach (var kvp in context)
                {
                    ctx[kvp.Key] = kvp.Value;
                }
            }

            return new LacerteSDKError(mapped.StatusCode, ctx, ex);
        }

        /// <summary>
        /// Returns true when <paramref name="sqlState"/> indicates a connection-
        /// level failure that should cause the caller to dispose+null the
        /// cached <see cref="System.Data.Odbc.OdbcConnection"/> so the next
        /// query re-opens fresh. Covers the same families
        /// <see cref="MapSqlState"/> routes to <see cref="StatusCodes.DBFNotAccessable"/>
        /// or <see cref="StatusCodes.CodeBaseError"/>: 08* (connection failure),
        /// IM002 (DSN/driver not found), HY* (driver-side error), S1* (legacy
        /// driver-side error). Per P15-045 — exposed as a predicate so
        /// <see cref="LacerteDsnReader"/> doesn't duplicate the classification.
        /// </summary>
        public static bool IsConnectionFatal(string? sqlState)
        {
            if (string.IsNullOrEmpty(sqlState)) return false;
            if (sqlState!.StartsWith("08", StringComparison.OrdinalIgnoreCase)) return true;
            if (sqlState.Equals("IM002", StringComparison.OrdinalIgnoreCase)) return true;
            if (sqlState.StartsWith("HY", StringComparison.OrdinalIgnoreCase)) return true;
            if (sqlState.StartsWith("S1", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Pure SQLSTATE → TAPIStatus mapping. Public so tests can fabricate
        /// SQLSTATEs without constructing an <see cref="OdbcException"/> (the ctor is internal).
        /// </summary>
        public static SqlStateMapping MapSqlState(string sqlState, string message)
        {
            // Order matters — IM002 must be checked before the broad "starts with I" pattern.
            if (string.IsNullOrEmpty(sqlState))
            {
                return new SqlStateMapping(StatusCodes.Error, "Error", message);
            }

            // Connection failures (08*) and the specific "DSN/Driver not found" (IM002)
            // both map to DBFNotAccessable.
            if (sqlState.StartsWith("08", StringComparison.OrdinalIgnoreCase)
                || sqlState.Equals("IM002", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlStateMapping(StatusCodes.DBFNotAccessable, "DBFNotAccessable", message);
            }

            // Table not found.
            if (sqlState.Equals("42S02", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlStateMapping(StatusCodes.DBNotSupported, "DBNotSupported", message);
            }

            // Column not found.
            if (sqlState.Equals("42S22", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlStateMapping(StatusCodes.NotExposed, "NotExposed", message);
            }

            // Syntax error.
            if (sqlState.Equals("42000", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlStateMapping(StatusCodes.Error, "Error", message);
            }

            // General driver errors — HY* (current) and S1* (legacy alias before SQL-92).
            if (sqlState.StartsWith("HY", StringComparison.OrdinalIgnoreCase)
                || sqlState.StartsWith("S1", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlStateMapping(StatusCodes.CodeBaseError, "CodeBaseError", message);
            }

            // Unknown SQLSTATE — fall through to the generic Error bucket.
            return new SqlStateMapping(StatusCodes.Error, "Error", message);
        }
    }

    /// <summary>
    /// Lightweight DTO returned from <see cref="OdbcErrorMapper.MapSqlState"/> so callers
    /// can inspect the mapping without constructing a full <see cref="LacerteSDKError"/>.
    /// </summary>
    public readonly struct SqlStateMapping
    {
        public int StatusCode { get; }
        public string StatusName { get; }
        public string Message { get; }

        public SqlStateMapping(int statusCode, string statusName, string message)
        {
            StatusCode = statusCode;
            StatusName = statusName;
            Message = message;
        }
    }
}
