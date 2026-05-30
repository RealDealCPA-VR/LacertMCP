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
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when any required argument is null/empty
        /// or when <paramref name="taxYear"/> is outside the supported 2018–2099 window.</exception>
        public static string Build(string dataPath, string returnType, int taxYear)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                throw new ArgumentException("dataPath must be a non-empty absolute path.", nameof(dataPath));
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

            // Wrap dataPath in braces if it contains a semicolon — otherwise the ODBC
            // provider treats everything after the ';' as the start of a new key/value pair.
            // The brace form is documented in the .NET ODBC provider docs.
            var datapathSegment = dataPath.IndexOf(';') >= 0
                ? "{" + dataPath + "}"
                : dataPath;

            return $"Driver={{LacerteDSIIDriver}};Datapath={datapathSegment};ReturnType={returnType};TaxYear={twoDigitYear}";
        }
    }
}
