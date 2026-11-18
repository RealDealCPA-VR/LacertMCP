using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LacertMCP.Cli.Update
{
    public enum InstallLayout
    {
        /// <summary>Unzipped release: <c>&lt;root&gt;\server\LacertMCP.Server.exe</c> + <c>&lt;root&gt;\cli\lacerte-mcp.exe</c>.</summary>
        Release,

        /// <summary>A git working copy of the repo (CLI running from <c>src\LacertMCP.Cli\bin\...</c>).</summary>
        SourceCheckout,

        Unknown,
    }

    public sealed class InstallInfo
    {
        public InstallInfo(InstallLayout layout, string root)
        {
            Layout = layout;
            Root = root;
        }

        public InstallLayout Layout { get; }

        /// <summary>Install root (release) or repo root (source checkout).</summary>
        public string Root { get; }

        public string ServerDir => Path.Combine(Root, "server");
        public string CliDir => Path.Combine(Root, "cli");
    }

    /// <summary>Outcome of <see cref="ReleaseInstaller.Apply"/>.</summary>
    public sealed class ApplyResult
    {
        public ApplyResult(string backupDir, FieldMapMergeResult fieldMap, int filesWritten, int filesRetired,
            FieldDictionaryMergeResult? dictionary = null)
        {
            BackupDir = backupDir;
            FieldMap = fieldMap;
            Dictionary = dictionary ?? new FieldDictionaryMergeResult(0, 0, Array.Empty<string>(), Array.Empty<string>());
            FilesWritten = filesWritten;
            FilesRetired = filesRetired;
        }

        public string BackupDir { get; }

        /// <summary>
        /// How this PC's <c>server\Schema\FieldAccessMap\base\*.json</c> entries were
        /// merged into the release's (see <see cref="FieldMapMerge"/>).
        /// </summary>
        public FieldMapMergeResult FieldMap { get; }

        /// <summary>How this PC's recorded field-dictionary entries were carried over (see <see cref="FieldDictionaryMerge"/>).</summary>
        public FieldDictionaryMergeResult Dictionary { get; }

        public int FilesWritten { get; }
        public int FilesRetired { get; }
    }

    /// <summary>
    /// Swaps an unzipped release into an existing release-layout install.
    ///
    /// <para>
    /// Windows will not let a running exe/dll be deleted or overwritten, but it
    /// WILL let it be renamed. So each existing file is deleted, or — when it is
    /// locked (this CLI's own binaries, or a server held by an MCP host run with
    /// <c>--allow-running</c>) — renamed to <c>*.old-update</c> and swept on the
    /// next update. The previous <c>server\</c> and <c>cli\</c> trees are copied
    /// to <c>backups\v&lt;old&gt;-&lt;stamp&gt;\</c> first, and a failed swap is rolled
    /// back from that copy.
    /// </para>
    /// <para>
    /// The writable-field map in <c>server\Schema\FieldAccessMap\base</c> mixes
    /// curated release rows with operator data (built on the Lacerte PC by the
    /// dumper or <c>field-access set</c>), so the installed shards are merged into
    /// the release's per entry by <see cref="FieldMapMerge"/> — the same rule
    /// <c>install-http.ps1</c> applies — unless <c>resetFieldMap</c> is set.
    /// Overlays and the manifest always come from the release.
    /// </para>
    /// <para>
    /// While files are being swapped, <see cref="PendingFileName"/> in the install
    /// root names the backup; if the swap is interrupted (console closed, reboot,
    /// power loss) the next <c>lacerte-mcp update</c> restores from it first.
    /// </para>
    /// </summary>
    public static class ReleaseInstaller
    {
        public const string ServerExe = "LacertMCP.Server.exe";
        public const string CliExe = "lacerte-mcp.exe";
        public const string RetiredSuffix = ".old-update";
        public const string PendingFileName = ".update-pending";
        public const int BackupsToKeep = 3;

        // dumper\ ships from v0.2.1 on; a dir the release doesn't carry is left alone.
        private static readonly string[] ManagedDirs = { "server", "cli", "dumper" };
        private static readonly string[] RootDocs = { "README.md", "LICENSE", "CHANGELOG.md" };

        // Earlier updates copied install-http.ps1 into the install root. Run from
        // there, it resolved its source to the install itself and deleted server\
        // before copying it; remove that copy (the installer belongs in the zip).
        private static readonly string[] RetiredRootFiles = { "install-http.ps1" };

        private const string BackupStampFormat = "yyyyMMdd-HHmmss";

        // Top-level file types the release owns inside server\ and cli\. Anything
        // else at top level (e.g. a logs\ folder when the host's cwd is server\)
        // is left alone by the stale-file sweep.
        private static readonly HashSet<string> ManagedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".config", ".pdb", ".xml", ".json",
        };

        /// <summary>
        /// Works out what kind of install the CLI is running from.
        /// <paramref name="explicitRoot"/> (from <c>--install-dir</c>) wins.
        /// </summary>
        public static InstallInfo Detect(string? explicitRoot, string exeDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot))
            {
                var full = Path.GetFullPath(explicitRoot!.Trim());
                if (IsReleaseRoot(full)) return new InstallInfo(InstallLayout.Release, full);
                if (IsSourceRoot(full)) return new InstallInfo(InstallLayout.SourceCheckout, full);
                return new InstallInfo(InstallLayout.Unknown, full);
            }

            var exe = new DirectoryInfo(Path.GetFullPath(exeDir));
            if (exe.Parent != null
                && string.Equals(exe.Name, "cli", StringComparison.OrdinalIgnoreCase)
                && IsReleaseRoot(exe.Parent.FullName))
            {
                return new InstallInfo(InstallLayout.Release, exe.Parent.FullName);
            }

            // Source checkout: walk up to the folder holding LacertMCP.sln + .git.
            for (var d = exe; d != null; d = d.Parent)
            {
                if (IsSourceRoot(d.FullName)) return new InstallInfo(InstallLayout.SourceCheckout, d.FullName);
            }

            return new InstallInfo(InstallLayout.Unknown, exe.FullName);
        }

        private static bool IsReleaseRoot(string dir)
            => File.Exists(Path.Combine(dir, "server", ServerExe));

        private static bool IsSourceRoot(string dir)
            => File.Exists(Path.Combine(dir, "LacertMCP.sln"))
               && (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")));

        /// <summary>The release zip's exact name for <paramref name="version"/>.</summary>
        public static string ZipAssetName(ReleaseVersion version) => "LacertMCP-v" + version + ".zip";

        /// <summary>
        /// Picks the release zip among a release's assets: exactly
        /// <c>LacertMCP-v&lt;version&gt;.zip</c>, nothing looser (another zip on the
        /// release could be a different build).
        /// </summary>
        public static ReleaseAsset? SelectZipAsset(ReleaseInfo release, ReleaseVersion version)
        {
            var exact = ZipAssetName(version);
            return release.Assets.FirstOrDefault(a => string.Equals(a.Name, exact, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Checks the downloaded file's size and (when published) SHA-256.</summary>
        public static void VerifyDownload(string path, ReleaseAsset asset)
        {
            var len = new FileInfo(path).Length;
            if (asset.Size >= 0 && len != asset.Size)
            {
                throw new UpdateException($"Downloaded {asset.Name} is {len} bytes; the release says {asset.Size}. Not installing.");
            }
            if (asset.Sha256 != null)
            {
                var actual = Sha256Hex(path);
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException($"SHA-256 mismatch for {asset.Name} (expected {asset.Sha256}, got {actual}). Not installing.");
                }
            }
        }

        public static string Sha256Hex(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Extracts <paramref name="zipPath"/> into <paramref name="destDir"/>,
        /// refusing any entry that would land outside it (zip-slip), then checks
        /// the release shape: <c>server\LacertMCP.Server.exe</c> (+ its .config and
        /// the FieldAccessMap manifest) and <c>cli\lacerte-mcp.exe</c>.
        /// </summary>
        public static void ExtractAndValidate(string zipPath, string destDir)
        {
            var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    // Compress-Archive writes '/' (PowerShell 7) or '\' (Windows PowerShell 5.1).
                    var rel = entry.FullName.Replace('\\', '/');
                    if (rel.Length == 0) continue;
                    var target = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UpdateException($"Release zip entry '{entry.FullName}' escapes the extraction folder. Not installing.");
                    }
                    if (rel.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: false);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new UpdateException("The downloaded release is not a valid zip. Not installing.", ex);
            }

            foreach (var required in new[]
                     {
                         Path.Combine("server", ServerExe),
                         Path.Combine("server", ServerExe + ".config"),
                         Path.Combine("server", "Schema", "FieldAccessMap", "manifest.json"),
                         Path.Combine("cli", CliExe),
                     })
            {
                if (!File.Exists(Path.Combine(root, required)))
                {
                    throw new UpdateException($"The release zip is missing {required}. Not installing.");
                }
            }
        }

        /// <summary>
        /// Backs up <paramref name="installRoot"/>'s server\ and cli\ trees, then
        /// swaps in <paramref name="stagedRoot"/>'s. Rolls back on failure.
        /// </summary>
        public static ApplyResult Apply(string stagedRoot, string installRoot, string currentVersionLabel, TextWriter log,
            bool resetFieldMap = false, string? targetVersionLabel = null)
        {
            SweepRetired(installRoot);

            var backupsRoot = Path.Combine(installRoot, "backups");
            var backupDir = Path.Combine(backupsRoot,
                "v" + Sanitize(currentVersionLabel) + "-" + DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture));
            if (Directory.Exists(backupDir)) backupDir += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            log.WriteLine($"  Backing up current install -> {backupDir}");
            foreach (var d in ManagedDirs)
            {
                var src = Path.Combine(installRoot, d);
                if (Directory.Exists(src)) CopyTree(src, Path.Combine(backupDir, d), skipRetired: true);
            }

            // Merge this PC's map into the staged tree, so the swap (and its
            // stale-file sweep) treats the merged shards as part of the release.
            var installedBase = Path.Combine(installRoot, "server", "Schema", "FieldAccessMap", "base");
            var stagedBase = Path.Combine(stagedRoot, "server", "Schema", "FieldAccessMap", "base");
            var fieldMap = resetFieldMap
                ? new FieldMapMergeResult(Array.Empty<ShardMergeStats>())
                : FieldMapMerge.Merge(installedBase, stagedBase);

            // Mappings recorded on this PC with `map-diff record` are verified
            // operator data too; carry them into the release's dictionary.
            var dictionary = FieldDictionaryMerge.Merge(
                Path.Combine(installRoot, "server", "Schema", "FieldDictionary"),
                Path.Combine(stagedRoot, "server", "Schema", "FieldDictionary"));

            // From here until the swap completes (or is undone) the install may be
            // a mix of versions; the marker lets the next run put it back.
            WritePending(installRoot, new PendingUpdate(backupDir, currentVersionLabel, targetVersionLabel ?? "unknown"));

            var written = 0;
            var retired = 0;
            try
            {
                foreach (var d in ManagedDirs)
                {
                    var staged = Path.Combine(stagedRoot, d);
                    if (!Directory.Exists(staged)) continue;
                    var (w, r) = ReplaceTree(staged, Path.Combine(installRoot, d));
                    written += w;
                    retired += r;
                }
                foreach (var doc in RootDocs)
                {
                    var src = Path.Combine(stagedRoot, doc);
                    if (File.Exists(src))
                    {
                        PlaceFile(src, Path.Combine(installRoot, doc));
                        written++;
                    }
                }
                foreach (var stale in RetiredRootFiles)
                {
                    var path = Path.Combine(installRoot, stale);
                    if (File.Exists(path)) RetireFile(path);
                }
            }
            catch (Exception ex)
            {
                log.WriteLine($"  Swap failed ({ex.Message}) — restoring from {backupDir}");
                var restored = Restore(backupDir, installRoot, log, out var restoreError);
                // A marker that survives here points at the backup just restored, so a
                // repeat restore next run is a no-op.
                if (restored) { try { ClearPending(installRoot); } catch { /* see above */ } }
                throw new UpdateException(
                    $"Update failed while swapping files: {ex.Message} " +
                    (restored
                        ? "(the previous version was restored)"
                        : $"(RESTORE FAILED: {restoreError}; the next `lacerte-mcp update` retries it from {backupDir})"), ex);
            }

            PruneBackups(backupsRoot, BackupsToKeep, backupDir);
            return new ApplyResult(backupDir, fieldMap, written, retired, dictionary);
        }

        /// <summary>
        /// Puts <paramref name="backupDir"/>'s <c>server\</c>, <c>cli\</c> and
        /// <c>dumper\</c> back in place under <paramref name="installRoot"/> (the
        /// server must not be running). Returns false, with the reason, if it failed.
        /// The pending marker is left for the caller to clear.
        /// </summary>
        public static bool Restore(string backupDir, string installRoot, TextWriter log, out string? error)
        {
            error = null;
            try
            {
                if (!Directory.Exists(Path.Combine(backupDir, "server")))
                {
                    throw new DirectoryNotFoundException($"the backup {backupDir} has no server\\ folder");
                }
                foreach (var d in ManagedDirs)
                {
                    var b = Path.Combine(backupDir, d);
                    if (Directory.Exists(b)) ReplaceTree(b, Path.Combine(installRoot, d));
                }
                log.WriteLine("  Restore complete; the previous version is back in place.");
                return true;
            }
            catch (Exception rex)
            {
                error = rex.Message;
                log.WriteLine($"  RESTORE FAILED ({rex.Message}). Copy server\\ and cli\\ back from {backupDir} by hand.");
                return false;
            }
        }

        // ---------------------------------------------------------------- pending marker

        /// <summary>Contents of <see cref="PendingFileName"/>: an update that started swapping files.</summary>
        public sealed class PendingUpdate
        {
            public PendingUpdate(string backupDir, string fromVersion, string targetVersion)
            {
                BackupDir = backupDir;
                FromVersion = fromVersion;
                TargetVersion = targetVersion;
            }

            public string BackupDir { get; }
            public string FromVersion { get; }
            public string TargetVersion { get; }
        }

        public static void WritePending(string installRoot, PendingUpdate pending)
        {
            var text = "backup=" + pending.BackupDir + "\r\n" +
                       "from=" + pending.FromVersion + "\r\n" +
                       "target=" + pending.TargetVersion + "\r\n";
            var path = Path.Combine(installRoot, PendingFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>The interrupted update recorded in the install, or null. A marker without a backup path reads as null.</summary>
        public static PendingUpdate? ReadPending(string installRoot)
        {
            var path = Path.Combine(installRoot, PendingFileName);
            if (!File.Exists(path)) return null;
            string? backup = null, from = null, target = null;
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (key == "backup") backup = value;
                else if (key == "from") from = value;
                else if (key == "target") target = value;
            }
            return string.IsNullOrEmpty(backup) ? null : new PendingUpdate(backup!, from ?? "unknown", target ?? "unknown");
        }

        public static bool HasPendingMarker(string installRoot) => File.Exists(Path.Combine(installRoot, PendingFileName));

        /// <summary>
        /// Deletes the marker. Throws if it can't: a marker left behind after a
        /// SUCCESSFUL update would make the next run roll it back.
        /// </summary>
        public static void ClearPending(string installRoot)
            => File.Delete(Path.Combine(installRoot, PendingFileName));

        /// <summary>
        /// Makes <paramref name="dest"/> match <paramref name="src"/>: every source
        /// file is placed, and release-managed files no longer in the source are
        /// retired (so a shard dropped from the release stops loading).
        /// </summary>
        private static (int Written, int Retired) ReplaceTree(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            var written = 0;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(RetiredSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Relative(src, file);
                wanted.Add(rel);
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                PlaceFile(file, target);
                written++;
            }

            var retired = 0;
            foreach (var file in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).ToList())
            {
                if (file.EndsWith(RetiredSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Relative(dest, file);
                if (wanted.Contains(rel) || !IsReleaseManaged(rel)) continue;
                RetireFile(file);
                retired++;
            }
            return (written, retired);
        }

        private static bool IsReleaseManaged(string rel)
        {
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length == 1) return ManagedExtensions.Contains(Path.GetExtension(rel));
            return string.Equals(parts[0], "Schema", StringComparison.OrdinalIgnoreCase);
        }

        private static void PlaceFile(string src, string target)
        {
            if (File.Exists(target)) RetireFile(target);
            File.Copy(src, target, overwrite: false);
        }

        /// <summary>Delete; if Windows says it is in use, rename it out of the way instead.</summary>
        private static void RetireFile(string path)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var moved = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + RetiredSuffix;
            File.Move(path, moved);
        }

        /// <summary>Best-effort removal of files a previous update had to rename aside.</summary>
        public static void SweepRetired(string installRoot)
        {
            foreach (var d in ManagedDirs)
            {
                var dir = Path.Combine(installRoot, d);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*" + RetiredSuffix, SearchOption.AllDirectories).ToList())
                {
                    try { File.Delete(f); } catch { /* still locked — next time */ }
                }
            }
        }

        private static void CopyTree(string src, string dest, bool skipRetired)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (skipRetired && file.EndsWith(RetiredSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(dest, Relative(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static readonly Regex BackupStamp = new Regex(@"-(\d{8}-\d{6})(-[0-9a-f]{6})?\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// When a backup was taken: the <c>yyyyMMdd-HHmmss</c> stamp in its folder
        /// name (creation time is reset when a folder is copied or restored), falling
        /// back to the folder's creation time for names that carry no stamp.
        /// </summary>
        public static DateTime BackupTimestamp(DirectoryInfo dir)
        {
            var m = BackupStamp.Match(dir.Name);
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, BackupStampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var stamp))
            {
                return stamp;
            }
            return dir.CreationTime;
        }

        /// <summary>
        /// Keeps the newest <paramref name="keep"/> backups (by <see cref="BackupTimestamp"/>),
        /// counting — and never deleting — <paramref name="current"/>, the one just made.
        /// </summary>
        public static void PruneBackups(string backupsRoot, int keep, string? current = null)
        {
            try
            {
                var currentFull = current == null ? null : Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
                var others = new DirectoryInfo(backupsRoot).GetDirectories()
                    .Where(d => currentFull == null
                                || !string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), currentFull, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(BackupTimestamp)
                    .ThenByDescending(d => d.Name, StringComparer.Ordinal)
                    .Skip(currentFull == null ? keep : Math.Max(0, keep - 1))
                    .ToList();
                foreach (var d in others)
                {
                    try { d.Delete(recursive: true); } catch { /* best-effort */ }
                }
            }
            catch { /* best-effort */ }
        }

        private static string Relative(string root, string file)
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).Substring(r.Length);
        }

        private static string Sanitize(string label)
        {
            var chars = label.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray();
            return chars.Length == 0 ? "unknown" : new string(chars);
        }
    }
}
