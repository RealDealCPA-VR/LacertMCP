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

        /// <summary>Full client roster.</summary>
        public const string Data1All = "select * from [DATA1-Client Info]";

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

        /// <summary>Latest EF status per client (joined across DATA1, EFilings, EFStatuses).</summary>
        public const string EfStatusJoin =
            "select ci.C1_0, ci.C1_2, ci.C1_3, s.EFStatus, s.EFStatusDate " +
            "from [DATA1-Client Info] as ci " +
            "left outer join [EFilings] as ef on ci.C1_0 = ef.ClientId " +
            "left outer join [EFStatuses] as s on ef.Id = s.EFilingId";

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

        /// <summary>Filter detail rows by Series (e.g. 11 = W-2, 12 = Interest).</summary>
        public static string DetailBySeries(string client, int series)
        {
            ValidateClient(client);
            return $"select * from [{client}-Detail] where Series = {series}";
        }

        /// <summary>Filter detail rows by Code (e.g. 800 = description labels).</summary>
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
    }
}
