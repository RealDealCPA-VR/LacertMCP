using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Audit round 6 — session-layer regression coverage:
    /// <list type="bullet">
    ///   <item><description>R6-01 — payload fingerprints are unkeyed SHA-256, so a
    ///   fingerprint persisted by ANOTHER process (simulated by hand-computing it
    ///   in-test) still replays after a "restart". The per-process HMAC key made
    ///   the persisted cache structurally dead across real restarts.</description></item>
    ///   <item><description>R6-03 — the fingerprint includes the session context
    ///   triple (dataPath, taxYear, module): same key + same args + different
    ///   context is a 9102 conflict, never a silent cross-context replay.</description></item>
    ///   <item><description>R6-04 — only Success results are cached; a live
    ///   bridge returning a failure status does not poison the key for 24h.</description></item>
    ///   <item><description>R6-10 — manager-path detail writes to an unknown
    ///   client throw DetailCreationError (matching SimulationStore.WriteDetail
    ///   and live) instead of auto-vivifying an orphan detail list.</description></item>
    ///   <item><description>R6-11 — multi-instance persistence: merge-on-write
    ///   keeps a sibling instance's entries; per-PID tmps; stale-tmp cleanup
    ///   rules.</description></item>
    ///   <item><description>R6-22 — CreateClient registration gate fires in both
    ///   modes (incl. live dryRun); sim dryRun returns a null ClientNumber like
    ///   live dryRun.</description></item>
    ///   <item><description>R6-23 — the P18-022 DBF-lock latch fires on the REAL
    ///   tool path (manager sim adapters), not just the store chokepoints.</description></item>
    ///   <item><description>R6-25 — SessionStatus reports effective registration
    ///   after the 24h expiry without mutating the latch.</description></item>
    /// </list>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionManagerR6Tests
    {
        private const string SeedField = "C1_15";

        public SessionManagerR6Tests()
        {
            // R6-23 / R6-03 field-write tests need a writable field on
            // module I / table 1 so the happy path reaches the row mutation.
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(
                module: 'I', table: 1, field: SeedField);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static async Task<LacerteSessionManager> NewRegisteredSimAsync()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            return mgr;
        }

        // Same reflection pattern as SessionManagerP17Tests /
        // SessionManagerBridgeWireupTests for the internal live-mode ctor.
        private static LacerteSessionManager NewLiveManagerWithFake(ILacerteBridge bridge)
        {
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) },
                modifiers: null);
            Assert.NotNull(ctor);
            return (LacerteSessionManager)ctor!.Invoke(
                new object?[] { bridge, new Dictionary<string, string?>() });
        }

        private static DetailRow NewRow(double amount, string description)
            => new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", amount, description);

        // Replicates LacerteSessionManager.BuildPayload's canonical form so a
        // test can compute the fingerprint a DIFFERENT process would have
        // persisted: SHA-256 hex over {"op":..., "args":[...]} serialized
        // with default JsonSerializerOptions. If this helper drifts from
        // BuildPayload, the R6-01 cross-restart test below fails loudly —
        // which is exactly the regression alarm we want, because any change
        // to the canonical form invalidates every fingerprint already
        // persisted on operator machines.
        private static string ComputeFingerprint(string op, params object?[] args)
        {
            var canon = new Dictionary<string, object?>
            {
                ["op"] = op,
                ["args"] = args,
            };
            var json = JsonSerializer.Serialize(canon);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // ===================================================================
        // R6-01 — cross-restart replay with a fingerprint persisted by a
        // "different process"
        // ===================================================================

        /// <summary>
        /// Simulates a REAL restart (not the same-process two-manager dance
        /// the original P17-004 test did, which passed only because both
        /// managers shared the static HMAC key): the persisted file is
        /// hand-crafted by the TEST, with a fingerprint computed by the
        /// test's own SHA-256 — no manager state is shared. A fresh manager
        /// must load it and REPLAY a byte-identical retry instead of
        /// throwing 9102 and re-executing the write.
        /// </summary>
        [Fact]
        public async Task Idempotency_FingerprintPersistedByAnotherProcess_ReplaysAfterRestart()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-r601-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                Directory.CreateDirectory(tmpDir);
                const string key = "r6-01-cross-restart";
                var row = NewRow(123.45, "r6-01-row");

                // The fingerprint the OTHER process would have computed for
                // WriteDetail("1001", row) under (IData2024, 2024, 'I') with
                // replaceIndex=null, dryRun=false. Arg order mirrors the
                // R6-03 context-threaded BuildPayload call site.
                var fingerprint = ComputeFingerprint(
                    "WriteDetail", SeedData.DataPathIData2024, 2024, "I",
                    "1001", row, null, false);

                var file = new
                {
                    version = 1,
                    entries = new[]
                    {
                        new
                        {
                            key,
                            fingerprint,
                            resultType = "int",
                            resultValue = StatusCodes.Success,
                            createdAt = DateTimeOffset.UtcNow,
                        },
                    },
                };
                File.WriteAllText(canonical, JsonSerializer.Serialize(file));

                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };
                using var mgr = new LacerteSessionManager(env);
                Assert.True(mgr.IdempotencyCacheCount >= 1,
                    "R6-01: the hand-crafted entry must load on cold start");
                await mgr.Register();
                mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

                var before = (await mgr.ReadDetail("1001")).Count;
                // Byte-identical retry: must REPLAY (Success, no append) —
                // pre-fix this threw 9102 because the fresh process's HMAC
                // key produced a different fingerprint for the same bytes.
                var status = await mgr.WriteDetail("1001", row, idempotencyKey: key);
                Assert.Equal(StatusCodes.Success, status);
                var after = (await mgr.ReadDetail("1001")).Count;
                Assert.Equal(before, after);
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        // ===================================================================
        // R6-03 — session context in the fingerprint
        // ===================================================================

        [Fact]
        public async Task WriteDetail_SameKeySameArgs_DifferentTaxYear_Throws9102()
        {
            using var mgr = await NewRegisteredSimAsync();
            var row = NewRow(77.0, "r6-03-year");

            var first = await mgr.WriteDetail("1001", row, idempotencyKey: "r6-03-wd-year");
            Assert.Equal(StatusCodes.Success, first);

            // Switch ONLY the tax year. Pre-fix: silent cached replay —
            // Success returned, wire never fired, the TY23 write never
            // landed. Post-fix: loud 9102.
            mgr.SetContext(SeedData.DataPathIData2024, 2023, 'I');
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("1001", row, idempotencyKey: "r6-03-wd-year"));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
        }

        [Fact]
        public async Task WriteField_SameKeySameArgs_DifferentDataPath_Throws9102()
        {
            using var mgr = await NewRegisteredSimAsync();

            var first = await mgr.WriteField(1, "1001", SeedField, "r6-03",
                idempotencyKey: "r6-03-wf-path");
            Assert.Equal(StatusCodes.Success, first);

            // Switch ONLY the dataPath (same year/module).
            mgr.SetContext(SeedData.DataPathIData2020, 2024, 'I');
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteField(1, "1001", SeedField, "r6-03",
                    idempotencyKey: "r6-03-wf-path"));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
        }

        [Fact]
        public async Task CreateClient_SameKeySameArgs_DifferentModule_Throws9102()
        {
            using var mgr = await NewRegisteredSimAsync();
            var args = new CreateClientArgs("R603CC", "Individual", "Ctx", "Test", null);

            var first = await mgr.CreateClient(args, idempotencyKey: "r6-03-cc-module");
            Assert.Equal(StatusCodes.Success, first.Status);

            // Switch ONLY the module. Pre-fix the module-I result replayed
            // and no client was ever created in module C; post-fix the
            // conflict fires BEFORE any validation gate (so we see 9102,
            // not TaxModuleMismatch).
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'C');
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.CreateClient(args, idempotencyKey: "r6-03-cc-module"));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
        }

        // ===================================================================
        // R6-04 — non-Success results are not cached
        // ===================================================================

        [Fact]
        public async Task WriteDetail_Live_FailureStatusNotCached_RetryReachesWire()
        {
            var fake = new ScriptedBridge();
            fake.WriteDetailStatuses.Enqueue(StatusCodes.DBFNotAccessable);
            fake.WriteDetailStatuses.Enqueue(StatusCodes.Success);
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var row = NewRow(50.0, "r6-04-wd");
            // Attempt 1: the live bridge RETURNS the failure int (return is
            // open in the Lacerte UI). It must NOT be cached under the key.
            // A non-Success live status is a failed write and now surfaces as a
            // structured error (sim parity) instead of a return value callers
            // could mistake for success.
            var e1 = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("JOHNS", row, idempotencyKey: "r6-04-wd"));
            Assert.Equal(StatusCodes.DBFNotAccessable, e1.StatusCode);
            Assert.False(mgr.TryGetIdempotencyEntryForTest("r6-04-wd", out _),
                "R6-04: a non-Success result must not be cached — pre-fix the failure was sticky for 24h");

            // Attempt 2 (operator closed the file): same key + same payload
            // must reach the wire again and succeed — pre-fix it replayed
            // the cached FAILURE without touching the bridge.
            var r2 = await mgr.WriteDetail("JOHNS", row, idempotencyKey: "r6-04-wd");
            Assert.Equal(StatusCodes.Success, r2);
            Assert.Equal(2, fake.WriteDetailCalls);
            Assert.True(mgr.TryGetIdempotencyEntryForTest("r6-04-wd", out _),
                "Success must be cached so a third byte-identical retry replays");
        }

        [Fact]
        public async Task CreateClient_Live_FailureStatusNotCached_RetryReachesWire()
        {
            var fake = new ScriptedBridge();
            fake.CreateClientResults.Enqueue(new BridgeCreateClientResult(StatusCodes.DBFNotAccessable, null));
            fake.CreateClientResults.Enqueue(new BridgeCreateClientResult(StatusCodes.Success, "1005"));
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var args = new CreateClientArgs("R604CC", "Individual", "F", "L", null);
            var e1 = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.CreateClient(args, idempotencyKey: "r6-04-cc"));
            Assert.Equal(StatusCodes.DBFNotAccessable, e1.StatusCode);
            Assert.False(mgr.TryGetIdempotencyEntryForTest("r6-04-cc", out _));

            var r2 = await mgr.CreateClient(args, idempotencyKey: "r6-04-cc");
            Assert.Equal(StatusCodes.Success, r2.Status);
            Assert.Equal("1005", r2.ClientNumber);
            Assert.Equal(2, fake.CreateClientCalls);
            Assert.True(mgr.TryGetIdempotencyEntryForTest("r6-04-cc", out _));
        }

        // ===================================================================
        // R6-10 — no auto-vivified detail list for unknown clients
        // ===================================================================

        [Fact]
        public async Task WriteDetail_UnknownClient_ThrowsDetailCreationError_NoOrphanList()
        {
            using var mgr = await NewRegisteredSimAsync();
            var row = NewRow(10.0, "r6-10");

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("9999", row));
            Assert.Equal(StatusCodes.DetailCreationError, ex.StatusCode);

            // No orphan list may have been seeded — pre-fix GetOrAdd left a
            // permanent empty list that subsequent reads surfaced as a
            // "real" (empty) detail file for a client that doesn't exist.
            var module = mgr.SimulationStoreForTest!
                .TryGetModule(SeedData.DataPathIData2024, 2024, 'I');
            Assert.NotNull(module);
            Assert.False(module!.Details.ContainsKey("9999"),
                "R6-10: a failed detail write must not seed an orphan detail list");
        }

        [Fact]
        public async Task WriteDetail_UnknownClient_DryRun_AlsoThrowsDetailCreationError()
        {
            using var mgr = await NewRegisteredSimAsync();
            var row = NewRow(11.0, "r6-10-dry");

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("9999", row, dryRun: true));
            Assert.Equal(StatusCodes.DetailCreationError, ex.StatusCode);
        }

        // ===================================================================
        // R6-11 — multi-instance persistence
        // ===================================================================

        /// <summary>
        /// Two concurrently-running instances sharing the same cache path:
        /// instance B's persist must MERGE with (not clobber) instance A's
        /// on-disk entries. Pre-fix the full-cache overwrite erased A's
        /// entries, silently dropping replay protection for A's writes.
        /// </summary>
        [Fact]
        public async Task IdempotencyPersistence_TwoInstances_MergeOnWrite_KeepsBothEntries()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-r611-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };

                // Both instances exist BEFORE either persists (the Claude
                // Desktop + Cursor steady state).
                using var mgrA = new LacerteSessionManager(env);
                using var mgrB = new LacerteSessionManager(env);
                await mgrA.Register();
                mgrA.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                await mgrB.Register();
                mgrB.SetContext(SeedData.DataPathIData2024, 2024, 'I');

                await mgrA.WriteDetail("1001", NewRow(1.0, "from-A"), idempotencyKey: "r6-11-A");
                // B persists AFTER A — pre-fix this overwrote the file with
                // only B's cache, erasing A's entry.
                await mgrB.WriteDetail("1001", NewRow(2.0, "from-B"), idempotencyKey: "r6-11-B");

                // A fresh "restarted" instance must see BOTH entries.
                using var mgrC = new LacerteSessionManager(env);
                Assert.True(mgrC.TryGetIdempotencyEntryForTest("r6-11-A", out _),
                    "R6-11: instance A's entry must survive instance B's persist");
                Assert.True(mgrC.TryGetIdempotencyEntryForTest("r6-11-B", out _));

                // Per-PID tmp naming: the legacy shared ".tmp" must never be
                // created, and no tmp lingers after successful persists.
                Assert.False(File.Exists(canonical + ".tmp"),
                    "R6-11: the legacy shared .tmp name must not be used");
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void IdempotencyPersistence_StaleOtherPidTmp_DeletedOnColdStart()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-r611s-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(canonical, "{\"Version\":1,\"Entries\":[]}");
                int otherPid = Process.GetCurrentProcess().Id + 12345;
                var staleTmp = canonical + "." + otherPid + ".tmp";
                File.WriteAllText(staleTmp, "{ torn");
                File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow.AddHours(-2));

                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };
                using var mgr = new LacerteSessionManager(env);
                Assert.False(File.Exists(staleTmp),
                    "R6-11: a >1h-old other-pid tmp is orphaned and must be cleaned up");
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void IdempotencyPersistence_RecentOtherPidTmp_LeftAloneOnColdStart()
        {
            // A RECENT other-pid tmp may belong to a live sibling instance
            // mid-persist; deleting it would race that instance's
            // File.Replace. Cold start must leave it alone.
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-r611r-" + Guid.NewGuid().ToString("N"));
            var canonical = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(canonical, "{\"Version\":1,\"Entries\":[]}");
                int otherPid = Process.GetCurrentProcess().Id + 12345;
                var freshTmp = canonical + "." + otherPid + ".tmp";
                File.WriteAllText(freshTmp, "{\"Version\":1,\"Entries\":[]}");

                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = canonical,
                };
                using var mgr = new LacerteSessionManager(env);
                Assert.True(File.Exists(freshTmp),
                    "R6-11: a recent other-pid tmp may be a live instance mid-persist — leave it alone");
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        // ===================================================================
        // R6-22 — CreateClient dryRun parity (registration gate + null
        // ClientNumber in both modes)
        // ===================================================================

        [Fact]
        public async Task CreateClient_Live_DryRun_Unregistered_ThrowsNotSignedIn_BeforeBridge()
        {
            var fake = new ScriptedBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            // Deliberately NOT registered.
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.CreateClient(new CreateClientArgs("R622L", "Individual"), dryRun: true));
            Assert.Equal(StatusCodes.NotSignedIn, ex.StatusCode);
            Assert.Equal(0, fake.CreateClientCalls);
        }

        [Fact]
        public async Task CreateClient_Sim_DryRun_Unregistered_ThrowsNotSignedIn()
        {
            // Parity check: sim rejects the same way (it always did via the
            // store gate; now both modes reject at the same chokepoint).
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.CreateClient(new CreateClientArgs("R622S", "Individual"), dryRun: true));
            Assert.Equal(StatusCodes.NotSignedIn, ex.StatusCode);
        }

        [Fact]
        public async Task CreateClient_Sim_DryRun_ReturnsNullClientNumber_LikeLive()
        {
            using var mgr = await NewRegisteredSimAsync();
            var before = (await mgr.ListClients(limit: 10_000)).Count;

            var result = await mgr.CreateClient(
                new CreateClientArgs("R622N", "Individual", "Dry", "Run", null), dryRun: true);

            Assert.Equal(StatusCodes.Success, result.Status);
            // Pre-fix sim returned the minted (then rolled-back) number while
            // live returned null — trivially distinguishable modes AND a
            // leaked identifier no follow-up read could resolve.
            Assert.Null(result.ClientNumber);
            Assert.Equal(before, (await mgr.ListClients(limit: 10_000)).Count);
        }

        // ===================================================================
        // R6-23 — DBF-lock latch on the manager (real tool) path
        // ===================================================================

        [Fact]
        public async Task WriteField_ManagerPath_LockedTable_ThrowsDBFNotAccessable()
        {
            using var mgr = await NewRegisteredSimAsync();
            var sim = mgr.SimulationStoreForTest!;

            sim.ToggleLock(2024, 'I', 1, locked: true);
            try
            {
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                    mgr.WriteField(1, "1001", SeedField, "blocked"));
                Assert.Equal(StatusCodes.DBFNotAccessable, ex.StatusCode);
            }
            finally
            {
                sim.ToggleLock(2024, 'I', 1, locked: false);
            }

            // Recovery path: unlock → same write succeeds.
            var status = await mgr.WriteField(1, "1001", SeedField, "ok");
            Assert.Equal(StatusCodes.Success, status);
        }

        [Fact]
        public async Task WriteField_ManagerPath_DryRun_LockedTable_AlsoThrowsDBFNotAccessable()
        {
            // dryRun must not mask the lock — the live SDK can't write a
            // locked DBF regardless of intent.
            using var mgr = await NewRegisteredSimAsync();
            var sim = mgr.SimulationStoreForTest!;

            sim.ToggleLock(2024, 'I', 1, locked: true);
            try
            {
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                    mgr.WriteField(1, "1001", SeedField, "blocked", dryRun: true));
                Assert.Equal(StatusCodes.DBFNotAccessable, ex.StatusCode);
            }
            finally
            {
                sim.ToggleLock(2024, 'I', 1, locked: false);
            }
        }

        [Fact]
        public async Task WriteDetail_ManagerPath_LockedDetailFile_ThrowsDBFNotAccessable()
        {
            using var mgr = await NewRegisteredSimAsync();
            var sim = mgr.SimulationStoreForTest!;

            // Detail files use the table=0 latch convention (P18-022).
            sim.ToggleLock(2024, 'I', 0, locked: true);
            try
            {
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                    mgr.WriteDetail("1001", NewRow(5.0, "r6-23-locked")));
                Assert.Equal(StatusCodes.DBFNotAccessable, ex.StatusCode);
            }
            finally
            {
                sim.ToggleLock(2024, 'I', 0, locked: false);
            }

            var status = await mgr.WriteDetail("1001", NewRow(5.0, "r6-23-unlocked"));
            Assert.Equal(StatusCodes.Success, status);
        }

        // ===================================================================
        // R6-25 — SessionStatus effective registration after expiry
        // ===================================================================

        [Fact]
        public async Task SessionStatus_AfterExpiry_ReportsNotRegistered_WithoutSideEffects()
        {
            using var mgr = await NewRegisteredSimAsync();
            Assert.True(mgr.SessionStatus().IsRegistered);

            mgr.SetExpiresAtForTest(DateTimeOffset.UtcNow.AddMinutes(-1));

            var snap = mgr.SessionStatus();
            Assert.False(snap.IsRegistered,
                "R6-25: a post-expiry snapshot must not report a green light");
            Assert.NotNull(snap.MinutesUntilExpiry);
            Assert.Equal(0d, snap.MinutesUntilExpiry!.Value);
            // ExpiresAt stays populated so the operator can see WHEN it lapsed.
            Assert.NotNull(snap.ExpiresAt);

            // NO side effects: the latch is owned by ThrowIfAuthExpired (the
            // gated-call probe), not by the pure status read.
            Assert.True(mgr.IsRegistered,
                "SessionStatus must not mutate the registration latch");
        }

        // -----------------------------------------------------------------
        // Scripted fake bridge — returns queued statuses so tests can model
        // a live SDK that RETURNS failure ints (rather than throwing).
        // -----------------------------------------------------------------

        private sealed class ScriptedBridge : ILacerteBridge
        {
            public int WriteDetailCalls;
            public int CreateClientCalls;
            public readonly Queue<int> WriteDetailStatuses = new Queue<int>();
            public readonly Queue<BridgeCreateClientResult> CreateClientResults =
                new Queue<BridgeCreateClientResult>();

            public int Register(string appName, string appId, int taxYear)
                => StatusCodes.AccessGranted;

            public bool CanUseNewApis(string appId, int year, char module) => true;

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(
                string dataPath, int year, char module)
                => Array.Empty<IReadOnlyDictionary<string, object?>>();

            public IReadOnlyDictionary<string, object?>? ReadClient(
                string dataPath, int year, char module, string clientNumber)
                => null;

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(
                string dataPath, int year, char module, int table,
                string? clientNumber = null, string? stateAbbrev = null)
                => Array.Empty<IReadOnlyDictionary<string, object?>>();

            public int WriteField(string appId, string dataPath, int year, char module,
                int table, int rowNum, string field, object? value)
                => StatusCodes.Success;

            public int UpdateTaxReturn(string appId, string dataPath, int year, char module,
                int table, string clientNumber, string field, object? value,
                int? subClientId, string? stateAbbrev)
                => StatusCodes.Success;

            public int WriteDetail(string appId, string dataPath, int year, char module,
                string clientNumber, DetailRow row, int? replaceIndex)
            {
                Interlocked.Increment(ref WriteDetailCalls);
                lock (WriteDetailStatuses)
                {
                    return WriteDetailStatuses.Count > 0
                        ? WriteDetailStatuses.Dequeue()
                        : StatusCodes.Success;
                }
            }

            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module,
                string clientId, string taxType, string? firstName, string? lastName, string? emailId)
            {
                Interlocked.Increment(ref CreateClientCalls);
                lock (CreateClientResults)
                {
                    return CreateClientResults.Count > 0
                        ? CreateClientResults.Dequeue()
                        : new BridgeCreateClientResult(StatusCodes.Success, "1");
                }
            }

            public void Dispose() { }
        }
    }
}
