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
            var cols = BuildColumns(clientKey, row);
            return "INSERT INTO [" + clientKey + "-Detail] (" + string.Join(", ", cols.Select(c => c.Column))
                + ") VALUES (" + string.Join(", ", cols.Select(c => c.Literal)) + ")";
        }

        /// <summary>
        /// FP-2 restore — builds the DELETE that removes the rows equal to
        /// <paramref name="row"/> on EVERY column <see cref="BuildInsertSql"/>
        /// writes for it (same columns, same order, same literals):
        /// <c>DELETE FROM [&lt;KEY&gt;-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND ...</c>.
        /// Same validation as the INSERT, plus a Suffix is required (every live
        /// write carries one). An ODBC DELETE on a DBF removes EVERY row the
        /// WHERE matches — the caller counts the rows it could match first and
        /// re-inserts the copies that must stay (<c>lacerte-mcp detail-restore apply</c>).
        /// The detail-write worker runs a DELETE only when it is exactly this
        /// method's output (<see cref="TryParseDeleteSql"/>).
        /// </summary>
        public static string BuildDeleteSql(string clientKey, DetailRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            RequireLiveSuffix(row);
            var cols = BuildColumns(clientKey, row);
            return "DELETE FROM [" + clientKey + "-Detail] WHERE "
                + string.Join(" AND ", cols.Select(c => c.Column + " = " + c.Literal));
        }

        /// <summary>
        /// Parses a DELETE and accepts it only when it is byte-for-byte what
        /// <see cref="BuildDeleteSql"/> builds for the parsed client key and row:
        /// one statement on the detail table, every written column present in
        /// the canonical order with canonical literals, nothing else. Never throws.
        /// </summary>
        public static bool TryParseDeleteSql(string? sql, out string clientKey, out DetailRow? row)
        {
            clientKey = string.Empty;
            row = null;
            if (string.IsNullOrEmpty(sql)) return false;
            var s = sql!;
            const string head = "DELETE FROM [";
            const string tableTail = "-Detail] WHERE ";
            if (!s.StartsWith(head, StringComparison.Ordinal)) return false;
            int pos = head.Length;
            int keyEnd = s.IndexOf(tableTail, pos, StringComparison.Ordinal);
            if (keyEnd <= pos || keyEnd - pos > 8) return false;
            var key = s.Substring(pos, keyEnd - pos);
            foreach (var ch in key)
            {
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))) return false;
            }
            pos = keyEnd + tableTail.Length;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                int colStart = pos;
                while (pos < s.Length && ((s[pos] >= 'A' && s[pos] <= 'Z') || (s[pos] >= 'a' && s[pos] <= 'z'))) pos++;
                if (pos == colStart) return false;
                var col = s.Substring(colStart, pos - colStart);
                if (values.ContainsKey(col)) return false;
                if (pos + 3 > s.Length || string.CompareOrdinal(s, pos, " = ", 0, 3) != 0) return false;
                pos += 3;
                if (pos >= s.Length) return false;
                string value;
                if (s[pos] == '\'')
                {
                    var sb = new StringBuilder();
                    pos++;
                    bool closed = false;
                    while (pos < s.Length)
                    {
                        if (s[pos] == '\'')
                        {
                            if (pos + 1 < s.Length && s[pos + 1] == '\'') { sb.Append('\''); pos += 2; continue; }
                            pos++;
                            closed = true;
                            break;
                        }
                        sb.Append(s[pos]);
                        pos++;
                    }
                    if (!closed) return false;
                    value = sb.ToString();
                }
                else
                {
                    int numStart = pos;
                    while (pos < s.Length && ((s[pos] >= '0' && s[pos] <= '9') || s[pos] == '-' || s[pos] == '.')) pos++;
                    if (pos == numStart) return false;
                    value = s.Substring(numStart, pos - numStart);
                }
                values[col] = value;
                if (pos == s.Length) break;
                if (pos + 5 > s.Length || string.CompareOrdinal(s, pos, " AND ", 0, 5) != 0) return false;
                pos += 5;
            }

            string V(string c) => values.TryGetValue(c, out var v) ? v : string.Empty;
            double? amount = null;
            if (values.TryGetValue("Amount", out var a))
            {
                if (!double.TryParse(a, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return false;
                amount = d;
            }
            var parsed = new DetailRow(V("Series"), V("Prefix"), V("Code"), V("Suffix"), V("StateAbbr"), V("CityAbbr"),
                V("Source"), V("TSJ"), string.Empty, amount, V("Description"));
            try
            {
                // The canonical rebuild decides: column set, order, literals.
                if (!string.Equals(BuildDeleteSql(key, parsed), s, StringComparison.Ordinal)) return false;
            }
            catch (LacerteSDKError)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
            clientKey = key;
            row = parsed;
            return true;
        }

        /// <summary>
        /// The (column, SQL literal) pairs the INSERT writes for <paramref name="row"/>,
        /// in order. Shared by the INSERT and the DELETE so the two can never
        /// disagree about which columns identify a written row.
        /// </summary>
        private static List<(string Column, string Literal)> BuildColumns(string clientKey, DetailRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            SqlTemplates.ValidateClient(clientKey);
            // Same content rules the session applies in both modes (lengths,
            // numeric Series/Prefix/Code/Suffix, Source/TSJ vocabulary,
            // ImportSrc blank, Amount range/precision).
            BridgeInputValidation.ValidateDetailRowContent(row, Op);

            var cols = new List<(string Column, string Literal)>(10);
            cols.Add(("Series", RequiredInt(row.Series, "row.Series")));
            var prefix = OptionalInt(row.Prefix, "row.Prefix");
            if (prefix != null) cols.Add(("Prefix", prefix));
            cols.Add(("Code", RequiredInt(row.Code, "row.Code")));
            var suffix = OptionalInt(row.Suffix, "row.Suffix");
            if (suffix != null) cols.Add(("Suffix", suffix));

            cols.Add(("StateAbbr", Quote((row.StateAbbr ?? string.Empty).Trim(), "row.StateAbbr")));
            cols.Add(("CityAbbr", Quote((row.CityAbbr ?? string.Empty).Trim(), "row.CityAbbr")));
            cols.Add(("Source", Quote(BridgeInputValidation.NormalizeDetailSource(row.Source) ?? Reject("row.Source", "row.Source must be blank, Nonsource or Source."), "row.Source")));
            cols.Add(("TSJ", Quote(BridgeInputValidation.NormalizeTsj(row.TSJ) ?? Reject("row.TSJ", "row.TSJ must be blank, Taxpayer, Spouse or Joint."), "row.TSJ")));
            if (row.Amount.HasValue) cols.Add(("Amount", FormatAmount(row.Amount.Value)));
            cols.Add(("Description", Quote(row.Description ?? string.Empty, "row.Description")));
            return cols;
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

        // -------------------------------------------------------------------
        // Restore (lacerte-mcp detail-restore) helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// A detail row read over ODBC, normalized exactly like the server's live
        /// read path (strings trimmed, numbers invariant, Amount as double).
        /// </summary>
        public static DetailRow FromOdbcRow(IReadOnlyDictionary<string, object?> raw)
            => LacerteSessionManager.ToDetailRow(raw);

        /// <summary>
        /// True when the driver COULD remove <paramref name="stored"/> for the
        /// DELETE <see cref="BuildDeleteSql"/> builds from <paramref name="written"/>
        /// under ANY plausible comparison rule — a deliberate superset of
        /// <see cref="RowMatches"/>: strings trimmed, case-insensitive, and
        /// matching when either is a prefix of the other (xBase "SET ANSI OFF"
        /// semantics, where <c>Description = ''</c> matches everything); a blank
        /// Prefix (not in the WHERE) matches any Prefix; Amount within 1.00;
        /// unparsable stored numbers match. The restore refuses a row when this
        /// set holds anything but identical copies, so how the Lacerte driver
        /// really compares can't make it delete a row it can't put back.
        /// </summary>
        public static bool CouldDeleteMatch(DetailRow written, DetailRow stored)
        {
            if (written == null || stored == null) return false;
            return LooseInt(written.Series, stored.Series)
                && LooseInt(written.Prefix, stored.Prefix)
                && LooseInt(written.Code, stored.Code)
                && LooseInt(written.Suffix, stored.Suffix)
                && LooseText(written.StateAbbr, stored.StateAbbr)
                && LooseText(written.CityAbbr, stored.CityAbbr)
                && (LooseText(written.Source, stored.Source)
                    || TextEquals(BridgeInputValidation.NormalizeDetailSource(written.Source) ?? written.Source,
                                  BridgeInputValidation.NormalizeDetailSource(stored.Source) ?? stored.Source))
                && (LooseText(written.TSJ, stored.TSJ) || TextEquals(TsjForCompare(written.TSJ), TsjForCompare(stored.TSJ)))
                && LooseAmount(written.Amount, stored.Amount)
                && LooseText(written.Description, stored.Description);
        }

        /// <summary>
        /// Exact identity of a stored row over all eleven columns (numbers
        /// canonical, strings trimmed and case-SENSITIVE, ImportSrc included).
        /// Two rows with the same key are interchangeable copies.
        /// </summary>
        public static string StrictKey(DetailRow r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            string N(string? v)
            {
                var d = ParseNumber(v);
                return d.HasValue ? d.Value.ToString("0.############", CultureInfo.InvariantCulture) : (v ?? string.Empty).Trim();
            }
            string T(string? v) => (v ?? string.Empty).Trim();
            string amount;
            if (!r.Amount.HasValue) amount = "<null>";
            else
            {
                try { amount = ((decimal)r.Amount.Value).ToString("0.############", CultureInfo.InvariantCulture); }
                catch (OverflowException) { amount = r.Amount.Value.ToString("R", CultureInfo.InvariantCulture); }
            }
            return string.Join("\u001f", N(r.Series), N(r.Prefix), N(r.Code), N(r.Suffix), T(r.StateAbbr), T(r.CityAbbr),
                T(r.Source), T(r.TSJ), T(r.ImportSrc), amount, T(r.Description));
        }

        private static bool LooseInt(string? written, string? stored)
        {
            var w = ParseNumber(written);
            if (w == null) return true; // not in the WHERE
            var s = ParseNumber(stored);
            if (s == null) return string.IsNullOrWhiteSpace(stored) ? w.Value == 0m : true;
            return w.Value == s.Value;
        }

        private static bool LooseText(string? written, string? stored)
        {
            var w = (written ?? string.Empty).Trim();
            var s = (stored ?? string.Empty).Trim();
            return w.StartsWith(s, StringComparison.OrdinalIgnoreCase) || s.StartsWith(w, StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooseAmount(double? written, double? stored)
        {
            if (!written.HasValue) return true; // not in the WHERE
            if (!stored.HasValue) return Math.Abs(written.Value) < 1.0;
            return Math.Abs(written.Value - stored.Value) < 1.0;
        }
    }
}
