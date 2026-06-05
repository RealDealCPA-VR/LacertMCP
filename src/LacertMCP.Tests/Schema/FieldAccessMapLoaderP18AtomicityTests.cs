using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    /// <summary>
    /// P18-007 + P18-020 — coverage for the round-5 hardening of
    /// <see cref="FieldAccessMapLoader"/>.
    ///
    /// <para>
    /// P18-007 (defense): the FieldAccessDumper non-atomic shard write
    /// previously could leave half-written base JSON on a kill/crash; the
    /// loader-side fix is a symmetric try/catch around the LoadModule
    /// Deserialize that degrades to an empty sentinel (mirroring the
    /// long-standing LoadOverlay behavior) so a single corrupt shard cannot
    /// process-wide-poison every write to that module for the lifetime of
    /// the process.
    /// </para>
    ///
    /// <para>
    /// P18-020 (atomic reload): <see cref="FieldAccessMapLoader.ReloadCaches"/>
    /// now Interlocked-swaps a single immutable snapshot rather than
    /// sequentially Clear()-ing two independent dictionaries; readers capture
    /// the snapshot once at entry so both the overlay-precedence lookup and
    /// the base-map fallback within a single GetFieldAccess call are drawn
    /// from the SAME generation. No (NEW base, OLD overlay) blend is
    /// observable across a concurrent reload.
    /// </para>
    ///
    /// <para>
    /// Lives in <see cref="AccessMapMutationCollection"/> because every test
    /// here writes a real shard file or seeds the loader's static snapshot —
    /// running in parallel with other access-map tests would race.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class FieldAccessMapLoaderP18AtomicityTests : IDisposable
    {
        private readonly string _shardPath;
        private readonly string? _originalShard;
        private readonly bool _shardExisted;

        private readonly string _overlayPath;
        private readonly string? _originalOverlay;
        private readonly bool _overlayExisted;

        public FieldAccessMapLoaderP18AtomicityTests()
        {
            var asmDir = Path.GetDirectoryName(typeof(FieldAccessMapLoader).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            _shardPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "base", "individual.json");
            _overlayPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "overlays", "ty98-not-supported.json");

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

            FieldAccessMapLoader.ReloadCaches();
        }

        public void Dispose()
        {
            if (_shardExisted && _originalShard != null)
            {
                File.WriteAllText(_shardPath, _originalShard);
            }
            else
            {
                File.WriteAllText(_shardPath, "{}\n");
            }

            if (_overlayExisted && _originalOverlay != null)
            {
                File.WriteAllText(_overlayPath, _originalOverlay);
            }
            else if (File.Exists(_overlayPath))
            {
                File.Delete(_overlayPath);
            }

            FieldAccessMapLoader.ReloadCaches();
        }

        // ============================================================
        // P18-007 — Malformed base shard degrades to empty (does NOT throw)
        // ============================================================

        [Fact]
        public void Malformed_Base_Shard_Degrades_To_Empty_Map_And_Does_Not_Poison_Cache()
        {
            // The dumper crashes mid-write of individual.json: a kill between
            // FileMode.Create's truncate and the full WriteAllText buffer
            // flush leaves a partial/garbled JSON file. Without P18-007's
            // try/catch around the Deserialize, every subsequent GetFieldAccess
            // for any field in module 'I' would re-open, re-parse, re-throw —
            // surfacing as InternalServerError on every single write to that
            // module for the rest of the process lifetime.
            File.WriteAllBytes(_shardPath, Encoding.UTF8.GetBytes("{ this is not json at all"));
            FieldAccessMapLoader.ReloadCaches();

            // First call: must not throw, falls through to Unknown sentinel.
            var a = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_15", 2024);
            Assert.Equal(FieldAccess.Unknown, a);

            // Second call: still no throw — proves the empty sentinel was
            // cached, not re-parsed-and-re-thrown.
            var b = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_99", 2024);
            Assert.Equal(FieldAccess.Unknown, b);

            // Universal-rule fast-path must still work even with a poisoned
            // base shard (proves the bug is isolated to base map lookup).
            var c = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_0", 2024);
            Assert.Equal(FieldAccess.ReadOnly, c);
        }

        [Fact]
        public void Malformed_Base_Shard_For_One_Module_Does_Not_Affect_Other_Modules()
        {
            // Module 'I' (individual.json) is poisoned with garbage; module
            // 'C' (corporate.json) is left intact. Lookups on 'C' should
            // continue to work normally — proves the per-module isolation.
            File.WriteAllBytes(_shardPath, Encoding.UTF8.GetBytes("garbage{}}{"));
            FieldAccessMapLoader.ReloadCaches();

            // Sanity: 'I' module degrades.
            Assert.Equal(FieldAccess.Unknown,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_SOMETHING", 2024));

            // 'C' module's universal rule still fires.
            Assert.Equal(FieldAccess.ReadOnly,
                FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_0", 2024));
        }

        // ============================================================
        // P18-020 — Atomic ReloadCaches snapshot semantics
        // ============================================================

        [Fact]
        public void ReloadCaches_AtomicSwap_NoBlendedSnapshot_VisibleToReader()
        {
            // Seed both base and overlay so a stale snapshot would have
            // entries to be "blended" against. The contract: every result
            // is consistent with EITHER the pre-reload OR the post-reload
            // snapshot, but never a torn mix (e.g. NEW base + OLD overlay
            // would surface as ReadWrite when the field is supposed to be
            // NotSupported per the overlay).
            File.WriteAllText(_shardPath,
                "{ \"I|1|C1_ATOMIC\": { \"access\": \"ReadWrite\", \"source\": \"test\" } }");
            File.WriteAllText(_overlayPath,
                "{ \"taxYear\": 2098, \"overrides\": { \"I|1|C1_ATOMIC\": { \"access\": \"NotSupported\", \"source\": \"test\" } } }");
            FieldAccessMapLoader.ReloadCaches();

            // Warm both caches with a TY2098 lookup → overlay wins → NotSupported.
            Assert.Equal(FieldAccess.NotSupported,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_ATOMIC", 2098));

            var readerExceptions = new List<Exception>();
            var observedValues = new HashSet<FieldAccess>();
            var observedLock = new object();
            var stopReader = 0;
            const int readerIterations = 10_000;

            // Reader thread: many concurrent GetFieldAccess calls, runs a
            // fixed iteration count to guarantee observations regardless of
            // reloader timing.
            var readerTask = Task.Run(() =>
            {
                for (int i = 0; i < readerIterations && Volatile.Read(ref stopReader) == 0; i++)
                {
                    try
                    {
                        var v = FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_ATOMIC", 2098);
                        lock (observedLock) observedValues.Add(v);
                    }
                    catch (Exception ex)
                    {
                        lock (observedLock) readerExceptions.Add(ex);
                        break;
                    }
                }
            });

            // Reloader thread: fire ReloadCaches in a tight loop, interleaved
            // with the reader.
            var reloaderTask = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    FieldAccessMapLoader.ReloadCaches();
                    Thread.SpinWait(20);
                }
                Interlocked.Exchange(ref stopReader, 1);
            });

            Task.WaitAll(new[] { readerTask, reloaderTask }, TimeSpan.FromSeconds(15));

            // Contract: no observable blended state. The legal result is
            // NotSupported (overlay wins on any snapshot, fresh or warmed).
            // ReadWrite would mean the loader saw the base shard but missed
            // the overlay — the (NEW base, OLD overlay) blend the snapshot
            // pattern eliminates. Unknown is not legal either: any non-empty
            // snapshot picks up both shards under the captured snapshot.
            // Empty snapshots are fine because the GetOrAdd factory inside
            // the snapshot re-loads both shards under the SAME snapshot
            // reference captured at GetFieldAccess entry.
            Assert.Empty(readerExceptions);
            Assert.NotEmpty(observedValues);
            Assert.DoesNotContain(FieldAccess.ReadWrite, observedValues);
            Assert.Contains(FieldAccess.NotSupported, observedValues);
        }

        [Fact]
        public void ReloadCaches_ReturnsEvictedCountsFromPriorSnapshot()
        {
            // The displaced snapshot's counts must be reported back so the
            // lacerte_reload_schema tool can surface them. After warming
            // both caches with at least one lookup, ReloadCaches reports
            // both as evicted.
            FieldAccessMapLoader.ReloadCaches();

            // Warm: one module + one overlay year.
            FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NOT_PRESENT", 2024);

            var (modules, overlays) = FieldAccessMapLoader.ReloadCaches();
            Assert.True(modules >= 1, $"expected >=1 module evicted, got {modules}");
            Assert.True(overlays >= 1, $"expected >=1 overlay evicted, got {overlays}");

            // Second reload sees zero — we just installed a fresh empty snapshot.
            var (m2, o2) = FieldAccessMapLoader.ReloadCaches();
            Assert.Equal(0, m2);
            Assert.Equal(0, o2);
        }

        [Fact]
        public void ReloadCaches_InstallsFreshEmptySnapshot()
        {
            // After ReloadCaches the in-memory snapshot's Modules and Overlays
            // dictionaries must both be empty (lazy-load on demand). This pins
            // the implementation detail that the swap installs a NEW snapshot
            // rather than just clearing the existing one in place — keeping
            // the (base, overlay) atomicity guarantee.
            FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NOT_PRESENT", 2024);

            FieldAccessMapLoader.ReloadCaches();

            var (modules, overlays) = GetSnapshotDicts();
            Assert.NotNull(modules);
            Assert.NotNull(overlays);
            Assert.Empty(modules!);
            Assert.Empty(overlays!);
        }

        [Fact]
        public void ReloadCaches_ConcurrentInvocations_DoNotThrow_AndConvergeToEmpty()
        {
            // 32 parallel ReloadCaches calls should never throw and should
            // converge to a single empty snapshot (Interlocked.Exchange is
            // atomic; whichever snapshot lands last wins). No torn state,
            // no NullReferenceException from a half-installed snapshot.
            FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NOT_PRESENT", 2024);
            FieldAccessMapLoader.GetFieldAccess('C', 1, "C1_NOT_PRESENT", 2024);

            var tasks = new Task[32];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() => FieldAccessMapLoader.ReloadCaches());
            }
            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));

            var (modules, overlays) = GetSnapshotDicts();
            Assert.NotNull(modules);
            Assert.NotNull(overlays);
            Assert.Empty(modules!);
            Assert.Empty(overlays!);
        }

        // ============================================================
        // helpers
        // ============================================================

        /// <summary>
        /// Reflect through the AccessMapSnapshot indirection to inspect the
        /// per-module + per-overlay cache dictionaries.
        /// </summary>
        private static (IDictionary? modules, IDictionary? overlays) GetSnapshotDicts()
        {
            var t = typeof(FieldAccessMapLoader);
            var snapshotField = t.GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Static);
            var snapshot = snapshotField?.GetValue(null);
            if (snapshot == null) return (null, null);
            var snapType = snapshot.GetType();
            var modules = snapType.GetProperty("Modules", BindingFlags.Public | BindingFlags.Instance)?.GetValue(snapshot) as IDictionary;
            var overlays = snapType.GetProperty("Overlays", BindingFlags.Public | BindingFlags.Instance)?.GetValue(snapshot) as IDictionary;
            return (modules, overlays);
        }
    }
}
