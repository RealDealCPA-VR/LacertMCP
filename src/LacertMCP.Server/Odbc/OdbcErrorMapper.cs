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
        /// <para>
        /// R6-36 — timeout (HYT00/HYT01/S1T00) and cancel (HY008/S1008)
        /// SQLSTATEs are excluded: the connection is still healthy after a
        /// slow or aborted query, so recycling it would just churn the
        /// provider pool.
        /// </para>
        /// </summary>
        public static bool IsConnectionFatal(string? sqlState)
        {
            if (string.IsNullOrEmpty(sqlState)) return false;
            // R6-36 — timeouts (HYT00 login timeout / HYT01 query timeout,
            // legacy S1T00) and operator cancellation (HY008, legacy S1008)
            // are NOT connection-fatal: the handle is still usable and
            // recycling it needlessly drops the provider-pool slot on every
            // slow query / cancel. They must be carved out BEFORE the broad
            // HY*/S1* families below.
            if (IsTimeoutOrCanceled(sqlState!)) return false;
            if (sqlState!.StartsWith("08", StringComparison.OrdinalIgnoreCase)) return true;
            if (sqlState.Equals("IM002", StringComparison.OrdinalIgnoreCase)) return true;
            if (sqlState.StartsWith("HY", StringComparison.OrdinalIgnoreCase)) return true;
            if (sqlState.StartsWith("S1", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // R6-36 — shared SQLSTATE classification for the timeout/cancel
        // carve-out used by both IsConnectionFatal and MapSqlState.
        private static bool IsTimeoutOrCanceled(string sqlState)
            => IsTimeout(sqlState) || IsCanceled(sqlState);

        private static bool IsTimeout(string sqlState)
            => sqlState.Equals("HYT00", StringComparison.OrdinalIgnoreCase)
               || sqlState.Equals("HYT01", StringComparison.OrdinalIgnoreCase)
               || sqlState.Equals("S1T00", StringComparison.OrdinalIgnoreCase); // legacy alias of HYT00

        private static bool IsCanceled(string sqlState)
            => sqlState.Equals("HY008", StringComparison.OrdinalIgnoreCase)
               || sqlState.Equals("S1008", StringComparison.OrdinalIgnoreCase); // legacy alias of HY008

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

            // R6-36 — timeouts and cancellation MUST be carved out BEFORE the
            // generic HY*/S1* → CodeBaseError bucket. CodeBaseError's catalog
            // remediation is "run File -> Repair Indexes" — actively wrong for
            // a slow share (timeout) and absurd for an intentional cancel.
            //
            // HYT00 (login timeout) / HYT01 (connection/query timeout) /
            // S1T00 (legacy): map to DBFNotAccessable — the catalog's
            // transient "file is missing, locked, or unreachable" code whose
            // remediation (check the data path, close the file in Lacerte,
            // retry) is the right operator move for a timed-out DBF read.
            // There is no dedicated timeout code in StatusCodes /
            // SyntheticStatusCodes; DBFNotAccessable is the closest
            // retry-after-unblocking semantic (it's also where 08*
            // connection failures land).
            if (IsTimeout(sqlState))
            {
                return new SqlStateMapping(
                    StatusCodes.DBFNotAccessable,
                    "DBFNotAccessable",
                    "ODBC timeout (" + sqlState.ToUpperInvariant() + "): " + message
                    + " — transient; verify the Lacerte data share is reachable and the DBF is not locked, then retry.");
            }

            // HY008 (operation canceled) / S1008 (legacy): the caller (or a
            // statement timeout) intentionally aborted the query. Generic
            // Error — not an index-corruption signal, and retry guidance is
            // the caller's call, not the catalog's.
            if (IsCanceled(sqlState))
            {
                return new SqlStateMapping(
                    StatusCodes.Error,
                    "Error",
                    "ODBC operation canceled (" + sqlState.ToUpperInvariant() + "): " + message);
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
