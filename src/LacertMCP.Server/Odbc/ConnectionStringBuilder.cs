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
    /// <code>Driver={LacerteDSIIDriver};Datapath=...;ReturnType=...;TaxYear=YY</code>
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
        /// rendered as two digits in the output per the SDK spec.
        ///
        /// <para>
        /// <b>Brace-wrap-always policy:</b> the emitted <c>Datapath</c> value is unconditionally
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

            // SDK floor per StatusCodes.InValidTaxYear remediation (>= 2018); upper bound is a
            // sanity guard so the two-digit truncation below never produces a non-sensical year.
            if (taxYear < 2018 || taxYear > 2099)
            {
                throw new ArgumentException($"taxYear must be between 2018 and 2099 (got {taxYear}).", nameof(taxYear));
            }

            var twoDigitYear = (taxYear % 100).ToString("D2");

            // Always brace-wrap dataPath and double any embedded '}' per ODBC grammar
            // (the literal-'}' escape inside a brace-quoted value is '}}'). This handles
            // ';', '}', '{', '=', and any other delimiter the ODBC parser honors —
            // the conditional-on-';' heuristic was an injection surface.
            var datapathSegment = "{" + dataPath.Replace("}", "}}") + "}";

            return $"Driver={{LacerteDSIIDriver}};Datapath={datapathSegment};ReturnType={returnType};TaxYear={twoDigitYear}";
        }
    }
}
