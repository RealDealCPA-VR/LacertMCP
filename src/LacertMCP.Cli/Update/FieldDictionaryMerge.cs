using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LacertMCP.Server.Schema;

namespace LacertMCP.Cli.Update
{
    /// <summary>Outcome of merging this PC's field dictionaries into a release's.</summary>
    public sealed class FieldDictionaryMergeResult
    {
        public FieldDictionaryMergeResult(int kept, int superseded, IReadOnlyList<string> conflicts, IReadOnlyList<string> unreadable)
        {
            Kept = kept;
            Superseded = superseded;
            Conflicts = conflicts;
            Unreadable = unreadable;
        }

        /// <summary>Entries recorded on this PC carried into the new release.</summary>
        public int Kept { get; }

        /// <summary>Entries the release now ships under the same id (the release's copy wins).</summary>
        public int Superseded { get; }

        /// <summary>This PC's entries dropped because the release maps the same cell under another id.</summary>
        public IReadOnlyList<string> Conflicts { get; }

        /// <summary>Installed dictionary files that didn't load (the release's copy is taken).</summary>
        public IReadOnlyList<string> Unreadable { get; }

        public IReadOnlyList<string> Describe()
        {
            var lines = new List<string>();
            if (Kept > 0) lines.Add($"Kept {Kept} field-dictionary entr{(Kept == 1 ? "y" : "ies")} recorded on this PC.");
            if (Superseded > 0) lines.Add($"{Superseded} of this PC's dictionary entr{(Superseded == 1 ? "y is" : "ies are")} now shipped by the release (the release's copy is used).");
            foreach (var c in Conflicts) lines.Add("Dropped this PC's dictionary entry " + c + " — the release maps the same cell under another id.");
            foreach (var u in Unreadable) lines.Add("This PC's " + u + " was not a valid field dictionary; took the release's copy.");
            return lines;
        }
    }

    /// <summary>
    /// Carries field-dictionary entries recorded on this PC with
    /// <c>lacerte-mcp map-diff record</c> into a new release's
    /// <c>Schema\FieldDictionary</c>, so an update or installer re-run never
    /// discards verified mappings. Per module, by entry id: the release's entries
    /// are kept as shipped; an installed entry with an id the release doesn't have
    /// is added; an installed entry whose id the release also has is superseded
    /// (the release's copy is a reviewed version of the same mapping); an installed
    /// entry that maps the same cell as a release entry under a different id, for
    /// overlapping years, is dropped and reported (the loader would reject the
    /// file otherwise). Every merged file is re-validated with the server's own
    /// loader before it is written; nothing is written if any release file is invalid.
    /// </summary>
    public static class FieldDictionaryMerge
    {
        public static FieldDictionaryMergeResult Merge(string installedDir, string releaseDir)
        {
            int kept = 0, superseded = 0;
            var conflicts = new List<string>();
            var unreadable = new List<string>();
            var plans = new List<(string Path, string Json)>();
            if (!Directory.Exists(installedDir)) return new FieldDictionaryMergeResult(0, 0, conflicts, unreadable);

            foreach (var installedPath in Directory.EnumerateFiles(installedDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(installedPath);
                var module = ModuleFor(name);
                if (module == null) continue;

                var installed = FieldDictionaryLoader.ValidateFile(installedPath, module, out _);
                if (installed == null)
                {
                    unreadable.Add(name);
                    continue;
                }
                if (installed.Entries.Count == 0) continue;

                var releasePath = Path.Combine(releaseDir, name);
                IReadOnlyList<FieldDictionaryEntry> release = Array.Empty<FieldDictionaryEntry>();
                if (File.Exists(releasePath))
                {
                    release = (FieldDictionaryLoader.ValidateFile(releasePath, module, out var releaseError)
                               ?? throw new UpdateException($"The release's field dictionary {name} is invalid ({releaseError}).")).Entries;
                }

                var merged = new List<FieldDictionaryEntry>(release);
                var releaseIds = new HashSet<string>(release.Select(e => e.Id), StringComparer.Ordinal);
                var added = 0;
                foreach (var e in installed.Entries)
                {
                    if (releaseIds.Contains(e.Id))
                    {
                        superseded++;
                        continue;
                    }
                    if (release.Any(r => r.TargetKey == e.TargetKey && FieldDictionaryLoader.YearsOverlap(r, e)))
                    {
                        conflicts.Add($"{e.Id} ({name})");
                        continue;
                    }
                    merged.Add(e);
                    added++;
                }
                if (added == 0) continue;

                var json = FieldDictionaryLoader.Serialize(module.Value, merged);
                // Same parser the server uses: never write a file it would reject.
                FieldDictionaryLoader.Parse(json, module.Value);
                plans.Add((releasePath, json));
                kept += added;
            }

            foreach (var (path, json) in plans)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            return new FieldDictionaryMergeResult(kept, superseded, conflicts, unreadable);
        }

        private static char? ModuleFor(string fileName)
        {
            var basename = Path.GetFileNameWithoutExtension(fileName);
            foreach (var m in Modules.All)
            {
                if (string.Equals(Modules.FileBasenameFor(m.Char), basename, StringComparison.OrdinalIgnoreCase)) return m.Char;
            }
            return null;
        }
    }
}
