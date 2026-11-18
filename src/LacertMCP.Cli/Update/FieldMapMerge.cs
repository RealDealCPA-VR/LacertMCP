using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Schema;

namespace LacertMCP.Cli.Update
{
    /// <summary>What <see cref="FieldMapMerge.Merge"/> did to one base shard.</summary>
    public sealed class ShardMergeStats
    {
        public ShardMergeStats(string name, int keptFromThisPc, int overridingRelease, int updatedFromRelease, bool installedUnreadable)
        {
            Name = name;
            KeptFromThisPc = keptFromThisPc;
            OverridingRelease = overridingRelease;
            UpdatedFromRelease = updatedFromRelease;
            InstalledUnreadable = installedUnreadable;
        }

        /// <summary>Shard file name (e.g. <c>individual.json</c>).</summary>
        public string Name { get; }

        /// <summary>Installed entries carried into the merged shard (absent from the release, or built on this PC).</summary>
        public int KeptFromThisPc { get; }

        /// <summary>Of <see cref="KeptFromThisPc"/>: entries that replaced a different release entry for the same field.</summary>
        public int OverridingRelease { get; }

        /// <summary>Installed curated entries that differed from the release's and were replaced by it.</summary>
        public int UpdatedFromRelease { get; }

        /// <summary>The installed shard was not valid JSON; the release's copy was taken as-is.</summary>
        public bool InstalledUnreadable { get; }
    }

    /// <summary>Outcome of <see cref="FieldMapMerge.Merge"/>.</summary>
    public sealed class FieldMapMergeResult
    {
        public FieldMapMergeResult(IReadOnlyList<ShardMergeStats> shards) => Shards = shards;

        /// <summary>Only shards where something happened (kept, updated or unreadable).</summary>
        public IReadOnlyList<ShardMergeStats> Shards { get; }

        public int KeptFromThisPc => Shards.Sum(s => s.KeptFromThisPc);
        public int OverridingRelease => Shards.Sum(s => s.OverridingRelease);
        public int UpdatedFromRelease => Shards.Sum(s => s.UpdatedFromRelease);

        /// <summary>Operator-facing lines describing the merge (empty when the release's map was taken unchanged).</summary>
        public IReadOnlyList<string> Describe()
        {
            var lines = new List<string>();
            var kept = Shards.Where(s => s.KeptFromThisPc > 0).ToList();
            if (kept.Count > 0)
            {
                lines.Add($"Kept {KeptFromThisPc} field-access entr{(KeptFromThisPc == 1 ? "y" : "ies")} from this PC's map " +
                          $"({string.Join(", ", kept.Select(s => $"{s.Name}: {s.KeptFromThisPc}"))})" +
                          (OverridingRelease > 0 ? $"; {OverridingRelease} of them override the release's entry for the same field." : "."));
            }
            if (UpdatedFromRelease > 0)
            {
                lines.Add($"Took {UpdatedFromRelease} corrected curated entr{(UpdatedFromRelease == 1 ? "y" : "ies")} from the release " +
                          $"({string.Join(", ", Shards.Where(s => s.UpdatedFromRelease > 0).Select(s => $"{s.Name}: {s.UpdatedFromRelease}"))}).");
            }
            foreach (var s in Shards.Where(s => s.InstalledUnreadable))
            {
                lines.Add($"This PC's {s.Name} was not valid JSON (the server was treating it as empty); took the release's copy.");
            }
            return lines;
        }
    }

    /// <summary>
    /// The ONE rule for carrying this PC's writable-field map
    /// (<c>server\Schema\FieldAccessMap\base\*.json</c>) across a release swap,
    /// used by <c>lacerte-mcp update</c> (<see cref="ReleaseInstaller.Apply"/>)
    /// and by <c>install-http.ps1</c> (through <c>lacerte-mcp field-access merge</c>).
    ///
    /// <para>
    /// Per ENTRY, starting from the release's shard:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>an installed entry built on this PC that the release doesn't have is kept;
    ///   an installed CURATED entry the release no longer ships is dropped (the release removed it on
    ///   purpose — keeping it would freeze a retracted classification);</description></item>
    ///   <item><description>an installed entry built on this PC — any <c>source</c> other than the
    ///   curated release sources (<see cref="ReleaseSources"/>: <c>sdk-pdf</c>, and <c>spec</c>, the
    ///   <see cref="FieldEntry"/> default) — overrides the release's entry for that field. That covers
    ///   the dumper (<c>live-probe</c> / <c>sim-probe</c>) and <c>lacerte-mcp field-access set</c>
    ///   (<c>manual</c> by default, or <c>introspect</c>);</description></item>
    ///   <item><description>an installed entry with a curated source is replaced by the release's,
    ///   so a corrected classification (e.g. ReadWrite → ReadOnly) always arrives.</description></item>
    /// </list>
    /// <para>
    /// Shards parse through the server's own <see cref="FieldAccessMapLoader.ValidateShardFile"/>.
    /// A merged shard is written sorted (ordinal key order) and indented like
    /// <c>field-access set</c> writes, with the shipped shards' camelCase property names.
    /// A shard where nothing from this PC survives is left byte-identical to the release's.
    /// </para>
    /// </summary>
    public static class FieldMapMerge
    {
        /// <summary><c>source</c> values of curated rows that ship in releases.</summary>
        public static readonly IReadOnlyCollection<string> ReleaseSources =
            new HashSet<string>(new[] { "sdk-pdf", "spec" }, StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions WriteOptions = BuildWriteOptions();

        private static JsonSerializerOptions BuildWriteOptions()
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            opts.Converters.Add(new JsonStringEnumConverter());
            return opts;
        }

        public static bool IsReleaseSource(string? source)
            => ReleaseSources.Contains((source ?? "spec").Trim());

        /// <summary>
        /// Merges the installed base shards in <paramref name="installedBaseDir"/> into the
        /// release's in <paramref name="releaseBaseDir"/>, rewriting the latter in place.
        /// Throws <see cref="UpdateException"/> when a RELEASE shard doesn't parse (nothing is written then).
        /// </summary>
        public static FieldMapMergeResult Merge(string installedBaseDir, string releaseBaseDir)
        {
            var stats = new List<ShardMergeStats>();
            if (!Directory.Exists(installedBaseDir)) return new FieldMapMergeResult(stats);

            var plans = new List<(string Path, SortedDictionary<string, FieldEntry> Rows)>();
            foreach (var installedPath in Directory.EnumerateFiles(installedBaseDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(installedPath);
                var releasePath = Path.Combine(releaseBaseDir, name);

                var installed = FieldAccessMapLoader.ValidateShardFile(installedPath, out var installedError);
                if (installed == null)
                {
                    stats.Add(new ShardMergeStats(name, 0, 0, 0, installedUnreadable: true));
                    continue;
                }
                if (installed.Count == 0) continue;

                IReadOnlyDictionary<string, FieldEntry> release = new Dictionary<string, FieldEntry>();
                if (File.Exists(releasePath))
                {
                    release = FieldAccessMapLoader.ValidateShardFile(releasePath, out var releaseError)
                              ?? throw new UpdateException($"The release's field-access shard {name} is not valid JSON ({releaseError}).");
                }

                var merged = new SortedDictionary<string, FieldEntry>(StringComparer.Ordinal);
                foreach (var kv in release) merged[kv.Key] = kv.Value;

                int kept = 0, overriding = 0, updated = 0;
                foreach (var kv in installed)
                {
                    if (!release.TryGetValue(kv.Key, out var incoming))
                    {
                        if (IsReleaseSource(kv.Value.Source))
                        {
                            // A curated row the release no longer ships was retracted.
                            updated++;
                            continue;
                        }
                        merged[kv.Key] = kv.Value;
                        kept++;
                    }
                    else if (!IsReleaseSource(kv.Value.Source))
                    {
                        merged[kv.Key] = kv.Value;
                        kept++;
                        if (kv.Value != incoming) overriding++;
                    }
                    else if (kv.Value != incoming)
                    {
                        updated++;
                    }
                }

                if (kept > 0 || updated > 0) stats.Add(new ShardMergeStats(name, kept, overriding, updated, installedUnreadable: false));
                if (kept > 0) plans.Add((releasePath, merged));
            }

            // Only write once every shard has parsed, so a bad release shard leaves nothing half-merged.
            foreach (var (path, rows) in plans)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(rows, WriteOptions) + "\n";
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            return new FieldMapMergeResult(stats);
        }
    }
}
