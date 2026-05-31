using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P15-044 — covers the <c>lacerte_reload_schema</c> MCP tool. The tool
    /// is the operator-facing surface for
    /// <see cref="FieldAccessMapLoader.ReloadCaches"/> so the
    /// FieldAccessDumper workflow can take effect without restarting the
    /// server (loader internals are tested separately in
    /// <c>FieldAccessMapLoaderReloadAndShardCountTests</c>; here we lock the
    /// envelope shape + the hot-reload behavior end-to-end through the
    /// wrapped tool handler).
    ///
    /// <para>
    /// Lives in <see cref="AccessMapMutationCollection"/> because these
    /// tests mutate the static caches and the on-disk shard file; running
    /// in parallel with the sibling access-map tests would race.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class SchemaReloadToolsTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        private static void ResetLoaderCache()
        {
            var t = typeof(FieldAccessMapLoader);
            var moduleCacheField = t.GetField("_moduleCache", BindingFlags.NonPublic | BindingFlags.Static);
            var overlayCacheField = t.GetField("_overlayCache", BindingFlags.NonPublic | BindingFlags.Static);
            (moduleCacheField?.GetValue(null) as IDictionary)?.Clear();
            (overlayCacheField?.GetValue(null) as IDictionary)?.Clear();
        }

        [Fact]
        public async Task ReloadSchema_ReturnsSuccessEnvelope_WithStructuredCounts()
        {
            ResetLoaderCache();

            var tool = new SchemaReloadTools();
            var json = await tool.LacerteReloadSchema();
            var root = Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");

            // Contract: every required field exists and is correctly typed.
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Number, result.GetProperty("modulesReloaded").ValueKind);
            Assert.Equal(JsonValueKind.Number, result.GetProperty("overlaysReloaded").ValueKind);
            Assert.Equal(JsonValueKind.String, result.GetProperty("schemaRoot").ValueKind);

            // Nothing was cached pre-call, so the counts must be zero.
            Assert.Equal(0, result.GetProperty("modulesReloaded").GetInt32());
            Assert.Equal(0, result.GetProperty("overlaysReloaded").GetInt32());
        }

        [Fact]
        public async Task ReloadSchema_ReportsEvictedCounts_AfterCachePopulated()
        {
            ResetLoaderCache();

            // Populate both caches: a module (I) and overlay (year 2024).
            FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NOT_PRESENT", 2024);
            // Hit a second module + year to confirm counts >1 are surfaced.
            FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_NOT_PRESENT", 2099);

            var tool = new SchemaReloadTools();
            var json = await tool.LacerteReloadSchema();
            var root = Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            var modules = result.GetProperty("modulesReloaded").GetInt32();
            var overlays = result.GetProperty("overlaysReloaded").GetInt32();
            Assert.True(modules >= 2, $"expected >=2 modules evicted, got {modules}");
            Assert.True(overlays >= 2, $"expected >=2 overlays evicted, got {overlays}");

            // Second call sees zero — we just cleared everything.
            var json2 = await tool.LacerteReloadSchema();
            var root2 = Parse(json2);
            var result2 = root2.GetProperty("result");
            Assert.Equal(0, result2.GetProperty("modulesReloaded").GetInt32());
            Assert.Equal(0, result2.GetProperty("overlaysReloaded").GetInt32());
        }

        [Fact]
        public async Task ReloadSchema_HotReloads_FreshShardOnDisk_WithoutRestart()
        {
            // End-to-end: mirror the dumper workflow through the MCP tool.
            // (1) empty shard -> Unknown
            // (2) rewrite shard with a real entry on disk
            // (3) without reload, cached empty map still wins (proves the bug)
            // (4) call the MCP tool -> reload happens
            // (5) lookup now reflects the on-disk shard.

            var asmDir = Path.GetDirectoryName(typeof(FieldAccessMapLoader).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            var shardPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "base", "individual.json");

            // Preserve original so other tests aren't perturbed.
            var existed = File.Exists(shardPath);
            var original = existed ? File.ReadAllText(shardPath) : null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shardPath)!);
                File.WriteAllText(shardPath, "{}\n");
                ResetLoaderCache();

                Assert.Equal(FieldAccess.Unknown,
                    FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_TOOLRELOAD", 2024));

                File.WriteAllText(shardPath,
                    "{ \"I|1|C1_TOOLRELOAD\": { \"access\": \"ReadWrite\", \"source\": \"tool-test\" } }");

                // Pre-reload — cached empty map still wins.
                Assert.Equal(FieldAccess.Unknown,
                    FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_TOOLRELOAD", 2024));

                // Reload via the MCP tool, not the loader directly.
                var tool = new SchemaReloadTools();
                var json = await tool.LacerteReloadSchema();
                var root = Parse(json);
                Assert.True(root.GetProperty("success").GetBoolean(), json);
                Assert.True(root.GetProperty("result").GetProperty("modulesReloaded").GetInt32() >= 1);

                // Post-reload — lookup reflects the on-disk shard.
                Assert.Equal(FieldAccess.ReadWrite,
                    FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_TOOLRELOAD", 2024));
            }
            finally
            {
                if (existed && original != null)
                {
                    File.WriteAllText(shardPath, original);
                }
                else
                {
                    File.WriteAllText(shardPath, "{}\n");
                }
                ResetLoaderCache();
            }
        }

        [Fact]
        public async Task ReloadSchema_SchemaRoot_IsRootedAndExists()
        {
            var tool = new SchemaReloadTools();
            var json = await tool.LacerteReloadSchema();
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var schemaRoot = root.GetProperty("result").GetProperty("schemaRoot").GetString();
            Assert.False(string.IsNullOrEmpty(schemaRoot));
            Assert.True(Path.IsPathRooted(schemaRoot), $"expected absolute path, got {schemaRoot}");
            Assert.True(Directory.Exists(schemaRoot), $"schema root should exist on disk: {schemaRoot}");
            // The tool's reported root must agree with the loader's view.
            Assert.Equal(FieldAccessMapLoader.GetSchemaRoot(), schemaRoot);
        }
    }
}
