using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                context.ExitCode = RunList(moduleStr[0], table, year, Console.Out);
            });

            return list;
        }

        public static int RunList(char module, int table, int year, TextWriter writer)
        {
            if (Modules.FileBasenameFor(module) == null)
            {
                Console.Error.WriteLine($"Unknown module '{module}'. Expected one of I/C/P/S/F/R/T/N/B.");
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
                writer.WriteLine($"Universal rule: C{table}_0 → ReadOnly (always, no entry needed).");
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

            set.AddOption(moduleOpt);
            set.AddOption(tableOpt);
            set.AddOption(fieldOpt);
            set.AddOption(accessOpt);
            set.AddOption(dataTypeOpt);
            set.AddOption(maxLenOpt);
            set.AddOption(sourceOpt);
            set.AddOption(noteOpt);

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
                context.ExitCode = RunSet(moduleStr[0], t, f, a, dt, ml, src, note, Console.Out);
            });

            return set;
        }

        public static int RunSet(char module, int table, string field, string accessRaw,
            string? dataType, int? maxLength, string source, string? note, TextWriter writer)
        {
            var basename = Modules.FileBasenameFor(module);
            if (basename == null)
            {
                Console.Error.WriteLine($"Unknown module '{module}'.");
                return 1;
            }
            if (!Enum.TryParse<FieldAccess>(accessRaw, ignoreCase: true, out var access))
            {
                Console.Error.WriteLine($"Invalid --access '{accessRaw}'. Expected: " +
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

        private static Command BuildDiscoverCommand()
        {
            var discover = new Command("discover",
                "Bulk-introspect the access map by write-probing each field (requires live SDK + Windows).");
            discover.SetHandler((context) =>
            {
                Console.Out.WriteLine(
                    "Bulk introspection requires live SDK + Windows. " +
                    "See follow-up F5 in Todo.md for the introspection script roadmap.");
                context.ExitCode = 2;
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
    }
}
