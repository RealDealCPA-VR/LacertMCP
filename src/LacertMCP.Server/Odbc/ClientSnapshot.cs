using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// FP-3 — the read seam <see cref="ClientSnapshotReader"/> runs its SQL
    /// through. Production wraps <see cref="LacerteDsnReader"/>
    /// (<see cref="DsnSnapshotSource"/>); tests inject a fake.
    /// </summary>
    public interface ISnapshotQuerySource : IDisposable
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql);
    }

    /// <summary>ODBC-backed <see cref="ISnapshotQuerySource"/> (read-only, like <see cref="LacerteDsnReader"/>).</summary>
    public sealed class DsnSnapshotSource : ISnapshotQuerySource
    {
        private readonly LacerteDsnReader _reader;

        public DsnSnapshotSource(string connectionString)
        {
            _reader = new LacerteDsnReader(connectionString);
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql) => _reader.Query(sql);

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>
    /// FP-3 — a point-in-time copy of one client's DATA1–DATA4 rows and detail
    /// rows, written by <c>lacerte-mcp map-diff snapshot</c> and compared by
    /// <c>map-diff compare / record</c>. Every cell is stored as a canonical
    /// invariant-culture string (see <see cref="ClientSnapshotReader.CanonicalValue"/>)
    /// so two snapshots compare by plain string equality.
    ///
    /// <para>It holds the client's real return data — use a test client and
    /// delete snapshot files when done.</para>
    /// </summary>
    public sealed class ClientSnapshot
    {
        public const string FormatId = "lacerte-mcp/map-diff-snapshot/1";

        [JsonPropertyName("format")] public string Format { get; set; } = FormatId;
        [JsonPropertyName("module")] public string Module { get; set; } = string.Empty;
        [JsonPropertyName("taxYear")] public int TaxYear { get; set; }
        [JsonPropertyName("client")] public string Client { get; set; } = string.Empty;
        [JsonPropertyName("takenAtUtc")] public string TakenAtUtc { get; set; } = string.Empty;
        [JsonPropertyName("machine")] public string? Machine { get; set; }
        [JsonPropertyName("dataPath")] public string? DataPath { get; set; }

        /// <summary><see cref="ClientSnapshotReader.TableFormTy21"/> or <see cref="ClientSnapshotReader.TableFormPreTy21"/>.</summary>
        [JsonPropertyName("tableForm")] public string TableForm { get; set; } = string.Empty;

        [JsonPropertyName("rows")] public List<SnapshotTableRow> Rows { get; set; } = new List<SnapshotTableRow>();
        [JsonPropertyName("detailRows")] public List<SnapshotDetailRow> DetailRows { get; set; } = new List<SnapshotDetailRow>();

        /// <summary>
        /// Reads that failed (key <c>DATA2</c>..<c>DATA4</c> or <c>detail</c>) — e.g. a
        /// TY21+ per-client table that does not exist yet. A failed read is
        /// "unknown", not "empty"; <c>map-diff record</c> refuses to map across it.
        /// </summary>
        [JsonPropertyName("readErrors")] public SortedDictionary<string, string> ReadErrors { get; set; }
            = new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>One DATA1–DATA4 row.</summary>
    public sealed class SnapshotTableRow
    {
        [JsonPropertyName("table")] public int Table { get; set; }

        /// <summary>Stable identity within the table: <c>client</c>, <c>sub=2</c>, <c>state=CA</c>, <c>state=CA|sub=2</c> (plus <c>#n</c> for duplicates).</summary>
        [JsonPropertyName("rowKey")] public string RowKey { get; set; } = string.Empty;
        [JsonPropertyName("subClient")] public string? SubClient { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }

        /// <summary>Column (upper case) → canonical value (null = DB null).</summary>
        [JsonPropertyName("cells")] public SortedDictionary<string, string?> Cells { get; set; }
            = new SortedDictionary<string, string?>(StringComparer.Ordinal);
    }

    /// <summary>One detail-file row (columns per the spec's detail-file layout).</summary>
    public sealed class SnapshotDetailRow
    {
        [JsonPropertyName("series")] public string Series { get; set; } = string.Empty;
        [JsonPropertyName("prefix")] public string Prefix { get; set; } = string.Empty;
        [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
        [JsonPropertyName("suffix")] public string Suffix { get; set; } = string.Empty;
        [JsonPropertyName("stateAbbr")] public string StateAbbr { get; set; } = string.Empty;
        [JsonPropertyName("cityAbbr")] public string CityAbbr { get; set; } = string.Empty;
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("tsj")] public string Tsj { get; set; } = string.Empty;
        [JsonPropertyName("importSrc")] public string ImportSrc { get; set; } = string.Empty;
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

        /// <summary>Where the row lives (Series/Prefix/Code/Suffix/State/City).</summary>
        [JsonIgnore]
        public string IdentityKey => string.Join("|", Series, Prefix, Code, Suffix, StateAbbr, CityAbbr);

        /// <summary>The whole row.</summary>
        [JsonIgnore]
        public string ContentKey => string.Join("|", IdentityKey, Source, Tsj, ImportSrc, Amount ?? "<null>", Description);
    }

    /// <summary>
    /// FP-3 — reads a <see cref="ClientSnapshot"/> over the Lacerte ODBC
    /// driver using the same <see cref="SqlTemplates"/> the server's live
    /// reader uses: TY21+ per-client tables for DATA2–DATA4 when
    /// <c>tableForm</c> is <see cref="TableFormTy21"/>, the global pre-TY21
    /// tables (filtered by client) otherwise. Read-only.
    /// </summary>
    public static class ClientSnapshotReader
    {
        public const string TableFormTy21 = "ty21";
        public const string TableFormPreTy21 = "pre-ty21";

        /// <summary>
        /// Table form to use when the operator does not choose: the SDK's
        /// per-client TY21+ tables from TY2021 on. (The MCP server asks the SDK's
        /// <c>CanUseNewApis</c> over COM instead; the CLI avoids COM, so it
        /// offers <c>--table-form</c> to override.)
        /// </summary>
        public static string DefaultTableForm(int taxYear) => taxYear >= 2021 ? TableFormTy21 : TableFormPreTy21;

        /// <summary>
        /// Reads the snapshot. Throws <see cref="LacerteSDKError"/> for an invalid
        /// client number or a failed DATA1 read, and <see cref="InvalidOperationException"/>
        /// when the client is not in DATA1. DATA2–DATA4 and detail read failures
        /// are recorded in <see cref="ClientSnapshot.ReadErrors"/> instead.
        /// </summary>
        public static ClientSnapshot Read(
            ISnapshotQuerySource source, char module, int taxYear, string client, string tableForm,
            string? dataPath, string? machine, DateTime takenAtUtc)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (Modules.GetByChar(module) == null)
                throw new ArgumentException($"Unknown module '{module}'. Expected one of I/C/P/S/F/R/T/N/B.", nameof(module));
            if (tableForm != TableFormTy21 && tableForm != TableFormPreTy21)
                throw new ArgumentException($"tableForm must be '{TableFormTy21}' or '{TableFormPreTy21}'.", nameof(tableForm));
            var key = (client ?? string.Empty).Trim().ToUpperInvariant();
            SqlTemplates.ValidateClient(key);

            var snap = new ClientSnapshot
            {
                Module = module.ToString(),
                TaxYear = taxYear,
                Client = key,
                TakenAtUtc = takenAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Machine = machine,
                DataPath = dataPath,
                TableForm = tableForm,
            };
            var ty21 = tableForm == TableFormTy21;

            // DATA1 — the client must exist; a failure here is fatal.
            var data1 = source.Query(SqlTemplates.Data1Where(key));
            if (data1.Count == 0)
            {
                throw new InvalidOperationException($"Client '{key}' was not found in DATA1 for this data path / year / module.");
            }
            AddRows(snap, 1, data1);

            // Same table choice as LacerteSessionManager.Live's per-client reads.
            TryAddRows(snap, 2, () => source.Query(ty21 ? SqlTemplates.Data2NewForm(key) : SqlTemplates.Data2OldForm(key)));
            TryAddRows(snap, 3, () => source.Query(ty21 ? SqlTemplates.Data3NewForm(key) : SqlTemplates.Data3OldForm(key)));
            TryAddRows(snap, 4, () => source.Query(ty21 ? SqlTemplates.Data4NewForm(key) : SqlTemplates.Data4OldFormAllStates(key)));

            try
            {
                foreach (var raw in source.Query(SqlTemplates.DetailAll(key)))
                {
                    snap.DetailRows.Add(ToDetail(raw));
                }
                snap.DetailRows.Sort((a, b) => string.CompareOrdinal(a.ContentKey, b.ContentKey));
            }
            catch (LacerteSDKError ex)
            {
                snap.DetailRows.Clear();
                snap.ReadErrors["detail"] = Describe(ex);
            }
            return snap;
        }

        private static void TryAddRows(ClientSnapshot snap, int table, Func<IReadOnlyList<IReadOnlyDictionary<string, object?>>> read)
        {
            try
            {
                AddRows(snap, table, read());
            }
            catch (LacerteSDKError ex)
            {
                snap.Rows.RemoveAll(r => r.Table == table);
                snap.ReadErrors["DATA" + table] = Describe(ex);
            }
        }

        private static string Describe(LacerteSDKError ex)
            => $"{ex.StatusCode} {ex.StatusName}: {ex.UserMessage}";

        private static void AddRows(ClientSnapshot snap, int table, IReadOnlyList<IReadOnlyDictionary<string, object?>> raw)
        {
            var rows = new List<SnapshotTableRow>();
            foreach (var r in raw)
            {
                var row = new SnapshotTableRow { Table = table };
                foreach (var kv in r)
                {
                    row.Cells[kv.Key.Trim().ToUpperInvariant()] = CanonicalValue(kv.Value);
                }
                string? Cell(params string[] names)
                {
                    foreach (var n in names)
                    {
                        if (row.Cells.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v!.Trim();
                    }
                    return null;
                }
                switch (table)
                {
                    case 1:
                        row.RowKey = "client";
                        break;
                    case 2:
                        row.SubClient = Cell("C2_1", "SUBCLIENTID", "SUBCLIENTNUMBER");
                        row.RowKey = "sub=" + (row.SubClient ?? "?");
                        break;
                    case 3:
                        row.State = Cell("C3_G1")?.ToUpperInvariant();
                        row.RowKey = "state=" + (row.State ?? "?");
                        break;
                    case 4:
                        row.State = Cell("C4_G1")?.ToUpperInvariant();
                        row.SubClient = Cell("C4_1", "SUBCLIENTID", "SUBCLIENTNUMBER");
                        row.RowKey = "state=" + (row.State ?? "?") + "|sub=" + (row.SubClient ?? "?");
                        break;
                }
                rows.Add(row);
            }

            // Disambiguate rows that share an identity (should not happen; kept
            // deterministic by ordering duplicates on their content).
            foreach (var group in rows.GroupBy(r => r.RowKey, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
            {
                var ordered = group.OrderBy(r => string.Join("\u001f", r.Cells.Select(c => c.Key + "=" + c.Value)), StringComparer.Ordinal).ToList();
                for (var i = 0; i < ordered.Count; i++) ordered[i].RowKey += "#" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            snap.Rows.AddRange(rows.OrderBy(r => r.RowKey, StringComparer.Ordinal));
        }

        private static SnapshotDetailRow ToDetail(IReadOnlyDictionary<string, object?> raw)
        {
            string S(string name)
            {
                foreach (var kv in raw)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return CanonicalValue(kv.Value)?.Trim() ?? string.Empty;
                    }
                }
                return string.Empty;
            }
            var amount = S("Amount");
            return new SnapshotDetailRow
            {
                Series = S("Series"),
                Prefix = S("Prefix"),
                Code = S("Code"),
                Suffix = S("Suffix"),
                StateAbbr = S("StateAbbr"),
                CityAbbr = S("CityAbbr"),
                Source = S("Source"),
                Tsj = S("TSJ"),
                ImportSrc = S("ImportSrc"),
                Amount = amount.Length == 0 ? null : amount,
                Description = S("Description"),
            };
        }

        /// <summary>
        /// Canonical invariant string for an ODBC value: DB null → null; CHAR
        /// right-trimmed (DBF pads with spaces); DATE → <c>yyyy-MM-dd</c> (time
        /// kept when non-midnight); numbers without trailing zeros; bool →
        /// <c>true</c>/<c>false</c>; bytes → base64.
        /// </summary>
        public static string? CanonicalValue(object? v)
        {
            switch (v)
            {
                case null: return null;
                case DBNull _: return null;
                case string s: return s.TrimEnd(' ', '\0');
                case DateTime dt:
                    return dt.TimeOfDay == TimeSpan.Zero
                        ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case decimal d: return d.ToString("0.############", CultureInfo.InvariantCulture);
                case double db: return db.ToString("0.############", CultureInfo.InvariantCulture);
                case float f: return ((double)f).ToString("0.######", CultureInfo.InvariantCulture);
                case byte[] bytes: return Convert.ToBase64String(bytes);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        // ---- JSON ------------------------------------------------------------------------

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string ToJson(ClientSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

        /// <summary>Parses a snapshot file; throws <see cref="JsonException"/> when it is not one.</summary>
        public static ClientSnapshot FromJson(string json)
        {
            var snap = JsonSerializer.Deserialize<ClientSnapshot>(json, JsonOptions)
                       ?? throw new JsonException("snapshot file is empty.");
            if (snap.Format != ClientSnapshot.FormatId)
                throw new JsonException($"not a map-diff snapshot (format '{snap.Format}', expected '{ClientSnapshot.FormatId}').");
            if (snap.Module.Length != 1 || Modules.GetByChar(snap.Module[0]) == null)
                throw new JsonException($"snapshot has an invalid module '{snap.Module}'.");
            snap.Rows ??= new List<SnapshotTableRow>();
            snap.DetailRows ??= new List<SnapshotDetailRow>();
            snap.ReadErrors ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in snap.Rows)
            {
                if (r == null) throw new JsonException("snapshot has a null row.");
                r.Cells ??= new SortedDictionary<string, string?>(StringComparer.Ordinal);
            }
            if (snap.DetailRows.Any(d => d == null)) throw new JsonException("snapshot has a null detail row.");
            return snap;
        }
    }
}
