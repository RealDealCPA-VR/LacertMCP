using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using LacertMCP.Server.Schema;

namespace LacertMCP.Tests
{
    /// <summary>
    /// P15-063 — shared additive in-memory seed for the
    /// <see cref="FieldAccessMapLoader"/> static cache. Replaces the
    /// triplicated <c>EnsureWritableFieldSeeded</c> reflection helpers
    /// previously inlined in <c>WireLogSessionIntegrationTests</c>,
    /// <c>SessionManagerBridgeWireupTests</c>, and
    /// <c>SessionManagerAutoReconnectTests</c>.
    ///
    /// <para>
    /// This helper mutates the loader's process-wide <c>_moduleCache</c>
    /// additively (never removes entries). All three callers are gated on
    /// <c>[Collection(AccessMapMutationCollection.Name)]</c> which serializes
    /// them with the on-disk Tools/* tests that use
    /// <see cref="LacertMCP.Tests.Tools.AccessMapTestFixture"/>.
    /// </para>
    ///
    /// <para>
    /// Use <see cref="AccessMapTestFixture"/> when you need a clean
    /// disk-backed reset of the loader between tests. Use this helper when
    /// you only need to GUARANTEE a single ReadWrite entry exists without
    /// touching disk or wiping other in-memory entries.
    /// </para>
    /// </summary>
    internal static class AccessMapInMemorySeed
    {
        private static readonly object _seedLock = new object();

        /// <summary>
        /// Idempotently inject a single ReadWrite <see cref="FieldEntry"/>
        /// into the <see cref="FieldAccessMapLoader"/>'s in-memory cache.
        /// No-ops if the loader internals shape changed (reflection failure)
        /// or if the entry already exists.
        /// </summary>
        public static void EnsureWritableFieldSeeded(
            char module = 'I',
            int table = 1,
            string field = "C1_15",
            string source = "test-seed")
        {
            lock (_seedLock)
            {
                // P18-020 — cache state lives inside an immutable AccessMapSnapshot
                // swapped atomically by ReloadCaches. Reach in through the snapshot
                // reference to seed the per-module dictionary additively.
                var t = typeof(FieldAccessMapLoader);
                var snapshotField = t.GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Static);
                var snapshot = snapshotField?.GetValue(null);
                if (snapshot == null) return;
                var modulesProp = snapshot.GetType().GetProperty("Modules", BindingFlags.Public | BindingFlags.Instance);
                var cache = modulesProp?.GetValue(snapshot) as ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>>;
                if (cache == null) return; // loader internals changed; tests will surface elsewhere.

                var key = $"{module}|{table}|{field}";
                if (cache.TryGetValue(module, out var existing) && existing.ContainsKey(key)) return;

                // Build a fresh map: existing entries plus our writable entry.
                var seed = new Dictionary<string, FieldEntry>();
                if (existing != null)
                {
                    foreach (var kv in existing) seed[kv.Key] = kv.Value;
                }
                seed[key] = new FieldEntry(FieldAccess.ReadWrite, Source: source);
                cache[module] = seed;
            }
        }
    }
}
