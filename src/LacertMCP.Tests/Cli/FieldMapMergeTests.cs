using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Update;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>Base-shard JSON in the shape the release ships (src/LacertMCP.Server/Schema/FieldAccessMap/base/*.json).</summary>
    internal static class ShardJson
    {
        public static string Row(string key, string access, string source, string? description = "Global field (documented access)",
            string addedAt = "2026-11-15")
            => $"  \"{key}\": {{\n    \"access\": \"{access}\",\n    \"source\": \"{source}\",\n" +
               (description != null ? $"    \"description\": \"{description}\",\n" : string.Empty) +
               $"    \"addedAt\": \"{addedAt}\"\n  }}";

        public static string Shard(params string[] rows) => "{\n" + string.Join(",\n", rows) + "\n}\n";

        /// <summary>Parses exactly as the server's loader does (fails the test if it wouldn't load).</summary>
        public static IReadOnlyDictionary<string, FieldEntry> Load(string path)
        {
            var map = FieldAccessMapLoader.ValidateShardFile(path, out var error);
            Assert.True(map != null, $"{path} does not load: {error}");
            return map!;
        }
    }

    /// <summary>
    /// H1 — the per-entry field-map merge shared by `lacerte-mcp update` and
    /// install-http.ps1 (via `field-access merge`), plus the launcher stop planning
    /// and environment scrubbing in LauncherService.
    /// </summary>
    public sealed class FieldMapMergeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _installed;
        private readonly string _release;

        public FieldMapMergeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "lacerte-mcp-merge-test-" + Guid.NewGuid().ToString("N"));
            _installed = Directory.CreateDirectory(Path.Combine(_root, "installed", "base")).FullName;
            _release = Directory.CreateDirectory(Path.Combine(_root, "release", "base")).FullName;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        private static readonly string ReleaseIndividual = ShardJson.Shard(
            ShardJson.Row("I|1|C1_G1", "ReadOnly", "sdk-pdf"),
            ShardJson.Row("I|1|C1_G12", "ReadOnly", "sdk-pdf", addedAt: "2026-11-20"),   // corrected from ReadWrite
            ShardJson.Row("I|1|C1_G13", "ReadWrite", "sdk-pdf"));

        [Fact]
        public void CuratedFixArrives_DumperAndManualEntriesKept()
        {
            File.WriteAllText(Path.Combine(_installed, "individual.json"), ShardJson.Shard(
                ShardJson.Row("I|1|C1_G1", "ReadOnly", "sdk-pdf"),
                ShardJson.Row("I|1|C1_G12", "ReadWrite", "sdk-pdf"),                         // stale curated row
                ShardJson.Row("I|1|C1_G13", "Hidden", "live-probe", null, "2026-11-18"),     // dumper --overwrite on this PC
                ShardJson.Row("I|1|C1_40", "ReadWrite", "live-probe", null, "2026-11-18"),   // dumper, not in the release
                ShardJson.Row("I|3|C3_7", "ReadOnly", "manual", "set by hand")));            // field-access set
            File.WriteAllText(Path.Combine(_release, "individual.json"), ReleaseIndividual);

            var result = FieldMapMerge.Merge(_installed, _release);

            var merged = ShardJson.Load(Path.Combine(_release, "individual.json"));
            Assert.Equal(5, merged.Count);
            Assert.Equal(FieldAccess.ReadOnly, merged["I|1|C1_G12"].Access);
            Assert.Equal("2026-11-20", merged["I|1|C1_G12"].AddedAt);
            Assert.Equal(FieldAccess.Hidden, merged["I|1|C1_G13"].Access);   // this PC's probe beats the curated row
            Assert.Equal("live-probe", merged["I|1|C1_G13"].Source);
            Assert.Equal(FieldAccess.ReadWrite, merged["I|1|C1_40"].Access);
            Assert.Equal("set by hand", merged["I|3|C3_7"].Description);

            var stats = Assert.Single(result.Shards);
            Assert.Equal(3, stats.KeptFromThisPc);
            Assert.Equal(1, stats.OverridingRelease);
            Assert.Equal(1, stats.UpdatedFromRelease);
            Assert.Contains(result.Describe(), l => l.StartsWith("Kept 3 field-access entries from this PC's map (individual.json: 3); 1 of them override", StringComparison.Ordinal));

            // Sorted ordinally and in the shipped camelCase shape, like `field-access set` keeps diffs stable.
            var text = File.ReadAllText(Path.Combine(_release, "individual.json"));
            var keys = merged.Keys.ToList();
            Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys.Select(k => k));
            Assert.True(text.IndexOf("\"I|1|C1_40\"", StringComparison.Ordinal) < text.IndexOf("\"I|1|C1_G1\"", StringComparison.Ordinal));
            Assert.Contains("\"access\": \"ReadOnly\"", text);
            Assert.DoesNotContain("\"Access\"", text);
            Assert.DoesNotContain("dataType", text); // nulls omitted
        }

        [Fact]
        public void InstalledIsAllCurated_ReleaseShardTakenByteForByte()
        {
            // The common case: an install of an older release, never touched on this PC.
            File.WriteAllText(Path.Combine(_installed, "individual.json"), ShardJson.Shard(
                ShardJson.Row("I|1|C1_G1", "ReadOnly", "sdk-pdf"),
                ShardJson.Row("I|1|C1_G12", "ReadWrite", "sdk-pdf"),
                ShardJson.Row("I|1|C1_G13", "ReadWrite", "spec")));
            File.WriteAllText(Path.Combine(_release, "individual.json"), ReleaseIndividual);

            var result = FieldMapMerge.Merge(_installed, _release);

            Assert.Equal(ReleaseIndividual, File.ReadAllText(Path.Combine(_release, "individual.json")));
            Assert.Equal(0, result.KeptFromThisPc);
            Assert.Equal(2, result.UpdatedFromRelease); // C1_G12 access + C1_G13 source
        }

        [Theory]
        [InlineData("{}\n")]   // what v0.1.0 shipped (3 bytes — the old ">2 bytes" rule kept it forever)
        [InlineData("{}")]
        [InlineData("")]
        public void EmptyInstalledShard_ReleaseTakenUnchanged(string installed)
        {
            File.WriteAllText(Path.Combine(_installed, "individual.json"), installed);
            File.WriteAllText(Path.Combine(_release, "individual.json"), ReleaseIndividual);

            var result = FieldMapMerge.Merge(_installed, _release);

            Assert.Empty(result.Shards);
            Assert.Empty(result.Describe());
            Assert.Equal(ReleaseIndividual, File.ReadAllText(Path.Combine(_release, "individual.json")));
        }

        [Fact]
        public void ShardAbsentFromRelease_EntriesCarriedIntoNewFile()
        {
            File.WriteAllText(Path.Combine(_installed, "scorp.json"), ShardJson.Shard(
                ShardJson.Row("S|1|C1_9", "ReadWrite", "live-probe", null)));

            FieldMapMerge.Merge(_installed, _release);

            var merged = ShardJson.Load(Path.Combine(_release, "scorp.json"));
            Assert.Equal(FieldAccess.ReadWrite, Assert.Single(merged).Value.Access);
        }

        [Fact]
        public void MalformedInstalledShard_ReleaseTaken_AndReported()
        {
            File.WriteAllText(Path.Combine(_installed, "individual.json"), "{ not json");
            File.WriteAllText(Path.Combine(_release, "individual.json"), ReleaseIndividual);

            var result = FieldMapMerge.Merge(_installed, _release);

            Assert.True(Assert.Single(result.Shards).InstalledUnreadable);
            Assert.Contains(result.Describe(), l => l.Contains("was not valid JSON"));
            Assert.Equal(ReleaseIndividual, File.ReadAllText(Path.Combine(_release, "individual.json")));
        }

        [Fact]
        public void MalformedReleaseShard_Throws_WritesNothing()
        {
            File.WriteAllText(Path.Combine(_installed, "corporate.json"), ShardJson.Shard(ShardJson.Row("C|1|C1_9", "ReadWrite", "manual")));
            File.WriteAllText(Path.Combine(_installed, "individual.json"), ShardJson.Shard(ShardJson.Row("I|1|C1_9", "ReadWrite", "manual")));
            File.WriteAllText(Path.Combine(_release, "individual.json"), "{ broken");

            Assert.Throws<UpdateException>(() => FieldMapMerge.Merge(_installed, _release));
            Assert.False(File.Exists(Path.Combine(_release, "corporate.json")));
        }

        // install-http.ps1 runs the same rule through this hidden verb.
        [Fact]
        public void FieldAccessMergeVerb_UsesSameRule()
        {
            File.WriteAllText(Path.Combine(_installed, "individual.json"), ShardJson.Shard(
                ShardJson.Row("I|1|C1_G12", "ReadWrite", "sdk-pdf"),
                ShardJson.Row("I|1|C1_40", "ReadWrite", "live-probe", null)));
            File.WriteAllText(Path.Combine(_release, "individual.json"), ReleaseIndividual);
            var outW = new StringWriter();
            var errW = new StringWriter();

            Assert.Equal(0, FieldAccessCommand.RunMerge(_installed, _release, outW, errW));

            var merged = ShardJson.Load(Path.Combine(_release, "individual.json"));
            Assert.Equal(FieldAccess.ReadOnly, merged["I|1|C1_G12"].Access);
            Assert.True(merged.ContainsKey("I|1|C1_40"));
            Assert.Contains("Kept 1 field-access entry", outW.ToString());

            Assert.Equal(1, FieldAccessCommand.RunMerge(_installed, Path.Combine(_root, "nope"), outW, errW));
        }

        // ------------------------------------------------------------ launcher stop planning (L2, L3, M2, M3)

        private const string Launcher = @"C:\Users\op\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\LacertMCP.vbs";
        private const string ServerDir = @"C:\Users\op\AppData\Local\LacertMCP\server";

        private static ProcInfo Wscript(int pid, string? cmd, bool canTerminate = true, int session = 1)
            => new ProcInfo { Pid = pid, ParentPid = 4, SessionId = session, Name = "wscript.exe", CommandLine = cmd, CanTerminate = canTerminate, ParentAlive = true };

        private static ProcInfo Server(int pid, int parent, string? path = ServerDir + @"\LacertMCP.Server.exe", bool parentAlive = true,
            bool canTerminate = true, int session = 1)
            => new ProcInfo { Pid = pid, ParentPid = parent, SessionId = session, Name = "LacertMCP.Server.exe", ImagePath = path, CanTerminate = canTerminate, ParentAlive = parentAlive };

        [Fact]
        public void Plan_StopsLauncherAndItsChild_LeavesStdioServer()
        {
            var plan = LauncherService.Plan(new[]
            {
                Wscript(10, "\"C:\\Windows\\System32\\WScript.exe\" \"" + Launcher + "\""),
                Wscript(11, "\"C:\\Windows\\System32\\WScript.exe\" \"D:\\other\\LacertMCP.vbs\""), // L3: same file name, other path
                Server(20, parent: 10),
                Server(21, parent: 777),                                   // Claude Desktop's stdio server (L2)
                Server(22, parent: 999, parentAlive: false),               // orphaned by a killed launcher
                Server(23, parent: 10, path: @"D:\elsewhere\server\LacertMCP.Server.exe"),
                Server(24, parent: 10, session: 2),                        // another signed-in user (M3)
            }, Launcher, ServerDir, sessionId: 1);

            Assert.Equal(new[] { 10 }, plan.Launchers);
            Assert.Equal(new[] { 20, 22 }, plan.Servers);
            Assert.Equal(new[] { 21 }, plan.Foreign);
            Assert.Empty(plan.Blockers);
        }

        [Fact]
        public void Plan_ElevatedLauncherOrServer_Blocks()
        {
            var unreadableWscript = LauncherService.Plan(new[] { Wscript(10, cmd: null, canTerminate: false) }, Launcher, ServerDir, 1);
            Assert.Single(unreadableWscript.Blockers);

            var elevatedServer = LauncherService.Plan(new[]
            {
                Wscript(10, "wscript \"" + Launcher + "\""),
                Server(20, parent: 10, canTerminate: false),
            }, Launcher, ServerDir, 1);
            Assert.Contains(elevatedServer.Blockers, b => b.Contains("PID 20"));
        }

        [Fact]
        public void Plan_UnreadableServerPath_NotOurs_Warned()
        {
            var plan = LauncherService.Plan(new[] { Server(20, parent: 777, path: null) }, Launcher, ServerDir, 1);
            Assert.Empty(plan.Servers);
            Assert.Empty(plan.Foreign);
            Assert.Single(plan.Warnings);
        }

        // M3: the real (read-only) process inspection the plan is built from.
        [Fact]
        public void ProcessInspector_ReadsOwnProcess()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) return;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            Assert.Equal(self.MainModule!.FileName, ProcessInspector.ImagePath(self.Id), StringComparer.OrdinalIgnoreCase);
            Assert.True(ProcessInspector.CanTerminate(self.Id));
            Assert.Equal(self.SessionId, ProcessInspector.CurrentSessionId);
            Assert.NotNull(ProcessInspector.Snapshot()); // WMI query shape is valid
            Assert.Null(ProcessInspector.ImagePath(-1));
        }

        // M5: nothing from the update's console leaks into the always-on server.
        [Fact]
        public void ScrubEnvironment_RemovesLacerteVariablesAndTokens()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LACERTE_SIMULATION"] = "1",
                ["lacerte_data_root"] = "D:\\x",
                ["GITHUB_TOKEN"] = "t",
                ["GH_TOKEN"] = "t",
                ["PATH"] = "C:\\Windows",
                ["USERPROFILE"] = "C:\\Users\\op",
            };
            LauncherService.ScrubEnvironment(env);
            Assert.Equal(new[] { "PATH", "USERPROFILE" }, env.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
