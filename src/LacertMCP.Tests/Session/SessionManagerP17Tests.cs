using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// P17 — cluster E_session new-behavior coverage:
    /// <list type="bullet">
    ///   <item><description>P17-001 — live <c>WriteDetail</c> with <c>replaceIndex</c>
    ///   throws <see cref="SyntheticStatusCodes.LiveReplaceIndexUnsupported"/>
    ///   (9019) BEFORE the bridge wire-call. Sim mode honors
    ///   <c>replaceIndex</c> unchanged.</description></item>
    ///   <item><description>P17-002 — table-discriminator gates fire in
    ///   <b>both</b> modes before <c>AssertWritable</c>, kill the
    ///   sim/live divergence on missing <c>subClientId</c> /
    ///   <c>stateAbbrev</c>.</description></item>
    ///   <item><description>P17-004 — idempotency cache survives a "process
    ///   restart" when <c>LACERTE_IDEMPOTENCY_PERSIST=1</c> + custom
    ///   <c>LACERTE_IDEMPOTENCY_PERSIST_PATH</c>. Expired entries are
    ///   dropped on load; unknown discriminators are skipped, not
    ///   thrown.</description></item>
    ///   <item><description>P17-007 — HMAC-keyed fingerprint prefixes never
    ///   ship to the wire envelope unless <c>LACERTE_DEBUG_WIRE=1</c>
    ///   is enabled. Two different payloads still produce two distinct
    ///   prefixes within a single process when diagnostics are on (the
    ///   triage UX P15-074 wanted) — but those prefixes are NOT plain
    ///   SHA-256, so offline brute-force on a small-search-space PII
    ///   value can't recover the cached secret.</description></item>
    ///   <item><description>P17-011 — <c>CloneRow</c> emits camelCase wire
    ///   keys for the three SDK-meta aliases plus the
    ///   <c>ClientID</c>-family Fields aliases, while preserving FoxPro
    ///   DBF column codes (<c>C1_*</c>) verbatim.</description></item>
    /// </list>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionManagerP17Tests
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static LacerteSessionManager NewLiveManagerWithFake(
            ILacerteBridge bridge, IDictionary<string, string?>? env = null)
        {
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) },
                modifiers: null);
            Assert.NotNull(ctor);
            var mgr = (LacerteSessionManager)ctor!.Invoke(new object?[] { bridge, env });
            mgr.SetRegisteredForTest(true);
            return mgr;
        }

        // ===================================================================
        // P17-001 — live WriteDetail with replaceIndex throws 9019 BEFORE
        // the bridge wire-call. Mirrors the sim path's replace semantics by
        // failing-closed rather than silently appending.
        // ===================================================================

        [Fact]
        public async Task WriteDetail_Live_WithReplaceIndex_Throws9019_BeforeBridge()
        {
            var fake = new CountingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "test");
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("JOHNS", row, replaceIndex: 2));

            Assert.Equal(SyntheticStatusCodes.LiveReplaceIndexUnsupported, ex.StatusCode);
            Assert.Equal("WriteDetail", ex.Context["op"]);
            Assert.Equal("JOHNS", ex.Context["clientNumber"]);
            Assert.Equal(2, ex.Context["replaceIndex"]);
            // P17-001 — bridge must NOT have been called.
            Assert.Equal(0, fake.WriteDetailCalls);
        }

        [Fact]
        public async Task WriteDetail_Live_WithoutReplaceIndex_ReachesBridge_WithNullReplaceIndex()
        {
            var fake = new CountingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "test");
            var status = await mgr.WriteDetail("JOHNS", row);

            Assert.Equal(StatusCodes.Success, status);
            Assert.Equal(1, fake.WriteDetailCalls);
            Assert.Null(fake.LastReplaceIndex);
        }

        [Fact]
        public async Task WriteDetail_Live_DryRun_WithReplaceIndex_StillThrows9019()
        {
            // P17-001 — dryRun must not mask the divergence. An operator
            // who probes with dryRun=true expecting "replace works" should
            // see the 9019 before believing the live path supports it.
            var fake = new CountingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "test");
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("JOHNS", row, replaceIndex: 0, dryRun: true));

            Assert.Equal(SyntheticStatusCodes.LiveReplaceIndexUnsupported, ex.StatusCode);
            Assert.Equal(0, fake.WriteDetailCalls);
        }

        [Fact]
        public async Task WriteDetail_Sim_WithReplaceIndex_HonorsReplace()
        {
            // P17-001 — sim path is unchanged: replaceIndex still replaces.
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var r1 = new DetailRow("S", "", "1", "", "", "", "", "", "", 100.0, "first");
            var r2 = new DetailRow("S", "", "1", "", "", "", "", "", "", 200.0, "second");
            await mgr.WriteDetail("1001", r1);
            var beforeCount = (await mgr.ReadDetail("1001")).Count;

            // Replace the last row instead of appending.
            await mgr.WriteDetail("1001", r2, replaceIndex: beforeCount - 1);
            var rows = await mgr.ReadDetail("1001");
            Assert.Equal(beforeCount, rows.Count); // count unchanged
            Assert.Equal("second", rows[beforeCount - 1].Description);
        }

        // ===================================================================
        // P17-002 — discriminator gates fire BEFORE the live wire-call.
        // ===================================================================

        [Fact]
        public async Task WriteField_Live_Table2_NoSubClientId_ThrowsSubClientIDRequired_BeforeBridge()
        {
            var fake = new CountingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteField(2, "JOHNS", "C2_5", "x"));

            Assert.Equal(SyntheticStatusCodes.SubClientIDRequired, ex.StatusCode);
            Assert.Equal(2, ex.Context["table"]);
            Assert.Equal("WriteField", ex.Context["op"]);
            // P17-002 — bridge must NOT have been called.
            Assert.Equal(0, fake.UpdateTaxReturnCalls);
            Assert.Equal(0, fake.WriteFieldCalls);
        }

        [Fact]
        public async Task WriteField_Live_Table3_NoStateAbbrev_ThrowsStateAbbrevRequired_BeforeBridge()
        {
            var fake = new CountingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteField(3, "JOHNS", "C3_1", "x"));

            Assert.Equal(SyntheticStatusCodes.StateAbbrevRequired, ex.StatusCode);
            Assert.Equal(3, ex.Context["table"]);
            Assert.Equal("WriteField", ex.Context["op"]);
            Assert.Equal(0, fake.UpdateTaxReturnCalls);
        }

        [Fact]
        public async Task WriteField_Live_Table4_MissingSubClientId_FiresFirst()
        {
            // P17-002 — when BOTH subClientId AND stateAbbrev are missing
            // for table 4, the subClientId check fires first (matches the
            // sim ordering at ApplySimFieldWriteWithUndo line ~1559-1563).
            var fake = new CountingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteField(4, "JOHNS", "C4_1", "x"));

            Assert.Equal(SyntheticStatusCodes.SubClientIDRequired, ex.StatusCode);
            Assert.Equal(4, ex.Context["table"]);
        }

        // ===================================================================
        // P17-004 — idempotency cache persistence (opt-in)
        // ===================================================================

        [Fact]
        public async Task IdempotencyPersistence_OffByDefault_LegacyBehavior()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            Assert.False(mgr.IdempotencyPersistEnabled);
            Assert.Null(mgr.IdempotencyCachePath);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task IdempotencyPersistence_OptIn_RoundTripsAcrossProcessRestart()
        {
            // Use a per-test temp file so concurrent tests don't collide.
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-" + Guid.NewGuid().ToString("N"));
            var tmpPath = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = tmpPath,
                };

                // Process 1: write a row with idempotency key, persistence
                // writes the entry to disk.
                using (var mgr1 = new LacerteSessionManager(env))
                {
                    await mgr1.Register();
                    mgr1.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                    var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 100.0, "persist-test");
                    var status = await mgr1.WriteDetail("1001", row, idempotencyKey: "p17-004-roundtrip");
                    Assert.Equal(StatusCodes.Success, status);
                    Assert.True(File.Exists(tmpPath), "cache file must be written on Store");
                }

                // Process 2: fresh manager constructs, loads from disk.
                using (var mgr2 = new LacerteSessionManager(env))
                {
                    await mgr2.Register();
                    mgr2.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                    Assert.True(mgr2.IdempotencyCacheCount >= 1,
                        "loaded cache must contain at least the prior process's entry");
                    // Replay with same payload returns cached result, no new append.
                    var beforeRows = (await mgr2.ReadDetail("1001")).Count;
                    var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 100.0, "persist-test");
                    var replay = await mgr2.WriteDetail("1001", row, idempotencyKey: "p17-004-roundtrip");
                    Assert.Equal(StatusCodes.Success, replay);
                    var afterRows = (await mgr2.ReadDetail("1001")).Count;
                    Assert.Equal(beforeRows, afterRows); // no double-append
                }
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public async Task IdempotencyPersistence_CorruptFile_DoesNotCrashColdStart()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-test-" + Guid.NewGuid().ToString("N"));
            var tmpPath = Path.Combine(tmpDir, "idempotency-cache.json");
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(tmpPath, "{ this is not valid json"); // corrupt

                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = tmpPath,
                };
                using var mgr = new LacerteSessionManager(env);
                Assert.True(mgr.IdempotencyPersistEnabled);
                Assert.Equal(0, mgr.IdempotencyCacheCount);
                await Task.CompletedTask;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        // ===================================================================
        // P17-007 — HMAC-keyed fingerprint, prefix gated behind WireLog
        // ===================================================================

        [Fact]
        public async Task IdempotencyConflict_WireLogDisabled_DoesNotLeakFingerprintPrefix()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var r1 = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "first");
            var r2 = new DetailRow("Int", "", "1", "", "", "", "1099INT", "T", "", 250.0, "second");
            await mgr.WriteDetail("1001", r1, idempotencyKey: "p17-007-leak");

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() =>
                mgr.WriteDetail("1001", r2, idempotencyKey: "p17-007-leak"));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
            // Default ops posture: prefix keys MUST NOT ship.
            Assert.False(ex.Context.ContainsKey("cachedFingerprintPrefix"));
            Assert.False(ex.Context.ContainsKey("requestedFingerprintPrefix"));
            // Timestamps still surface (the actually-actionable triage signal).
            Assert.True(ex.Context.ContainsKey("existingEntryCreatedAt"));
            Assert.True(ex.Context.ContainsKey("existingEntryAgeMs"));
        }

        // ===================================================================
        // P17-011 — CloneRow camelCase wire keys
        // ===================================================================

        [Fact]
        public async Task ReadClient_RowKeys_AreCamelCase()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var row = await mgr.ReadClient("1001");
            Assert.NotNull(row);
            // Three SDK-meta aliases CloneRow injects:
            Assert.True(row!.ContainsKey("clientNumber"),
                "P17-011: row must expose camelCase clientNumber");
            // SubClientID and StateAbbrev are optional on DATA1 rows — only
            // assert they are NOT present in PascalCase if present at all.
            Assert.False(row.ContainsKey("ClientNumber"));
            Assert.False(row.ContainsKey("SubClientID"));
            Assert.False(row.ContainsKey("StateAbbrev"));
            // Fields-level ClientID alias normalized.
            Assert.True(row.ContainsKey("clientId"));
            Assert.False(row.ContainsKey("ClientID"));
            // FoxPro DBF column codes preserved verbatim.
            Assert.Contains(row.Keys, k => k.StartsWith("C1_", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ReadSubclients_RowKeys_AreCamelCase_IncludingSubClientId()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');

            var rows = await mgr.ReadSubclients("1001");
            Assert.NotEmpty(rows);
            var row = rows[0];
            Assert.True(row.ContainsKey("clientNumber"));
            Assert.True(row.ContainsKey("subClientId"),
                "P17-011: DATA2 row must expose camelCase subClientId");
            Assert.False(row.ContainsKey("SubClientID"));
        }

        // -----------------------------------------------------------------
        // Hand-rolled fake bridge — counts WriteDetail calls and records the
        // last replaceIndex it saw so a "did the gate fire before the wire"
        // assertion has structural evidence.
        // -----------------------------------------------------------------

        private sealed class CountingBridge : ILacerteBridge
        {
            public int WriteDetailCalls;
            public int? LastReplaceIndex;
            public int UpdateTaxReturnCalls;
            public int WriteFieldCalls;

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
            {
                Interlocked.Increment(ref WriteFieldCalls);
                return StatusCodes.Success;
            }

            public int UpdateTaxReturn(string appId, string dataPath, int year, char module,
                int table, string clientNumber, string field, object? value,
                int? subClientId, string? stateAbbrev)
            {
                Interlocked.Increment(ref UpdateTaxReturnCalls);
                return StatusCodes.Success;
            }

            public int WriteDetail(string appId, string dataPath, int year, char module,
                string clientNumber, DetailRow row, int? replaceIndex)
            {
                Interlocked.Increment(ref WriteDetailCalls);
                LastReplaceIndex = replaceIndex;
                return StatusCodes.Success;
            }

            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module,
                string clientId, string taxType, string? firstName, string? lastName, string? emailId)
                => new BridgeCreateClientResult(StatusCodes.Success, "1");

            public void Dispose() { }
        }
    }
}
