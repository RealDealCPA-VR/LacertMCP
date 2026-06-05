using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    /// <summary>
    /// Coverage for the 3 AssertWritable branches the original
    /// FieldAccessMapLoaderTests file did not exercise (Hidden, NotExposed,
    /// NotSupported), the overlay-wins-over-base precedence rule, the
    /// malformed-overlay degrade-to-empty behavior, the case-insensitive
    /// universal C{t}_0 rule, and an allocation-free fast-path smoke check
    /// for the universal rule.
    ///
    /// <para>
    /// Lives in <see cref="AccessMapMutationCollection"/> because every test
    /// here writes a real shard file or seeds the loader's static caches —
    /// running in parallel with other access-map tests would race.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class FieldAccessMapLoaderAdditionalTests : IDisposable
    {
        private readonly string _shardPath;
        private readonly string? _originalShard;
        private readonly bool _shardExisted;

        private readonly string _overlayPath;
        private readonly string? _originalOverlay;
        private readonly bool _overlayExisted;
        private readonly List<string> _extraOverlayPaths = new List<string>();

        public FieldAccessMapLoaderAdditionalTests()
        {
            var asmDir = Path.GetDirectoryName(typeof(FieldAccessMapLoader).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();

            // Always use Individual / Table 1 for these tests (matches the
            // sibling AccessMapTestFixture's pattern and keeps key strings
            // consistent across tests).
            _shardPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "base", "individual.json");
            // Use TY2099 for overlay tests — out of any realistic build range
            // so we don't touch the shipped TY21 overlay or any future real
            // overlay.
            _overlayPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "overlays", "ty99-not-supported.json");

            if (File.Exists(_shardPath))
            {
                _shardExisted = true;
                _originalShard = File.ReadAllText(_shardPath);
            }
            if (File.Exists(_overlayPath))
            {
                _overlayExisted = true;
                _originalOverlay = File.ReadAllText(_overlayPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_shardPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(_overlayPath)!);

            ResetLoaderCache();
        }

        public void Dispose()
        {
            // Restore the original individual.json shard, or write {} if it
            // didn't exist (matches the v1 shipped default).
            if (_shardExisted && _originalShard != null)
            {
                File.WriteAllText(_shardPath, _originalShard);
            }
            else
            {
                File.WriteAllText(_shardPath, "{}\n");
            }

            // Restore the overlay; if it didn't exist, delete it so we leave
            // no synthetic shard behind.
            if (_overlayExisted && _originalOverlay != null)
            {
                File.WriteAllText(_overlayPath, _originalOverlay);
            }
            else if (File.Exists(_overlayPath))
            {
                File.Delete(_overlayPath);
            }

            // Clean up any other overlay paths the test wrote (e.g. ty97).
            foreach (var path in _extraOverlayPaths)
            {
                if (File.Exists(path)) File.Delete(path);
            }

            ResetLoaderCache();
        }

        // ---- P14-024: missing AssertWritable branches ----

        [Fact]
        public void AssertWritable_Hidden_Throws_NotExposed_Status7()
        {
            // Seed a Hidden entry — the SDK exposes the field for read but
            // refuses writes; AssertWritable should throw NotExposed (7).
            WriteShard("{ \"I|1|C1_HIDDEN\": { \"access\": \"Hidden\", \"source\": \"test\" } }");

            var ex = Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, "C1_HIDDEN", 2024));
            Assert.Equal(StatusCodes.NotExposed, ex.StatusCode);
            Assert.Equal("NotExposed", ex.StatusName);
            // Status 7 per AssertWritable's documented mapping.
            Assert.Equal(7, (int)ex.StatusCode);
        }

        [Fact]
        public void AssertWritable_NotExposed_Throws_NotExposed_Status7()
        {
            WriteShard("{ \"I|1|C1_NOTEXPOSED\": { \"access\": \"NotExposed\", \"source\": \"test\" } }");

            var ex = Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, "C1_NOTEXPOSED", 2024));
            Assert.Equal(StatusCodes.NotExposed, ex.StatusCode);
            Assert.Equal(7, (int)ex.StatusCode);
        }

        [Fact]
        public void AssertWritable_NotSupported_Throws_FieldNotSupported_Status26()
        {
            // NotSupported is typically delivered via the overlay (TY{n}
            // deprecation list), but the base map shape accepts it too —
            // seeding it in the base lets us assert the switch-case in
            // isolation from overlay-precedence.
            WriteShard("{ \"I|1|C1_DROPPED\": { \"access\": \"NotSupported\", \"source\": \"test\" } }");

            var ex = Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, "C1_DROPPED", 2024));
            Assert.Equal(StatusCodes.FieldNotSupported, ex.StatusCode);
            Assert.Equal("FieldNotSupported", ex.StatusName);
            Assert.Equal(26, (int)ex.StatusCode);
        }

        // ---- P14-024: overlay-wins-over-base precedence ----

        [Fact]
        public void GetFieldAccess_Overlay_Wins_Over_Base()
        {
            // Base says ReadWrite; overlay for TY2099 says NotSupported.
            // Lookup against TY2099 must return NotSupported (overlay wins).
            WriteShard("{ \"I|1|C1_SHADOWED\": { \"access\": \"ReadWrite\", \"source\": \"test\" } }");
            WriteOverlay(2099, "{ \"taxYear\": 2099, \"overrides\": { \"I|1|C1_SHADOWED\": { \"access\": \"NotSupported\", \"source\": \"test\" } } }");

            Assert.Equal(FieldAccess.NotSupported,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_SHADOWED", 2099));
        }

        // ---- P14-032: per-year overlay semantic (full-replace, not cumulative) ----

        [Fact]
        public void Overlay_PerYear_Semantic_Does_Not_Inherit_To_Later_Year()
        {
            // Per DECISIONS.md 2026-05-30 ("Per-year access-map overlay
            // semantic"), overlay files are scoped to the exact year named
            // in the filename. A TY99 overlay should NOT be applied when
            // looking up the same field in TY97 — and we should not have
            // ANY other overlay loaded for adjacent years.
            WriteShard("{ \"I|1|C1_DROPPED99\": { \"access\": \"ReadWrite\", \"source\": \"test\" } }");
            WriteOverlay(2099, "{ \"taxYear\": 2099, \"overrides\": { \"I|1|C1_DROPPED99\": { \"access\": \"NotSupported\", \"source\": \"test\" } } }");

            // TY99 -> overlay fires -> NotSupported.
            Assert.Equal(FieldAccess.NotSupported,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_DROPPED99", 2099));

            // TY97 has no overlay file -> falls through to base ReadWrite.
            // (Confirms the per-year semantic — the TY99 entry does not
            // propagate downward into TY97.)
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_DROPPED99", 2097));
        }

        // ---- P14-024: malformed overlay degrades gracefully (does not poison cache) ----

        [Fact]
        public void Malformed_Overlay_Degrades_To_Empty_And_Does_Not_Poison_Cache()
        {
            WriteShard("{ \"I|1|C1_BASE\": { \"access\": \"ReadWrite\", \"source\": \"test\" } }");
            // Garbage JSON — without the try/catch around the deserialize,
            // every call would re-throw (because the cache entry never gets
            // stored) and effectively gate every write for the lifetime of
            // the process.
            WriteOverlayRaw(2099, "{ this is not json at all");

            // First call: should NOT throw, base map fires.
            var a = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_BASE", 2099);
            Assert.Equal(FieldAccess.ReadWrite, a);

            // Second call: still no throw, still ReadWrite — proves the
            // sentinel was cached (not re-parsed-and-re-thrown).
            var b = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_BASE", 2099);
            Assert.Equal(FieldAccess.ReadWrite, b);

            // A non-cataloged field on the same year should still resolve
            // to Unknown (not throw) — proves the loader didn't end up in a
            // half-loaded state.
            var c = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NOPE", 2099);
            Assert.Equal(FieldAccess.Unknown, c);
        }

        // ---- P14-029: universal C{t}_0 rule is case-insensitive ----

        [Fact]
        public void GetFieldAccess_LowercaseClientNumber_Returns_ReadOnly()
        {
            // No shard needed — universal rule fires before any lookup.
            Assert.Equal(FieldAccess.ReadOnly,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "c1_0", 2024));
        }

        [Theory]
        [InlineData("C1_0", false)]
        [InlineData("c1_0", false)]
        [InlineData("C1_0", true)]
        [InlineData("c1_0", true)]
        public void AssertWritable_ClientNumber_AnyCase_Force_Still_Throws_ExposedForRead(string field, bool force)
        {
            // The universal rule is unconditional — force:true ONLY overrides
            // Unknown, never ReadOnly, regardless of field-name casing.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, field, 2024, force: force));
            Assert.Equal(StatusCodes.ExposedForRead, ex.StatusCode);
        }

        [Theory]
        [InlineData('I', 1, "c1_0")]
        [InlineData('I', 1, "C1_0")]
        [InlineData('I', 2, "c2_0")]
        [InlineData('I', 7, "C7_0")]
        [InlineData('C', 1, "c1_0")]
        [InlineData('P', 5, "c5_0")]
        public void GetFieldAccess_UniversalRule_CaseInsensitive_AcrossModulesAndTables(
            char module, int table, string field)
        {
            Assert.Equal(FieldAccess.ReadOnly,
                FieldAccessMapLoader.GetFieldAccess(module, table, field, 2024));
        }

        [Fact]
        public void GetFieldAccess_BaseLookup_IsCaseInsensitive_On_FieldName()
        {
            // Base shards are authored uppercase. A lowercased payload field
            // name must still resolve through the base map (loader normalizes
            // to uppercase before keying).
            WriteShard("{ \"I|1|C1_G15\": { \"access\": \"ReadWrite\", \"source\": \"test\" } }");

            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_G15", 2024));
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "c1_g15", 2024));
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_g15", 2024));
        }

        // ---- P14-063: allocation-free universal rule fast path ----

        [Fact]
        public void UniversalRule_FastPath_Resolves_Without_Loading_Shards()
        {
            // Sanity check the perf intent: the universal-rule fast path
            // must not require loading the module shard to resolve a
            // C{table}_0 hit. We clear the cache, call GetFieldAccess for
            // a client-number field, and assert the module cache is still
            // empty afterwards (no LoadModule call happened).
            ResetLoaderCache();

            Assert.Equal(FieldAccess.ReadOnly,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_0", 2024));

            var modules = GetSnapshotModulesDict();
            Assert.NotNull(modules);
            Assert.Empty(modules!);
        }

        // ---- helpers ----

        private void WriteShard(string json)
        {
            File.WriteAllText(_shardPath, json);
            ResetLoaderCache();
        }

        private void WriteOverlay(int year, string json)
        {
            var yy = (year % 100).ToString("D2");
            var path = Path.Combine(
                Path.GetDirectoryName(_overlayPath)!,
                "ty" + yy + "-not-supported.json");
            File.WriteAllText(path, json);
            if (!string.Equals(path, _overlayPath, StringComparison.OrdinalIgnoreCase))
            {
                _extraOverlayPaths.Add(path);
            }
            ResetLoaderCache();
        }

        private void WriteOverlayRaw(int year, string raw)
        {
            // Same as WriteOverlay but takes literal bytes — for malformed
            // JSON we don't want any escaping surprises.
            var yy = (year % 100).ToString("D2");
            var path = Path.Combine(
                Path.GetDirectoryName(_overlayPath)!,
                "ty" + yy + "-not-supported.json");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(raw));
            if (!string.Equals(path, _overlayPath, StringComparison.OrdinalIgnoreCase))
            {
                _extraOverlayPaths.Add(path);
            }
            ResetLoaderCache();
        }

        private static void ResetLoaderCache()
        {
            // P18-020 — cache state moved into a single immutable AccessMapSnapshot
            // swapped atomically by ReloadCaches. Call ReloadCaches to install a
            // fresh empty snapshot (semantically equivalent to clearing the two
            // dictionaries from the previous design).
            FieldAccessMapLoader.ReloadCaches();
        }

        /// <summary>
        /// Reflect through the AccessMapSnapshot indirection to inspect the
        /// per-module cache dictionary. Used by tests that want to assert
        /// "no LoadModule call happened" (e.g. the universal-rule fast path).
        /// </summary>
        private static IDictionary? GetSnapshotModulesDict()
        {
            var t = typeof(FieldAccessMapLoader);
            var snapshotField = t.GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Static);
            var snapshot = snapshotField?.GetValue(null);
            if (snapshot == null) return null;
            var modulesProp = snapshot.GetType().GetProperty("Modules", BindingFlags.Public | BindingFlags.Instance);
            return modulesProp?.GetValue(snapshot) as IDictionary;
        }
    }
}
