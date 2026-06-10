using System;
using System.Globalization;

namespace LacertMCP.Server.Tools.Internal
{
    /// <summary>
    /// R6-33a — single source of truth for interpreting the EF-status
    /// <c>StatusDate</c> string as a point in time. Both
    /// <c>ClientSummaryTools.ProjectEfStatus</c> and
    /// <c>FirmDashboardTools</c>' latest-StatusDate watermark use this
    /// parse so the two tools can never disagree on "latest" for the same
    /// rows (the dashboard previously used <c>string.CompareOrdinal</c>,
    /// which mis-orders any non-ISO date shape the summary tool parses).
    /// </summary>
    internal static class EfStatusDates
    {
        /// <summary>
        /// Parse a StatusDate string into a UTC <see cref="DateTime"/>.
        /// Invariant-culture <see cref="DateTime.TryParse(string, IFormatProvider, DateTimeStyles, out DateTime)"/>
        /// with AssumeUniversal|AdjustToUniversal — identical semantics to
        /// the original ClientSummaryTools.ProjectEfStatus call site.
        /// </summary>
        public static bool TryParse(string? statusDate, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrEmpty(statusDate)) return false;
            return DateTime.TryParse(
                statusDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc);
        }
    }
}
