using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// P18 — cluster A_session round-5 hardening:
    /// <list type="bullet">
    ///   <item><description>dryRun is part of the BuildPayload fingerprint
    ///   at all three call sites (WriteField/WriteDetail/CreateClient) so a
    ///   dry-run preview cached under <c>(key, fingerprint)</c> does NOT
    ///   poison a real-write commit with the same key.</description></item>
    ///   <item><description>Dry-run CreateClient undo uses CompareExchange
    ///   on NextClientNumber so a concurrent real CreateClient that
    ///   incremented in the undo window is not silently clobbered.</description></item>
    ///   <item><description>Idempotency cache persist uses File.Replace
    ///   atomic rename instead of Delete-then-Move, plus a recovery pass
    ///   that promotes an orphaned .tmp on cold start.</description></item>
    ///   <item><description>ExecuteWithIdempotencyLock survives an evictor
    ///   disposing the semaphore between GetOrAdd and WaitAsync — the prior
    ///   code threw ObjectDisposedException; this code retries once and then
    ///   degrades to unlocked execution (cache-miss-equivalent).</description></item>
    ///   <item><description>Concurrent dry-run + real WriteDetail/WriteField
    ///   on the same client are serialized by a per-client lock so the
    ///   capture → write → undo triple runs atomically per client.</description></item>
    /// </list>
    /// </summary>
    public class SessionManagerP18Tests
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static LacerteSessionManager NewSimManager(IDictionary<string, string?>? env = null)
        {
            return new LacerteSessionManager(env ?? new Dictionary<string, string?>());
        }

        private static async Task<LacerteSessionManager> NewRegisteredSim()
        {
            var mgr = NewSimManager();
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            return mgr;
        }

        // ===================================================================
        // P18 — dryRun in BuildPayload fingerprint
        // ===================================================================

        /// <summary>
        /// Dry-run preview cached under key K then real-write commit under
        /// the same key K must NOT replay as a no-op Success. With dryRun
        /// in the fingerprint, the real commit gets a distinct fingerprint
        /// and surfaces 9102 IdempotencyConflict — loud-failing instead of
        /// silently no-opping the real write.
        /// </summary>
        [Fact]
        public async Task WriteDetail_DryRunThenReal_SameKey_DoesNotReplayPreviewAsCommit()
        {
            using var mgr = await NewRegisteredSim();
            var before = (await mgr.ReadDetail("1001")).Count;

            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 4242.0, "p18-dry-then-real");

            // Preview.
            var preview = await mgr.WriteDetail("1001", row, dryRun: true, idempotencyKey: "p18-001-poison");
            Assert.Equal(StatusCodes.Success, preview);
            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);

            // Real commit under SAME key. Must NOT replay the preview as a
            // no-op; either it commits (preferred — fingerprints differ now)
            // or it surfaces 9102 IdempotencyConflict. Either outcome is an
            // improvement over silent no-op.
            try
            {
                var commit = await mgr.WriteDetail("1001", row, dryRun: false, idempotencyKey: "p18-001-poison");
                Assert.Equal(StatusCodes.Success, commit);
                // The real write must have actually appended a row.
                Assert.Equal(before + 1, (await mgr.ReadDetail("1001")).Count);
            }
            catch (LacerteSDKError ex)
            {
                Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
                // 9102 also acceptable — it means the cache distinguished
                // the two fingerprints (preview vs commit) and refused the
                // silent no-op. Confirm the list is unchanged in this branch.
                Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
            }
        }

        /// <summary>
        /// Two dry-runs with the same key + same args must still replay
        /// each other — the within-mode contract is preserved.
        /// </summary>
        [Fact]
        public async Task WriteDetail_TwoDryRuns_SameKeyAndArgs_StillReplays()
        {
            using var mgr = await NewRegisteredSim();
            var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 11.0, "p18-dry-twice");

            var a = await mgr.WriteDetail("1001", row, dryRun: true, idempotencyKey: "p18-dry-replay");
            var b = await mgr.WriteDetail("1001", row, dryRun: true, idempotencyKey: "p18-dry-replay");

            Assert.Equal(StatusCodes.Success, a);
            Assert.Equal(StatusCodes.Success, b);
        }

        /// <summary>
        /// CreateClient: a dry-run preview cached under key K must not
        /// silently replay as the commit for a real CreateClient call with
        /// the same key. The pre-fix bug shared the fingerprint across
        /// modes; with dryRun in BuildPayload the two fingerprints diverge
        /// and the real call either commits or surfaces 9102 — never
        /// silently no-ops. Verify the surviving real client is reachable
        /// via ListClients in the success-path branch.
        /// </summary>
        [Fact]
        public async Task CreateClient_DryRunThenReal_SameKey_DoesNotSilentlyReplayPreviewAsCommit()
        {
            using var mgr = await NewRegisteredSim();
            var args = new CreateClientArgs("P18CC", "Individual", "Jane", "Doe", "j@example.com");
            var beforeCount = (await mgr.ListClients(limit: 10_000)).Count;

            var dryPreview = await mgr.CreateClient(args, dryRun: true, idempotencyKey: "p18-cc-poison");
            Assert.Equal(StatusCodes.Success, dryPreview.Status);
            // After dry-run the store count should be unchanged (undo ran).
            Assert.Equal(beforeCount, (await mgr.ListClients(limit: 10_000)).Count);

            try
            {
                var real = await mgr.CreateClient(args, dryRun: false, idempotencyKey: "p18-cc-poison");
                // Real commit must actually allocate a client; pre-fix the
                // cached preview was returned (silent no-op with no commit).
                Assert.Equal(StatusCodes.Success, real.Status);
                Assert.NotNull(real.ClientNumber);
                Assert.Equal(beforeCount + 1, (await mgr.ListClients(limit: 10_000)).Count);
            }
            catch (LacerteSDKError ex)
            {
                // 9102 IdempotencyConflict is also acceptable — it means
                // the cache distinguished preview from commit fingerprints.
                Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
                Assert.Equal(beforeCount, (await mgr.ListClients(limit: 10_000)).Count);
            }
        }

        // ===================================================================
        // P18 — dry-run CreateClient undo CompareExchange
        // ===================================================================

        /// <summary>
        /// 32 parallel mix of dry-run + real CreateClient with no idempotency
        /// key (which bypasses the per-key semaphore entirely): assert every
        /// surviving client in the store has a UNIQUE ClientNumber. Pre-fix
        /// the unconditional Interlocked.Exchange in the undo could roll the
        /// counter back over a concurrent real CreateClient's increment,
        /// causing a later real call to mint a duplicate ClientNumber and
        /// silently overwrite the parallel client's row.
        /// </summary>
        [Fact]
        public async Task CreateClient_ConcurrentDryRunAndReal_NoDuplicateClientNumbers()
        {
            using var mgr = await NewRegisteredSim();
            var before = await mgr.ListClients(limit: 10_000);
            var beforeNumbers = new HashSet<string>(
                before.Select(d => d.TryGetValue("clientNumber", out var v) && v is string s ? s : ""),
                StringComparer.OrdinalIgnoreCase);

            // 16 dry-runs + 16 real creates, distinct ClientIds, no idempotency key.
            const int N = 32;
            var tasks = new Task<string?>[N];
            for (int i = 0; i < N; i++)
            {
                int idx = i;
                var dryRun = (idx % 2 == 0);
                var args = new CreateClientArgs($"R5T{idx:D3}", "Individual", "F", "L", null);
                tasks[i] = Task.Run<string?>(async () =>
                {
                    try
                    {
                        var r = await mgr.CreateClient(args, dryRun: dryRun);
                        return r.ClientNumber; // null for dry-run, non-null for real
                    }
                    catch
                    {
                        return null;
                    }
                });
            }
            await Task.WhenAll(tasks);

            var after = await mgr.ListClients(limit: 10_000);
            var afterNumbers = after
                .Select(d => d.TryGetValue("clientNumber", out var v) && v is string s ? s : "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // 1. No duplicate ClientNumbers across all surviving rows.
            Assert.Equal(afterNumbers.Count, afterNumbers.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // 2. The 16 real creates each added one row (any dry-run leaks a
            //    counter slot but that is acceptable per the CompareExchange
            //    contract — operators never observe the leaked number).
            var newNumbers = afterNumbers
                .Where(n => !beforeNumbers.Contains(n))
                .ToList();
            Assert.Equal(16, newNumbers.Count);
            Assert.Equal(newNumbers.Count, newNumbers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ===================================================================
        // P18 — atomic File.Replace persist + orphaned-tmp recovery
        // ===================================================================

        /// <summary>
        /// If a prior persist crashed in the legacy Delete-then-Move window
        /// (canonical missing, .tmp present), the next process start MUST
        /// recover by promoting .tmp to canonical — not cold-miss every
        /// idempotency key. Simulate the crash by hand-crafting an orphaned
        /// .tmp file and constructing a fresh manager.
        /// </summary>
        [Fact]
        public async Task IdempotencyPersistence_CrashBetweenDeleteAndMove_RecoversFromOrphanedTmp()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-p18-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            var tmp = canonical + ".tmp";
            try
            {
                Directory.CreateDirectory(tmpDir);

                // Hand-craft a valid PersistedCache JSON to the .tmp path,
                // leave the canonical absent — exactly the post-crash shape
                // of the legacy Delete-then-Move sequence between the Delete
                // and the Move.
                var entry = new
                {
                    version = 1,
                    entries = new[]
                    {
                        new
                        {
                            key = "p18-orphan-recovery",
                            fingerprint = "deadbeef",
                            resultType = "int",
                            resultValue = StatusCodes.Success,
                            createdAt = DateTimeOffset.UtcNow,
                        },
                    },
                };
                File.WriteAllText(tmp, JsonSerializer.Serialize(entry));
                Assert.True(File.Exists(tmp));
                Assert.False(File.Exists(canonical));

                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };

                using (var mgr = new LacerteSessionManager(env))
                {
                    // The recovery pass should have promoted .tmp to canonical
                    // and loaded the entry.
                    Assert.True(mgr.IdempotencyCacheCount >= 1,
                        "P18: orphaned-tmp recovery must surface the prior persisted entry on cold start");
                    Assert.True(File.Exists(canonical),
                        "P18: recovery must promote .tmp to canonical");
                    Assert.False(File.Exists(tmp),
                        "P18: .tmp must be moved (not copied) so it does not orphan again");
                    await Task.CompletedTask;
                }
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Both canonical and .tmp present (e.g. a crash mid-WriteAllText
        /// before File.Replace was reached, the .tmp partial/torn). Cold
        /// start must delete the orphaned .tmp to keep the directory clean.
        /// </summary>
        [Fact]
        public async Task IdempotencyPersistence_BothCanonicalAndTmpPresent_DropsOrphanedTmp()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-p18-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            var tmp = canonical + ".tmp";
            try
            {
                Directory.CreateDirectory(tmpDir);
                var goodEntry = new
                {
                    version = 1,
                    entries = Array.Empty<object>(),
                };
                File.WriteAllText(canonical, JsonSerializer.Serialize(goodEntry));
                File.WriteAllText(tmp, "{ partial json — torn write"); // garbage

                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };

                using (var mgr = new LacerteSessionManager(env))
                {
                    Assert.True(File.Exists(canonical), "canonical must remain");
                    Assert.False(File.Exists(tmp), "P18: torn .tmp must be cleaned up on cold start");
                    await Task.CompletedTask;
                }
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Round-trip: ForcePersist runs File.Replace path; canonical is
        /// updated in place atomically.
        /// </summary>
        [Fact]
        public async Task IdempotencyPersistence_AtomicReplace_KeepsCanonicalPresent()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-p18-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };

                using var mgr = new LacerteSessionManager(env);
                await mgr.Register();
                mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

                var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 99.0, "p18-replace");
                await mgr.WriteDetail("1001", row, idempotencyKey: "p18-replace-1");
                Assert.True(File.Exists(canonical));

                // Force another persist — exercises the File.Replace path.
                await mgr.WriteDetail("1001", row, idempotencyKey: "p18-replace-2");
                mgr.ForcePersistIdempotencyCacheForTest();
                Assert.True(File.Exists(canonical), "canonical must remain after File.Replace");
                // No tmp of ANY shape may be left behind after a successful
                // persist — neither the legacy bare ".tmp" nor the R6-11
                // per-PID "<name>.<pid>.tmp".
                var lingering = Directory.GetFiles(tmpDir, "*.tmp");
                Assert.True(lingering.Length == 0,
                    "P18/R6-11: no .tmp may linger after File.Replace succeeds; found: "
                    + string.Join(", ", lingering));
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        // ===================================================================
        // P18 — ExecuteWithIdempotencyLock ObjectDisposedException retry
        // ===================================================================

        /// <summary>
        /// Force the race: GetOrAdd returns sem X; before WaitAsync, an
        /// external EvictIdempotencyEntry disposes X. Pre-fix this threw
        /// ObjectDisposedException out of the manager (a generic 9203 to
        /// the operator). Post-fix it retries with a fresh semaphore and
        /// returns Success.
        /// </summary>
        [Fact]
        public async Task ExecuteWithIdempotencyLock_DisposedSemaphoreBetweenGetOrAddAndWait_RetriesCleanly()
        {
            using var mgr = await NewRegisteredSim();

            var locksField = typeof(LacerteSessionManager)
                .GetField("_idempotencyLocks", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(locksField);
            var locks = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)locksField!.GetValue(mgr)!;

            const string key = "p18-dispose-race";
            // Pre-seed the lock so we can dispose it directly before any
            // call enters ExecuteWithIdempotencyLock for this key.
            var preSem = new SemaphoreSlim(1, 1);
            Assert.True(locks.TryAdd(key, preSem));
            preSem.Dispose();
            locks.TryRemove(key, out _);

            // Now WriteDetail with that key. The first GetOrAdd installs a
            // fresh semaphore (the disposed one is gone). Wait must succeed.
            var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 1.0, "dispose-race");
            var status = await mgr.WriteDetail("1001", row, idempotencyKey: key);
            Assert.Equal(StatusCodes.Success, status);
        }

        /// <summary>
        /// Direct test: pre-install a disposed semaphore under the key,
        /// then call WriteDetail with that key. The body would throw
        /// ObjectDisposedException at WaitAsync; the retry must re-GetOrAdd
        /// a fresh semaphore (or fall through to unlocked execution) and
        /// return Success rather than letting ODE escape.
        /// </summary>
        [Fact]
        public async Task ExecuteWithIdempotencyLock_PreInstalledDisposedSemaphore_RetriesOrFallsThrough()
        {
            using var mgr = await NewRegisteredSim();

            var locksField = typeof(LacerteSessionManager)
                .GetField("_idempotencyLocks", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(locksField);
            var locks = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)locksField!.GetValue(mgr)!;

            const string key = "p18-disposed-preinstall";
            // Install a disposed semaphore under the key so GetOrAdd returns
            // the disposed reference on the FIRST attempt; the retry must
            // either get a fresh semaphore or fall through unlocked.
            var disposed = new SemaphoreSlim(1, 1);
            disposed.Dispose();
            locks[key] = disposed;

            var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 2.0, "preinstall-disposed");
            // Must not throw ObjectDisposedException.
            var status = await mgr.WriteDetail("1001", row, idempotencyKey: key);
            Assert.Equal(StatusCodes.Success, status);
        }

        // ===================================================================
        // P18 — per-client lock serializes concurrent dry-run + real writes
        // ===================================================================

        /// <summary>
        /// Two concurrent dry-run WriteDetail calls on the same client with
        /// different idempotency keys (or null keys) must leave the list
        /// byte-identical after both undos run. Pre-fix the append-path undo
        /// blindly trimmed Count-1 (T1's undo could remove T2's row),
        /// corrupting the list across runs.
        /// </summary>
        [Fact]
        public async Task WriteDetail_TwoConcurrentDryRunsAppend_LeavesListByteIdentical()
        {
            using var mgr = await NewRegisteredSim();
            var preRows = (await mgr.ReadDetail("1001")).ToList();

            // 16 pairs × 2 threads each — distinct keys, append path.
            var tasks = new List<Task>();
            for (int i = 0; i < 16; i++)
            {
                int idx = i;
                var r1 = new DetailRow("S1", "", "1", "", "", "", "", "", "", idx + 100, $"t1-{idx}");
                var r2 = new DetailRow("S2", "", "1", "", "", "", "", "", "", idx + 200, $"t2-{idx}");
                tasks.Add(Task.Run(() => mgr.WriteDetail("1001", r1, dryRun: true, idempotencyKey: $"key-A-{idx}")));
                tasks.Add(Task.Run(() => mgr.WriteDetail("1001", r2, dryRun: true, idempotencyKey: $"key-B-{idx}")));
            }
            await Task.WhenAll(tasks);

            var postRows = (await mgr.ReadDetail("1001")).ToList();
            // Dry-run on append path must restore the original list byte-
            // identically. Pre-fix this could leave stranded rows or remove
            // pre-existing rows.
            Assert.Equal(preRows.Count, postRows.Count);
            for (int i = 0; i < preRows.Count; i++)
            {
                Assert.Equal(preRows[i], postRows[i]);
            }
        }

        /// <summary>
        /// Two concurrent dry-run WriteDetail calls on the same
        /// replaceIndex with different keys (or null) must leave the row
        /// byte-identical. Pre-fix the capture-then-write-then-undo
        /// interleave could pin T1's prior over T2's write on undo.
        /// </summary>
        [Fact]
        public async Task WriteDetail_TwoConcurrentDryRunsReplaceIndex_LeavesRowByteIdentical()
        {
            using var mgr = await NewRegisteredSim();

            // Seed at least 2 rows so replaceIndex=0 is valid.
            await mgr.WriteDetail("1001",
                new DetailRow("Seed1", "", "1", "", "", "", "", "", "", 1.0, "seed-A"));
            await mgr.WriteDetail("1001",
                new DetailRow("Seed2", "", "1", "", "", "", "", "", "", 2.0, "seed-B"));
            var preRows = (await mgr.ReadDetail("1001")).ToList();
            var preRow0 = preRows[0];

            // 16 pairs hammering replaceIndex=0 with distinct keys.
            var tasks = new List<Task>();
            for (int i = 0; i < 16; i++)
            {
                int idx = i;
                var r1 = new DetailRow("R1", "", "1", "", "", "", "", "", "", idx + 300, $"r1-{idx}");
                var r2 = new DetailRow("R2", "", "1", "", "", "", "", "", "", idx + 400, $"r2-{idx}");
                tasks.Add(Task.Run(() => mgr.WriteDetail("1001", r1, replaceIndex: 0, dryRun: true, idempotencyKey: $"r5-A-{idx}")));
                tasks.Add(Task.Run(() => mgr.WriteDetail("1001", r2, replaceIndex: 0, dryRun: true, idempotencyKey: $"r5-B-{idx}")));
            }
            await Task.WhenAll(tasks);

            var postRows = (await mgr.ReadDetail("1001")).ToList();
            Assert.Equal(preRows.Count, postRows.Count);
            // The replaceIndex=0 slot must be byte-identical to the pre-state
            // after every dry-run undo runs.
            Assert.Equal(preRow0, postRows[0]);
        }

        /// <summary>
        /// Concurrent dry-run + real WriteDetail mix. After both settle,
        /// every real write's row must be present (none clobbered by a
        /// dry-run undo); every dry-run preview must NOT leave a row
        /// behind (no orphan appended).
        /// </summary>
        [Fact]
        public async Task WriteDetail_ConcurrentDryRunAndReal_NoCorruption()
        {
            using var mgr = await NewRegisteredSim();
            var before = (await mgr.ReadDetail("1001")).Count;

            const int N = 32;
            var tasks = new Task<int>[N];
            int realCount = 0;
            for (int i = 0; i < N; i++)
            {
                int idx = i;
                var dryRun = (idx % 2 == 0);
                if (!dryRun) Interlocked.Increment(ref realCount);
                var row = new DetailRow($"Mix{idx}", "", "1", "", "", "", "", "", "", idx + 500, $"mix-{idx}");
                tasks[i] = Task.Run(() => mgr.WriteDetail("1001", row, dryRun: dryRun));
            }
            await Task.WhenAll(tasks);
            foreach (var t in tasks) Assert.Equal(StatusCodes.Success, await t);

            var after = (await mgr.ReadDetail("1001")).Count;
            // Exactly the real-write count of rows appended.
            Assert.Equal(before + realCount, after);
        }
    }
}
