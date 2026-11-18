using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// FP-2 — builds the ODBC <c>INSERT</c> that appends one detail row to a
    /// client's detail file, and compares rows for the post-write read-back.
    ///
    /// <para>
    /// The SDK Developer Instructions say detail files are read and written
    /// through the ODBC driver (the COM library has no detail-write method)
    /// and that the driver does NOT validate what is written. Every value is
    /// therefore validated here before it becomes SQL text:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Client key: <see cref="SqlTemplates.ValidateClient"/> (1-8 <c>[A-Z0-9]</c>) — the same table naming the read path uses (<c>[&lt;client&gt;-Detail]</c>).</description></item>
    ///   <item><description>Series / Code: required; Prefix / Suffix: optional (omitted from the column list when blank so the driver applies its default). All four are emitted as invariant integer literals.</description></item>
    ///   <item><description>Amount: invariant decimal literal, never exponent form; omitted when null.</description></item>
    ///   <item><description>Strings: single-quoted with <c>'</c> doubled; ANY control character (including tab / CR / LF) is rejected.</description></item>
    ///   <item><description>ImportSrc is never written (the SDK marks it read-only).</description></item>
    /// </list>
    /// <para>Pure functions; safe from any thread.</para>
    /// </summary>
    public static class DetailRowWriter
    {
        private const string Op = "WriteDetail";

        /// <summary>
        /// Builds the INSERT for <paramref name="clientKey"/> (already trimmed and
        /// upper-cased) from <paramref name="row"/>. Throws <see cref="LacerteSDKError"/>
        /// (9008 DetailWriteInvalid, or 24 InValidClientName for the key) on anything
        /// that can't be written safely.
        /// </summary>
        public static string BuildInsertSql(string clientKey, DetailRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            SqlTemplates.ValidateClient(clientKey);
            // Same content rules the session applies in both modes (lengths,
            // numeric Series/Prefix/Code/Suffix, Source/TSJ vocabulary,
            // ImportSrc blank, Amount range/precision).
            BridgeInputValidation.ValidateDetailRowContent(row, Op);

            var columns = new List<string>(10);
            var values = new List<string>(10);

            columns.Add("Series");
            values.Add(RequiredInt(row.Series, "row.Series"));
            var prefix = OptionalInt(row.Prefix, "row.Prefix");
            if (prefix != null)
            {
                columns.Add("Prefix");
                values.Add(prefix);
            }
            columns.Add("Code");
            values.Add(RequiredInt(row.Code, "row.Code"));
            var suffix = OptionalInt(row.Suffix, "row.Suffix");
            if (suffix != null)
            {
                columns.Add("Suffix");
                values.Add(suffix);
            }

            columns.Add("StateAbbr");
            values.Add(Quote((row.StateAbbr ?? string.Empty).Trim(), "row.StateAbbr"));
            columns.Add("CityAbbr");
            values.Add(Quote((row.CityAbbr ?? string.Empty).Trim(), "row.CityAbbr"));
            columns.Add("Source");
            values.Add(Quote(BridgeInputValidation.NormalizeDetailSource(row.Source) ?? Reject("row.Source", "row.Source must be blank, Nonsource or Source."), "row.Source"));
            columns.Add("TSJ");
            values.Add(Quote(BridgeInputValidation.NormalizeTsj(row.TSJ) ?? Reject("row.TSJ", "row.TSJ must be blank, Taxpayer, Spouse or Joint."), "row.TSJ"));

            if (row.Amount.HasValue)
            {
                columns.Add("Amount");
                values.Add(FormatAmount(row.Amount.Value));
            }

            columns.Add("Description");
            values.Add(Quote(row.Description ?? string.Empty, "row.Description"));

            return "INSERT INTO [" + clientKey + "-Detail] (" + string.Join(", ", columns)
                + ") VALUES (" + string.Join(", ", values) + ")";
        }

        /// <summary>
        /// Invariant decimal literal for an Amount: no exponent, no grouping,
        /// at most 12 decimals (the validator already rejected more).
        /// </summary>
        internal static string FormatAmount(double amount)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount))
            {
                Reject("row.Amount", "row.Amount must be a finite number.");
            }
            // decimal keeps the double's shortest round-trip digits (15
            // significant) and its ToString never produces exponent form.
            var d = (decimal)amount;
            return d.ToString("0.############", CultureInfo.InvariantCulture);
        }

        private static string RequiredInt(string? value, string field)
        {
            var s = OptionalInt(value, field);
            if (s == null)
            {
                Reject(field, field + " is required for a live detail-row write.");
            }
            return s!;
        }

        private static string? OptionalInt(string? value, string field)
        {
            var t = (value ?? string.Empty).Trim();
            if (t.Length == 0) return null;
            // ASCII digits with an optional leading minus only — int.Parse
            // alone would accept culture-specific signs and whitespace.
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (!((c >= '0' && c <= '9') || (i == 0 && c == '-' && t.Length > 1)))
                {
                    Reject(field, field + " must be a whole number.");
                }
            }
            if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                Reject(field, field + " is outside the Integer range.");
            }
            return n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>SQL string literal: control characters rejected, quotes doubled.</summary>
        internal static string Quote(string value, string field)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('\'');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["op"] = Op,
                            ["field"] = field,
                            ["index"] = i,
                            ["charCode"] = (int)c,
                            ["reason"] = field + " must not contain control characters (tab, line breaks, etc.) in a live detail-row write.",
                        });
                }
                if (c == '\'') sb.Append('\'');
                sb.Append(c);
            }
            sb.Append('\'');
            RequireAnsi(value, field);
            return sb.ToString();
        }

        // DBF text is stored in an ANSI code page; a character it can't hold is
        // converted by the driver, the read-back then can't match, and the row
        // is already in the file. Refuse it up front instead.
        private static readonly Encoding Ansi = Encoding.GetEncoding(1252,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        private static void RequireAnsi(string value, string field)
        {
            try
            {
                Ansi.GetBytes(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["op"] = Op,
                        ["field"] = field,
                        ["index"] = ex.Index,
                        ["reason"] = field + " contains a character Lacerte's detail file can't store (outside Windows-1252, e.g. an emoji or a non-Latin letter). Replace it and retry.",
                    });
            }
        }

        /// <summary>
        /// Live detail rows must carry a Suffix: the SDK says it starts at 1000
        /// for standard fields and at 1 for suffixed fields, so an omitted Suffix
        /// (left to the driver's default, likely 0) would write a row Lacerte
        /// doesn't use. Simulation keeps accepting a blank Suffix.
        /// </summary>
        public static void RequireLiveSuffix(DetailRow row)
        {
            if (row != null && string.IsNullOrWhiteSpace(row.Suffix))
            {
                Reject("Suffix", "Suffix is required for a live detail row: 1000 for a standard field, or the field's own suffix (starting at 1) for suffixed fields (Lacerte SDK). Take it from the field dictionary entry.");
            }
        }

        private static string Reject(string field, string reason)
        {
            throw new LacerteSDKError(SyntheticStatusCodes.DetailWriteInvalid,
                new Dictionary<string, object?>
                {
                    ["op"] = Op,
                    ["field"] = field,
                    ["reason"] = reason,
                });
        }

        // -------------------------------------------------------------------
        // Read-back comparison
        // -------------------------------------------------------------------

        /// <summary>
        /// Number of rows in <paramref name="rows"/> equal to <paramref name="written"/>
        /// on every column the INSERT writes (see <see cref="RowMatches"/>).
        /// </summary>
        public static int CountMatching(IEnumerable<DetailRow> rows, DetailRow written)
            => rows.Count(r => RowMatches(written, r));

        /// <summary>
        /// True when <paramref name="stored"/> carries the values <paramref name="written"/>
        /// put in the written columns. Integers compare numerically (a blank
        /// Prefix/Suffix — omitted from the INSERT — matches a stored blank or 0);
        /// strings compare trimmed and case-insensitively (DBF CHAR padding;
        /// a Description the driver truncated does NOT match); Amount compares
        /// numerically. ImportSrc is ignored (never written).
        /// </summary>
        public static bool RowMatches(DetailRow written, DetailRow stored)
        {
            if (written == null || stored == null) return false;
            return IntEquals(written.Series, stored.Series, blankMatchesZero: false)
                && IntEquals(written.Prefix, stored.Prefix, blankMatchesZero: true)
                && IntEquals(written.Code, stored.Code, blankMatchesZero: false)
                && IntEquals(written.Suffix, stored.Suffix, blankMatchesZero: true)
                && TextEquals(written.StateAbbr, stored.StateAbbr)
                && TextEquals(written.CityAbbr, stored.CityAbbr)
                && TextEquals(BridgeInputValidation.NormalizeDetailSource(written.Source) ?? written.Source,
                              BridgeInputValidation.NormalizeDetailSource(stored.Source) ?? stored.Source)
                && TextEquals(TsjForCompare(written.TSJ), TsjForCompare(stored.TSJ))
                && AmountEquals(written.Amount, stored.Amount)
                && TextEquals(written.Description, stored.Description);
        }

        private static bool IntEquals(string? written, string? stored, bool blankMatchesZero)
        {
            var w = ParseNumber(written);
            var s = ParseNumber(stored);
            if (w == null)
            {
                return s == null || (blankMatchesZero && s.Value == 0m);
            }
            return s != null && w.Value == s.Value;
        }

        private static decimal? ParseNumber(string? v)
        {
            var t = (v ?? string.Empty).Trim();
            if (t.Length == 0) return null;
            return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
        }

        // The SDK documents TSJ as "Joint (or blank)": a blank written TSJ the
        // driver stores as Joint (or vice versa) is the same value.
        private static string TsjForCompare(string? tsj)
        {
            var n = BridgeInputValidation.NormalizeTsj(tsj) ?? tsj ?? string.Empty;
            return string.IsNullOrWhiteSpace(n) ? "Joint" : n;
        }

        private static bool TextEquals(string? a, string? b)
            => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool AmountEquals(double? written, double? stored)
        {
            if (!written.HasValue) return true; // column not written
            if (!stored.HasValue) return false;
            try
            {
                return decimal.Round((decimal)written.Value, 10) == decimal.Round((decimal)stored.Value, 10);
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
