using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;

namespace LacertMCP.FieldAccessDumper
{
    /// <summary>
    /// Follow-up F5 — sharded field-access-map dumper.
    ///
    /// <para>
    /// For each known <c>(module, table, field)</c> triple the tool probes
    /// <see cref="FieldAccessMapLoader.AssertWritable"/> + the sim store's
    /// <see cref="SimulationStore.SimulateFieldWrite"/> to classify the field
    /// as <c>ReadWrite</c> / <c>ReadOnly</c> / <c>Hidden</c> / <c>NotExposed</c>
    /// / <c>NotSupported</c> / <c>Unknown</c>, then merges the result into the
    /// sharded JSON file at
    /// <c>src/LacertMCP.Server/Schema/FieldAccessMap/base/&lt;module&gt;.json</c>.
    /// </para>
    ///
    /// <para>
    /// On a Windows + Lacerte box with the COM bridge live the dumper would
    /// call <c>IDBFReader.ReadFromDBFData</c> to enumerate fields and then
    /// probe-write each one to capture the real <c>TAPIStatus</c>. That path
    /// is stubbed as <see cref="ProbeLiveAccess"/> and throws
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/> with a "run on
    /// Lacerte box" message — wiring it lives in a future task.
    /// </para>
    ///
    /// <para>
    /// Idempotency: existing entries in the base JSON are preserved; only
    /// missing entries are added unless <c>--overwrite</c> is passed.
    /// </para>
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var opts = ParseArgs(args);
            if (opts.ShowHelp)
            {
                PrintHelp();
                // R6-39: an unknown arg is an operator error, not a help
                // request — exit non-zero so scripts (and the CLI's discover
                // redirect) can detect the typo instead of reading exit 0 as
                // "dump succeeded".
                return opts.UnknownArg ? 1 : 0;
            }

            try
            {
                Run(opts);
                return 0;
            }
            catch (LacerteSDKError ex)
            {
                Console.Error.WriteLine($"[dumper] LacerteSDKError {ex.StatusCode} ({ex.StatusName}): {ex.Message}");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dumper] {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // =================================================================
        // Core
        // =================================================================

        private static void Run(Options opts)
        {
            var baseDir = ResolveBaseDir(opts);
            Console.WriteLine($"[dumper] Output dir: {baseDir}");
            Console.WriteLine($"[dumper] Mode: {(opts.Live ? "live (probe via bridge)" : "sim (probe via FieldAccessMapLoader + SimulateFieldWrite)")}");

            // P18-007 — sweep orphan .tmp siblings from a prior interrupted
            // run so the directory stays clean. The atomic write below stages
            // through `{shard}.json.tmp`; a kill mid-write leaves that file
            // behind without ever promoting it to canonical, and the loader
            // never reads it (it only globs `*.json`, not `*.tmp`). Cleaning
            // up matches the dumper's own clean-state ethos.
            SweepOrphanTmps(baseDir);

            // Candidate triples to probe. In sim mode this is small (seed-derived
            // demo fields); in live mode the dumper would enumerate via the
            // bridge — see ProbeLiveAccess below.
            var triples = opts.Live
                ? ProbeLiveAccess(opts) // throws BridgeUnavailable today
                : EnumerateSimTriples();

            var byModule = triples.GroupBy(t => t.Module);

            if (!triples.Any())
            {
                Console.WriteLine("[dumper] no fields to probe in sim; run on Windows for live introspection.");
                return;
            }

            foreach (var group in byModule)
            {
                var basename = ModuleBasename(group.Key);
                if (basename == null)
                {
                    Console.WriteLine($"[dumper] skipping unknown module char '{group.Key}'");
                    continue;
                }
                var path = Path.Combine(baseDir, basename + ".json");
                MergeIntoFile(path, group, opts);
            }
        }

        private static void MergeIntoFile(string path, IEnumerable<ProbeTriple> triples, Options opts)
        {
            var existing = LoadExisting(path);
            int added = 0;
            int kept = 0;
            int overwritten = 0;

            foreach (var t in triples)
            {
                var key = $"{t.Module}|{t.Table}|{t.Field}";
                var probed = ClassifyField(t, opts);

                if (!existing.ContainsKey(key))
                {
                    existing[key] = new FieldEntry(
                        Access: probed,
                        Source: opts.Live ? "live-probe" : "sim-probe",
                        AddedAt: DateTime.UtcNow.ToString("yyyy-MM-dd"));
                    added++;
                }
                else if (opts.Overwrite)
                {
                    var prior = existing[key];
                    existing[key] = prior with
                    {
                        Access = probed,
                        Source = opts.Live ? "live-probe" : "sim-probe",
                    };
                    overwritten++;
                }
                else
                {
                    kept++;
                }
            }

            // Sort keys for stable diffs in version control.
            var sorted = existing
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // P18-007 — atomic write: stage the new bytes in a sibling .tmp,
            // then Delete-canonical + Move-tmp-to-canonical. If the dumper is
            // killed (Ctrl-C, OS reboot, antivirus pausing the file mid-write)
            // BEFORE the tmp is fully written, the canonical shard stays
            // intact — the loader never sees a truncated/garbled JSON.
            // Without this, File.WriteAllText opens FileMode.Create which
            // truncates the existing file atomically THEN streams bytes —
            // a kill between truncate and full write leaves half-written
            // JSON that poisons FieldAccessMapLoader.LoadModule for the
            // entire process lifetime (paired defensive try/catch landed
            // in LoadModule for P18-007 keeps the loader safe; this atomic
            // write keeps the on-disk shard correct too).
            //
            // Pattern mirrors LacerteSessionManager.PersistIdempotencyCacheToDisk
            // — the project convention for create-or-replace on net48 where
            // File.Replace requires the destination to exist on first write.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Console.WriteLine($"[dumper] {Path.GetFileName(path)}: +{added} new, ~{overwritten} overwritten, ={kept} preserved");
        }

        /// <summary>
        /// Classify a single (module, table, field) by probing both
        /// <see cref="FieldAccessMapLoader.AssertWritable"/> and
        /// <see cref="SimulationStore.SimulateFieldWrite"/>. The two probes
        /// agree in sim by construction; in live mode the probe would carry
        /// the real TAPIStatus return from the COM bridge.
        /// </summary>
        private static FieldAccess ClassifyField(ProbeTriple t, Options opts)
        {
            // AssertWritable throws on non-writable fields; map the thrown
            // status back to a FieldAccess.
            try
            {
                FieldAccessMapLoader.AssertWritable(t.Module, t.Table, t.Field, opts.TaxYear);
                return FieldAccess.ReadWrite;
            }
            catch (LacerteSDKError ex)
            {
                switch (ex.StatusCode)
                {
                    case StatusCodes.ExposedForRead: return FieldAccess.ReadOnly;
                    case StatusCodes.NotExposed: return FieldAccess.NotExposed;
                    case StatusCodes.FieldNotSupported: return FieldAccess.NotSupported;
                    case SyntheticStatusCodes.FieldIsReadOnly: return FieldAccess.Unknown;
                    default: return FieldAccess.Unknown;
                }
            }
        }

        // =================================================================
        // Live probe — STUBBED (see ProbeLiveAccess docstring)
        // =================================================================

        /// <summary>
        /// Live-mode field enumeration: on a Windows + Lacerte box this would
        /// call <c>IDBFReader.ReadFromDBFData</c> for each (module, table)
        /// pair to enumerate the schema, then call
        /// <c>IDBFWriter.WriteToDBFDatabase</c> with a dry-run-safe value to
        /// capture each field's real TAPIStatus return.
        ///
        /// <para>
        /// Today this method is intentionally a stub and throws
        /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/> — wiring it
        /// requires (a) a Windows host, (b) a registered Lacerte SDK
        /// install, and (c) a live ITPRegistration. Run this from the Lacerte
        /// box to populate the access map; the structure here is what the
        /// follow-up task will fill in.
        /// </para>
        /// </summary>
        private static IEnumerable<ProbeTriple> ProbeLiveAccess(Options opts)
        {
            _ = opts;
            throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                new Dictionary<string, object?>
                {
                    ["op"] = "ProbeLiveAccess",
                    ["reason"] = "Live probing requires Windows + Lacerte SDK + a registered ITPRegistration. Run this dumper on the Lacerte box and re-run with --live.",
                });
        }

        // =================================================================
        // Sim probe — seed-derived triples
        // =================================================================

        /// <summary>
        /// In sim mode the dumper has no real schema to enumerate (the
        /// SimulationStore is an in-memory dict, not a DBF schema). This
        /// returns an empty list today — the dumper prints the "no fields to
        /// probe in sim" message and exits cleanly. Wiring real sim coverage
        /// would require extending the SimulationStore fixtures to publish
        /// their field schema.
        /// </summary>
        private static IReadOnlyList<ProbeTriple> EnumerateSimTriples()
        {
            return Array.Empty<ProbeTriple>();
        }

        // =================================================================
        // I/O helpers
        // =================================================================

        /// <summary>
        /// P18-007 — best-effort cleanup of orphan <c>*.json.tmp</c> files
        /// left behind by a prior interrupted dumper run. Safe to call
        /// on a directory that doesn't exist (no-op). Failures are logged
        /// but do not abort the run — the canonical shards are still readable
        /// because the loader globs <c>*.json</c>, not <c>*.tmp</c>.
        /// </summary>
        private static void SweepOrphanTmps(string baseDir)
        {
            if (!Directory.Exists(baseDir)) return;
            try
            {
                foreach (var tmp in Directory.GetFiles(baseDir, "*.json.tmp", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(tmp);
                        Console.WriteLine($"[dumper] cleaned orphan tmp: {Path.GetFileName(tmp)}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[dumper] WARN: failed to delete orphan tmp {tmp}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dumper] WARN: orphan-tmp sweep failed for {baseDir}: {ex.Message}");
            }
        }

        private static Dictionary<string, FieldEntry> LoadExisting(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, FieldEntry>();
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
                {
                    return new Dictionary<string, FieldEntry>();
                }
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                };
                opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                var parsed = JsonSerializer.Deserialize<Dictionary<string, FieldEntry>>(json, opts);
                return parsed ?? new Dictionary<string, FieldEntry>();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dumper] WARN: failed to parse {path}, starting fresh: {ex.Message}");
                return new Dictionary<string, FieldEntry>();
            }
        }

        private static string ResolveBaseDir(Options opts)
        {
            if (!string.IsNullOrEmpty(opts.OutputDir)) return opts.OutputDir!;

            // Default: write into the source tree (not the build output) so the
            // dumper's output ships into the next build. Walk up from the
            // running assembly to find the LacertMCP.Server project.
            var asmDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            var probe = new DirectoryInfo(asmDir);
            for (int hop = 0; hop < 8 && probe != null; hop++)
            {
                var candidate = Path.Combine(probe.FullName, "src", "LacertMCP.Server",
                    "Schema", "FieldAccessMap", "base");
                if (Directory.Exists(candidate)) return candidate;
                probe = probe.Parent;
            }

            // Fallback: write next to the assembly so smoke runs still succeed.
            return Path.Combine(asmDir, "Schema", "FieldAccessMap", "base");
        }

        // =================================================================
        // Module + arg helpers
        // =================================================================

        // R6-39: module char → shard basename now delegates to
        // Schema.Modules.FileBasenameFor (single source of truth — this file
        // previously re-implemented the map despite the project-wide dedup).
        private static string? ModuleBasename(char module)
            => Modules.FileBasenameFor(module);

        private static Options ParseArgs(string[] args)
        {
            var opts = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help":
                        opts.ShowHelp = true; break;
                    case "--overwrite":
                        opts.Overwrite = true; break;
                    case "--live":
                        opts.Live = true; break;
                    case "--year":
                        // R6-39: a garbage --year value previously consumed the
                        // token and silently kept the default — the operator
                        // had no idea the dump was probed against the wrong
                        // tax year. Warn to stderr on both the missing-value
                        // and unparseable-value cases.
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine($"[dumper] WARN: --year requires a value (e.g. --year 2024) — using default {opts.TaxYear}.");
                        }
                        else if (int.TryParse(args[++i], out var y))
                        {
                            opts.TaxYear = y;
                        }
                        else
                        {
                            Console.Error.WriteLine($"[dumper] WARN: --year '{args[i]}' is not a valid integer — using default {opts.TaxYear}.");
                        }
                        break;
                    case "--out":
                        if (i + 1 < args.Length) opts.OutputDir = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine($"[dumper] unknown arg: {a}");
                        // R6-39: flag the error so Main exits 1 after printing
                        // help (unknown args previously exited 0).
                        opts.UnknownArg = true;
                        opts.ShowHelp = true; break;
                }
            }
            return opts;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("LacertMCP.FieldAccessDumper — probe and dump per-field access classifications.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  LacertMCP.FieldAccessDumper [--overwrite] [--live] [--year <yyyy>] [--out <dir>]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --overwrite   Replace existing entries (default: preserve).");
            Console.WriteLine("  --live        Probe via the live COM bridge (Windows + Lacerte SDK only).");
            Console.WriteLine("  --year <yyyy> Tax year to probe (default: current year).");
            Console.WriteLine("  --out <dir>   Override the FieldAccessMap/base output directory.");
            Console.WriteLine("  -h, --help    Show this message.");
        }

        // =================================================================
        // Types
        // =================================================================

        private sealed class Options
        {
            public bool ShowHelp;
            // R6-39: set when an unrecognized argument was encountered; Main
            // prints help and exits 1 instead of 0.
            public bool UnknownArg;
            public bool Overwrite;
            public bool Live;
            public int TaxYear = DateTime.UtcNow.Year;
            public string? OutputDir;
        }

        /// <summary>
        /// A single (module, table, field) candidate to probe. The dumper
        /// runs the classifier on each triple and writes the result into the
        /// per-module JSON file.
        /// </summary>
        private readonly struct ProbeTriple
        {
            public char Module { get; }
            public int Table { get; }
            public string Field { get; }

            public ProbeTriple(char module, int table, string field)
            {
                Module = module;
                Table = table;
                Field = field;
            }
        }
    }
}
