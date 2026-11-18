using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    /// <summary>
    /// FP-1a — the SDK range-rule fallback in <see cref="FieldAccessMapLoader"/>,
    /// gated by the field dictionary. Literal entries and overlays always win;
    /// with the shipped (empty) dictionaries nothing changes.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldAccessRangeRuleFallbackTests : IDisposable
    {
        public FieldAccessRangeRuleFallbackTests()
        {
            FieldAccessMapLoader.ReloadCaches();
        }

        public void Dispose()
        {
            FieldDictionaryLoader.RootOverrideForTests.Value = null;
            FieldAccessMapLoader.ReloadCaches();
        }

        [Theory]
        [InlineData(1, "C1_1", FieldAccess.ReadWrite)]
        [InlineData(1, "C1_999", FieldAccess.ReadWrite)]
        [InlineData(3, "C3_1", FieldAccess.ReadWrite)]
        [InlineData(2, "C2_1", FieldAccess.ReadOnly)]
        [InlineData(2, "C2_2", FieldAccess.ReadWrite)]
        [InlineData(4, "C4_1", FieldAccess.ReadOnly)]
        [InlineData(4, "c4_7", FieldAccess.ReadWrite)]
        [InlineData(5, "C5_3", FieldAccess.ReadOnly)]
        [InlineData(6, "C6_3", FieldAccess.ReadOnly)]
        [InlineData(7, "C7_3", FieldAccess.ReadOnly)]
        [InlineData(1, "C1_G1", FieldAccess.Hidden)]
        [InlineData(1, "C1_G77", FieldAccess.Hidden)]
        [InlineData(2, "C2_G2", FieldAccess.Hidden)]
        [InlineData(3, "C3_G2", FieldAccess.Hidden)]
        [InlineData(4, "C4_G9", FieldAccess.Hidden)]
        [InlineData(6, "C6_G1", FieldAccess.Hidden)]
        [InlineData(7, "C7_G4", FieldAccess.Hidden)]
        [InlineData(5, "C5_G1", FieldAccess.ReadWrite)]
        [InlineData(5, "C5_G70", FieldAccess.ReadWrite)]
        public void SdkRangeRuleAccess_FollowsTheManual(int table, string field, FieldAccess expected)
        {
            Assert.Equal(expected, FieldAccessMapLoader.SdkRangeRuleAccess(table, field));
        }

        [Theory]
        [InlineData(1, "C1_0")]   // client number: universal rule, not a range rule
        [InlineData(2, "C2_G1")]  // structural columns ship as literal entries
        [InlineData(3, "C3_G1")]
        [InlineData(4, "C4_G1")]
        [InlineData(1, "C1_G0")]
        [InlineData(1, "C2_5")]   // wrong table
        [InlineData(1, "X1_5")]
        [InlineData(1, "C1_")]
        [InlineData(1, "")]
        [InlineData(8, "C8_1")]
        public void SdkRangeRuleAccess_NoRule_ReturnsNull(int table, string field)
        {
            Assert.Null(FieldAccessMapLoader.SdkRangeRuleAccess(table, field));
        }

        /// <summary>
        /// The FP-1a safety proof: with the shipped dictionaries (all empty) every
        /// cataloged field — and a wide grid of uncataloged ones — resolves exactly
        /// as the pre-FP-1a lookup (universal rule → exact-year overlay → base →
        /// Unknown) computed from the raw shard files.
        /// </summary>
        [Fact]
        public void ShippedEmptyDictionaries_EveryFieldResolvesExactlyAsBefore()
        {
            foreach (var m in Modules.All) Assert.Empty(FieldDictionaryLoader.GetEntries(m.Char));

            var root = FieldAccessMapLoader.GetSchemaRoot();
            var baseMaps = new Dictionary<char, IReadOnlyDictionary<string, FieldEntry>>();
            foreach (var m in Modules.All)
            {
                var path = Path.Combine(root, "base", m.FileBasename + ".json");
                baseMaps[m.Char] = File.Exists(path)
                    ? FieldAccessMapLoader.ValidateShardFile(path, out var err) ?? throw new InvalidOperationException(path + ": " + err)
                    : new Dictionary<string, FieldEntry>();
            }
            var years = new[] { 2018, 2019, 2020, 2021, 2022, 2023, 2024, 2025, 2026 };
            var overlays = new Dictionary<int, IReadOnlyDictionary<string, OverlayEntry>>();
            foreach (var y in years)
            {
                var path = Path.Combine(root, "overlays", "ty" + (y % 100).ToString("D2") + "-not-supported.json");
                overlays[y] = File.Exists(path)
                    ? (FieldAccessMapLoader.ValidateOverlayFile(path, out var err) ?? throw new InvalidOperationException(path + ": " + err)).Overrides
                    : new Dictionary<string, OverlayEntry>();
            }

            FieldAccess Legacy(char module, int table, string field, int year)
            {
                if (string.Equals(field, "C" + table + "_0", StringComparison.OrdinalIgnoreCase)) return FieldAccess.ReadOnly;
                var key = module + "|" + table + "|" + field.ToUpperInvariant();
                if (overlays[year].TryGetValue(key, out var o)) return o.Access;
                if (baseMaps[module].TryGetValue(key, out var b)) return b.Access;
                return FieldAccess.Unknown;
            }

            // Every key any shard or overlay names, plus a grid of uncataloged names.
            var probes = new HashSet<(char, int, string)>();
            foreach (var kv in baseMaps)
                foreach (var k in kv.Value.Keys) probes.Add(ParseKey(k));
            foreach (var o in overlays.Values)
                foreach (var k in o.Keys) probes.Add(ParseKey(k));
            foreach (var m in Modules.All)
            {
                for (var t = 1; t <= 7; t++)
                {
                    for (var n = 0; n <= 120; n++) probes.Add((m.Char, t, "C" + t + "_" + n));
                    for (var n = 0; n <= 90; n++) probes.Add((m.Char, t, "C" + t + "_G" + n));
                }
            }
            Assert.True(probes.Count > 10000);

            var mismatches = new List<string>();
            foreach (var (module, table, field) in probes)
            {
                foreach (var y in years)
                {
                    var expected = Legacy(module, table, field, y);
                    var resolution = FieldAccessMapLoader.ResolveFieldAccess(module, table, field, y);
                    if (resolution.Access != expected || resolution.Source == FieldAccessMapLoader.SourceSdkRangeRule)
                    {
                        mismatches.Add($"{module}|{table}|{field} TY{y}: expected {expected}, got {resolution.Access} ({resolution.Source})");
                    }
                }
            }
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
        }

        private static (char, int, string) ParseKey(string key)
        {
            var parts = key.Split('|');
            return (parts[0][0], int.Parse(parts[1]), parts[2]);
        }

        [Fact]
        public void Fallback_GrantsAccess_OnlyForDictionaryListedFields()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('C',
                TempFieldDictionary.Field("1120.test_one", 1, "C1_9876"),
                TempFieldDictionary.Field("1120.state_one", 3, "C3_9876"),
                TempFieldDictionary.Field("1120.sub_one", 2, "C2_9876"),
                TempFieldDictionary.Field("1120.global_one", 1, "C1_G9876"));

            var r = FieldAccessMapLoader.ResolveFieldAccess('C', 1, "C1_9876", 2024);
            Assert.Equal(FieldAccess.ReadWrite, r.Access);
            Assert.Equal(FieldAccessMapLoader.SourceSdkRangeRule, r.Source);
            Assert.Equal("1120.test_one", r.DictionaryId);
            Assert.Equal(FieldAccess.ReadWrite, FieldAccessMapLoader.GetFieldAccess('C', 1, "c1_9876", 2024));
            Assert.Equal(FieldAccess.ReadWrite, FieldAccessMapLoader.GetFieldAccess('C', 3, "C3_9876", 2024));
            Assert.Equal(FieldAccess.ReadWrite, FieldAccessMapLoader.GetFieldAccess('C', 2, "C2_9876", 2024));
            Assert.Equal(FieldAccess.Hidden, FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_G9876", 2024));
            FieldAccessMapLoader.AssertWritable('C', 1, "C1_9876", 2024); // does not throw

            // Unlisted neighbours, another table, and other modules stay fail-closed.
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_9877", 2024));
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('C', 3, "C3_9877", 2024));
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('S', 1, "C1_9876", 2024));
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_9876", 2024));
            var ex = Assert.Throws<LacerteSDKError>(() => FieldAccessMapLoader.AssertWritable('C', 1, "C1_9877", 2024));
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, ex.StatusCode);
            Assert.False(ex.Context.ContainsKey("accessSource"));
        }

        [Fact]
        public void Fallback_RespectsEntryTaxYears()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('P', TempFieldDictionary.Field("1065.only_2024", 1, "C1_9876", 2024));
            Assert.Equal(FieldAccess.ReadWrite, FieldAccessMapLoader.GetFieldAccess('P', 1, "C1_9876", 2024));
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('P', 1, "C1_9876", 2023));
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('P', 1, "C1_9876", 2025));
        }

        [Fact]
        public void LiteralEntries_AlwaysWinOverTheFallback()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('I',
                // Base ReadWrite literal; the range rule alone would say Hidden.
                TempFieldDictionary.Field("1040.g4", 1, "C1_G4"),
                // Base ReadOnly literal; the range rule alone would say Hidden.
                TempFieldDictionary.Field("1040.g1", 1, "C1_G1"),
                // Base ReadOnly literal for the subclient number.
                TempFieldDictionary.Field("1040.sub", 2, "C2_1"),
                // TY21 overlay NotSupported; no base entry.
                TempFieldDictionary.Field("1040.c1_99", 1, "C1_99"));

            var g4 = FieldAccessMapLoader.ResolveFieldAccess('I', 1, "C1_G4", 2024);
            Assert.Equal(FieldAccess.ReadWrite, g4.Access);
            Assert.Equal(FieldAccessMapLoader.SourceBase, g4.Source);

            var g1 = FieldAccessMapLoader.ResolveFieldAccess('I', 1, "C1_G1", 2024);
            Assert.Equal(FieldAccess.ReadOnly, g1.Access);
            Assert.Equal(FieldAccessMapLoader.SourceBase, g1.Source);

            Assert.Equal(FieldAccessMapLoader.SourceBase, FieldAccessMapLoader.ResolveFieldAccess('I', 2, "C2_1", 2024).Source);

            // The exact-year overlay still blocks a dictionary-listed field...
            var ty21 = FieldAccessMapLoader.ResolveFieldAccess('I', 1, "C1_99", 2021);
            Assert.Equal(FieldAccess.NotSupported, ty21.Access);
            Assert.Equal(FieldAccessMapLoader.SourceOverlay, ty21.Source);
            var ex = Assert.Throws<LacerteSDKError>(() => FieldAccessMapLoader.AssertWritable('I', 1, "C1_99", 2021, force: true));
            Assert.Equal(StatusCodes.FieldNotSupported, ex.StatusCode);
            Assert.False(ex.Context.ContainsKey("accessSource"));

            // ...and only a year without a literal entry falls to the range rule.
            var ty24 = FieldAccessMapLoader.ResolveFieldAccess('I', 1, "C1_99", 2024);
            Assert.Equal(FieldAccess.ReadWrite, ty24.Access);
            Assert.Equal(FieldAccessMapLoader.SourceSdkRangeRule, ty24.Source);

            // The universal client-number rule is untouched.
            Assert.Equal(FieldAccessMapLoader.SourceUniversalRule, FieldAccessMapLoader.ResolveFieldAccess('I', 1, "C1_0", 2024).Source);
        }

        [Fact]
        public void AssertWritable_RangeRuleRefusal_ReportsProvenance()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('C',
                TempFieldDictionary.Field("1120.hidden_global", 1, "C1_G9876"),
                TempFieldDictionary.Field("1120.sub_number", 4, "C4_9876"));

            var ex = Assert.Throws<LacerteSDKError>(() => FieldAccessMapLoader.AssertWritable('C', 1, "C1_G9876", 2024, force: true));
            Assert.Equal(StatusCodes.NotExposed, ex.StatusCode);
            Assert.Equal(FieldAccessMapLoader.SourceSdkRangeRule, ex.Context["accessSource"]);
            Assert.Equal("1120.hidden_global", ex.Context["dictionaryId"]);

            FieldAccessMapLoader.AssertWritable('C', 4, "C4_9876", 2024);
        }

        [Fact]
        public void CatalogedFields_IncludeDictionaryFields_OnlyForTheirTableAndYear()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('C', TempFieldDictionary.Field("1120.test_one", 1, "C1_9876", 2024));
            Assert.Contains("C1_9876", FieldAccessMapLoader.GetCatalogedFields('C', 1, 2024));
            Assert.DoesNotContain("C1_9876", FieldAccessMapLoader.GetCatalogedFields('C', 1, 2023));
            Assert.DoesNotContain("C1_9876", FieldAccessMapLoader.GetCatalogedFields('C', 2, 2024));
            Assert.DoesNotContain("C1_9876", FieldAccessMapLoader.GetCatalogedFields('S', 1, 2024));
        }

        [Fact]
        public void InvalidDictionary_FailsClosed()
        {
            using var dict = new TempFieldDictionary();
            // One bad entry (unknown dataType) poisons the whole file -> no fallback.
            dict.WriteRaw('C',
                "{\"id\":\"ok\",\"concept\":\"c\",\"form\":\"1120\",\"kind\":\"field\",\"table\":1,\"field\":\"C1_9876\",\"needsSubclient\":false,\"needsState\":false,\"dataType\":\"amount\",\"verifiedOn\":\"x\",\"source\":\"manual\"}",
                "{\"id\":\"bad\",\"concept\":\"c\",\"form\":\"1120\",\"kind\":\"field\",\"table\":1,\"field\":\"C1_9877\",\"needsSubclient\":false,\"needsState\":false,\"dataType\":\"money\",\"verifiedOn\":\"x\",\"source\":\"manual\"}");
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_9876", 2024));
            Assert.NotNull(FieldDictionaryLoader.Load('C').Error);
        }

        [Fact]
        public void ReloadCaches_PicksUpNewDictionaryEntries()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('C');
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_9876", 2024));

            // Write without the helper's reload: the cached (empty) dictionary still answers.
            File.WriteAllText(FieldDictionaryLoader.FilePathFor('C', dict.Root)!,
                FieldDictionaryLoader.Serialize('C', new[] { TempFieldDictionary.Field("1120.late", 1, "C1_9876") }));
            Assert.Equal(FieldAccess.Unknown, FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_9876", 2024));

            // lacerte_reload_schema -> FieldAccessMapLoader.ReloadCaches also reloads the dictionary.
            FieldAccessMapLoader.ReloadCaches();
            Assert.Equal(FieldAccess.ReadWrite, FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_9876", 2024));
        }
    }
}
