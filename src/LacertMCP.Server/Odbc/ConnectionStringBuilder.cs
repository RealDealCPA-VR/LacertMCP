using System;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// Builds the DSN-less ODBC connection string for the Lacerte ODBC driver
    /// (<c>LacerteDSIIDriver</c>) per <c>docs/research/agents/03-odbc-dbf-access.md</c> §2.
    ///
    /// <para>
    /// Shape:
    /// <code>Driver={LacerteDSIIDriver};DataDir={...};TaxYear=YYYY;ReturnType=...</code>
    /// </para>
    ///
    /// <para>
    /// R7-SRC — per the Intuit SDK Developer Instructions ("Connection String"):
    /// <c>driver=LacerteDSIIDriver; DataDir=c:\Lacerte\18tax\idata; TaxYear=2019; ReturnType=Individual</c>.
    /// The key is <c>DataDir</c> (the earlier research note's <c>Datapath</c> is
    /// not a driver keyword) and <c>TaxYear</c> is the FOUR-digit year.
    /// </para>
    ///
    /// <para>
    /// The braces around the driver name are mandatory — the ODBC provider treats unbraced
    /// driver names as DSNs. The <c>ReturnType</c> + <c>TaxYear</c> fields are documented as
    /// "optional" by Intuit but must always be sent because the standard install layout
    /// (<c>C:\Lacerte\YYdata\</c>) mixes multiple modules in one directory; omitting them
    /// surfaces ambiguous results (the "only one LacerteDSII shows up" Access bug).
    /// </para>
    ///
    /// <para>Threading: pure function; safe to call from any thread.</para>
    /// </summary>
    public static class ConnectionStringBuilder
    {
        /// <summary>
        /// Builds the DSN-less connection string. <paramref name="dataPath"/> is the absolute
        /// year-specific data directory (e.g. <c>C:\Lacerte\24data</c>),
        /// <paramref name="returnType"/> is one of <c>Individual, Corporate, Partnership,
        /// SCorporate, Fiduciary, Exempt, Estate, Gift, Benefit</c>, and
        /// <paramref name="taxYear"/> is the four-digit calendar year (e.g. 2024) — it is
        /// rendered as all four digits per the SDK Developer Instructions (R7-SRC).
        ///
        /// <para>
        /// <b>Brace-wrap-always policy:</b> the emitted <c>DataDir</c> value is unconditionally
        /// brace-wrapped (<c>{...}</c>) and any embedded <c>}</c> in the input is doubled to
        /// <c>}}</c> per the ODBC connection-string grammar. This closes the
        /// connection-string-injection surface — a heuristic that only braces "when the path
        /// contains <c>;</c>" leaves other delimiters (<c>}</c>, <c>=</c>, CR/LF) able to
        /// break out of the value and inject sibling keys (e.g. a second <c>Driver=</c>).
        /// Do not re-introduce the conditional bracing.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when any required argument is null/empty,
        /// when <paramref name="dataPath"/> contains a control character (anything &lt; 0x20,
        /// including <c>\0</c>, <c>\r</c>, <c>\n</c>, <c>\t</c>), or when
        /// <paramref name="taxYear"/> is outside the supported 2018–2099 window.</exception>
        public static string Build(string dataPath, string returnType, int taxYear)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                throw new ArgumentException("dataPath must be a non-empty absolute path.", nameof(dataPath));
            }

            // Reject control characters outright. These have no legitimate place in a
            // filesystem path and are the highest-risk parser-confusion characters for an
            // ODBC connection-string parser (NUL terminates the string in some providers,
            // CR/LF can split keys in others).
            for (int i = 0; i < dataPath.Length; i++)
            {
                if (dataPath[i] < 0x20)
                {
                    throw new ArgumentException(
                        $"dataPath must not contain control characters (found 0x{((int)dataPath[i]):X2} at index {i}).",
                        nameof(dataPath));
                }
            }

            if (string.IsNullOrWhiteSpace(returnType))
            {
                throw new ArgumentException("returnType must be a non-empty module name (Individual, Corporate, ...).", nameof(returnType));
            }

            // R7-31 — returnType is inlined UNBRACED (unlike Datapath), so a
            // value containing ';' could append extra connection-string keys.
            // Today it only comes from Modules, but enforce the closed set at
            // the boundary that builds the string.
            if (Schema.Modules.GetByReturnType(returnType) == null)
            {
                throw new ArgumentException(
                    $"returnType '{returnType}' is not a known Lacerte module ReturnType (Individual, Corporate, Partnership, SCorporate, Fiduciary, Exempt, Estate, Gift, Benefit).",
                    nameof(returnType));
            }

            // SDK floor per StatusCodes.InValidTaxYear remediation (>= 2018); upper bound is a
            // sanity guard against a non-sensical year.
            if (taxYear < 2018 || taxYear > 2099)
            {
                throw new ArgumentException($"taxYear must be between 2018 and 2099 (got {taxYear}).", nameof(taxYear));
            }

            // Always brace-wrap dataPath and double any embedded '}' per ODBC grammar
            // (the literal-'}' escape inside a brace-quoted value is '}}'). This handles
            // ';', '}', '{', '=', and any other delimiter the ODBC parser honors —
            // the conditional-on-';' heuristic was an injection surface.
            var datapathSegment = "{" + dataPath.Replace("}", "}}") + "}";

            var year = taxYear.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"Driver={{LacerteDSIIDriver}};DataDir={datapathSegment};TaxYear={year};ReturnType={returnType}";
        }
    }
}
