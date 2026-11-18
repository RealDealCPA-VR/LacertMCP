using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Update;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// `lacerte-mcp update` — release-install swap (against a fake release source
    /// and a fake always-on service, never the network), the scheduled task, and
    /// the source-checkout fast-forward (against throwaway local git repos).
    /// </summary>
    public sealed class UpdateCommandTests : IDisposable
    {
        private readonly string _root;

        public UpdateCommandTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "lacerte-mcp-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        // ------------------------------------------------------------ version + parsing

        [Theory]
        [InlineData("0.2.1", "0.2.0", 1)]
        [InlineData("v0.2.1", "0.2.1+abc1234", 0)]
        [InlineData("0.10.0", "0.9.9", 1)]
        [InlineData("1.0.0", "1.0.0-rc.1", 1)]
        [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
        [InlineData("1.0.0-alpha", "1.0.0-1", 1)]
        [InlineData("0.1.0.0", "0.1.0", 0)]
        public void ReleaseVersion_Compares_SemVer(string a, string b, int expected)
        {
            Assert.True(ReleaseVersion.TryParse(a, out var va));
            Assert.True(ReleaseVersion.TryParse(b, out var vb));
            Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("latest")]
        [InlineData("1.2")]
        [InlineData("1.2.3 ; rm")]
        public void ReleaseVersion_Rejects_NonVersions(string s)
            => Assert.False(ReleaseVersion.TryParse(s, out _));

        [Fact]
        public void GitHubParse_ReadsTagAssetsAndDigest()
        {
            var r = GitHubReleaseSource.Parse(
                "{\"tag_name\":\"v0.2.1\",\"html_url\":\"https://x/r\",\"assets\":[{\"name\":\"LacertMCP-v0.2.1.zip\"," +
                "\"browser_download_url\":\"https://x/a.zip\",\"size\":42,\"digest\":\"sha256:ABCDEF\"}]}");
            Assert.Equal("v0.2.1", r.Tag);
            var a = Assert.Single(r.Assets);
            Assert.Equal(42, a.Size);
            Assert.Equal("abcdef", a.Sha256);
        }

        // ------------------------------------------------------------ layout detection

        [Fact]
        public void Detect_ReleaseLayout_FromCliFolder()
        {
            var install = MakeInstall("0.1.0");
            var info = ReleaseInstaller.Detect(null, Path.Combine(install, "cli"));
            Assert.Equal(InstallLayout.Release, info.Layout);
            Assert.Equal(Path.GetFullPath(install), info.Root);
        }

        [Fact]
        public void Detect_SourceCheckout_WalksUpToSolution()
        {
            var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
            File.WriteAllText(Path.Combine(repo, "LacertMCP.sln"), "");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var bin = Directory.CreateDirectory(Path.Combine(repo, "src", "LacertMCP.Cli", "bin", "x64", "Debug")).FullName;
            var info = ReleaseInstaller.Detect(null, bin);
            Assert.Equal(InstallLayout.SourceCheckout, info.Layout);
            Assert.Equal(repo, info.Root);
        }

        [Fact]
        public async Task UnknownLayout_Refuses()
        {
            var lone = Directory.CreateDirectory(Path.Combine(_root, "lone")).FullName;
            var (code, _, err) = await Run(new UpdateCommand.Options(), new FakeSource(), lone, "0.1.0");
            Assert.Equal(1, code);
            Assert.Contains("--install-dir", err);
        }

        // ------------------------------------------------------------ release install

        [Fact]
        public async Task ReleaseUpdate_SwapsFiles_BacksUp_RetiresStaleFiles_KeepsLogs()
        {
            var install = MakeInstall("0.1.0");
            // An empty ("{}") shard the release no longer ships is not operator data: retired.
            File.WriteAllText(Path.Combine(install, "server", "Schema", "FieldAccessMap", "base", "dropped.json"), "{}");
            File.WriteAllText(Path.Combine(install, "server", "Old.Dependency.dll"), "stale");
            Directory.CreateDirectory(Path.Combine(install, "server", "logs"));
            File.WriteAllText(Path.Combine(install, "server", "logs", "lacerte-1.log"), "keep me");
            // H2: an earlier updater copied the installer into the install root.
            File.WriteAllText(Path.Combine(install, "install-http.ps1"), "installer 0.1.0");
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"));

            var (code, output, err) = await Run(new UpdateCommand.Options(), source, Path.Combine(install, "cli"), "0.1.0");

            Assert.True(code == 0, output + err);
            Assert.Equal("server 0.2.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            Assert.Equal("cli 0.2.0", File.ReadAllText(Path.Combine(install, "cli", "lacerte-mcp.exe")));
            Assert.Equal("dumper 0.2.0", File.ReadAllText(Path.Combine(install, "dumper", "LacertMCP.FieldAccessDumper.exe")));
            Assert.Equal("overlay 0.2.0", File.ReadAllText(Path.Combine(install, "server", "Schema", "FieldAccessMap", "overlays", "ty21.json")));
            Assert.Equal("readme 0.2.0", File.ReadAllText(Path.Combine(install, "README.md")));
            Assert.False(File.Exists(Path.Combine(install, "server", "Schema", "FieldAccessMap", "base", "dropped.json")));
            Assert.False(File.Exists(Path.Combine(install, "server", "Old.Dependency.dll")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "server", "logs", "lacerte-1.log")));
            Assert.False(File.Exists(Path.Combine(install, ".update.lock")));

            var backup = Assert.Single(Directory.GetDirectories(Path.Combine(install, "backups")));
            Assert.StartsWith("v0.1.0-", Path.GetFileName(backup));
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(backup, "server", "LacertMCP.Server.exe")));
            Assert.Equal("stale", File.ReadAllText(Path.Combine(backup, "server", "Old.Dependency.dll")));

            Assert.Contains("Updated v0.1.0 -> v0.2.0", output);
            Assert.DoesNotContain("field-access entr", output);
            Assert.Contains("Restart Claude Desktop", output);
            Assert.Contains("updated v0.1.0 -> v0.2.0", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));

            // H2: the installer never lands in (and a stale copy is removed from) the
            // install root — run from there it would delete server\ before copying it.
            Assert.False(File.Exists(Path.Combine(install, "install-http.ps1")));
            // M1: the in-progress marker is gone after a successful update.
            Assert.False(File.Exists(Path.Combine(install, ReleaseInstaller.PendingFileName)));
        }

        // H1: per-entry merge of this PC's writable-field map into the release's —
        // a curated fix arrives, an entry built on this PC is kept, --reset-field-map
        // takes the release's shard as-is.
        [Fact]
        public async Task ReleaseUpdate_MergesFieldMap_CuratedFixArrives_DumperEntryKept_UnlessReset()
        {
            var install = MakeInstall("0.1.0");
            var shard = Path.Combine(install, "server", "Schema", "FieldAccessMap", "base", "individual.json");
            File.WriteAllText(shard, ShardJson.Shard(
                ShardJson.Row("I|1|C1_G12", "ReadWrite", "sdk-pdf"),          // curated, corrected in the release
                ShardJson.Row("I|1|C1_G13", "ReadWrite", "sdk-pdf"),          // curated, unchanged
                ShardJson.Row("I|1|C1_77", "ReadWrite", "live-probe", null))); // built on this PC by the dumper
            const string releaseShard = "{\n" +
                "  \"I|1|C1_G12\": { \"access\": \"ReadOnly\", \"source\": \"sdk-pdf\", \"description\": \"Global field (documented access)\", \"addedAt\": \"2026-11-20\" },\n" +
                "  \"I|1|C1_G13\": { \"access\": \"ReadWrite\", \"source\": \"sdk-pdf\", \"description\": \"Global field (documented access)\", \"addedAt\": \"2026-11-15\" }\n" +
                "}\n";
            var zip = MakeReleaseZip("0.2.0", tweak: stage =>
                File.WriteAllText(Path.Combine(stage, "server", "Schema", "FieldAccessMap", "base", "individual.json"), releaseShard));

            var (code, output, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip), Path.Combine(install, "cli"), "0.1.0");
            Assert.True(code == 0, output + err);

            var merged = ShardJson.Load(shard);
            Assert.Equal(FieldAccess.ReadOnly, merged["I|1|C1_G12"].Access);     // the safety fix arrived
            Assert.Equal(FieldAccess.ReadWrite, merged["I|1|C1_G13"].Access);
            Assert.Equal("live-probe", merged["I|1|C1_77"].Source);               // the dumper's row survived
            Assert.Equal(3, merged.Count);
            Assert.Contains("Kept 1 field-access entry from this PC's map (individual.json: 1)", output);
            Assert.Contains("Took 1 corrected curated entry from the release", output);
            Assert.Equal("server 0.2.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));

            (code, output, err) = await Run(new UpdateCommand.Options { Force = true, ResetFieldMap = true },
                FakeSource.For("0.2.0", zip), Path.Combine(install, "cli"), "0.2.0");
            Assert.True(code == 0, output + err);
            Assert.Equal(releaseShard, File.ReadAllText(shard));
        }

        [Fact]
        public async Task Check_ReportsAvailable_Exit2_ChangesNothing()
        {
            var install = MakeInstall("0.1.0");
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"));
            var (code, output, _) = await Run(new UpdateCommand.Options { Check = true }, source, Path.Combine(install, "cli"), "0.1.0+abc1234");
            Assert.Equal(2, code);
            Assert.Contains("Update available", output);
            Assert.Equal(0, source.Downloads);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            Assert.False(File.Exists(Path.Combine(install, UpdateCommand.LogFileName)));
        }

        [Fact]
        public async Task AlreadyCurrent_Exit0_NoDownload()
        {
            var install = MakeInstall("0.2.0");
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"));
            var (code, output, _) = await Run(new UpdateCommand.Options(), source, Path.Combine(install, "cli"), "0.2.0");
            Assert.Equal(0, code);
            Assert.Contains("Already up to date", output);
            Assert.Equal(0, source.Downloads);
        }

        [Fact]
        public async Task Tag_RequestsThatRelease_AndAllowsDowngrade()
        {
            var install = MakeInstall("0.3.0");
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"));
            var (code, output, err) = await Run(new UpdateCommand.Options { Tag = "0.2.0" }, source, Path.Combine(install, "cli"), "0.3.0");
            Assert.True(code == 0, output + err);
            Assert.Equal("v0.2.0", source.RequestedTag);
            Assert.Contains("Downgrading", output);
            Assert.Equal("server 0.2.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
        }

        [Fact]
        public async Task DigestMismatch_RefusesAndLeavesInstallUntouched()
        {
            var install = MakeInstall("0.1.0");
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"), sha256Override: new string('0', 64));
            var (code, _, err) = await Run(new UpdateCommand.Options(), source, Path.Combine(install, "cli"), "0.1.0");
            Assert.Equal(1, code);
            Assert.Contains("SHA-256 mismatch", err);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            Assert.False(Directory.Exists(Path.Combine(install, "backups")));
            Assert.Contains("FAILED", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));
        }

        [Fact]
        public async Task ZipSlipEntry_Refused()
        {
            var install = MakeInstall("0.1.0");
            var zip = MakeReleaseZip("0.2.0", extra: z =>
            {
                using var w = new StreamWriter(z.CreateEntry("../../escaped.txt").Open());
                w.Write("x");
            });
            var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip), Path.Combine(install, "cli"), "0.1.0");
            Assert.Equal(1, code);
            Assert.Contains("escapes", err);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
        }

        [Fact]
        public async Task ZipMissingServer_Refused()
        {
            var install = MakeInstall("0.1.0");
            var zip = MakeReleaseZip("0.2.0", omitServerExe: true);
            var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip), Path.Combine(install, "cli"), "0.1.0");
            Assert.Equal(1, code);
            Assert.Contains("missing", err);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
        }

        // No always-on launcher (a stdio server under Claude Desktop): a running
        // server blocks the swap unless --allow-running.
        [Fact]
        public async Task RunningStdioServer_Refused_UnlessAllowRunning()
        {
            var install = MakeInstall("0.1.0");
            var zip = MakeReleaseZip("0.2.0");
            var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip), Path.Combine(install, "cli"), "0.1.0",
                running: _ => new[] { 4242 });
            Assert.Equal(1, code);
            Assert.Contains("4242", err);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));

            (code, _, err) = await Run(new UpdateCommand.Options { AllowRunning = true }, FakeSource.For("0.2.0", zip), Path.Combine(install, "cli"), "0.1.0",
                running: _ => new[] { 4242 });
            Assert.True(code == 0, err);
        }

        // install-http.ps1's always-on launcher: stopped only after the download is
        // verified, files swapped, restarted, /healthz checked.
        [Fact]
        public async Task AlwaysOnServer_StoppedSwappedRestarted()
        {
            var install = MakeInstall("0.1.0");
            var svc = new FakeService(install);
            var (code, output, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", MakeReleaseZip("0.2.0")),
                Path.Combine(install, "cli"), "0.1.0", running: _ => new[] { 99 }, service: svc);

            Assert.True(code == 0, output + err);
            Assert.Equal(new[] { "stop:server 0.1.0", "start:server 0.2.0", "health" }, svc.Calls);
            Assert.Contains("Server is back up: http://localhost:8767/", output);
        }

        [Fact]
        public async Task AlwaysOnServer_UntouchedWhenDownloadIsBad()
        {
            var install = MakeInstall("0.1.0");
            var svc = new FakeService(install);
            var (code, _, _) = await Run(new UpdateCommand.Options(),
                FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"), sha256Override: new string('0', 64)),
                Path.Combine(install, "cli"), "0.1.0", service: svc);
            Assert.Equal(1, code);
            Assert.Empty(svc.Calls);
        }

        // A swap that fails part-way rolls back to the old files and still
        // restarts the server, so the PC is never left without one.
        [Fact]
        public async Task AlwaysOnServer_FailedSwap_RollsBackAndRestartsOldVersion()
        {
            var install = MakeInstall("0.1.0");
            var zip = MakeReleaseZip("0.2.0", extra: z =>
            {
                using var w = new StreamWriter(z.CreateEntry("server/New.Dependency.dll").Open());
                w.Write("new");
            });
            // A directory squatting on a path the release needs makes the copy fail.
            var svc = new FakeService(install)
            {
                OnStop = () => Directory.CreateDirectory(Path.Combine(install, "server", "New.Dependency.dll")),
            };

            var (code, output, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip),
                Path.Combine(install, "cli"), "0.1.0", service: svc);

            Assert.Equal(1, code);
            Assert.Contains("Restore complete", output);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            Assert.Equal(new[] { "stop:server 0.1.0", "start:server 0.1.0" }, svc.Calls);
            Assert.Contains("Update failed", err);
            Assert.Contains("FAILED", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));
        }

        // H3: a new version that doesn't come up is rolled back (so the next run
        // doesn't call the broken install "up to date").
        [Fact]
        public async Task AlwaysOnServer_UnhealthyAfterRestart_RollsBack_Exit1()
        {
            var install = MakeInstall("0.1.0");
            var svc = new FakeService(install) { Healthy = false };
            var (code, output, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", MakeReleaseZip("0.2.0")),
                Path.Combine(install, "cli"), "0.1.0", service: svc);
            Assert.Equal(1, code);
            Assert.Contains("did not answer", output);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            Assert.Equal("cli 0.1.0", File.ReadAllText(Path.Combine(install, "cli", "lacerte-mcp.exe")));
            Assert.Equal(new[] { "stop:server 0.1.0", "start:server 0.2.0", "health", "stop:server 0.2.0", "start:server 0.1.0", "health" }, svc.Calls);
            Assert.Contains("Rolled back to v0.1.0", err);
            Assert.Contains("rolled back to v0.1.0", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));
            Assert.False(File.Exists(Path.Combine(install, ReleaseInstaller.PendingFileName)));
        }

        // L5: the swapped-in server must report the release's version.
        [Fact]
        public async Task WrongVersionAfterSwap_RollsBack()
        {
            var install = MakeInstall("0.1.0");
            var zip = MakeReleaseZip("0.2.0", tweak: stage =>
                File.WriteAllText(Path.Combine(stage, "server", "LacertMCP.Server.exe"), "server 0.1.9"));
            var svc = new FakeService(install);
            var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip),
                Path.Combine(install, "cli"), "0.1.0", service: svc);
            Assert.Equal(1, code);
            Assert.Contains("reports version 0.1.9, not v0.2.0", err);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            // Never started the wrong build: stop, restore, start the old one.
            Assert.Equal(new[] { "stop:server 0.1.0", "start:server 0.1.0", "health" }, svc.Calls);
            Assert.Contains("rolled back to v0.1.0", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));
        }

        [Fact]
        public void SelectZipAsset_RequiresExactName()
        {
            Assert.True(ReleaseVersion.TryParse("0.2.0", out var v));
            var other = new ReleaseAsset("LacertMCP-v0.2.0-debug.zip", "https://x/a", 1, null);
            Assert.Null(ReleaseInstaller.SelectZipAsset(new ReleaseInfo("v0.2.0", new[] { other }), v));
            var exact = new ReleaseAsset("LacertMCP-v0.2.0.zip", "https://x/b", 1, null);
            Assert.Same(exact, ReleaseInstaller.SelectZipAsset(new ReleaseInfo("v0.2.0", new[] { other, exact }), v));
        }

        // M2: an elevated launcher can't be stopped by a non-elevated update — abort
        // before the swap, change nothing, and do NOT start a second launcher.
        [Fact]
        public async Task AlwaysOnServer_StopDenied_AbortsBeforeSwap_NoRestart()
        {
            var install = MakeInstall("0.1.0");
            var svc = new FakeService(install) { StopDenied = "The server (LacertMCP.Server PID 7) runs with higher rights." };
            var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", MakeReleaseZip("0.2.0")),
                Path.Combine(install, "cli"), "0.1.0", service: svc);
            Assert.Equal(1, code);
            Assert.Equal(new[] { "stop-denied" }, svc.Calls);
            Assert.Contains("Sign out of Windows and back in", err);
            Assert.Equal("server 0.1.0", File.ReadAllText(Path.Combine(install, "server", "LacertMCP.Server.exe")));
            Assert.False(Directory.Exists(Path.Combine(install, "backups")));
            Assert.Contains("couldn't stop the always-on server", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));
        }

        // L2: with a launcher, a server it didn't start (Claude Desktop's stdio
        // server) gets the same refusal as without one.
        [Fact]
        public async Task AlwaysOnServer_ForeignStdioServer_Refused_UnlessAllowRunning()
        {
            var install = MakeInstall("0.1.0");
            var zip = MakeReleaseZip("0.2.0");
            var svc = new FakeService(install) { Foreign = new[] { 5150 } };
            var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", zip),
                Path.Combine(install, "cli"), "0.1.0", service: svc);
            Assert.Equal(1, code);
            Assert.Contains("5150", err);
            Assert.Empty(svc.Calls);
            Assert.Contains("refused", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));

            (code, _, err) = await Run(new UpdateCommand.Options { AllowRunning = true }, FakeSource.For("0.2.0", zip),
                Path.Combine(install, "cli"), "0.1.0", service: svc);
            Assert.True(code == 0, err);
        }

        // M1: an interrupted swap (marker left behind) is restored from its backup
        // at the start of the next run, which then updates normally.
        [Fact]
        public async Task InterruptedUpdate_RestoredFromBackup_ThenUpdates()
        {
            var install = MakeInstall("0.1.0");
            var backup = Path.Combine(install, "backups", "v0.1.0-20261101-030000");
            WriteTree(backup, "0.1.0");
            // A half-done swap: the server is new, the CLI is still old.
            File.WriteAllText(Path.Combine(install, "server", "LacertMCP.Server.exe"), "server 0.2.0");
            ReleaseInstaller.WritePending(install, new ReleaseInstaller.PendingUpdate(backup, "0.1.0", "0.2.0"));
            var svc = new FakeService(install);

            var (code, output, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", MakeReleaseZip("0.2.0")),
                Path.Combine(install, "cli"), installed: null!, service: svc);

            Assert.True(code == 0, output + err);
            Assert.Contains("was interrupted", output);
            Assert.Equal(new[] { "stop:server 0.2.0", "start:server 0.1.0", "stop:server 0.1.0", "start:server 0.2.0", "health" }, svc.Calls);
            Assert.Contains("Updated v0.1.0 -> v0.2.0", output);
            Assert.Equal("cli 0.2.0", File.ReadAllText(Path.Combine(install, "cli", "lacerte-mcp.exe")));
            Assert.False(File.Exists(Path.Combine(install, ReleaseInstaller.PendingFileName)));
            var log = File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName));
            Assert.Contains("found an interrupted update (v0.1.0 -> v0.2.0)", log);
            Assert.Contains("restored v0.1.0 after the interrupted update", log);
        }

        [Fact]
        public async Task InterruptedUpdate_BackupGone_Reinstalls()
        {
            var install = MakeInstall("0.2.0");
            ReleaseInstaller.WritePending(install, new ReleaseInstaller.PendingUpdate(Path.Combine(install, "backups", "gone"), "0.1.0", "0.2.0"));
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"));
            var (code, output, err) = await Run(new UpdateCommand.Options(), source, Path.Combine(install, "cli"), installed: null!);
            Assert.True(code == 0, output + err);
            Assert.Equal(1, source.Downloads); // not "Already up to date"
            Assert.False(File.Exists(Path.Combine(install, ReleaseInstaller.PendingFileName)));
        }

        // L1: unexpected exceptions (I/O, network) reach update.log too.
        [Fact]
        public async Task UnexpectedException_LoggedAndExit1()
        {
            var install = MakeInstall("0.1.0");
            var source = FakeSource.For("0.2.0", MakeReleaseZip("0.2.0"));
            source.DownloadThrows = new IOException("disk full");
            var (code, _, err) = await Run(new UpdateCommand.Options(), source, Path.Combine(install, "cli"), "0.1.0");
            Assert.Equal(1, code);
            Assert.Contains("Update failed: IOException: disk full", err);
            Assert.Contains("FAILED: IOException: disk full", File.ReadAllText(Path.Combine(install, UpdateCommand.LogFileName)));
        }

        // L7: pruning goes by the stamp in the folder name, and never removes the backup just made.
        [Fact]
        public void PruneBackups_OrdersByNameStamp_KeepsCurrent()
        {
            var root = Directory.CreateDirectory(Path.Combine(_root, "backups")).FullName;
            string Make(string name, DateTime created)
            {
                var d = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                Directory.SetCreationTime(d, created);
                return d;
            }
            // Creation times deliberately disagree with the stamps (e.g. a restored copy).
            var oldest = Make("v0.1.0-20260101-030000", DateTime.Now);
            var older = Make("v0.1.1-20260201-030000", DateTime.Now.AddDays(-30));
            var newer = Make("v0.1.2-20260301-030000", DateTime.Now.AddDays(-60));
            var current = Make("v0.1.3-20250101-030000", DateTime.Now.AddDays(-90)); // clock was wrong; still the one just made

            ReleaseInstaller.PruneBackups(root, 3, current);

            Assert.True(Directory.Exists(current));
            Assert.True(Directory.Exists(newer));
            Assert.True(Directory.Exists(older));
            Assert.False(Directory.Exists(oldest));
        }

        [Fact]
        public void LauncherParse_MatchesInstallAndReadsPort()
        {
            // Same shape install-http.ps1 writes.
            var serverDir = Path.Combine(_root, "LacertMCP", "server");
            var exe = Path.Combine(serverDir, "LacertMCP.Server.exe");
            var vbs = "Set env = sh.Environment(\"PROCESS\")\r\n" +
                      "env(\"LACERTE_LIVE\") = \"1\"\r\n" +
                      "env(\"LACERTE_HTTP_PORT\") = \"9123\"\r\n" +
                      "Do\r\n  sh.Run \"\"\"" + exe + "\"\" --http\", 0, True\r\n";

            var info = LauncherService.Parse(@"C:\Startup\LacertMCP.vbs", vbs, serverDir);
            Assert.NotNull(info);
            Assert.Equal(9123, info!.Port);
            Assert.Null(LauncherService.Parse(@"C:\Startup\LacertMCP.vbs", vbs, Path.Combine(_root, "other", "server")));
            Assert.Equal(LauncherService.DefaultPort, LauncherService.Parse("x", vbs.Replace("\"9123\"", "\"x\""), serverDir)!.Port);
        }

        [Fact]
        public void LauncherService_NoLauncherFile_IsNotAService()
        {
            var startup = Directory.CreateDirectory(Path.Combine(_root, "startup")).FullName;
            Assert.Null(new LauncherService(startup).Find(Path.Combine(_root, "x", "server")));
        }

        [Fact]
        public async Task Schedule_Daily_RegistersTask_Off_RemovesIt()
        {
            var install = MakeInstall("0.1.0");
            var calls = new List<string>();
            string? xml = null, xmlPath = null;
            Func<string, (int, string)> schtasks = a =>
            {
                calls.Add(a);
                var m = System.Text.RegularExpressions.Regex.Match(a, "/XML \"([^\"]+)\"");
                if (m.Success) { xmlPath = m.Groups[1].Value; xml = File.ReadAllText(xmlPath); } // exists during the call
                return (0, "SUCCESS");
            };

            var (code, output, err) = await Run(new UpdateCommand.Options { Schedule = "daily", At = "02:30" }, new FakeSource(),
                Path.Combine(install, "cli"), "0.1.0", schtasks: schtasks);
            Assert.True(code == 0, output + err);
            var cli = Path.Combine(Path.GetFullPath(install), "cli", "lacerte-mcp.exe");
            Assert.StartsWith("/Create /F /TN \"" + UpdateCommand.ScheduledTaskName + "\" /XML \"", calls[0]);
            Assert.NotNull(xml);
            // M4: a missed 02:30 run (PC asleep/off) runs as soon as possible, as the
            // signed-in user, only while signed in, without elevation.
            var doc = System.Xml.Linq.XDocument.Parse(xml!);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
            Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
            Assert.Equal("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value);
            Assert.False(string.IsNullOrWhiteSpace(doc.Descendants(ns + "UserId").Single().Value));
            Assert.EndsWith("T02:30:00", doc.Descendants(ns + "StartBoundary").Single().Value);
            Assert.Equal("1", doc.Descendants(ns + "DaysInterval").Single().Value);
            Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
            Assert.Equal(cli, doc.Descendants(ns + "Command").Single().Value);
            Assert.Equal("update", doc.Descendants(ns + "Arguments").Single().Value);
            Assert.False(File.Exists(xmlPath)); // temp XML cleaned up

            (code, _, _) = await Run(new UpdateCommand.Options { Schedule = "off" }, new FakeSource(),
                Path.Combine(install, "cli"), "0.1.0", schtasks: schtasks);
            Assert.Equal(0, code);
            Assert.Equal("/Delete /F /TN \"" + UpdateCommand.ScheduledTaskName + "\"", calls[1]);

            (code, _, err) = await Run(new UpdateCommand.Options { Schedule = "daily", At = "25:00" }, new FakeSource(),
                Path.Combine(install, "cli"), "0.1.0", schtasks: schtasks);
            Assert.Equal(1, code);
            Assert.Contains("HH:mm", err);
            Assert.Equal(2, calls.Count);
        }

        [Fact]
        public async Task ConcurrentUpdate_Refused()
        {
            var install = MakeInstall("0.1.0");
            using (new FileStream(Path.Combine(install, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (code, _, err) = await Run(new UpdateCommand.Options(), FakeSource.For("0.2.0", MakeReleaseZip("0.2.0")), Path.Combine(install, "cli"), "0.1.0");
                Assert.Equal(1, code);
                Assert.Contains("already running", err);
            }
        }

        // A genuinely running exe can't be deleted or overwritten on Windows, only
        // renamed — exactly the state of lacerte-mcp.exe during its own update and
        // of the server under --allow-running. Uses a copy of ping.exe as the
        // "server" so the lock is a real image lock, not a simulated one.
        [Fact]
        public async Task LockedRunningExe_IsRenamedAside_ThenSweptNextTime()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            if (!File.Exists(ping)) return;

            var install = MakeInstall("0.1.0");
            var serverExe = Path.Combine(install, "server", "LacertMCP.Server.exe");
            File.Copy(ping, serverExe, overwrite: true);
            using var proc = Process.Start(new ProcessStartInfo(serverExe, "-n 60 127.0.0.1")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            try
            {
                Assert.ThrowsAny<Exception>(() => File.Delete(serverExe)); // precondition: really locked

                var (code, output, err) = await Run(new UpdateCommand.Options { AllowRunning = true },
                    FakeSource.For("0.2.0", MakeReleaseZip("0.2.0")), Path.Combine(install, "cli"), "0.1.0",
                    running: _ => new[] { proc.Id });
                Assert.True(code == 0, output + err);
                Assert.Equal("server 0.2.0", File.ReadAllText(serverExe));
                Assert.Single(Directory.GetFiles(Path.Combine(install, "server"), "*" + ReleaseInstaller.RetiredSuffix));
            }
            finally
            {
                try { proc.Kill(); } catch { /* exited */ }
                proc.WaitForExit(10_000);
            }

            ReleaseInstaller.SweepRetired(install);
            Assert.Empty(Directory.GetFiles(Path.Combine(install, "server"), "*" + ReleaseInstaller.RetiredSuffix));
        }

        // ------------------------------------------------------------ source checkout

        [Fact]
        public void SourceCheckout_FastForwardsToUpstream()
        {
            if (!GitAvailable()) return;
            var origin = Path.Combine(_root, "origin.git");
            Git(_root, $"init --bare -q \"{origin}\"");
            var mine = Path.Combine(_root, "mine");
            var theirs = Path.Combine(_root, "theirs");
            Git(_root, $"clone -q \"{origin}\" \"{mine}\"");
            File.WriteAllText(Path.Combine(mine, "LacertMCP.sln"), "v1");
            Git(mine, "add -A");
            Git(mine, "commit -q -m one");
            Git(mine, "push -q origin HEAD");
            Git(_root, $"clone -q \"{origin}\" \"{theirs}\"");
            File.WriteAllText(Path.Combine(theirs, "new.txt"), "from github");
            Git(theirs, "add -A");
            Git(theirs, "commit -q -m two");
            Git(theirs, "push -q origin HEAD");

            var outW = new StringWriter();
            var errW = new StringWriter();
            Assert.Equal(2, UpdateCommand.RunSourceCheckout(mine, check: true, outW, errW));
            Assert.False(File.Exists(Path.Combine(mine, "new.txt")));

            File.WriteAllText(Path.Combine(mine, "LacertMCP.sln"), "dirty");
            Assert.Equal(1, UpdateCommand.RunSourceCheckout(mine, check: false, outW, errW));
            Assert.Contains("uncommitted", errW.ToString());
            Git(mine, "checkout -q -- LacertMCP.sln");

            Assert.Equal(0, UpdateCommand.RunSourceCheckout(mine, check: false, outW, errW));
            Assert.Equal("from github", File.ReadAllText(Path.Combine(mine, "new.txt")));
            Assert.Contains("Pulled 1 commit", outW.ToString());
            // L6: a trailing backslash must not escape the closing quote of git -C "...".
            Assert.Equal(0, UpdateCommand.RunSourceCheckout(mine + Path.DirectorySeparatorChar, check: true, outW, errW));
        }

        // ------------------------------------------------------------ helpers

        private static async Task<(int Code, string Out, string Err)> Run(
            UpdateCommand.Options o, IReleaseSource source, string exeDir, string installed,
            Func<string, IReadOnlyList<int>>? running = null, IServiceControl? service = null,
            Func<string, (int, string)>? schtasks = null)
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            var env = new UpdateCommand.Deps(source, exeDir)
            {
                InstalledVersionOverride = installed,
                // The fake server exe's content says its version ("server 0.2.0").
                ReadServerVersion = FakeServerVersion,
                RunningServers = running ?? (_ => Array.Empty<int>()),
                HealthTimeout = TimeSpan.FromSeconds(1),
            };
            if (service != null) env.Service = service;
            if (schtasks != null) env.Schtasks = schtasks;
            var code = await UpdateCommand.RunAsync(o, env, outW, errW, CancellationToken.None);
            return (code, outW.ToString(), errW.ToString());
        }

        private static string? FakeServerVersion(string serverDir)
        {
            var exe = Path.Combine(serverDir, "LacertMCP.Server.exe");
            if (!File.Exists(exe)) return null;
            var text = File.ReadAllText(exe);
            return text.StartsWith("server ", StringComparison.Ordinal) ? text.Substring("server ".Length) : null;
        }

        /// <summary>A release-layout install whose files say which version they are.</summary>
        private string MakeInstall(string version)
        {
            var root = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            WriteTree(root, version);
            return root;
        }

        private string MakeReleaseZip(string version, bool omitServerExe = false, Action<ZipArchive>? extra = null,
            Action<string>? tweak = null)
        {
            var stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            WriteTree(stage, version);
            File.WriteAllText(Path.Combine(stage, "README.md"), "readme " + version);
            // The real zip carries the installer at its root (scripts/release.ps1).
            File.WriteAllText(Path.Combine(stage, "install-http.ps1"), "installer " + version);
            if (omitServerExe) File.Delete(Path.Combine(stage, "server", "LacertMCP.Server.exe"));
            tweak?.Invoke(stage);
            var zip = stage + ".zip";
            ZipFile.CreateFromDirectory(stage, zip);
            if (extra != null)
            {
                using var z = ZipFile.Open(zip, ZipArchiveMode.Update);
                extra(z);
            }
            return zip;
        }

        /// <summary>The release-zip shape: server\ (+ schema tree), cli\, dumper\. Base shards ship as "{}".</summary>
        private static void WriteTree(string root, string version)
        {
            var map = Path.Combine(root, "server", "Schema", "FieldAccessMap");
            Directory.CreateDirectory(Path.Combine(map, "base"));
            Directory.CreateDirectory(Path.Combine(map, "overlays"));
            Directory.CreateDirectory(Path.Combine(root, "cli"));
            Directory.CreateDirectory(Path.Combine(root, "dumper"));
            File.WriteAllText(Path.Combine(root, "server", "LacertMCP.Server.exe"), "server " + version);
            File.WriteAllText(Path.Combine(root, "server", "LacertMCP.Server.exe.config"), "<configuration/>");
            File.WriteAllText(Path.Combine(map, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(map, "base", "individual.json"), "{}");
            File.WriteAllText(Path.Combine(map, "overlays", "ty21.json"), "overlay " + version);
            File.WriteAllText(Path.Combine(root, "cli", "lacerte-mcp.exe"), "cli " + version);
            File.WriteAllText(Path.Combine(root, "dumper", "LacertMCP.FieldAccessDumper.exe"), "dumper " + version);
        }

        private static bool GitAvailable()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                p.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void Git(string cwd, string args)
        {
            var psi = new ProcessStartInfo("git", "-c user.name=t -c user.email=t@example.invalid -c init.defaultBranch=main " + args)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {args} failed: {err.Result}");
        }

        private sealed class FakeService : IServiceControl
        {
            private readonly string _install;

            public FakeService(string install) => _install = install;

            public List<string> Calls { get; } = new List<string>();
            public bool Healthy { get; set; } = true;
            public Action? OnStop { get; set; }
            public IReadOnlyList<int> Foreign { get; set; } = Array.Empty<int>();

            /// <summary>Non-null = Stop refuses like an elevated launcher would.</summary>
            public string? StopDenied { get; set; }

            private string ServerExe
            {
                get
                {
                    var exe = Path.Combine(_install, "server", "LacertMCP.Server.exe");
                    return File.Exists(exe) ? File.ReadAllText(exe) : "(none)";
                }
            }

            public ServiceInfo? Find(string serverDir) => new ServiceInfo(Path.Combine(_install, "LacertMCP.vbs"), 8767);

            public IReadOnlyList<int> ForeignServers(ServiceInfo service, string serverDir) => Foreign;

            public void Stop(ServiceInfo service, string serverDir)
            {
                if (StopDenied != null)
                {
                    Calls.Add("stop-denied");
                    throw new ServiceStopException(StopDenied);
                }
                Calls.Add("stop:" + ServerExe);
                OnStop?.Invoke();
            }

            public void Start(ServiceInfo service) => Calls.Add("start:" + ServerExe);

            public Task<bool> WaitHealthyAsync(ServiceInfo service, TimeSpan timeout, CancellationToken ct)
            {
                Calls.Add("health");
                return Task.FromResult(Healthy);
            }
        }

        private sealed class FakeSource : IReleaseSource
        {
            private ReleaseInfo? _release;
            private string? _zip;

            public int Downloads { get; private set; }
            public string? RequestedTag { get; private set; }
            public Exception? DownloadThrows { get; set; }

            public static FakeSource For(string version, string zip, string? sha256Override = null)
            {
                var asset = new ReleaseAsset($"LacertMCP-v{version}.zip", "https://example.invalid/" + version + ".zip",
                    new FileInfo(zip).Length, sha256Override ?? ReleaseInstaller.Sha256Hex(zip));
                return new FakeSource { _zip = zip, _release = new ReleaseInfo("v" + version, new[] { asset }) };
            }

            public Task<ReleaseInfo> GetReleaseAsync(string repo, string? tag, CancellationToken ct)
            {
                RequestedTag = tag;
                return Task.FromResult(_release ?? throw new UpdateException("no release"));
            }

            public Task DownloadAsync(ReleaseAsset asset, string destinationPath, CancellationToken ct)
            {
                Downloads++;
                if (DownloadThrows != null) throw DownloadThrows;
                File.Copy(_zip!, destinationPath);
                return Task.CompletedTask;
            }
        }
    }
}
