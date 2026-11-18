using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp detail-restore {list | plan | apply}</c> — FP-2 restore:
    /// undo the detail rows the MCP server itself INSERTED for a client, using
    /// the journal the server writes next to its pre-write backup
    /// (<c>&lt;backup&gt;.journal.jsonl</c>).
    ///
    /// <para>
    /// Only journaled rows are ever removed. Rows the operator entered in
    /// Lacerte are not in the journal and are never touched; a journaled row
    /// counts as "still present" only beyond the copies the backup already had.
    /// </para>
    ///
    /// <para>
    /// An ODBC DELETE on a DBF removes EVERY row its WHERE matches, so
    /// <c>apply</c> removes one copy of a row like this: count the copies the
    /// DELETE could match (n; refuse unless they are all identical), DELETE in
    /// the out-of-process worker, re-read and check exactly those n rows went,
    /// re-INSERT n-1 copies, re-read and check the row now has n-1 copies and
    /// every other row is unchanged. It stops at the first failure and says
    /// exactly what to re-enter.
    /// </para>
    ///
    /// <para>ODBC DELETE on the Lacerte driver is UNVERIFIED: test on a TEST client first
    /// (REGRESSION_CHECKLIST "FP-2 restore live items").</para>
    /// </summary>
    public static class DetailRestoreCommand
    {
        public const string SnapshotKind = "lacerte-mcp detail-row backup";

        /// <summary>The DELETE takes every copy, so more than this many identical copies is refused (removed by hand instead).</summary>
        public const int MaxIdenticalCopies = 25;

        internal const string UnverifiedWarning =
            "WARNING: removing detail rows over the Lacerte ODBC driver (DELETE) is UNVERIFIED. Try it on a TEST client first " +
            "(REGRESSION_CHECKLIST \"FP-2 restore live items\") before using it on a real return.";

        internal const string StopWritersWarning =
            "Stop every agent / MCP server session writing to this client first: the server's per-client lock does not cover this command.";

        /// <summary>Seams for plan/apply (production: the Lacerte ODBC driver and the server exe's worker).</summary>
        public sealed class Deps
        {
            /// <summary>ODBC connection string → a fresh read-only source (one per read).</summary>
            public Func<string, ISnapshotQuerySource> SourceFactory { get; set; } = cs => new DsnSnapshotSource(cs);
            public IDetailWriteRunner InsertRunner { get; set; } = new ProcessDetailWriteRunner();
            public IDetailDeleteRunner DeleteRunner { get; set; } = new ProcessDetailWriteRunner();
            public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
            public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;
        }

        public static Command Build()
        {
            var cmd = new Command("detail-restore",
                "Undo the detail rows the MCP server wrote to a client (live FP-2 writes), using the journal saved next to its pre-write backup. " +
                "Never removes rows entered in Lacerte. DELETE over the Lacerte ODBC driver is unverified: try it on a TEST client first.");
            cmd.AddCommand(BuildListCommand());
            cmd.AddCommand(BuildPlanCommand());
            cmd.AddCommand(BuildApplyCommand());
            return cmd;
        }

        private static Command BuildListCommand()
        {
            var c = new Command("list", "List the detail-row backups and journals (client, module, year, data path, time, rows journaled).");
            var dirOpt = new Option<string?>("--dir",
                "Backup folder. Default: LACERTE_DETAIL_SNAPSHOT_DIR (absolute path), else %APPDATA%\\lacerte-mcp\\detail-snapshots - the server's folder.");
            c.AddOption(dirOpt);
            c.SetHandler(context =>
            {
                context.ExitCode = RunList(context.ParseResult.GetValueForOption(dirOpt), ProcessEnv(), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildPlanCommand()
        {
            var c = new Command("plan",
                "Read the client's current detail rows over the Lacerte ODBC driver and show which journaled rows are still present " +
                "(would be removed), which are already gone, and which look edited. Changes nothing.");
            var journalOpt = new Option<string>("--journal", "The <backup>.journal.jsonl file (see `detail-restore list`).") { IsRequired = true };
            c.AddOption(journalOpt);
            c.SetHandler(context =>
            {
                context.ExitCode = RunPlan(context.ParseResult.GetValueForOption(journalOpt) ?? string.Empty, new Deps(), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildApplyCommand()
        {
            var c = new Command("apply",
                "Remove ONE copy of each journaled row that is still present (DELETE + re-insert of the other identical copies, each " +
                "in a separate worker process, verified by a re-read). Stops at the first failure. Without --confirm it only prints the plan.");
            var journalOpt = new Option<string>("--journal", "The <backup>.journal.jsonl file (see `detail-restore list`).") { IsRequired = true };
            var confirmOpt = new Option<bool>("--confirm", () => false, "Actually remove the rows. Without it, the plan is printed and nothing changes.");
            c.AddOption(journalOpt);
            c.AddOption(confirmOpt);
            c.SetHandler(context =>
            {
                var r = context.ParseResult;
                context.ExitCode = RunApply(r.GetValueForOption(journalOpt) ?? string.Empty, r.GetValueForOption(confirmOpt),
                    new Deps(), Console.Out, Console.Error);
            });
            return c;
        }

        private static IDictionary<string, string?> ProcessEnv()
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            {
                d[(string)kv.Key] = kv.Value as string;
            }
            return d;
        }

        // ---- list ---------------------------------------------------------------------------

        public static int RunList(string? dir, IDictionary<string, string?> env, TextWriter stdout, TextWriter stderr)
        {
            var folder = string.IsNullOrWhiteSpace(dir) ? LacerteSessionManager.ResolveDetailSnapshotDirectory(env) : Path.GetFullPath(dir!.Trim());
            if (string.IsNullOrEmpty(folder))
            {
                stderr.WriteLine($"No backup folder: pass --dir, or set {LacerteSessionManager.DetailSnapshotDirEnvVar} (absolute path) or APPDATA.");
                return 1;
            }
            if (!Directory.Exists(folder))
            {
                stdout.WriteLine($"# {folder}");
                stdout.WriteLine("  (folder does not exist - the server has not taken a detail-row backup here yet)");
                return 0;
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(folder, "*.json")) names.Add(Path.GetFileName(f).Substring(0, Path.GetFileName(f).Length - 5));
            foreach (var f in Directory.GetFiles(folder, "*" + DetailWriteJournal.JournalSuffix))
            {
                var n = Path.GetFileName(f);
                names.Add(n.Substring(0, n.Length - DetailWriteJournal.JournalSuffix.Length));
            }

            stdout.WriteLine($"# {folder}");
            int shown = 0;
            foreach (var stem in names)
            {
                var snapPath = Path.Combine(folder, stem + ".json");
                var journalPath = Path.Combine(folder, stem + DetailWriteJournal.JournalSuffix);
                SnapshotInfo? snap = null;
                string? snapProblem = null;
                if (File.Exists(snapPath))
                {
                    snap = TryReadSnapshot(snapPath, out snapProblem);
                    if (snap == null && snapProblem == NotABackup) continue; // some other JSON file
                }
                else if (!File.Exists(journalPath))
                {
                    continue;
                }

                int journaled = 0, restored = 0;
                DetailJournalEntry? first = null;
                if (File.Exists(journalPath))
                {
                    try
                    {
                        var entries = DetailWriteJournal.Read(journalPath, new List<string>());
                        journaled = entries.Count;
                        first = entries.FirstOrDefault();
                        var records = ReadRecords(DetailWriteJournal.RestoredPathFor(journalPath), new List<string>());
                        restored = entries.Count(e => records.TryGetValue(e.EntryId, out var rec) && IsTerminal(rec.Result));
                    }
                    catch (IOException ex)
                    {
                        snapProblem = "journal unreadable: " + ex.Message;
                    }
                }

                var client = snap?.Client ?? first?.ClientNumber ?? "?";
                var module = snap?.Module ?? first?.Module ?? "?";
                var year = snap?.TaxYear ?? first?.TaxYear;
                var dataPath = snap?.DataPath ?? first?.DataPath ?? "?";
                var taken = snap?.TakenAt ?? first?.At ?? "?";
                stdout.WriteLine($"- {stem}");
                stdout.WriteLine($"    client {client}, module {module}, TY{(year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : "?")}, data path {dataPath}");
                stdout.WriteLine($"    backup taken {taken}: {(snap != null ? snap.Rows.Count.ToString(CultureInfo.InvariantCulture) + " row(s)" : File.Exists(snapPath) ? "UNREADABLE (" + snapProblem + ")" : "MISSING")}");
                stdout.WriteLine(File.Exists(journalPath)
                    ? $"    journal: {journaled} row(s) written by the MCP, {restored} already restored/gone -> {journalPath}"
                    : "    journal: none (no row written after the backup, or written by a server version without the journal)");
                shown++;
            }
            if (shown == 0) stdout.WriteLine("  (no detail-row backups)");
            return 0;
        }

        // ---- shared loading --------------------------------------------------------------------

        private const string NotABackup = "not a lacerte-mcp detail-row backup";

        internal sealed class SnapshotInfo
        {
            public string Client = string.Empty;
            public string Module = string.Empty;
            public int TaxYear;
            public string DataPath = string.Empty;
            public string TakenAt = string.Empty;
            public List<DetailRow> Rows = new List<DetailRow>();
        }

        internal static SnapshotInfo? TryReadSnapshot(string path, out string? problem)
        {
            problem = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                    || kind.GetString() != SnapshotKind)
                {
                    problem = NotABackup;
                    return null;
                }
                var info = new SnapshotInfo
                {
                    Client = Str(root, "clientNumber"),
                    Module = Str(root, "module"),
                    TaxYear = root.TryGetProperty("taxYear", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : 0,
                    DataPath = Str(root, "dataPath"),
                    TakenAt = Str(root, "takenAt"),
                };
                if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                {
                    problem = "the backup has no rows array";
                    return null;
                }
                foreach (var r in rows.EnumerateArray())
                {
                    var row = JsonSerializer.Deserialize<DetailRow>(r.GetRawText());
                    if (row == null)
                    {
                        problem = "the backup has a null row";
                        return null;
                    }
                    info.Rows.Add(row);
                }
                return info;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is FormatException)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static string Str(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        /// <summary>Everything plan/apply need about one journal.</summary>
        internal sealed class JournalContext
        {
            public string JournalPath = string.Empty;
            public string SnapshotPath = string.Empty;
            public string RestoredPath = string.Empty;
            public List<DetailJournalEntry> Entries = new List<DetailJournalEntry>();
            public List<string> Problems = new List<string>();
            public string DataPath = string.Empty;
            public int Year;
            public char Module;
            public string Client = string.Empty;
            public string ConnectionString = string.Empty;
            /// <summary>The backup's rows; null when the backup can't be read (apply refuses).</summary>
            public List<DetailRow>? Baseline;
            public string? BaselineProblem;
            /// <summary>entryId → last restore record.</summary>
            public Dictionary<string, RestoreRecord> Records = new Dictionary<string, RestoreRecord>(StringComparer.Ordinal);
        }

        internal static JournalContext? LoadJournal(string journalPath, TextWriter stderr)
        {
            if (string.IsNullOrWhiteSpace(journalPath))
            {
                stderr.WriteLine("--journal is required.");
                return null;
            }
            var full = Path.GetFullPath(journalPath.Trim());
            if (!File.Exists(full))
            {
                stderr.WriteLine($"Journal not found: {full}");
                return null;
            }
            var jc = new JournalContext
            {
                JournalPath = full,
                SnapshotPath = DetailWriteJournal.SnapshotPathFor(full),
                RestoredPath = DetailWriteJournal.RestoredPathFor(full),
            };
            try
            {
                jc.Entries = DetailWriteJournal.Read(full, jc.Problems);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"Journal unreadable: {ex.Message}");
                return null;
            }
            if (jc.Entries.Count == 0)
            {
                stderr.WriteLine($"The journal holds no readable entries: {full}" + (jc.Problems.Count > 0 ? " (" + string.Join("; ", jc.Problems) + ")" : string.Empty));
                return null;
            }

            var first = jc.Entries[0];
            foreach (var e in jc.Entries)
            {
                if (!string.Equals(e.DataPath, first.DataPath, StringComparison.OrdinalIgnoreCase) || e.TaxYear != first.TaxYear
                    || !string.Equals(e.Module, first.Module, StringComparison.Ordinal)
                    || !string.Equals(e.ClientNumber, first.ClientNumber, StringComparison.Ordinal))
                {
                    stderr.WriteLine("Refusing: the journal mixes clients / data paths / years / modules (entry " + e.EntryId + "). It was not written by one server process for one client.");
                    return null;
                }
            }
            var moduleInfo = first.Module.Length == 1 ? Modules.GetByChar(first.Module[0]) : null;
            if (moduleInfo == null)
            {
                stderr.WriteLine($"Refusing: the journal names an unknown module '{first.Module}'.");
                return null;
            }
            jc.DataPath = first.DataPath;
            jc.Year = first.TaxYear;
            jc.Module = first.Module[0];
            jc.Client = first.ClientNumber;
            try
            {
                SqlTemplates.ValidateClient(jc.Client);
                jc.ConnectionString = ConnectionStringBuilder.Build(jc.DataPath, moduleInfo.ReturnType, jc.Year);
            }
            catch (LacerteSDKError ex)
            {
                stderr.WriteLine($"Refusing: the journal's client number '{jc.Client}' is invalid ({ex.StatusName}).");
                return null;
            }
            catch (ArgumentException ex)
            {
                stderr.WriteLine("Refusing: the journal's data path / year can't be used: " + ex.Message.Split('\n')[0]);
                return null;
            }

            if (!File.Exists(jc.SnapshotPath))
            {
                jc.BaselineProblem = "the backup " + jc.SnapshotPath + " is missing";
            }
            else
            {
                var snap = TryReadSnapshot(jc.SnapshotPath, out var problem);
                if (snap == null) jc.BaselineProblem = "the backup " + jc.SnapshotPath + " can't be read (" + problem + ")";
                else if (!string.Equals(snap.Client, jc.Client, StringComparison.Ordinal) || snap.TaxYear != jc.Year
                         || !string.Equals(snap.Module, first.Module, StringComparison.Ordinal)
                         || !string.Equals(snap.DataPath, jc.DataPath, StringComparison.OrdinalIgnoreCase))
                    jc.BaselineProblem = "the backup " + jc.SnapshotPath + " is for a different client / year / module / data path";
                else jc.Baseline = snap.Rows;
            }

            try
            {
                jc.Records = ReadRecords(jc.RestoredPath, jc.Problems);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"Restore record unreadable ({jc.RestoredPath}): {ex.Message}");
                return null;
            }
            return jc;
        }

        // ---- restore records (the .restored.jsonl sidecar) -------------------------------------------

        public const string ResultStarted = "started";
        public const string ResultRemoved = "removed";
        public const string ResultAlreadyGone = "already-gone";
        public const string ResultAborted = "aborted-nothing-changed";

        /// <summary>One line of <c>&lt;journal&gt;.restored.jsonl</c>; the last line per entry wins.</summary>
        public sealed class RestoreRecord
        {
            public const string KindId = "lacerte-mcp detail-restore record";
            [JsonPropertyName("kind")] public string Kind { get; set; } = KindId;
            [JsonPropertyName("entryId")] public string EntryId { get; set; } = string.Empty;
            [JsonPropertyName("at")] public string At { get; set; } = string.Empty;
            [JsonPropertyName("result")] public string Result { get; set; } = string.Empty;
            /// <summary>Identical copies of the row present before the DELETE (started/removed).</summary>
            [JsonPropertyName("copiesBefore")] public int? CopiesBefore { get; set; }
            [JsonPropertyName("note")] public string? Note { get; set; }
        }

        internal static bool IsTerminal(string result) => result == ResultRemoved || result == ResultAlreadyGone;

        internal static Dictionary<string, RestoreRecord> ReadRecords(string path, List<string> problems)
        {
            var d = new Dictionary<string, RestoreRecord>(StringComparer.Ordinal);
            if (!File.Exists(path)) return d;
            int n = 0;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<RestoreRecord>(line);
                    if (r == null || r.Kind != RestoreRecord.KindId || string.IsNullOrEmpty(r.EntryId)) { problems.Add($"restore record line {n}: not a record"); continue; }
                    d[r.EntryId] = r;
                }
                catch (JsonException ex)
                {
                    problems.Add($"restore record line {n}: unreadable ({ex.Message})");
                }
            }
            return d;
        }

        private static void AppendRecord(JournalContext jc, Deps deps, string entryId, string result, int? copiesBefore, string? note)
        {
            var rec = new RestoreRecord
            {
                EntryId = entryId,
                At = deps.Now().ToString("o", CultureInfo.InvariantCulture),
                Result = result,
                CopiesBefore = copiesBefore,
                Note = note,
            };
            DetailWriteJournal.AppendLine(jc.RestoredPath, DetailWriteJournal.SerializeLine(rec));
            jc.Records[entryId] = rec;
        }

        // ---- classification (pure) -------------------------------------------------------------

        public const string StatusRemove = "remove";
        public const string StatusGone = "gone";
        public const string StatusDone = "done";
        public const string StatusInterrupted = "interrupted";
        public const string StatusInvalid = "invalid";

        /// <summary>What plan/apply would do with one journal entry.</summary>
        public sealed class EntryPlan
        {
            public DetailJournalEntry Entry { get; set; } = new DetailJournalEntry();
            public string Status { get; set; } = string.Empty;
            /// <summary>Rows the DELETE could match right now (remove only).</summary>
            public int Copies { get; set; }
            /// <summary>Identical copies the backup already had (never removed).</summary>
            public int BaselineCopies { get; set; }
            /// <summary>Non-null: the row can't be removed safely (remove only); apply skips it.</summary>
            public string? Unsafe { get; set; }
            public List<string> Warnings { get; set; } = new List<string>();
        }

        /// <summary>
        /// Classifies every journal entry against <paramref name="current"/> rows.
        /// Per identical journaled row (k pending entries): m copies present now,
        /// b in the backup → min(k, m - b) entries are "remove", the rest "gone".
        /// </summary>
        internal static List<EntryPlan> Classify(JournalContext jc, IReadOnlyList<DetailRow> current)
        {
            var plans = new List<EntryPlan>();
            var groups = new Dictionary<string, List<EntryPlan>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var e in jc.Entries)
            {
                var p = new EntryPlan { Entry = e };
                plans.Add(p);
                if (jc.Records.TryGetValue(e.EntryId, out var rec))
                {
                    if (IsTerminal(rec.Result)) { p.Status = StatusDone; continue; }
                    if (rec.Result == ResultStarted)
                    {
                        p.Status = StatusInterrupted;
                        p.Copies = rec.CopiesBefore ?? 0;
                        continue;
                    }
                }
                string key;
                try
                {
                    key = DetailRowWriter.BuildInsertSql(jc.Client, e.Row!);
                }
                catch (Exception ex) when (ex is LacerteSDKError || ex is ArgumentException)
                {
                    p.Status = StatusInvalid;
                    p.Warnings.Add("the journaled row is not a valid detail row (" + ex.Message.Split('\n')[0] + "); it is not touched.");
                    continue;
                }
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new List<EntryPlan>();
                    groups[key] = g;
                    order.Add(key);
                }
                g.Add(p);
            }

            foreach (var key in order)
            {
                var g = groups[key];
                var row = g[0].Entry.Row!;
                int m = DetailRowWriter.CountMatching(current, row);
                int b = jc.Baseline == null ? 0 : DetailRowWriter.CountMatching(jc.Baseline, row);
                int present = Math.Max(0, Math.Min(g.Count, m - b));
                for (int i = 0; i < g.Count; i++)
                {
                    var p = g[i];
                    p.BaselineCopies = b;
                    if (i < present)
                    {
                        p.Status = StatusRemove;
                        var candidates = current.Where(r => DetailRowWriter.CouldDeleteMatch(row, r)).ToList();
                        p.Copies = candidates.Count;
                        p.Unsafe = UnsafeReason(row, candidates);
                        if (p.Unsafe == null)
                        {
                            try
                            {
                                DetailRowWriter.BuildDeleteSql(jc.Client, row);
                            }
                            catch (Exception ex) when (ex is LacerteSDKError || ex is ArgumentException)
                            {
                                p.Unsafe = "no exact DELETE can be built for this row (" + ex.Message.Split('\n')[0] + "). Remove it by hand in Lacerte.";
                            }
                        }
                        if (b > 0) p.Warnings.Add($"the backup already had {b} identical cop{(b == 1 ? "y" : "ies")} of this row; {(b == 1 ? "it stays" : "they stay")}.");
                    }
                    else
                    {
                        p.Status = StatusGone;
                        // Same input line and the same amount or description, but not equal: likely this row, edited.
                        var lookalikes = current.Where(r => SameLocation(row, r) && !DetailRowWriter.RowMatches(row, r)
                            && (SameAmount(row, r) || string.Equals((row.Description ?? string.Empty).Trim(), (r.Description ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        foreach (var l in lookalikes)
                        {
                            p.Warnings.Add("looks edited in Lacerte? a row at the same Series/Prefix/Code/Suffix/State/City now reads " + Describe(l) + " - it is NOT removed.");
                        }
                    }
                }
            }
            return plans;
        }

        /// <summary>Null when the DELETE can only take identical copies of the journaled row that we can put back.</summary>
        internal static string? UnsafeReason(DetailRow row, List<DetailRow> candidates)
        {
            if (candidates.Count == 0) return "no row the DELETE could match was found";
            var other = candidates.FirstOrDefault(r => !DetailRowWriter.RowMatches(row, r));
            if (other != null)
            {
                return "the driver's DELETE could also remove a different row (" + Describe(other) + "), which this command can't put back exactly. Remove the journaled row by hand in Lacerte.";
            }
            if (candidates.Select(DetailRowWriter.StrictKey).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                return "the copies of this row differ in columns the MCP does not write (e.g. Prefix / ImportSrc / letter case), so the re-inserted copies would not be identical. Remove it by hand in Lacerte.";
            }
            if (candidates.Count > MaxIdenticalCopies)
            {
                return $"{candidates.Count} identical copies (more than {MaxIdenticalCopies}); the DELETE takes them all. Remove it by hand in Lacerte.";
            }
            return null;
        }

        private static bool SameAmount(DetailRow a, DetailRow b)
            => a.Amount.HasValue && b.Amount.HasValue && Math.Abs(a.Amount.Value - b.Amount.Value) < 0.005;

        private static bool SameLocation(DetailRow a, DetailRow b)
        {
            string N(string? v)
            {
                var t = (v ?? string.Empty).Trim();
                return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("0.############", CultureInfo.InvariantCulture)
                    : t.Length == 0 ? "0" : t;
            }
            return N(a.Series) == N(b.Series) && N(a.Prefix) == N(b.Prefix) && N(a.Code) == N(b.Code) && N(a.Suffix) == N(b.Suffix)
                   && string.Equals((a.StateAbbr ?? string.Empty).Trim(), (b.StateAbbr ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                   && string.Equals((a.CityAbbr ?? string.Empty).Trim(), (b.CityAbbr ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        internal static string Describe(DetailRow r)
        {
            string Show(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v!.Trim();
            var amount = r.Amount.HasValue ? r.Amount.Value.ToString("0.############", CultureInfo.InvariantCulture) : "-";
            return $"Series {Show(r.Series)} / Prefix {Show(r.Prefix)} / Code {Show(r.Code)} / Suffix {Show(r.Suffix)} / State {Show(r.StateAbbr)} / City {Show(r.CityAbbr)}"
                + $" / Source {Show(r.Source)} / TSJ {Show(r.TSJ)} / Amount {amount} / Description \"{(r.Description ?? string.Empty).Trim()}\"";
        }

        // ---- plan -----------------------------------------------------------------------------------

        public static int RunPlan(string journalPath, Deps deps, TextWriter stdout, TextWriter stderr)
        {
            var jc = LoadJournal(journalPath, stderr);
            if (jc == null) return 1;
            if (!TryReadCurrent(jc, deps, out var current, out var readError))
            {
                stderr.WriteLine($"Could not read the client's current detail rows over the Lacerte ODBC driver: {readError}");
                return 1;
            }
            var plans = Classify(jc, current);
            WritePlan(jc, plans, current, stdout);
            return 0;
        }

        private static void WritePlan(JournalContext jc, List<EntryPlan> plans, IReadOnlyList<DetailRow> current, TextWriter w)
        {
            w.WriteLine($"# Restore plan for client {jc.Client} (module {jc.Module}, TY{jc.Year}, data path {jc.DataPath})");
            w.WriteLine($"  journal: {jc.JournalPath}");
            w.WriteLine($"  backup:  {jc.SnapshotPath}" + (jc.BaselineProblem != null ? "  ** " + jc.BaselineProblem + " - apply refuses without it **" : $" ({jc.Baseline!.Count} row(s) before the MCP's first write)"));
            w.WriteLine($"  current detail rows: {current.Count}");
            foreach (var p in jc.Problems) w.WriteLine("  warning: " + p + " (a row on an unreadable line is not removed)");
            w.WriteLine("  " + UnverifiedWarning);
            w.WriteLine();

            void Section(string title, Func<EntryPlan, bool> pick)
            {
                var items = plans.Where(pick).ToList();
                w.WriteLine($"{title}: {items.Count}");
                foreach (var p in items)
                {
                    w.WriteLine($"  - [{p.Entry.Outcome}, written {p.Entry.At}] {Describe(p.Entry.Row!)}");
                    if (p.Status == StatusRemove)
                    {
                        w.WriteLine(p.Unsafe != null
                            ? "      NOT REMOVABLE: " + p.Unsafe
                            : $"      {p.Copies} identical cop{(p.Copies == 1 ? "y" : "ies")} now; apply leaves {p.Copies - 1}.");
                    }
                    if (p.Status == StatusInterrupted)
                    {
                        int c = DetailRowWriter.CountMatching(current, p.Entry.Row!);
                        w.WriteLine("      " + DescribeInterrupted(p.Copies, c));
                    }
                    foreach (var warn in p.Warnings) w.WriteLine("      " + warn);
                }
            }
            Section("Still present - would be removed (one copy each)", p => p.Status == StatusRemove);
            Section("Already gone - nothing to do", p => p.Status == StatusGone);
            Section("Interrupted earlier apply - checked first", p => p.Status == StatusInterrupted);
            Section("Already restored", p => p.Status == StatusDone);
            Section("Invalid journal rows - never touched", p => p.Status == StatusInvalid);
            w.WriteLine();
            w.WriteLine("Rows not in this journal (entered in Lacerte, or present in the backup) are never removed.");
        }

        private static string DescribeInterrupted(int copiesBefore, int now)
        {
            int want = copiesBefore - 1;
            if (now == want) return $"{now} cop{(now == 1 ? "y" : "ies")} present = what the interrupted removal should leave; apply marks it removed.";
            if (now == copiesBefore) return $"{now} cop{(now == 1 ? "y" : "ies")} present = nothing was removed; apply retries it.";
            if (now < want) return $"{now} cop{(now == 1 ? "y" : "ies")} present but {want} should be: RE-ENTER {want - now} cop{(want - now == 1 ? "y" : "ies")} of this row in Lacerte, then run apply again.";
            return $"{now} copies present, more than the {copiesBefore} before the interrupted removal (another writer?). Check the client by hand.";
        }

        // ---- apply ----------------------------------------------------------------------------------

        public static int RunApply(string journalPath, bool confirm, Deps deps, TextWriter stdout, TextWriter stderr)
        {
            var jc = LoadJournal(journalPath, stderr);
            if (jc == null) return 1;
            if (!TryReadCurrent(jc, deps, out var current, out var readError))
            {
                stderr.WriteLine($"Could not read the client's current detail rows over the Lacerte ODBC driver: {readError}. Nothing was changed.");
                return 1;
            }
            var plans = Classify(jc, current);
            if (!confirm)
            {
                WritePlan(jc, plans, current, stdout);
                stdout.WriteLine();
                stdout.WriteLine("Nothing was changed: pass --confirm to remove the rows listed as still present.");
                stdout.WriteLine(StopWritersWarning);
                return 1;
            }
            if (jc.Baseline == null)
            {
                stderr.WriteLine($"Refusing: {jc.BaselineProblem}. Without the backup the command can't tell the MCP's copies from rows that were already there. Nothing was changed.");
                return 1;
            }

            stdout.WriteLine($"# Restoring client {jc.Client} (module {jc.Module}, TY{jc.Year}, data path {jc.DataPath})");
            stdout.WriteLine("  " + UnverifiedWarning);
            stdout.WriteLine("  " + StopWritersWarning);
            var removed = new List<DetailJournalEntry>();
            var skipped = new HashSet<string>(StringComparer.Ordinal);

            // 1. Earlier interrupted removals first.
            foreach (var p in plans.Where(p => p.Status == StatusInterrupted))
            {
                int c = DetailRowWriter.CountMatching(current, p.Entry.Row!);
                if (c == p.Copies - 1)
                {
                    if (!TryRecord(jc, deps, p.Entry.EntryId, ResultRemoved, p.Copies, "completed by an earlier interrupted apply (copy count checked)", stdout, removed, stderr)) return 1;
                    stdout.WriteLine($"  marked removed (earlier interrupted apply had finished): {Describe(p.Entry.Row!)}");
                }
                else if (c == p.Copies)
                {
                    if (!TryRecord(jc, deps, p.Entry.EntryId, ResultAborted, p.Copies, "earlier interrupted apply changed nothing", stdout, removed, stderr)) return 1;
                }
                else
                {
                    stderr.WriteLine("STOPPED: an earlier apply was interrupted while removing " + Describe(p.Entry.Row!) + ".");
                    stderr.WriteLine("  " + DescribeInterrupted(p.Copies, c));
                    stderr.WriteLine("  Nothing was changed in this run. After fixing the client in Lacerte, run apply again.");
                    return 1;
                }
            }

            // 2. One entry at a time, re-reading and re-classifying before each.
            bool firstPass = true;
            while (true)
            {
                if (!firstPass && !TryReadCurrent(jc, deps, out current, out readError))
                {
                    return Stop(stderr, removed, "could not re-read the client's detail rows (" + readError + ") before the next removal. The rows removed so far are verified.", null);
                }
                firstPass = false;
                plans = Classify(jc, current);

                foreach (var gone in plans.Where(p => p.Status == StatusGone))
                {
                    if (!TryRecord(jc, deps, gone.Entry.EntryId, ResultAlreadyGone, null, null, stdout, removed, stderr)) return 1;
                    stdout.WriteLine($"  already gone: {Describe(gone.Entry.Row!)}");
                    foreach (var warn in gone.Warnings) stdout.WriteLine("    " + warn);
                }

                var next = plans.FirstOrDefault(p => p.Status == StatusRemove && !skipped.Contains(p.Entry.EntryId));
                if (next == null) break;
                if (next.Unsafe != null)
                {
                    skipped.Add(next.Entry.EntryId);
                    stdout.WriteLine($"  SKIPPED (not changed): {Describe(next.Entry.Row!)}");
                    stdout.WriteLine("    " + next.Unsafe);
                    continue;
                }
                var result = RemoveOne(jc, deps, next.Entry, current, stdout, stderr, removed);
                if (result != 0) return result;
            }

            stdout.WriteLine();
            stdout.WriteLine($"Removed {removed.Count} journaled row(s); {skipped.Count} skipped. Every removal was verified by a re-read.");
            if (skipped.Count > 0)
            {
                stdout.WriteLine("Skipped rows were NOT changed - remove them by hand in Lacerte if they must go.");
                return 1;
            }
            return 0;
        }

        private static bool TryRecord(JournalContext jc, Deps deps, string entryId, string result, int? copies, string? note,
            TextWriter stdout, List<DetailJournalEntry> removed, TextWriter stderr)
        {
            try
            {
                AppendRecord(jc, deps, entryId, result, copies, note);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Stop(stderr, removed, $"could not write the restore record {jc.RestoredPath} ({ex.Message}).", null);
                return false;
            }
        }

        /// <summary>Removes one copy of <paramref name="entry"/>'s row. 0 = done and verified; 1 = stopped (state reported).</summary>
        private static int RemoveOne(JournalContext jc, Deps deps, DetailJournalEntry entry, IReadOnlyList<DetailRow> before,
            TextWriter stdout, TextWriter stderr, List<DetailJournalEntry> removed)
        {
            var row = entry.Row!;
            var copies = before.Where(r => DetailRowWriter.CouldDeleteMatch(row, r)).ToList();
            int n = copies.Count;
            var unsafeReason = UnsafeReason(row, copies);
            if (unsafeReason != null)
            {
                return Stop(stderr, removed, "refused before changing anything: " + unsafeReason, null);
            }
            var othersBefore = before.Where(r => !DetailRowWriter.RowMatches(row, r)).ToList();

            string deleteSql, insertSql;
            try
            {
                deleteSql = DetailRowWriter.BuildDeleteSql(jc.Client, row);
                insertSql = DetailRowWriter.BuildInsertSql(jc.Client, row);
            }
            catch (Exception ex) when (ex is LacerteSDKError || ex is ArgumentException)
            {
                return Stop(stderr, removed, "refused before changing anything: the journaled row can't be turned into SQL (" + ex.Message.Split('\n')[0] + ").", null);
            }

            stdout.WriteLine($"  removing one of {n} cop{(n == 1 ? "y" : "ies")}: {Describe(row)}");
            if (!TryRecord(jc, deps, entry.EntryId, ResultStarted, n, null, stdout, removed, stderr)) return 1;

            // DELETE (worker process).
            DetailWriteRunResult del;
            try { del = deps.DeleteRunner.RunDelete(jc.ConnectionString, deleteSql, deps.Timeout) ?? new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = "no result" }; }
            catch (Exception ex) { del = new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = ex.GetType().Name + ": " + ex.Message }; }

            if (!TryReadCurrent(jc, deps, out var after1, out var err1))
            {
                return Stop(stderr, removed, $"the DELETE {Outcome(del)}, and the rows could not be re-read ({err1}): the state of this row is UNKNOWN. Run `detail-restore plan` to see it.", null);
            }
            int c1 = DetailRowWriter.CountMatching(after1, row);
            var (missing1, extra1) = MultisetDiff(othersBefore, after1.Where(r => !DetailRowWriter.RowMatches(row, r)));
            bool othersSame1 = missing1.Count == 0 && extra1.Count == 0;

            if (c1 == n && othersSame1)
            {
                if (!TryRecord(jc, deps, entry.EntryId, ResultAborted, n, "DELETE " + Outcome(del), stdout, removed, stderr)) return 1;
                return Stop(stderr, removed,
                    del.Status == DetailWriteRunStatus.Ok
                        ? "the DELETE ran but removed nothing (the Lacerte driver did not match the row). Nothing was changed for this row; remove it by hand in Lacerte."
                        : $"the DELETE {Outcome(del)}. A re-read shows nothing was changed for this row.",
                    null);
            }
            if (!(c1 == 0 && othersSame1))
            {
                return Stop(stderr, removed, $"the DELETE {Outcome(del)} and did not remove exactly the {n} identical cop{(n == 1 ? "y" : "ies")} expected.",
                    StateReport(row, n - 1, c1, missing1, extra1));
            }

            // Re-insert n-1 copies (worker process each, same INSERT the server uses).
            for (int i = 1; i <= n - 1; i++)
            {
                DetailWriteRunResult ins;
                try { ins = deps.InsertRunner.Run(jc.ConnectionString, insertSql, deps.Timeout) ?? new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = "no result" }; }
                catch (Exception ex) { ins = new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = ex.GetType().Name + ": " + ex.Message }; }
                if (ins.Status != DetailWriteRunStatus.Ok)
                {
                    if (!TryReadCurrent(jc, deps, out var afterFail, out var errFail))
                    {
                        return Stop(stderr, removed, $"re-inserting copy {i} of {n - 1} {Outcome(ins)}, and the rows could not be re-read ({errFail}). "
                            + $"Up to {n - 1} cop{(n - 1 == 1 ? "y" : "ies")} of {Describe(row)} may need re-entering in Lacerte; run `detail-restore plan`.", null);
                    }
                    int cf = DetailRowWriter.CountMatching(afterFail, row);
                    var (mf, ef) = MultisetDiff(othersBefore, afterFail.Where(r => !DetailRowWriter.RowMatches(row, r)));
                    return Stop(stderr, removed, $"re-inserting copy {i} of {n - 1} {Outcome(ins)}.", StateReport(row, n - 1, cf, mf, ef));
                }
            }

            // Verify.
            if (!TryReadCurrent(jc, deps, out var after2, out var err2))
            {
                return Stop(stderr, removed, $"the row was deleted and {n - 1} cop{(n - 1 == 1 ? "y" : "ies")} re-inserted, but the verification re-read failed ({err2}). Run `detail-restore plan` to check.", null);
            }
            var matching2 = after2.Where(r => DetailRowWriter.RowMatches(row, r)).ToList();
            var (missing2, extra2) = MultisetDiff(othersBefore, after2.Where(r => !DetailRowWriter.RowMatches(row, r)));
            if (matching2.Count != n - 1 || missing2.Count != 0 || extra2.Count != 0)
            {
                return Stop(stderr, removed, "verification failed after the DELETE and re-inserts.", StateReport(row, n - 1, matching2.Count, missing2, extra2));
            }
            string? note = null;
            var originalKey = DetailRowWriter.StrictKey(copies[0]);
            if (matching2.Any(r => DetailRowWriter.StrictKey(r) != originalKey))
            {
                note = "the re-inserted copies differ from the originals in columns the MCP does not write (e.g. Prefix/ImportSrc as the driver defaults them)";
                stdout.WriteLine("    warning: " + note + ".");
            }
            if (!TryRecord(jc, deps, entry.EntryId, ResultRemoved, n, note, stdout, removed, stderr))
            {
                stderr.WriteLine("  (the row above WAS removed and verified; a later plan shows it as already gone.)");
                return 1;
            }
            removed.Add(entry);
            stdout.WriteLine($"    removed and verified ({n - 1} identical cop{(n - 1 == 1 ? "y" : "ies")} left, every other row unchanged).");
            return 0;
        }

        private static string Outcome(DetailWriteRunResult r)
        {
            switch (r.Status)
            {
                case DetailWriteRunStatus.Ok: return "ran (rows affected: " + (r.RowsAffected.HasValue ? r.RowsAffected.Value.ToString(CultureInfo.InvariantCulture) : "?") + ")";
                case DetailWriteRunStatus.Failed: return "failed (" + (r.SqlState != null ? "SQLSTATE " + r.SqlState + ": " : string.Empty) + (r.Error ?? "error") + ")";
                case DetailWriteRunStatus.TimedOut: return "timed out (the worker was stopped)";
                case DetailWriteRunStatus.NotStarted: return "could not start (" + (r.Error ?? "?") + ")";
                default: return "crashed (the worker exited without a result)";
            }
        }

        private static List<string> StateReport(DetailRow row, int want, int now, List<DetailRow> missingOthers, List<DetailRow> extraOthers)
        {
            var lines = new List<string>();
            lines.Add($"This row now has {now} cop{(now == 1 ? "y" : "ies")}; {want} should remain: {Describe(row)}");
            if (now < want) lines.Add($"RE-ENTER in Lacerte: {want - now} cop{(want - now == 1 ? "y" : "ies")} of that row.");
            else if (now > want) lines.Add($"{now - want} extra cop{(now - want == 1 ? "y is" : "ies are")} still present (the MCP's row is not removed yet).");
            foreach (var m in missingOthers) lines.Add("RE-ENTER in Lacerte (another row the DELETE removed): " + Describe(m));
            foreach (var e in extraOthers) lines.Add("Unexpected new row (another writer?): " + Describe(e));
            return lines;
        }

        private static int Stop(TextWriter stderr, List<DetailJournalEntry> removed, string what, List<string>? state)
        {
            stderr.WriteLine("STOPPED: " + what);
            if (state != null) foreach (var s in state) stderr.WriteLine("  " + s);
            stderr.WriteLine($"  Rows removed and verified before this point: {removed.Count}.");
            foreach (var r in removed) stderr.WriteLine("    - " + Describe(r.Row!));
            stderr.WriteLine("  No further rows were touched. Fix the client in Lacerte if told to above, then run `detail-restore plan`.");
            return 1;
        }

        /// <summary>(in <paramref name="before"/> but not after, in after but not before) by exact row identity.</summary>
        internal static (List<DetailRow> Missing, List<DetailRow> Extra) MultisetDiff(IEnumerable<DetailRow> before, IEnumerable<DetailRow> after)
        {
            var pool = new Dictionary<string, List<DetailRow>>(StringComparer.Ordinal);
            foreach (var r in before)
            {
                var k = DetailRowWriter.StrictKey(r);
                if (!pool.TryGetValue(k, out var l)) pool[k] = l = new List<DetailRow>();
                l.Add(r);
            }
            var extra = new List<DetailRow>();
            foreach (var r in after)
            {
                var k = DetailRowWriter.StrictKey(r);
                if (pool.TryGetValue(k, out var l) && l.Count > 0) l.RemoveAt(l.Count - 1);
                else extra.Add(r);
            }
            return (pool.Values.SelectMany(l => l).ToList(), extra);
        }

        private static bool TryReadCurrent(JournalContext jc, Deps deps, out IReadOnlyList<DetailRow> rows, out string? error)
        {
            rows = Array.Empty<DetailRow>();
            error = null;
            try
            {
                using var source = deps.SourceFactory(jc.ConnectionString);
                rows = source.Query(SqlTemplates.DetailAll(jc.Client)).Select(DetailRowWriter.FromOdbcRow).ToList();
                return true;
            }
            catch (LacerteSDKError ex)
            {
                error = $"{ex.StatusCode} {ex.StatusName}: {ex.UserMessage}";
                return false;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }
}
