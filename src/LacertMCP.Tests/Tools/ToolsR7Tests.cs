using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Round-7 deep-review regressions for the tool layer.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class ToolsR7Tests
    {
        public ToolsR7Tests()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_15");
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_71");
        }

        private static async Task<LacerteSessionManager> NewSimAsync(string dataPath, int year)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(dataPath, year, 'I');
            return mgr;
        }

        private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

        // R7-16 — multi-word names match across the first/last columns.
        [Theory]
        [InlineData("John Smith")]
        [InlineData("Smith, John")]
        [InlineData("  john   smith ")]
        public async Task R716_ClientFind_FullName_MatchesSplitColumns(string name)
        {
            using var mgr = await NewSimAsync(SeedData.DataPathIData2020, 2020);
            var root = Root(await new ClientsTools(mgr).LacerteClientFind(name));
            Assert.True(root.GetProperty("success").GetBoolean());
            var matches = root.GetProperty("result").GetProperty("matches").EnumerateArray().ToArray();
            Assert.Single(matches);
            Assert.Equal("1001", matches[0].GetProperty("clientNumber").GetString());
        }

        [Fact]
        public async Task R716_ClientFind_FullName_AllTokensRequired()
        {
            using var mgr = await NewSimAsync(SeedData.DataPathIData2020, 2020);
            var root = Root(await new ClientsTools(mgr).LacerteClientFind("John Jones"));
            Assert.Equal(0, root.GetProperty("result").GetProperty("total").GetInt32());
        }

        // R7-18 — argument errors are 9017, not 9203.
        [Fact]
        public async Task R718_ArgumentException_MapsToInvalidArgument()
        {
            using var mgr = await NewSimAsync(SeedData.DataPathIData2024, 2024);
            var blank = Root(await new ClientsTools(mgr).LacerteClientFind("   "));
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, blank.GetProperty("statusCode").GetInt32());
            Assert.Equal("name", blank.GetProperty("context").GetProperty("argument").GetString());

            var negative = Root(await new SubclientsTools(mgr).LacerteSubclientRead("1001", limit: -1));
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, negative.GetProperty("statusCode").GetInt32());
        }

        // R7-18b — a BCL-raised ArgumentException is an in-server bug: 9203.
        [Fact]
        public async Task R718_BclArgumentException_StaysInternalServerError()
        {
            var json = await WrapToolHandler.Run(() =>
            {
                var list = new List<int>();
                return Task.FromResult<object>(list[3]); // ArgumentOutOfRangeException from System
            });
            var root = Root(json);
            Assert.Equal(SyntheticStatusCodes.InternalServerError, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task R718_Cancellation_PropagatesInsteadOfInternalError()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WrapToolHandler.Run(() =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    return Task.FromResult<object>(1);
                }));
        }

        // R7-19 — both write tools report their own op name.
        [Fact]
        public async Task R719_FieldWrite_RecordsToolNameInStats()
        {
            using var mgr = await NewSimAsync(SeedData.DataPathIData2024, 2024);
            await new FieldWriteTools(mgr).LacerteFieldWrite(1, "1001", "C1_15", "x");
            var stats = JsonSerializer.Serialize(StatsRegistry.Snapshot());
            Assert.Contains("\"op\":\"LacerteFieldWrite\"", stats);
            Assert.DoesNotContain("\"op\":\"Run\"", stats);
        }

        // R7-24 — field_access_map lists cataloged fields beyond the old grid.
        [Fact]
        public async Task R724_FieldAccessMap_ListsCatalogedFieldsOutsideOldProbeGrid()
        {
            var root = Root(await new MetadataTools().LacerteFieldAccessMap("I", 1, 2024));
            var fields = root.GetProperty("result").GetProperty("fields");
            Assert.True(fields.TryGetProperty("C1_71", out var access), root.ToString());
            Assert.Equal("ReadWrite", access.GetString());
            Assert.Equal("ReadOnly", fields.GetProperty("C1_0").GetString());
        }

        // R7-34 — every `lacerte_*` token quoted in server source (hints,
        // descriptions, prompts) must be a real tool or prompt. Pins the
        // "lacerte_create_client" class of drift.
        [Fact]
        public void R734_EveryLacerteToolReferenceInServerSource_Resolves()
        {
            var asm = typeof(ClientsTools).Assembly;
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in asm.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)))
            {
                var tool = m.GetCustomAttribute<McpServerToolAttribute>();
                if (tool != null) known.Add(!string.IsNullOrEmpty(tool.Name) ? tool.Name! : ToSnake(m.Name));
                var prompt = m.GetCustomAttribute<McpServerPromptAttribute>();
                if (prompt != null) known.Add(!string.IsNullOrEmpty(prompt.Name) ? prompt.Name! : ToSnake(m.Name));
            }
            Assert.NotEmpty(known);

            var serverDir = Path.Combine(RepoRoot(), "src", "LacertMCP.Server");
            var rx = new Regex(@"(?<![A-Za-z0-9_\-/.])lacerte_[a-z]+(?:_[a-z]+)*\b", RegexOptions.CultureInvariant);
            var unknown = new List<string>();
            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                  && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            {
                foreach (Match m in rx.Matches(File.ReadAllText(file)))
                {
                    if (!known.Contains(m.Value)) unknown.Add(Path.GetFileName(file) + ": " + m.Value);
                }
            }
            Assert.True(unknown.Count == 0, "Unknown tool references:\n" + string.Join("\n", unknown.Distinct()));
        }

        private static string ToSnake(string s)
            => Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LacertMCP.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }
}
