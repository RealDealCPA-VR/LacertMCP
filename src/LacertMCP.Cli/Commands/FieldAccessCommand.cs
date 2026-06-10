using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LacertMCP.Server.Schema;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp field-access {set | list | discover}</c>
    ///
    /// <para>
    /// Operator surface for the per-field access map (agent 04 §5). <c>list</c> reads via
    /// <see cref="FieldAccessMapLoader"/>; <c>set</c> upserts a row into the right
    /// <c>Schema/FieldAccessMap/base/&lt;module&gt;.json</c> shard and writes it back
    /// sorted; <c>discover</c> is a stub today — bulk introspection needs the live SDK
    /// (follow-up F5 in <c>Todo.md</c>).
    /// </para>
    /// </summary>
    public static class FieldAccessCommand
    {
        // Char → filename basename now lives on Schema.Modules.FileBasenameFor
        // (single source of truth — formerly duplicated in this CLI, in the
        // server's FieldAccessMapLoader, and in MetadataTools).

        public static Command Build()
        {
            var cmd = new Command("field-access",
                "Inspect or edit the per-field access map (manual + future bulk introspection).");

            cmd.AddCommand(BuildListCommand());
            cmd.AddCommand(BuildSetCommand());
            cmd.AddCommand(BuildDiscoverCommand());

            return cmd;
        }

        // ---- list -------------------------------------------------------------------

        private static Command BuildListCommand()
        {
            var list = new Command("list", "List access-map entries for a module + table.");
            // System.CommandLine beta4 does not parse `char` natively — accept as string and
            // pull the first character. Same idiom is used by `set` below.
            var moduleOpt = new Option<string>("--module",
                "Module character (I/C/P/S/F/R/T/N/B).") { IsRequired = true };
            var tableOpt = new Option<int>("--table",
                "Table number (1..7).") { IsRequired = true };
            var yearOpt = new Option<int>("--year", () => DateTime.UtcNow.Year - 1,
                "Tax year (used to pick overlay).");

            list.AddOption(moduleOpt);
            list.AddOption(tableOpt);
            list.AddOption(yearOpt);

            list.SetHandler((context) =>
            {
                var moduleStr = context.ParseResult.GetValueForOption(moduleOpt) ?? "";
                if (moduleStr.Length != 1)
                {
                    Console.Error.WriteLine("--module must be a single character.");
                    context.ExitCode = 1;
                    return;
                }
                var table = context.ParseResult.GetValueForOption(tableOpt);
                var year = context.ParseResult.GetValueForOption(yearOpt);
                // P15-067: pass stdout + stderr explicitly so the routing is grep-able
                // at the closure boundary, not buried in the method.
                context.ExitCode = RunList(moduleStr[0], table, year, Console.Out, Console.Error);
            });

            return list;
        }

        /// <summary>
        /// Back-compat overload — preserved for existing call sites that pre-date the
        /// P15-067 <c>stderr</c> parameter. Routes errors to <see cref="Console.Error"/>.
        /// </summary>
        public static int RunList(char module, int table, int year, TextWriter writer)
            => RunList(module, table, year, writer, Console.Error);

        public static int RunList(char module, int table, int year, TextWriter writer, TextWriter stderr)
        {
            if (Modules.FileBasenameFor(module) == null)
            {
                stderr.WriteLine($"Unknown module '{module}'. Expected one of I/C/P/S/F/R/T/N/B.");
                return 1;
            }
            // P16-021: range-validate --table. Without this `--table 99` cheerfully
            // printed `Universal rule: C99_0 → ReadOnly` for a table that doesn't
            // exist, eroding trust in the on-disk map.
            if (!ValidateTable(table, stderr))
            {
                return 1;
            }

            // Read the raw shard so we can print everything (the loader is lookup-oriented).
            var rows = ReadShard(module);
            var prefix = module + "|" + table + "|";
            var filtered = rows
                .Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(r => r.Key, StringComparer.Ordinal)
                .ToList();

            if (filtered.Count == 0)
            {
                writer.WriteLine($"(no entries in base map for module={module} table={table})");
                // R6-28: the universal rule is ONLY the exact `C{table}_0` field
                // (ClientNumber — the literal letter C on EVERY module, per
                // FieldAccessMapLoader.IsClientNumberField). The prior wording
                // claimed "any field ending in _0" and printed {module}{table}_0
                // (e.g. I1_0), which the loader classifies Unknown, not ReadOnly.
                writer.WriteLine($"Universal rule: the ClientNumber field C{table}_0 is ReadOnly on every module — no entry needed. (No other field gets special treatment; fields merely ending in _0 are NOT covered by this rule.)");
                return 0;
            }

            writer.WriteLine($"# Module {module} / DATA{table} — {filtered.Count} entries (year={year})");
            foreach (var kv in filtered)
            {
                var key = kv.Key;
                var entry = kv.Value;
                var field = key.Substring(prefix.Length);
                // Apply overlay so callers see the effective access (mirrors loader behavior).
                var effective = FieldAccessMapLoader.GetFieldAccess(module, table, field, year);
                writer.WriteLine($"  {field,-12} base={entry.Access,-12} effective={effective,-12} src={entry.Source}");
            }
            return 0;
        }

        // ---- set --------------------------------------------------------------------

        private static Command BuildSetCommand()
        {
            var set = new Command("set", "Upsert a single field entry in the base map.");

            var moduleOpt = new Option<string>("--module", "Module character.") { IsRequired = true };
            var tableOpt = new Option<int>("--table", "Table number (1..7).") { IsRequired = true };
            var fieldOpt = new Option<string>("--field", "Field name (e.g. C1_42).") { IsRequired = true };
            var accessOpt = new Option<string>("--access",
                "FieldAccess value: ReadWrite | ReadOnly | Hidden | NotExposed | NotSupported | Unknown.") { IsRequired = true };
            var dataTypeOpt = new Option<string?>("--data-type", () => null, "Optional declared data type (string/int/...).");
            var maxLenOpt = new Option<int?>("--max-length", () => null, "Optional max length (chars).");
            var sourceOpt = new Option<string>("--source", () => "manual", "Provenance tag (spec | introspect | manual).");
            var noteOpt = new Option<string?>("--note", () => null, "Optional description / note.");
            // P16-021: --force is the escape hatch when the operator legitimately
            // wants to write a field name that doesn't match the
            // <letter><table>_<n> convention (e.g. legacy import).
            var forceOpt = new Option<bool>("--force", () => false,
                "Override the field-name shape check (defaults to <letter><table>_<n>, e.g. C1_42).");

            set.AddOption(moduleOpt);
            set.AddOption(tableOpt);
            set.AddOption(fieldOpt);
            set.AddOption(accessOpt);
            set.AddOption(dataTypeOpt);
            set.AddOption(maxLenOpt);
            set.AddOption(sourceOpt);
            set.AddOption(noteOpt);
            set.AddOption(forceOpt);

            set.SetHandler((context) =>
            {
                var moduleStr = context.ParseResult.GetValueForOption(moduleOpt) ?? "";
                if (moduleStr.Length != 1)
                {
                    Console.Error.WriteLine("--module must be a single character.");
                    context.ExitCode = 1;
                    return;
                }
                var t = context.ParseResult.GetValueForOption(tableOpt);
                var f = context.ParseResult.GetValueForOption(fieldOpt)!;
                var a = context.ParseResult.GetValueForOption(accessOpt)!;
                var dt = context.ParseResult.GetValueForOption(dataTypeOpt);
                var ml = context.ParseResult.GetValueForOption(maxLenOpt);
                var src = context.ParseResult.GetValueForOption(sourceOpt)!;
                var note = context.ParseResult.GetValueForOption(noteOpt);
                var force = context.ParseResult.GetValueForOption(forceOpt);
                // P15-067: pass stdout + stderr explicitly so the routing is grep-able
                // at the closure boundary.
                context.ExitCode = RunSet(moduleStr[0], t, f, a, dt, ml, src, note, force, Console.Out, Console.Error);
            });

            return set;
        }

        /// <summary>
        /// Back-compat overload — preserved for existing call sites that pre-date the
        /// P15-067 <c>stderr</c> parameter. Routes errors to <see cref="Console.Error"/>.
        /// </summary>
        public static int RunSet(char module, int table, string field, string accessRaw,
            string? dataType, int? maxLength, string source, string? note, TextWriter writer)
            => RunSet(module, table, field, accessRaw, dataType, maxLength, source, note, writer, Console.Error);

        /// <summary>
        /// Back-compat overload — preserved for existing call sites that pre-date the
        /// P16-021 <c>force</c> parameter. Defaults to no override (force=false).
        /// </summary>
        public static int RunSet(char module, int table, string field, string accessRaw,
            string? dataType, int? maxLength, string source, string? note, TextWriter writer, TextWriter stderr)
            => RunSet(module, table, field, accessRaw, dataType, maxLength, source, note, force: false, writer, stderr);

        public static int RunSet(char module, int table, string field, string accessRaw,
            string? dataType, int? maxLength, string source, string? note, bool force, TextWriter writer, TextWriter stderr)
        {
            // R6-28 — uppercase-normalize BEFORE any validation or shard write.
            // FieldAccessMapLoader.GetFieldAccess uppercases the field name and
            // matches the module char case-sensitively (Modules.GetByChar), so a
            // lowercase `--field c1_5` previously wrote a shard key the loader
            // could never read back — a permanently dead row (the exact failure
            // class P16-021 was meant to close).
            module = char.ToUpperInvariant(module);
            field = field?.ToUpperInvariant() ?? string.Empty;

            var basename = Modules.FileBasenameFor(module);
            if (basename == null)
            {
                stderr.WriteLine($"Unknown module '{module}'.");
                return 1;
            }
            // P16-021: range-validate --table before we mutate the shard. Without
            // this guard `--table 99` silently wrote a key the loader couldn't read,
            // polluting the on-disk JSON with junk that became a PR-review puzzle.
            if (!ValidateTable(table, stderr))
            {
                return 1;
            }
            // P16-021: shape-check --field. The convention is <letter><table>_<n>
            // (e.g. C1_42, I3_0). A typo like `oops_typo` silently became a dead
            // row. --force is the explicit escape hatch for legacy imports.
            if (!force && !FieldShapeRegex.IsMatch(field))
            {
                stderr.WriteLine($"--field '{field}' does not match the <letter><table>_<n> convention (e.g. C1_42). Pass --force to override.");
                return 1;
            }
            if (!Enum.TryParse<FieldAccess>(accessRaw, ignoreCase: true, out var access))
            {
                stderr.WriteLine($"Invalid --access '{accessRaw}'. Expected: " +
                    string.Join(" | ", Enum.GetNames(typeof(FieldAccess))));
                return 1;
            }

            var path = ShardPath(basename);
            var rows = ReadShard(module);
            var key = module + "|" + table + "|" + field;
            var existed = rows.TryGetValue(key, out var prior);

            var entry = new FieldEntry(
                Access: access,
                DataType: dataType,
                MaxLength: maxLength,
                Description: note,
                Source: source,
                AddedAt: DateTime.UtcNow.ToString("yyyy-MM-dd"));
            rows[key] = entry;

            WriteShardSorted(path, rows);

            // Print diff so the operator can audit before checking in.
            writer.WriteLine($"# Wrote {path}");
            if (existed)
            {
                writer.WriteLine($"  - {key}: access={prior!.Access} src={prior.Source}");
                writer.WriteLine($"  + {key}: access={entry.Access} src={entry.Source}" +
                    (entry.DataType != null ? $" type={entry.DataType}" : "") +
                    (entry.MaxLength.HasValue ? $" maxLen={entry.MaxLength}" : ""));
            }
            else
            {
                writer.WriteLine($"  + {key}: access={entry.Access} src={entry.Source}" +
                    (entry.DataType != null ? $" type={entry.DataType}" : "") +
                    (entry.MaxLength.HasValue ? $" maxLen={entry.MaxLength}" : ""));
            }

            // Pop the loader cache so a subsequent `list` in the same process shows the new value.
            try { FieldAccessMapLoader_ResetCache(); } catch { /* internal accessor — best effort */ }
            return 0;
        }

        // ---- discover ---------------------------------------------------------------

        // P15-029: Exit code 3 = "not implemented through this verb" (intentionally
        // distinct from Doctor's exit 2 = "partial: warnings or environment skip" —
        // see DoctorCommand summary). Operators on Windows + live SDK should NOT toggle env vars; the
        // work was reassigned to LacertMCP.FieldAccessDumper.exe (the standalone
        // introspector that ships under src/LacertMCP.FieldAccessDumper/).
        private static Command BuildDiscoverCommand()
        {
            var discover = new Command("discover",
                "Stub — bulk introspection moved to LacertMCP.FieldAccessDumper.exe. " +
                "This verb exits 3 with a redirect message.");
            discover.SetHandler((context) =>
            {
                Console.Out.WriteLine(
                    "Bulk field-access introspection is not exposed through this command — " +
                    "use the standalone introspector instead:");
                Console.Out.WriteLine(
                    "  LacertMCP.FieldAccessDumper.exe [--overwrite] [--live] [--year <yyyy>] [--out <dir>]");
                Console.Out.WriteLine(
                    "  (the dumper probes all known modules/tables itself — there are no --module/--table flags;");
                Console.Out.WriteLine(
                    "  --live requires Windows + the Lacerte SDK; project lives under src/LacertMCP.FieldAccessDumper/).");
                Console.Out.WriteLine();
                Console.Out.WriteLine(
                    "To set entries by hand off-box: `lacerte-mcp field-access set --module <X> --table <N> " +
                    "--field <FIELD> --access <ReadWrite|ReadOnly|Hidden|NotExposed|NotSupported|Unknown>` " +
                    "or edit Schema/FieldAccessMap/base/<module>.json directly.");
                context.ExitCode = 3;
            });
            return discover;
        }

        // ---- shard I/O --------------------------------------------------------------

        private static readonly JsonSerializerOptions _jsonOptions = BuildJsonOptions();

        private static JsonSerializerOptions BuildJsonOptions()
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // P16-068: keep operator-readable text (notes / descriptions /
                // data-type tags that may contain `<`, `>`, `+`, `&`) literal in
                // the on-disk shard JSON. Shards are PR-reviewed; \uXXXX noise
                // makes diffs unreadable.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            opts.Converters.Add(new JsonStringEnumConverter());
            return opts;
        }

        public static string ShardPath(string basename)
        {
            // Resolve relative to the running assembly — same convention the server loader uses.
            // The .Cli assembly is co-deployed with the .Server assembly + Schema tree, so this works.
            var asmDir = Path.GetDirectoryName(typeof(FieldAccessCommand).Assembly.Location)!;
            return Path.Combine(asmDir, "Schema", "FieldAccessMap", "base", basename + ".json");
        }

        private static Dictionary<string, FieldEntry> ReadShard(char module)
        {
            var basename = Modules.FileBasenameFor(module);
            if (basename == null)
            {
                return new Dictionary<string, FieldEntry>();
            }
            var path = ShardPath(basename);
            if (!File.Exists(path)) return new Dictionary<string, FieldEntry>();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return new Dictionary<string, FieldEntry>();
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, FieldEntry>>(json, _jsonOptions);
                return parsed ?? new Dictionary<string, FieldEntry>();
            }
            catch
            {
                return new Dictionary<string, FieldEntry>();
            }
        }

        private static void WriteShardSorted(string path, Dictionary<string, FieldEntry> rows)
        {
            // Sort keys ordinally so PR diffs stay stable.
            var sorted = new SortedDictionary<string, FieldEntry>(rows, StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(sorted, _jsonOptions);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // The loader exposes ResetCacheForTests internally; we reach it via reflection only.
        // Safe: the loader is in the same solution so the symbol exists; failing reflection is a no-op.
        private static void FieldAccessMapLoader_ResetCache()
        {
            var t = typeof(FieldAccessMapLoader);
            var m = t.GetMethod("ResetCacheForTests",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            m?.Invoke(null, null);
        }

        // ---- P16-021 input validation helpers --------------------------------------

        /// <summary>
        /// P16-021: shared range guard for <c>--table</c>. DATA tables are 1..7
        /// (1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status, 6=Display,
        /// 7=Forms — see Modules / Tables in the schema layer). Used by both
        /// <see cref="RunList"/> and <see cref="RunSet"/> so a typo can't pollute
        /// the on-disk shard or print confidently-wrong universal-rule text.
        /// </summary>
        private static bool ValidateTable(int table, TextWriter stderr)
        {
            if (table < 1 || table > 7)
            {
                stderr.WriteLine($"--table must be in 1..7 (got {table}). DATA tables are numbered 1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status, 6=Display, 7=Forms.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// P16-021: field-name convention regex — <c>&lt;letter&gt;&lt;table&gt;_&lt;n&gt;</c>
        /// (e.g. <c>C1_42</c>, <c>c1_42</c>, <c>I3_0</c>). Case-insensitive on the
        /// leading letter — R6-28: <c>RunSet</c> uppercase-normalizes before
        /// writing, so lowercase input is accepted but stored uppercase to match
        /// the loader's lookup. Trailing digits are unbounded. Operator can pass
        /// <c>--force</c> to bypass for legacy imports.
        /// </summary>
        private static readonly Regex FieldShapeRegex = new Regex(
            "^[A-Za-z][1-7]_\\d+$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
