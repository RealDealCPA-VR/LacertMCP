using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

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
        public ApplyResult(string backupDir, IReadOnlyList<string> keptFieldMapShards, int filesWritten, int filesRetired)
        {
            BackupDir = backupDir;
            KeptFieldMapShards = keptFieldMapShards;
            FilesWritten = filesWritten;
            FilesRetired = filesRetired;
        }

        public string BackupDir { get; }

        /// <summary>
        /// Installed <c>server\Schema\FieldAccessMap\base\*.json</c> shards kept in
        /// place of the release's copy (file names), because they differ from it.
        /// </summary>
        public IReadOnlyList<string> KeptFieldMapShards { get; }

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
    /// The writable-field map in <c>server\Schema\FieldAccessMap\base</c> is
    /// operator data (built on the Lacerte PC by the dumper), so — exactly like
    /// <c>install-http.ps1</c> — any non-empty installed base shard is kept over
    /// the release's copy unless <c>resetFieldMap</c> is set. Overlays and the
    /// manifest always come from the release.
    /// </para>
    /// </summary>
    public static class ReleaseInstaller
    {
        public const string ServerExe = "LacertMCP.Server.exe";
        public const string CliExe = "lacerte-mcp.exe";
        public const string RetiredSuffix = ".old-update";
        public const int BackupsToKeep = 3;

        // dumper\ ships from v0.2.1 on; a dir the release doesn't carry is left alone.
        private static readonly string[] ManagedDirs = { "server", "cli", "dumper" };
        private static readonly string[] RootDocs = { "README.md", "LICENSE", "CHANGELOG.md", "install-http.ps1" };

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

        /// <summary>Picks the release zip among a release's assets.</summary>
        public static ReleaseAsset? SelectZipAsset(ReleaseInfo release, ReleaseVersion version)
        {
            var exact = "LacertMCP-v" + version + ".zip";
            return release.Assets.FirstOrDefault(a => string.Equals(a.Name, exact, StringComparison.OrdinalIgnoreCase))
                   ?? release.Assets.FirstOrDefault(a =>
                       a.Name.StartsWith("LacertMCP-", StringComparison.OrdinalIgnoreCase)
                       && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
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
            bool resetFieldMap = false)
        {
            SweepRetired(installRoot);

            var backupsRoot = Path.Combine(installRoot, "backups");
            var backupDir = Path.Combine(backupsRoot,
                "v" + Sanitize(currentVersionLabel) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            if (Directory.Exists(backupDir)) backupDir += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            log.WriteLine($"  Backing up current install -> {backupDir}");
            foreach (var d in ManagedDirs)
            {
                var src = Path.Combine(installRoot, d);
                if (Directory.Exists(src)) CopyTree(src, Path.Combine(backupDir, d), skipRetired: true);
            }

            var installedBase = Path.Combine(installRoot, "server", "Schema", "FieldAccessMap", "base");
            var stagedBase = Path.Combine(stagedRoot, "server", "Schema", "FieldAccessMap", "base");
            var keep = resetFieldMap ? new List<string>() : ShardsToKeep(installedBase, stagedBase);
            if (keep.Count > 0)
            {
                // Overlay the operator's shards onto the staged tree, so the swap
                // (and its stale-file sweep) treats them as part of the release.
                Directory.CreateDirectory(stagedBase);
                foreach (var name in keep) File.Copy(Path.Combine(installedBase, name), Path.Combine(stagedBase, name), overwrite: true);
            }

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
            }
            catch (Exception ex)
            {
                log.WriteLine($"  Swap failed ({ex.Message}) — restoring from {backupDir}");
                try
                {
                    foreach (var d in ManagedDirs)
                    {
                        var b = Path.Combine(backupDir, d);
                        if (Directory.Exists(b)) ReplaceTree(b, Path.Combine(installRoot, d));
                    }
                    log.WriteLine("  Restore complete; the previous version is back in place.");
                }
                catch (Exception rex)
                {
                    log.WriteLine($"  RESTORE FAILED ({rex.Message}). Copy server\\ and cli\\ back from {backupDir} by hand.");
                }
                throw new UpdateException($"Update failed while swapping files: {ex.Message}", ex);
            }

            PruneBackups(backupsRoot, BackupsToKeep);
            return new ApplyResult(backupDir, keep, written, retired);
        }

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

        /// <summary>
        /// Installed base shards to keep: non-empty (more than "{}") and different
        /// from the release's copy (or absent from the release).
        /// </summary>
        private static List<string> ShardsToKeep(string installedBase, string stagedBase)
        {
            var keep = new List<string>();
            if (!Directory.Exists(installedBase)) return keep;
            foreach (var file in Directory.EnumerateFiles(installedBase, "*.json"))
            {
                if (new FileInfo(file).Length <= 2) continue;
                var name = Path.GetFileName(file);
                var incoming = Path.Combine(stagedBase, name);
                if (File.Exists(incoming) && string.Equals(Sha256Hex(file), Sha256Hex(incoming), StringComparison.Ordinal)) continue;
                keep.Add(name);
            }
            keep.Sort(StringComparer.OrdinalIgnoreCase);
            return keep;
        }

        private static void PruneBackups(string backupsRoot, int keep)
        {
            try
            {
                var dirs = new DirectoryInfo(backupsRoot).GetDirectories()
                    .OrderByDescending(d => d.CreationTimeUtc)
                    .Skip(keep);
                foreach (var d in dirs)
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
