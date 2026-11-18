using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Odbc
{
    /// <summary>
    /// FP-2 restore — one line of the detail-write journal: a detail row the
    /// MCP server INSERTED into a client's detail file (live, over the
    /// out-of-process worker). Written next to the pre-write backup
    /// (<c>&lt;backup&gt;.journal.jsonl</c>) and read by
    /// <c>lacerte-mcp detail-restore</c>, which removes exactly these rows and
    /// nothing else.
    /// </summary>
    public sealed class DetailJournalEntry
    {
        public const string KindId = "lacerte-mcp detail-write journal";

        [JsonPropertyName("kind")] public string Kind { get; set; } = KindId;
        [JsonPropertyName("version")] public int Version { get; set; } = 1;

        /// <summary>Unique id of this line (the restore marks lines done by it).</summary>
        [JsonPropertyName("entryId")] public string EntryId { get; set; } = string.Empty;
        [JsonPropertyName("at")] public string At { get; set; } = string.Empty;
        [JsonPropertyName("dataPath")] public string DataPath { get; set; } = string.Empty;
        [JsonPropertyName("taxYear")] public int TaxYear { get; set; }
        [JsonPropertyName("module")] public string Module { get; set; } = string.Empty;
        [JsonPropertyName("clientNumber")] public string ClientNumber { get; set; } = string.Empty;

        /// <summary>File name of the backup taken before the first write (same folder).</summary>
        [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }

        /// <summary>The row exactly as written (every column the INSERT wrote).</summary>
        [JsonPropertyName("row")] public DetailRow? Row { get; set; }
        [JsonPropertyName("insertSql")] public string? InsertSql { get; set; }
        [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }

        /// <summary><see cref="DetailWriteJournal.OutcomeVerified"/> / <see cref="DetailWriteJournal.OutcomeUnverified"/> / <see cref="DetailWriteJournal.OutcomeLandedAfterError"/>.</summary>
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = string.Empty;
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    /// <summary>File naming, append and read for the detail-write journal.</summary>
    public static class DetailWriteJournal
    {
        /// <summary>The INSERT ran and the read-back found exactly one new matching row.</summary>
        public const string OutcomeVerified = "verified";
        /// <summary>The worker reported the INSERT ran, but the read-back couldn't run or didn't show exactly one new matching row.</summary>
        public const string OutcomeUnverified = "unverified";
        /// <summary>The worker failed (crash/timeout/driver error) but a re-read shows the row IS in the file.</summary>
        public const string OutcomeLandedAfterError = "landed-after-error";

        public const string JournalSuffix = ".journal.jsonl";
        public const string RestoredSuffix = ".restored.jsonl";

        private static readonly JsonSerializerOptions LineOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary><c>X.json</c> → <c>X.journal.jsonl</c> (file name only).</summary>
        public static string JournalFileNameFor(string snapshotFileName)
        {
            var name = Path.GetFileName(snapshotFileName);
            return (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 5) : name) + JournalSuffix;
        }

        /// <summary>Full journal path next to <paramref name="snapshotPath"/>.</summary>
        public static string JournalPathFor(string snapshotPath)
        {
            var dir = Path.GetDirectoryName(snapshotPath) ?? string.Empty;
            return Path.Combine(dir, JournalFileNameFor(snapshotPath));
        }

        /// <summary><c>X.journal.jsonl</c> → <c>X.json</c> (same folder).</summary>
        public static string SnapshotPathFor(string journalPath)
        {
            var dir = Path.GetDirectoryName(journalPath) ?? string.Empty;
            var name = Path.GetFileName(journalPath);
            var stem = name.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - JournalSuffix.Length)
                : Path.GetFileNameWithoutExtension(name);
            return Path.Combine(dir, stem + ".json");
        }

        /// <summary><c>X.journal.jsonl</c> → <c>X.journal.restored.jsonl</c>: what the restore already did.</summary>
        public static string RestoredPathFor(string journalPath)
        {
            var full = journalPath;
            return (full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? full.Substring(0, full.Length - 6) : full) + RestoredSuffix;
        }

        /// <summary>One JSON line (UTF-8, no BOM) ending in <c>\n</c>.</summary>
        public static byte[] SerializeLine<T>(T value)
        {
            var json = JsonSerializer.Serialize(value, LineOptions);
            return new UTF8Encoding(false).GetBytes(json + "\n");
        }

        /// <summary>
        /// Appends <paramref name="line"/> in one write and flushes it to disk
        /// (open-append, write, flush, close). Throws on any I/O failure.
        /// </summary>
        public static void AppendLine(string path, byte[] line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(line, 0, line.Length);
                fs.Flush(true);
            }
        }

        /// <summary>
        /// Reads every line of a journal. Lines that don't parse as a journal
        /// entry (e.g. a torn last line after a crash) are reported in
        /// <paramref name="problems"/> and skipped.
        /// </summary>
        public static List<DetailJournalEntry> Read(string path, List<string> problems)
        {
            var entries = new List<DetailJournalEntry>();
            int n = 0;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<DetailJournalEntry>(line, LineOptions);
                    if (e == null || e.Kind != DetailJournalEntry.KindId || e.Row == null || string.IsNullOrEmpty(e.EntryId))
                    {
                        problems.Add($"line {n}: not a detail-write journal entry");
                        continue;
                    }
                    entries.Add(e);
                }
                catch (JsonException ex)
                {
                    problems.Add($"line {n}: unreadable ({ex.Message})");
                }
            }
            return entries;
        }
    }
}
