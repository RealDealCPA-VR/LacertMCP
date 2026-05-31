using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// SQL templates for every read path the MCP exposes, ported verbatim from
    /// <c>docs/research/agents/03-odbc-dbf-access.md</c> §4 (pre-TY21 and TY21+ forms) and §5
    /// (per-client detail files).
    ///
    /// <para>
    /// The <c>client</c> placeholder is substituted as a SQL identifier (table name in the
    /// TY21+ form, literal in WHERE clauses) and CANNOT be parameterized via
    /// <c>OdbcParameter</c> for the table-name uses. To prevent SQL injection at this
    /// boundary, every method that takes a <c>client</c> string passes it through
    /// <see cref="ValidateClient"/> first; invalid identifiers throw
    /// <see cref="LacerteSDKError"/> with <see cref="StatusCodes.InValidClientName"/>.
    /// </para>
    ///
    /// <para>Threading: pure functions; safe to call from any thread.</para>
    /// </summary>
    public static class SqlTemplates
    {
        // Client identifier shape: ASCII uppercase letters / digits, 1-8 chars.
        // The 8-char ceiling is per StatusCodes.LengthExceeded remediation; the
        // uppercase + digits alphabet matches the Lacerte ClientID spec.
        private static readonly Regex ClientIdentifierPattern = new Regex(
            "^[A-Z0-9]{1,8}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Validates that a client number is a safe SQL identifier per the
        /// Lacerte ClientID spec (1-8 uppercase ASCII alphanumerics).
        /// Throws <see cref="LacerteSDKError"/> with <see cref="StatusCodes.InValidClientName"/>
        /// on rejection.
        /// </summary>
        public static void ValidateClient(string client)
        {
            if (client == null || !ClientIdentifierPattern.IsMatch(client))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["client"] = client,
                        ["expected"] = "1-8 chars matching ^[A-Z0-9]{1,8}$",
                    });
            }
        }

        // -------------------------------------------------------------------
        // DATA1 — Client Info (global, both forms)
        // -------------------------------------------------------------------

        /// <summary>Full client roster (unbounded — callers should prefer
        /// <see cref="Data1Top"/> for paginated reads).</summary>
        public const string Data1All = "select * from [DATA1-Client Info]";

        /// <summary>
        /// First <paramref name="limit"/> rows from the client roster. Uses
        /// <c>SELECT TOP n</c> which the Lacerte ODBC (FoxPro-style) driver
        /// supports per agent 03 §4.
        ///
        /// <para><b>Pagination caveat:</b> the Lacerte ODBC dialect does NOT
        /// support <c>OFFSET</c>/<c>FETCH NEXT</c>/<c>ROW_NUMBER()</c>, so true
        /// server-side OFFSET-based paging is not available. Live mode must
        /// either (a) overfetch <c>offset + limit</c> rows via this template
        /// and slice in-memory, or (b) issue a separate <c>SELECT COUNT(*)</c>
        /// for total and use TOP per page. The current
        /// <c>ClientsTools.LacerteClientList</c> uses the in-memory slice path
        /// because <c>Pagination.Page&lt;T&gt;</c> needs an exact <c>total</c>
        /// that a single TOP query cannot provide.</para>
        /// </summary>
        public static string Data1Top(int limit)
        {
            ValidateLimit(limit);
            return $"select top {limit} * from [DATA1-Client Info]";
        }

        /// <summary>
        /// First <paramref name="limit"/> rows from the client roster whose
        /// conventional name field starts with <paramref name="namePrefix"/>.
        /// <para><paramref name="nameColumn"/> is the C1_N column to filter on
        /// (typically C1_2 for individual modules, C1_1 for entity modules).
        /// It is validated as a C{t}_N identifier to prevent SQL injection.</para>
        /// <para>Same OFFSET caveat as <see cref="Data1Top"/>: callers needing a
        /// stable <c>total</c> must overfetch and slice in-memory.</para>
        /// </summary>
        public static string Data1TopWhereName(int limit, string nameColumn, string namePrefix)
        {
            ValidateLimit(limit);
            ValidateColumn(nameColumn);
            var safePrefix = EscapeSqlLiteral(namePrefix);
            return $"select top {limit} * from [DATA1-Client Info] where {nameColumn} like '{safePrefix}%'";
        }

        /// <summary>One client row keyed by client number.</summary>
        public static string Data1Where(string client)
        {
            ValidateClient(client);
            return $"select * from [DATA1-Client Info] where C1_0 = '{client}'";
        }

        // -------------------------------------------------------------------
        // DATA2 — Subclients (changed between pre-TY21 and TY21+)
        // -------------------------------------------------------------------

        /// <summary>Pre-TY21 DATA2 (global table, filter by C2_0).</summary>
        public static string Data2OldForm(string client)
        {
            ValidateClient(client);
            return $"select * from [DATA2-Subclient Info] where C2_0 = '{client}'";
        }

        /// <summary>TY21+ DATA2 (per-client table prefix).</summary>
        public static string Data2NewForm(string client)
        {
            ValidateClient(client);
            return $"select * from [{client}-SubClientInfo]";
        }

        // -------------------------------------------------------------------
        // DATA3 — State Client Info (changed between pre-TY21 and TY21+)
        // -------------------------------------------------------------------

        /// <summary>Pre-TY21 DATA3 (global table, filter by C3_0).</summary>
        public static string Data3OldForm(string client)
        {
            ValidateClient(client);
            return $"select * from [DATA3-State Client Info] where C3_0 = '{client}'";
        }

        /// <summary>Pre-TY21 DATA3 (global table, filter by C3_0 + 2-letter state).</summary>
        public static string Data3OldFormByState(string client, string state)
        {
            ValidateClient(client);
            ValidateState(state);
            return $"select * from [DATA3-State Client Info] where C3_0 = '{client}' and C3_G1 = '{state}'";
        }

        /// <summary>TY21+ DATA3 (per-client table prefix).</summary>
        public static string Data3NewForm(string client)
        {
            ValidateClient(client);
            return $"select * from [{client}-StateClientInfo]";
        }

        // -------------------------------------------------------------------
        // DATA4 — State Subclient Info (changed between pre-TY21 and TY21+)
        // -------------------------------------------------------------------

        /// <summary>Pre-TY21 DATA4 (global table, filter by C4_0 + state).</summary>
        public static string Data4OldForm(string client, string state)
        {
            ValidateClient(client);
            ValidateState(state);
            return $"select * from [DATA4-State Subclient Info] where C4_0 = '{client}' and C4_G1 = '{state}'";
        }

        /// <summary>TY21+ DATA4 (per-client table prefix).</summary>
        public static string Data4NewForm(string client)
        {
            ValidateClient(client);
            return $"select * from [{client}-StateSubClientInfo]";
        }

        // -------------------------------------------------------------------
        // DATA5/6/7 — still global in both forms per agent 03 §4
        // -------------------------------------------------------------------

        /// <summary>DATA5 status info for one client.</summary>
        public static string Data5(string client)
        {
            ValidateClient(client);
            return $"select * from [DATA5-Status Info] where C5_0 = '{client}'";
        }

        /// <summary>DATA6 display info for one client.</summary>
        public static string Data6(string client)
        {
            ValidateClient(client);
            return $"select * from [DATA6-Display Info] where C6_0 = '{client}'";
        }

        /// <summary>DATA7 form info (forms present in return) for one client.</summary>
        public static string Data7(string client)
        {
            ValidateClient(client);
            return $"select * from [DATA7-Form Info] where C7_0 = '{client}'";
        }

        // -------------------------------------------------------------------
        // EF Status — TY19+ EFilings + EFStatuses JOIN per agent 03 §4
        // -------------------------------------------------------------------

        /// <summary>
        /// All EF status rows joined per client (full history, no MAX/window
        /// filter). Column projection MUST stay in lock-step with
        /// <c>LacerteSessionManager.ReadEFStatus</c> and the
        /// <c>EFileTools.LacerteEfStatus</c> tool description — the live wire
        /// shape and the sim shape are required to be observationally identical
        /// per <c>CLAUDE.md</c> "Simulation and live must be observationally
        /// identical for the happy path".
        ///
        /// <para>Columns: ClientId, EFilingId, EFId, InstanceId, Status, StatusDate.</para>
        /// </summary>
        public const string EfStatusJoin =
            "select ci.C1_0 as ClientId, ef.Id as EFilingId, ef.EFId, ef.InstanceId, " +
            "s.EFStatus as Status, s.EFStatusDate as StatusDate " +
            "from [DATA1-Client Info] as ci " +
            "left outer join [EFilings] as ef on ci.C1_0 = ef.ClientId " +
            "left outer join [EFStatuses] as s on ef.Id = s.EFilingId";

        /// <summary>
        /// P15-010 — EF-status join filtered by a since-date threshold on
        /// <c>EFStatusDate</c>. Used by the operator "watch e-file status"
        /// poll workflow (REQUIREMENTS §1.4) so a 6-week e-file history
        /// doesn't round-trip on every poll.
        ///
        /// <para>
        /// The since-date is emitted as a SQL date literal in
        /// <c>yyyy-MM-dd</c> (ISO 8601) since LacerteDSII (FoxPro-style)
        /// does not honor ODBC parameter binding for date columns
        /// reliably. The caller is responsible for passing a non-null
        /// <see cref="DateTime"/>; this helper formats it via
        /// <see cref="CultureInfo.InvariantCulture"/> to avoid locale
        /// surprises. The same LEFT OUTER JOIN shape as <see cref="EfStatusJoin"/>
        /// is preserved so client-without-eFiling rows still appear (their
        /// null StatusDate is naturally excluded by the &gt;= comparison —
        /// the workflow only wants rows that changed since the threshold,
        /// so dropping the unsubmitted rows is correct).
        /// </para>
        /// </summary>
        public static string EfStatusJoinSince(DateTime sinceUtc)
        {
            var iso = sinceUtc.ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);
            return
                "select ci.C1_0 as ClientId, ef.Id as EFilingId, ef.EFId, ef.InstanceId, " +
                "s.EFStatus as Status, s.EFStatusDate as StatusDate " +
                "from [DATA1-Client Info] as ci " +
                "left outer join [EFilings] as ef on ci.C1_0 = ef.ClientId " +
                "left outer join [EFStatuses] as s on ef.Id = s.EFilingId " +
                "where s.EFStatusDate >= {d '" + iso + "'}";
        }

        // -------------------------------------------------------------------
        // Detail file reads (per-client [<CLIENT>-Detail] logical tables) — §5
        // -------------------------------------------------------------------

        /// <summary>Read every column from a client's detail file.</summary>
        public static string DetailAll(string client)
        {
            ValidateClient(client);
            return
                $"select Series, Prefix, Code, Suffix, StateAbbr, CityAbbr, " +
                $"Source, TSJ, ImportSrc, Amount, Description " +
                $"from [{client}-Detail]";
        }

        /// <summary>
        /// Filter detail rows by Series (e.g. 11 = W-2, 12 = Interest, 13 = Dividends,
        /// 14 = Dependents, 20 = Income, 30 = State apportionment, 40 = Partners).
        ///
        /// <para>Series is a NUMERIC screen identifier per spec §2 and the
        /// live ODBC wire shape (agent 03 §4 example: <c>Series = 11</c>) —
        /// the WHERE clause emits an unquoted integer literal so the SQL
        /// engine performs an integer comparison (not lexicographic, which
        /// would order "0","1","10","2"). The sim contract is aligned via
        /// (a) <see cref="LacertMCP.Server.Tools.DetailsTools"/> tool-layer
        /// validation enforcing <c>Series</c> matches <c>^\d+$</c> on every
        /// write, and (b) the seed fixture emitting numeric Series strings
        /// (e.g. "11" for Wages) instead of the older friendly labels.
        /// Together these ensure the in-memory <see cref="LacertMCP.Server.Session.DetailRow"/>
        /// Series strings are always parseable integers, so a live-SQL filter
        /// produced by this template will agree with a sim-side predicate
        /// (audit task P14-055 closed via lighter-fix path: column stays
        /// <c>string</c> on the wire, numeric-string invariant enforced at
        /// the validator + seed boundary).</para>
        /// </summary>
        public static string DetailBySeries(string client, int series)
        {
            ValidateClient(client);
            return $"select * from [{client}-Detail] where Series = {series}";
        }

        /// <summary>
        /// Filter detail rows by Code (e.g. 800 = description labels).
        ///
        /// <para>Same NUMERIC-column contract as <see cref="DetailBySeries"/>:
        /// the WHERE clause emits an unquoted integer literal, matching the
        /// live wire shape documented in agent 03 §4. The sim contract is
        /// aligned via tool-layer validation enforcing <c>Code</c> matches
        /// <c>^-?\d+$</c> (see <see cref="LacertMCP.Server.Tools.DetailsTools"/>).</para>
        /// </summary>
        public static string DetailByCode(string client, int code)
        {
            ValidateClient(client);
            return $"select * from [{client}-Detail] where Code = {code}";
        }

        // -------------------------------------------------------------------
        // State abbreviation guard
        // -------------------------------------------------------------------

        private static readonly Regex StatePattern = new Regex(
            "^[A-Z]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static void ValidateState(string state)
        {
            if (state == null || !StatePattern.IsMatch(state))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidStateAbbrev,
                    new Dictionary<string, object?>
                    {
                        ["state"] = state,
                        ["expected"] = "2 uppercase ASCII letters",
                    });
            }
        }

        // -------------------------------------------------------------------
        // Pagination + column-identifier guards
        // -------------------------------------------------------------------

        // C{t}_N column identifier shape: C, digit (1-7), underscore, digits/G.
        // Permits both ordinal (C1_2) and global-state (C3_G1) columns.
        private static readonly Regex ColumnIdentifierPattern = new Regex(
            "^C[1-7]_(G?[0-9]+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static void ValidateLimit(int limit)
        {
            if (limit <= 0 || limit > 100_000)
            {
                throw new LacerteSDKError(
                    StatusCodes.OutOfIndex,
                    new Dictionary<string, object?>
                    {
                        ["limit"] = limit,
                        ["expected"] = "1..100000",
                    });
            }
        }

        private static void ValidateColumn(string column)
        {
            if (column == null || !ColumnIdentifierPattern.IsMatch(column))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidStateAbbrev,
                    new Dictionary<string, object?>
                    {
                        ["column"] = column,
                        ["expected"] = "C{t}_N or C{t}_GN per DBF column convention",
                    });
            }
        }

        // Safe single-quote escape for SQL string literals (doubles internal
        // single-quotes, the standard SQL-92 escape). Prefix-only filters
        // never appear in identifier position.
        private static string EscapeSqlLiteral(string input)
        {
            if (input == null) return string.Empty;
            return input.Replace("'", "''");
        }
    }
}
