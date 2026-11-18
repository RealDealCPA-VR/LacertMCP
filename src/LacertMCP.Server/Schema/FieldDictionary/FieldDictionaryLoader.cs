using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace LacertMCP.Server.Schema
{
    /// <summary>
    /// FP-3 — one verified mapping from a tax concept (e.g. "1120 gross
    /// receipts") to where Lacerte stores it: a DATA1–DATA4 column
    /// (<c>kind = "field"</c>) or a detail-file Series/Code
    /// (<c>kind = "detail"</c>). Entries are only ever added after the mapping
    /// was observed on a real Lacerte install (<c>lacerte-mcp map-diff</c>);
    /// see <c>Schema/FieldDictionary/README.md</c>.
    /// </summary>
    public sealed class FieldDictionaryEntry
    {
        public FieldDictionaryEntry(
            string id, string concept, string form, string? line, string kind,
            int? table, string? field, int? series, int? code, int? prefix, int? suffix,
            bool needsSubclient, bool needsState, string dataType, string? notes,
            IReadOnlyList<int>? taxYears, string verifiedOn, string source)
        {
            Id = id;
            Concept = concept;
            Form = form;
            Line = line;
            Kind = kind;
            Table = table;
            Field = field;
            Series = series;
            Code = code;
            Prefix = prefix;
            Suffix = suffix;
            NeedsSubclient = needsSubclient;
            NeedsState = needsState;
            DataType = dataType;
            Notes = notes;
            TaxYears = taxYears ?? Array.Empty<int>();
            VerifiedOn = verifiedOn;
            Source = source;
        }

        /// <summary>Stable identifier, e.g. <c>1120.gross_receipts</c>.</summary>
        public string Id { get; }
        /// <summary>Plain-English concept.</summary>
        public string Concept { get; }
        /// <summary>IRS / state form, e.g. <c>1120</c>.</summary>
        public string Form { get; }
        /// <summary>Form line or box (optional).</summary>
        public string? Line { get; }
        /// <summary><c>field</c> or <c>detail</c>.</summary>
        public string Kind { get; }
        /// <summary>kind=field: DATA table 1..4.</summary>
        public int? Table { get; }
        /// <summary>kind=field: column name, upper case (e.g. <c>C1_37</c>).</summary>
        public string? Field { get; }
        /// <summary>kind=detail: Series (Lacerte input screen).</summary>
        public int? Series { get; }
        /// <summary>kind=detail: Code (input field on the screen).</summary>
        public int? Code { get; }
        /// <summary>kind=detail: observed Prefix (optional).</summary>
        public int? Prefix { get; }
        /// <summary>kind=detail: observed Suffix (optional).</summary>
        public int? Suffix { get; }
        public bool NeedsSubclient { get; }
        public bool NeedsState { get; }
        /// <summary>One of <see cref="FieldDictionaryLoader.DataTypes"/>.</summary>
        public string DataType { get; }
        public string? Notes { get; }
        /// <summary>Tax years the mapping was verified for; empty = every year.</summary>
        public IReadOnlyList<int> TaxYears { get; }
        /// <summary>e.g. <c>TY2024 on LACERTE-PC, 2026-11-20</c>.</summary>
        public string VerifiedOn { get; }
        /// <summary><c>map-diff</c> or <c>manual</c>.</summary>
        public string Source { get; }

        public bool IsField => string.Equals(Kind, FieldDictionaryLoader.KindField, StringComparison.Ordinal);

        /// <summary>True when the entry applies to <paramref name="taxYear"/> (null = any year).</summary>
        public bool AppliesToYear(int? taxYear)
            => taxYear == null || TaxYears.Count == 0 || TaxYears.Contains(taxYear.Value);

        /// <summary>
        /// The storage target the entry points at — two entries with the same
        /// target and overlapping years are a conflict.
        /// </summary>
        public string TargetKey => IsField
            ? "field|" + Table!.Value.ToString(CultureInfo.InvariantCulture) + "|" + Field
            : "detail|" + Series!.Value.ToString(CultureInfo.InvariantCulture)
              + "|" + Code!.Value.ToString(CultureInfo.InvariantCulture)
              + "|" + (Prefix.HasValue ? Prefix.Value.ToString(CultureInfo.InvariantCulture) : "-")
              + "|" + (Suffix.HasValue ? Suffix.Value.ToString(CultureInfo.InvariantCulture) : "-");

        /// <summary>Human-readable target, e.g. <c>DATA1 C1_37</c> or <c>detail Series 11 Code 1</c>.</summary>
        public string DescribeTarget()
        {
            if (IsField) return "DATA" + Table + " " + Field;
            var sb = new StringBuilder("detail Series " + Series + " Code " + Code);
            if (Prefix.HasValue) sb.Append(" Prefix " + Prefix);
            if (Suffix.HasValue) sb.Append(" Suffix " + Suffix);
            return sb.ToString();
        }

        /// <summary>
        /// Ordered camelCase projection (absent optionals omitted) — the shape
        /// both the on-disk file and the <c>lacerte_field_dictionary</c> tool use.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, object?>> ToOrderedProperties()
        {
            var p = new List<KeyValuePair<string, object?>>
            {
                new KeyValuePair<string, object?>("id", Id),
                new KeyValuePair<string, object?>("concept", Concept),
                new KeyValuePair<string, object?>("form", Form),
            };
            if (Line != null) p.Add(new KeyValuePair<string, object?>("line", Line));
            p.Add(new KeyValuePair<string, object?>("kind", Kind));
            if (IsField)
            {
                p.Add(new KeyValuePair<string, object?>("table", Table));
                p.Add(new KeyValuePair<string, object?>("field", Field));
            }
            else
            {
                p.Add(new KeyValuePair<string, object?>("series", Series));
                p.Add(new KeyValuePair<string, object?>("code", Code));
                if (Prefix.HasValue) p.Add(new KeyValuePair<string, object?>("prefix", Prefix));
                if (Suffix.HasValue) p.Add(new KeyValuePair<string, object?>("suffix", Suffix));
            }
            p.Add(new KeyValuePair<string, object?>("needsSubclient", NeedsSubclient));
            p.Add(new KeyValuePair<string, object?>("needsState", NeedsState));
            p.Add(new KeyValuePair<string, object?>("dataType", DataType));
            if (Notes != null) p.Add(new KeyValuePair<string, object?>("notes", Notes));
            if (TaxYears.Count > 0) p.Add(new KeyValuePair<string, object?>("taxYears", TaxYears.ToArray()));
            p.Add(new KeyValuePair<string, object?>("verifiedOn", VerifiedOn));
            p.Add(new KeyValuePair<string, object?>("source", Source));
            return p;
        }

        /// <summary>The ordered projection as a dictionary (for tool envelopes).</summary>
        public Dictionary<string, object?> ToDictionary()
        {
            var d = new Dictionary<string, object?>();
            foreach (var kv in ToOrderedProperties()) d[kv.Key] = kv.Value;
            return d;
        }
    }

    /// <summary>One module's parsed dictionary file.</summary>
    public sealed class FieldDictionaryFile
    {
        public FieldDictionaryFile(char module, IReadOnlyList<FieldDictionaryEntry> entries)
        {
            Module = module;
            Entries = entries;
        }

        public char Module { get; }
        public IReadOnlyList<FieldDictionaryEntry> Entries { get; }
    }

    /// <summary>Cached result of loading one module's dictionary from disk.</summary>
    public sealed class FieldDictionaryLoadResult
    {
        public FieldDictionaryLoadResult(char module, string path, bool fileExists,
            IReadOnlyList<FieldDictionaryEntry> entries, string? error)
        {
            Module = module;
            Path = path;
            FileExists = fileExists;
            Entries = entries;
            Error = error;
            var index = new Dictionary<string, List<FieldDictionaryEntry>>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (!e.IsField) continue;
                var key = FieldDictionaryLoader.FieldIndexKey(e.Table!.Value, e.Field!);
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<FieldDictionaryEntry>();
                list.Add(e);
            }
            FieldIndex = index;
        }

        public char Module { get; }
        public string Path { get; }
        public bool FileExists { get; }

        /// <summary>Entries in file order; empty when the file is missing or invalid.</summary>
        public IReadOnlyList<FieldDictionaryEntry> Entries { get; }

        /// <summary>Non-null when the file exists but failed validation (entries are then empty — fail-closed).</summary>
        public string? Error { get; }

        internal IReadOnlyDictionary<string, List<FieldDictionaryEntry>> FieldIndex { get; }
    }

    /// <summary>
    /// FP-3 — strict, cached loader for <c>Schema/FieldDictionary/&lt;module&gt;.json</c>.
    ///
    /// <para>
    /// The dictionary is safety-relevant: <see cref="FieldAccessMapLoader"/>
    /// consults it to decide whether the SDK range rules may grant access to a
    /// field that has no literal access-map entry (FP-1a). So parsing is
    /// strict — unknown properties, unknown <c>kind</c> / <c>dataType</c> /
    /// <c>source</c>, missing required properties, a module mismatch,
    /// duplicate ids, or two entries pointing at the same target for
    /// overlapping years all reject the WHOLE file. A rejected or missing file
    /// loads as zero entries (fail-closed: no range-rule fallback for that
    /// module), and the error is kept on <see cref="FieldDictionaryLoadResult.Error"/>
    /// so <c>lacerte_field_dictionary</c> can surface it.
    /// </para>
    ///
    /// <para>
    /// Files live next to the access map (<c>{assemblyDir}/Schema/FieldDictionary</c>,
    /// a sibling of the folder <see cref="FieldAccessMapLoader.GetSchemaRoot"/>
    /// resolves) and are copied to the build output by the server csproj.
    /// Caching mirrors <see cref="FieldAccessMapLoader"/>: one immutable
    /// snapshot swapped atomically by <see cref="ReloadCaches"/> (which
    /// <see cref="FieldAccessMapLoader.ReloadCaches"/> also calls, so
    /// <c>lacerte_reload_schema</c> picks up a newly recorded mapping).
    /// </para>
    /// </summary>
    public static class FieldDictionaryLoader
    {
        public const string KindField = "field";
        public const string KindDetail = "detail";
        public const string SourceMapDiff = "map-diff";
        public const string SourceManual = "manual";

        public static readonly IReadOnlyList<string> Kinds = new[] { KindField, KindDetail };
        public static readonly IReadOnlyList<string> DataTypes = new[] { "amount", "text", "date", "bool", "integer", "percent" };
        public static readonly IReadOnlyList<string> Sources = new[] { SourceMapDiff, SourceManual };

        /// <summary>Folder name under <c>Schema/</c>.</summary>
        public const string FolderName = "FieldDictionary";

        private static readonly Regex IdPattern = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}\\z", RegexOptions.CultureInvariant);

        // C{table}_{n} or C{table}_G{n}, tables 1..4 only (the dictionary maps
        // input fields; DATA5-7 are status / display / forms tables).
        private static readonly Regex FieldPattern = new Regex(
            "^C([1-4])_(G?)([0-9]{1,5})\\z", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> CommonProps = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "concept", "form", "line", "kind", "needsSubclient", "needsState",
            "dataType", "notes", "taxYears", "verifiedOn", "source",
        };
        private static readonly HashSet<string> FieldProps = new HashSet<string>(StringComparer.Ordinal) { "table", "field" };
        private static readonly HashSet<string> DetailProps = new HashSet<string>(StringComparer.Ordinal) { "series", "code", "prefix", "suffix" };

        // ---- cache -------------------------------------------------------------------

        private sealed class DictionarySnapshot
        {
            public ConcurrentDictionary<string, FieldDictionaryLoadResult> Modules { get; }
                = new ConcurrentDictionary<string, FieldDictionaryLoadResult>(StringComparer.OrdinalIgnoreCase);

            // The shipped folder, resolved once per generation: resolving it
            // probes the disk, and FieldAccessMapLoader asks the dictionary on
            // every lookup that has no literal access-map entry.
            public Lazy<string> DefaultRoot { get; } = new Lazy<string>(ResolveDefaultRoot, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private static DictionarySnapshot _snapshot = new DictionarySnapshot();

        /// <summary>
        /// Test seam: when set (per async flow), the dictionary is read from this
        /// folder instead of the shipped one. The cache is keyed by folder, so a
        /// test's temp dictionary never leaks into another flow's lookups.
        /// </summary>
        internal static readonly AsyncLocal<string?> RootOverrideForTests = new AsyncLocal<string?>();

        /// <summary>Absolute path of the dictionary folder the loader reads.</summary>
        public static string GetRoot() => RootFor(Volatile.Read(ref _snapshot));

        private static string RootFor(DictionarySnapshot snap)
        {
            var overridden = RootOverrideForTests.Value;
            return !string.IsNullOrEmpty(overridden) ? overridden! : snap.DefaultRoot.Value;
        }

        private static string ResolveDefaultRoot()
        {
            var accessMapRoot = FieldAccessMapLoader.GetSchemaRoot();
            var schemaDir = Path.GetDirectoryName(accessMapRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return Path.Combine(string.IsNullOrEmpty(schemaDir) ? accessMapRoot : schemaDir!, FolderName);
        }

        /// <summary>Path of one module's dictionary file under <paramref name="root"/>, or null for an unknown module.</summary>
        public static string? FilePathFor(char module, string root)
        {
            var basename = Modules.FileBasenameFor(module);
            return basename == null ? null : Path.Combine(root, basename + ".json");
        }

        /// <summary>Loads (or returns the cached) dictionary for <paramref name="module"/>.</summary>
        public static FieldDictionaryLoadResult Load(char module)
        {
            var snap = Volatile.Read(ref _snapshot);
            var root = RootFor(snap);
            return snap.Modules.GetOrAdd(root + "|" + module, _ => LoadFromDisk(module, root));
        }

        /// <summary>All entries for the module (empty when missing or invalid).</summary>
        public static IReadOnlyList<FieldDictionaryEntry> GetEntries(char module) => Load(module).Entries;

        /// <summary>
        /// The kind=field entry for this exact (table, field) that applies to
        /// <paramref name="taxYear"/>, or null. Field matching is case-insensitive.
        /// </summary>
        public static FieldDictionaryEntry? FindFieldEntry(char module, int table, string field, int taxYear)
        {
            if (string.IsNullOrEmpty(field)) return null;
            var loaded = Load(module);
            if (loaded.FieldIndex.Count == 0) return null;
            if (!loaded.FieldIndex.TryGetValue(FieldIndexKey(table, field.ToUpperInvariant()), out var list)) return null;
            foreach (var e in list)
            {
                if (e.AppliesToYear(taxYear)) return e;
            }
            return null;
        }

        /// <summary>
        /// Every kind=field entry for (module, table) that applies to
        /// <paramref name="taxYear"/>. Used to list dictionary-gated fields in
        /// the access-map catalog.
        /// </summary>
        public static IEnumerable<FieldDictionaryEntry> GetFieldEntries(char module, int table, int taxYear)
            => Load(module).Entries.Where(e => e.IsField && e.Table == table && e.AppliesToYear(taxYear));

        /// <summary>Forget every cached dictionary. Returns how many module entries were evicted.</summary>
        public static int ReloadCaches()
        {
            var prior = Interlocked.Exchange(ref _snapshot, new DictionarySnapshot());
            return prior.Modules.Count;
        }

        internal static string FieldIndexKey(int table, string upperField)
            => table.ToString(CultureInfo.InvariantCulture) + "|" + upperField;

        private static FieldDictionaryLoadResult LoadFromDisk(char module, string root)
        {
            var path = FilePathFor(module, root);
            if (path == null)
            {
                return new FieldDictionaryLoadResult(module, string.Empty, false, Array.Empty<FieldDictionaryEntry>(), null);
            }
            if (!File.Exists(path))
            {
                return new FieldDictionaryLoadResult(module, path, false, Array.Empty<FieldDictionaryEntry>(), null);
            }
            try
            {
                var parsed = Parse(File.ReadAllText(path), module);
                return new FieldDictionaryLoadResult(module, path, true, parsed.Entries, null);
            }
            catch (JsonException ex)
            {
                // Fail closed: an invalid dictionary grants nothing.
                return new FieldDictionaryLoadResult(module, path, true, Array.Empty<FieldDictionaryEntry>(), ex.Message);
            }
            catch (IOException ex)
            {
                return new FieldDictionaryLoadResult(module, path, true, Array.Empty<FieldDictionaryEntry>(), ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new FieldDictionaryLoadResult(module, path, true, Array.Empty<FieldDictionaryEntry>(), ex.Message);
            }
        }

        // ---- validation (the ONE parse path) -------------------------------------------

        /// <summary>
        /// Validates a dictionary file exactly as the loader reads it. Returns the
        /// parsed file, or null with <paramref name="error"/> set.
        /// </summary>
        public static FieldDictionaryFile? ValidateFile(string path, char? expectedModule, out string? error)
        {
            error = null;
            try
            {
                return Parse(File.ReadAllText(path), expectedModule);
            }
            catch (JsonException ex) { error = ex.Message; return null; }
            catch (IOException ex) { error = ex.Message; return null; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; return null; }
        }

        /// <summary>
        /// Strict parse. Throws <see cref="JsonException"/> for anything the
        /// loader must reject (see the class summary).
        /// </summary>
        public static FieldDictionaryFile Parse(string json, char? expectedModule)
        {
            if (json == null) throw new JsonException("dictionary text is null.");
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            }
            catch (JsonException ex)
            {
                throw new JsonException("not valid JSON: " + ex.Message, ex);
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the root must be an object { \"module\": ..., \"entries\": [...] }.");
                var rootProps = ReadProperties(root, "root");
                foreach (var name in rootProps.Keys)
                {
                    if (name != "module" && name != "entries") throw new JsonException($"root: unknown property '{name}'.");
                }

                if (!rootProps.TryGetValue("module", out var moduleEl) || moduleEl.ValueKind != JsonValueKind.String)
                    throw new JsonException("root: 'module' (a single module character) is required.");
                var moduleStr = moduleEl.GetString() ?? string.Empty;
                if (moduleStr.Length != 1 || Modules.GetByChar(moduleStr[0]) == null)
                    throw new JsonException($"root: 'module' must be one of {string.Join("/", Modules.All.Select(m => m.Char))} (got '{moduleStr}').");
                var module = moduleStr[0];
                if (expectedModule.HasValue && expectedModule.Value != module)
                    throw new JsonException($"root: 'module' is '{module}' but this file belongs to module '{expectedModule.Value}'.");

                if (!rootProps.TryGetValue("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
                    throw new JsonException("root: 'entries' (an array) is required.");

                var entries = new List<FieldDictionaryEntry>();
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                foreach (var el in entriesEl.EnumerateArray())
                {
                    var entry = ParseEntry(el, index);
                    if (!ids.Add(entry.Id)) throw new JsonException($"entries[{index}]: duplicate id '{entry.Id}'.");
                    foreach (var other in entries)
                    {
                        if (other.TargetKey == entry.TargetKey && YearsOverlap(other, entry))
                        {
                            throw new JsonException(
                                $"entries[{index}] ('{entry.Id}'): same target ({entry.DescribeTarget()}) as '{other.Id}' for overlapping tax years.");
                        }
                    }
                    entries.Add(entry);
                    index++;
                }
                return new FieldDictionaryFile(module, entries);
            }
        }

        /// <summary>True when the two entries' tax-year scopes intersect (an empty list means every year).</summary>
        public static bool YearsOverlap(FieldDictionaryEntry a, FieldDictionaryEntry b)
        {
            if (a.TaxYears.Count == 0 || b.TaxYears.Count == 0) return true;
            return a.TaxYears.Intersect(b.TaxYears).Any();
        }

        private static Dictionary<string, JsonElement> ReadProperties(JsonElement obj, string where)
        {
            var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in obj.EnumerateObject())
            {
                if (props.ContainsKey(p.Name)) throw new JsonException($"{where}: duplicate property '{p.Name}'.");
                props[p.Name] = p.Value;
            }
            return props;
        }

        private static FieldDictionaryEntry ParseEntry(JsonElement el, int index)
        {
            var where = $"entries[{index}]";
            if (el.ValueKind != JsonValueKind.Object) throw new JsonException($"{where}: must be an object.");
            var props = ReadProperties(el, where);

            var id = RequiredString(props, "id", where);
            if (!IdPattern.IsMatch(id))
                throw new JsonException($"{where}: id '{id}' must be 1-128 chars of letters, digits, '.', '_' or '-' (e.g. 1120.gross_receipts).");
            where = $"entries[{index}] ('{id}')";

            var kind = RequiredString(props, "kind", where);
            if (!Kinds.Contains(kind, StringComparer.Ordinal))
                throw new JsonException($"{where}: unknown kind '{kind}' (expected {string.Join(" | ", Kinds)}).");
            var isField = kind == KindField;

            foreach (var name in props.Keys)
            {
                var allowed = CommonProps.Contains(name) || (isField ? FieldProps.Contains(name) : DetailProps.Contains(name));
                if (!allowed)
                {
                    var hint = (isField ? DetailProps : FieldProps).Contains(name) ? $" (not valid for kind={kind})" : string.Empty;
                    throw new JsonException($"{where}: unknown property '{name}'{hint}.");
                }
            }

            var concept = RequiredString(props, "concept", where);
            var form = RequiredString(props, "form", where);
            var line = OptionalString(props, "line", where);
            var notes = OptionalString(props, "notes", where);
            var dataType = RequiredString(props, "dataType", where);
            if (!DataTypes.Contains(dataType, StringComparer.Ordinal))
                throw new JsonException($"{where}: unknown dataType '{dataType}' (expected {string.Join(" | ", DataTypes)}).");
            var source = RequiredString(props, "source", where);
            if (!Sources.Contains(source, StringComparer.Ordinal))
                throw new JsonException($"{where}: unknown source '{source}' (expected {string.Join(" | ", Sources)}).");
            var verifiedOn = RequiredString(props, "verifiedOn", where);
            var needsSubclient = RequiredBool(props, "needsSubclient", where);
            var needsState = RequiredBool(props, "needsState", where);
            var taxYears = OptionalYears(props, where);

            int? table = null, series = null, code = null, prefix = null, suffix = null;
            string? field = null;
            if (isField)
            {
                table = RequiredInt(props, "table", where);
                if (table < 1 || table > 4) throw new JsonException($"{where}: table must be 1..4 (got {table}).");
                field = RequiredString(props, "field", where);
                var m = FieldPattern.Match(field);
                if (!m.Success)
                    throw new JsonException($"{where}: field '{field}' must be an upper-case C{{table}}_{{n}} or C{{table}}_G{{n}} column of table 1..4.");
                if (m.Groups[1].Value != table.Value.ToString(CultureInfo.InvariantCulture))
                    throw new JsonException($"{where}: field '{field}' does not belong to table {table}.");
                var n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (n == 0)
                    throw new JsonException($"{where}: field '{field}' is a key column (C{{t}}_0 is the client number; G0 does not exist).");
                // Consistency with the table: DATA2/DATA4 rows are per subclient,
                // DATA3/DATA4 rows are per state.
                var wantSub = table == 2 || table == 4;
                var wantState = table == 3 || table == 4;
                if (needsSubclient != wantSub)
                    throw new JsonException($"{where}: needsSubclient must be {(wantSub ? "true" : "false")} for a DATA{table} field.");
                if (needsState != wantState)
                    throw new JsonException($"{where}: needsState must be {(wantState ? "true" : "false")} for a DATA{table} field.");
            }
            else
            {
                series = RequiredInt(props, "series", where);
                code = RequiredInt(props, "code", where);
                prefix = OptionalInt(props, "prefix", where);
                suffix = OptionalInt(props, "suffix", where);
                if (series < 0) throw new JsonException($"{where}: series must be >= 0.");
                if (code < 0) throw new JsonException($"{where}: code must be >= 0.");
                if (prefix < 0) throw new JsonException($"{where}: prefix must be >= 0.");
                if (suffix < 0) throw new JsonException($"{where}: suffix must be >= 0.");
            }

            return new FieldDictionaryEntry(id, concept, form, line, kind, table, field, series, code, prefix, suffix,
                needsSubclient, needsState, dataType, notes, taxYears, verifiedOn, source);
        }

        private static string RequiredString(Dictionary<string, JsonElement> props, string name, string where)
        {
            if (!props.TryGetValue(name, out var v)) throw new JsonException($"{where}: '{name}' is required.");
            if (v.ValueKind != JsonValueKind.String) throw new JsonException($"{where}: '{name}' must be a string.");
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s)) throw new JsonException($"{where}: '{name}' must not be blank.");
            return s!;
        }

        private static string? OptionalString(Dictionary<string, JsonElement> props, string name, string where)
        {
            if (!props.TryGetValue(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) throw new JsonException($"{where}: '{name}' must be a string when present.");
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s)) throw new JsonException($"{where}: '{name}' must not be blank when present (omit it instead).");
            return s;
        }

        private static bool RequiredBool(Dictionary<string, JsonElement> props, string name, string where)
        {
            if (!props.TryGetValue(name, out var v)) throw new JsonException($"{where}: '{name}' is required.");
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            throw new JsonException($"{where}: '{name}' must be true or false.");
        }

        private static int RequiredInt(Dictionary<string, JsonElement> props, string name, string where)
        {
            if (!props.TryGetValue(name, out var v)) throw new JsonException($"{where}: '{name}' is required.");
            return ReadInt(v, name, where);
        }

        private static int? OptionalInt(Dictionary<string, JsonElement> props, string name, string where)
            => props.TryGetValue(name, out var v) ? ReadInt(v, name, where) : (int?)null;

        private static int ReadInt(JsonElement v, string name, string where)
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var i))
                throw new JsonException($"{where}: '{name}' must be an integer.");
            return i;
        }

        private static IReadOnlyList<int> OptionalYears(Dictionary<string, JsonElement> props, string where)
        {
            if (!props.TryGetValue("taxYears", out var v)) return Array.Empty<int>();
            if (v.ValueKind != JsonValueKind.Array) throw new JsonException($"{where}: 'taxYears' must be an array of years.");
            var years = new List<int>();
            foreach (var y in v.EnumerateArray())
            {
                var year = ReadInt(y, "taxYears", where);
                if (year < 2018 || year > 2099) throw new JsonException($"{where}: tax year {year} is outside 2018..2099.");
                if (years.Contains(year)) throw new JsonException($"{where}: tax year {year} is listed twice.");
                years.Add(year);
            }
            years.Sort();
            return years;
        }

        // ---- serialization ---------------------------------------------------------------

        /// <summary>
        /// Stable on-disk form: entries sorted by id (ordinal, case-insensitive
        /// then ordinal), fixed property order, two-space indent, LF line
        /// endings, trailing newline. The output always round-trips through
        /// <see cref="Parse"/>.
        /// </summary>
        public static string Serialize(char module, IEnumerable<FieldDictionaryEntry> entries)
        {
            var sorted = entries
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            using (var ms = new MemoryStream())
            {
                var options = new JsonWriterOptions
                {
                    Indented = true,
                    NewLine = "\n",
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                using (var w = new Utf8JsonWriter(ms, options))
                {
                    w.WriteStartObject();
                    w.WriteString("module", module.ToString());
                    w.WriteStartArray("entries");
                    foreach (var e in sorted)
                    {
                        w.WriteStartObject();
                        foreach (var kv in e.ToOrderedProperties())
                        {
                            switch (kv.Value)
                            {
                                case string s: w.WriteString(kv.Key, s); break;
                                case bool b: w.WriteBoolean(kv.Key, b); break;
                                case int i: w.WriteNumber(kv.Key, i); break;
                                case int[] arr:
                                    w.WriteStartArray(kv.Key);
                                    foreach (var y in arr) w.WriteNumberValue(y);
                                    w.WriteEndArray();
                                    break;
                                default:
                                    throw new InvalidOperationException($"Unexpected dictionary property type for '{kv.Key}'.");
                            }
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            }
        }
    }
}
