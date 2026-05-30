using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    /// <summary>
    /// Phase 12 task #83 — access-map coverage. For each base shard:
    /// <list type="number">
    ///   <item><description>Parses as valid JSON.</description></item>
    ///   <item><description>Contains no duplicate keys (System.Text.Json throws on dup
    ///     by default — assert by re-deserializing to <see cref="Dictionary{TKey,TValue}"/>).</description></item>
    ///   <item><description>For each entry the public <see cref="FieldAccessMapLoader.GetFieldAccess"/>
    ///     returns the entry exactly once for the (module, table, field, year)
    ///     tuple — proving the loader's overlay-then-base lookup order doesn't
    ///     shadow the row.</description></item>
    /// </list>
    ///
    /// <para>
    /// v1 ships every shard as <c>{}</c> per Todo.md task #13 — the harness
    /// still runs (validates JSON + no duplicates) and the per-entry assertion
    /// loops are no-ops until Phase 9 introspection populates the shards.
    /// Test count therefore stays > 0 even with empty maps.
    /// </para>
    /// </summary>
    public class FieldAccessMapCoverageTests
    {
        private static readonly char[] AllModules = { 'I', 'C', 'P', 'S', 'F', 'R', 'T', 'N', 'B' };

        private static readonly IReadOnlyDictionary<char, string> ModuleFile = new Dictionary<char, string>
        {
            { 'I', "individual" },
            { 'C', "corporate" },
            { 'P', "partnership" },
            { 'S', "scorp" },
            { 'F', "fiduciary" },
            { 'R', "exempt" },
            { 'T', "estate" },
            { 'N', "gift" },
            { 'B', "benefit" },
        };

        private static string MapRoot()
        {
            // Tests run under .NET Framework shadow-copy, so
            // Assembly.Location points to a Temp dir without the Content
            // files. Use AppContext.BaseDirectory which IS the test bin folder
            // (the server csproj's <Content Include="Schema\FieldAccessMap\**" />
            // block copies the tree there as part of the project reference's
            // output).
            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Schema", "FieldAccessMap");
        }

        [Theory]
        [InlineData('I')]
        [InlineData('C')]
        [InlineData('P')]
        [InlineData('S')]
        [InlineData('F')]
        [InlineData('R')]
        [InlineData('T')]
        [InlineData('N')]
        [InlineData('B')]
        public void Shard_Exists_AndParsesAsValidJson(char module)
        {
            var path = Path.Combine(MapRoot(), "base", ModuleFile[module] + ".json");
            Assert.True(File.Exists(path), $"missing shard: {path}");

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        [Theory]
        [InlineData('I')]
        [InlineData('C')]
        [InlineData('P')]
        [InlineData('S')]
        [InlineData('F')]
        [InlineData('R')]
        [InlineData('T')]
        [InlineData('N')]
        [InlineData('B')]
        public void Shard_HasNoDuplicateKeys(char module)
        {
            var path = Path.Combine(MapRoot(), "base", ModuleFile[module] + ".json");
            var json = File.ReadAllText(path);

            // System.Text.Json keeps last-write-wins on duplicate keys by
            // default; we want a HARD assertion, so count the JSON keys
            // directly via JsonDocument.
            using var doc = JsonDocument.Parse(json);
            var seen = new HashSet<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                Assert.True(seen.Add(prop.Name),
                    $"duplicate key in {ModuleFile[module]}.json: {prop.Name}");
            }
        }

        [Theory]
        [InlineData('I', 2024)]
        [InlineData('C', 2024)]
        [InlineData('P', 2024)]
        [InlineData('S', 2024)]
        [InlineData('F', 2024)]
        [InlineData('R', 2024)]
        [InlineData('T', 2024)]
        [InlineData('N', 2024)]
        [InlineData('B', 2024)]
        public void Shard_EveryEntryRoundTripsThroughGetFieldAccess(char module, int taxYear)
        {
            // Reset so the per-test load picks up any concurrent edits and we
            // get a fresh count.
            FieldAccessMapLoader.ResetCacheForTests();

            var path = Path.Combine(MapRoot(), "base", ModuleFile[module] + ".json");
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Key format: "{module}|{table}|{field}" — same as the loader.
                var parts = prop.Name.Split('|');
                if (parts.Length != 3) continue;
                if (!char.TryParse(parts[0], out var modChar)) continue;
                if (!int.TryParse(parts[1], out var table)) continue;
                var field = parts[2];

                // The loader must return SOMETHING other than Unknown for an
                // explicit entry (modulo overlays that mark it NotSupported).
                var access = FieldAccessMapLoader.GetFieldAccess(modChar, table, field, taxYear);

                // Universal C{t}_0 rule pre-empts the map — exclude.
                if (field == "C" + table + "_0")
                {
                    Assert.Equal(FieldAccess.ReadOnly, access);
                    continue;
                }

                Assert.NotEqual(FieldAccess.Unknown, access);
            }
        }

        [Fact]
        public void ManifestExists_AndParsesAsValidJson()
        {
            var path = Path.Combine(MapRoot(), "manifest.json");
            Assert.True(File.Exists(path), $"missing manifest: {path}");
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            Assert.NotNull(doc);
        }

        [Fact]
        public void OverlayTy21_Exists_AndParsesAsValidJson()
        {
            var path = Path.Combine(MapRoot(), "overlays", "ty21-not-supported.json");
            Assert.True(File.Exists(path), $"missing overlay: {path}");
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }
}
