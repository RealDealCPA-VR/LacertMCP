using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// Outcome of reading a field back after a write.
    /// <see cref="Verified"/>: true = read-back equals what was written;
    /// false = it does not (caller must surface 9022 WriteNotVerified);
    /// null = the row can't be identified unambiguously, so no claim is made
    /// either way (<see cref="Note"/> says why).
    /// </summary>
    public sealed record FieldWriteVerification(bool? Verified, object? ReadBack, string? Note);

    /// <summary>
    /// Read-back verification for field writes. A Success status from the
    /// SDK only means "accepted"; this re-reads the cell (live: over a FRESH
    /// ODBC connection so no driver cache can hide a failed write) and
    /// compares it with what was written.
    /// </summary>
    public sealed partial class LacerteSessionManager
    {
        /// <summary>
        /// Reads <paramref name="field"/> back for the row the write targeted and
        /// compares it with <paramref name="written"/>. Mode-agnostic: sim reads the
        /// in-memory store, live reads ODBC.
        /// </summary>
        public async Task<FieldWriteVerification> VerifyFieldWrite(
            int table, string clientNumber, string field, object? written,
            int? subClientId, string? stateAbbrev,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = RequireContext();
            var state = (table == 3 || table == 4) ? stateAbbrev : null;

            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
            if (Mode == SessionMode.Live)
            {
                rows = LiveReadTable(ctx, table, clientNumber, state, fresh: true);
            }
            else
            {
                rows = await ReadTable(table, clientNumber, state, cancellationToken).ConfigureAwait(false);
            }

            if (table == 2 || table == 4)
            {
                if (rows.Any(r => r.ContainsKey("subClientId")))
                {
                    rows = rows.Where(r => r.TryGetValue("subClientId", out var v) && v != null
                        && Convert.ToString(v, CultureInfo.InvariantCulture) == Convert.ToString(subClientId, CultureInfo.InvariantCulture))
                        .ToList();
                }
                else if (rows.Count > 1)
                {
                    return new FieldWriteVerification(null, null,
                        "Not verified: the subclient rows carry no subClientId column to pick the written row.");
                }
            }

            if (rows.Count == 0)
            {
                return new FieldWriteVerification(false, null, "No row found for this client/state/subclient after the write.");
            }
            if (rows.Count > 1)
            {
                return new FieldWriteVerification(null, null,
                    "Not verified: " + rows.Count + " rows match this target, so the written row can't be identified.");
            }

            var row = rows[0];
            object? readBack = null;
            bool found = false;
            foreach (var kv in row)
            {
                if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
                {
                    readBack = kv.Value;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return new FieldWriteVerification(false, null, "Field " + field + " is not present on the row read back.");
            }

            return ValuesMatch(written, readBack, out var note)
                ? new FieldWriteVerification(true, readBack, note)
                : new FieldWriteVerification(false, readBack, "Read-back value differs from the value written.");
        }

        /// <summary>
        /// Lenient-but-safe equality between a written value and the stored one:
        /// exact after trimming; numerically equal; case-only differences
        /// (Lacerte upper-cases some fields) and equal calendar dates in
        /// different formats count as a match with an explanatory note.
        /// </summary>
        internal static bool ValuesMatch(object? written, object? stored, out string? note)
        {
            note = null;
            var w = AsText(written);
            var s = AsText(stored);
            if (w.Length == 0 && s.Length == 0) return true;
            if (string.Equals(w, s, StringComparison.Ordinal)) return true;

            // Leading zeros are significant (ZIP, SSN, EIN, client codes):
            // "07001" vs "7001" must NOT count as numerically equal.
            if (!HasSignificantLeadingZero(w) && !HasSignificantLeadingZero(s)
                && TryNumber(w, out var wn) && TryNumber(s, out var sn))
            {
                return wn == sn;
            }
            if (TryDate(w, out var wd) && TryDate(s, out var sd))
            {
                if (wd == sd) { note = "Stored as " + s + " (same date)."; return true; }
                return false;
            }
            if (string.Equals(w, s, StringComparison.OrdinalIgnoreCase))
            {
                note = "Lacerte stored it as " + s + " (letter case changed).";
                return true;
            }
            if (written is bool b)
            {
                // Checkbox-style fields commonly store X / Y / 1 for true and blank / N / 0 for false.
                var truthy = s == "X" || s == "Y" || s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
                var falsy = s.Length == 0 || s == "N" || s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase);
                if (b ? truthy : falsy) { note = "Stored as '" + s + "'."; return true; }
            }
            return false;
        }

        private static string AsText(object? v)
        {
            switch (v)
            {
                case null: return string.Empty;
                case string str: return str.Trim();
                case System.Text.Json.JsonElement je:
                    return je.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (je.GetString() ?? string.Empty).Trim()
                        : je.ToString().Trim();
                case bool bo: return bo ? "true" : "false";
                case DateTime dt: return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture).Trim();
                default: return (v.ToString() ?? string.Empty).Trim();
            }
        }

        private static bool HasSignificantLeadingZero(string s)
            => s.Length > 1 && s[0] == '0' && s[1] != '.';

        private static bool TryNumber(string s, out decimal d)
        {
            d = 0;
            if (s.Length == 0) return false;
            var cleaned = s.Replace(",", string.Empty).Replace("$", string.Empty);
            return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out d);
        }

        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "MM/dd/yyyy", "M/d/yyyy", "MMddyyyy", "yyyyMMdd", "MM-dd-yyyy",
        };

        private static bool TryDate(string s, out DateTime d)
        {
            return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }
    }
}
