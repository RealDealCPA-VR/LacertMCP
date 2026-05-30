using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// Phase 9 task #67 — verifies the session manager actually routes
    /// chokepoints through <see cref="ILacerteBridge"/> in live mode (not
    /// through the simulation store, which is null in live).
    ///
    /// <para>
    /// Uses a hand-written fake bridge (no Moq dependency) injected via the
    /// internal test-only ctor on <see cref="LacerteSessionManager"/>.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionManagerBridgeWireupTests
    {
        // -----------------------------------------------------------------
        // In-memory access-map seed. Mutates the loader's static cache via
        // reflection — avoids the on-disk race that AccessMapTestFixture's
        // file-write approach has with Stream B's parallel Tools tests.
        //
        // The session manager's WriteField calls FieldAccessMapLoader.AssertWritable
        // synchronously; we need at least one entry that resolves to ReadWrite
        // for the wire-up tests to reach the bridge route.
        // -----------------------------------------------------------------

        private const char WritableModule = 'I';
        private const int WritableTable = 1;
        private const string WritableField = "C1_15";

        private static readonly object _seedLock = new object();
        private static bool _seeded;

        private static void EnsureWritableFieldSeeded()
        {
            // Idempotent: one-shot seed across all tests in this class.
            // Mutating the loader's _moduleCache is process-wide, but since
            // we ONLY add (never remove) entries, races with Stream B's
            // tests are tolerable — their AccessMapTestFixture clears the
            // cache and re-reads from disk every test, so they're unaffected
            // by our additive entry.
            //
            // Actually safer: do the seed lazily per-test under a lock and
            // re-seed if the cache got cleared since last test.
            lock (_seedLock)
            {
                var t = typeof(FieldAccessMapLoader);
                var cacheField = t.GetField("_moduleCache", BindingFlags.NonPublic | BindingFlags.Static);
                var cache = cacheField?.GetValue(null) as ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>>;
                if (cache == null) return; // loader internals changed; tests will surface that elsewhere.

                if (cache.TryGetValue(WritableModule, out var existing) &&
                    existing.ContainsKey(BuildKey(WritableModule, WritableTable, WritableField)))
                {
                    _seeded = true;
                    return;
                }

                // Build a fresh map that has at minimum our writable entry
                // plus whatever was on disk for the module.
                var seed = new Dictionary<string, FieldEntry>();
                if (existing != null)
                {
                    foreach (var kv in existing) seed[kv.Key] = kv.Value;
                }
                seed[BuildKey(WritableModule, WritableTable, WritableField)] =
                    new FieldEntry(FieldAccess.ReadWrite, Source: "test-seed");
                cache[WritableModule] = seed;
                _seeded = true;
                _ = _seeded; // suppress unused-field warning; field intent is "have we seeded this process".
            }
        }

        private static string BuildKey(char module, int table, string field)
            => $"{module}|{table}|{field}";


        // -----------------------------------------------------------------
        // Internal ctor visible to tests via InternalsVisibleTo? No — the
        // ctor is `internal`, and Tests already references Server. The
        // internal ctor is visible only within the Server assembly, so we
        // call it via reflection here to keep Server's surface unchanged.
        // -----------------------------------------------------------------

        private static LacerteSessionManager NewLiveManagerWithFake(ILacerteBridge bridge)
        {
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) },
                modifiers: null);
            Assert.NotNull(ctor);
            return (LacerteSessionManager)ctor!.Invoke(new object?[] { bridge, null });
        }

        // -----------------------------------------------------------------
        // Bridge wire-up: CanUseNewApis routes to bridge in live mode
        // -----------------------------------------------------------------

        [Fact]
        public void Live_CanUseNewApis_RoutesToBridge_AndCaches()
        {
            var fake = new RecordingBridge();
            fake.CanUseNewApisResult = true;
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            Assert.True(mgr.CanUseNewApis(2024, 'I'));
            Assert.True(mgr.CanUseNewApis(2024, 'I')); // second call must hit cache, not bridge
            Assert.Equal(1, fake.CanUseNewApisCalls);
        }

        // -----------------------------------------------------------------
        // Bridge wire-up: WriteField routes (TY21+ → UpdateTaxReturn; pre-TY21 → WriteField)
        // -----------------------------------------------------------------

        [Fact]
        public async Task Live_WriteField_TY24_RoutesToUpdateTaxReturn()
        {
            // The access-map gate fires BEFORE the bridge route in WriteField.
            // Use the writable field the AccessMapTestFixture seeds (C1_15 on
            // I/DATA1) so the gate passes and we reach the bridge route.
            EnsureWritableFieldSeeded();
            var fake = new RecordingBridge { CanUseNewApisResult = true };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, WritableModule);

            var result = await mgr.WriteField(
                table: WritableTable,
                clientNumber: "JOHNS",
                field: WritableField,
                value: "test-value");

            Assert.Equal(StatusCodes.Success, result);
            Assert.Equal(0, fake.WriteFieldCalls);
            Assert.Equal(1, fake.UpdateTaxReturnCalls);
        }

        [Fact]
        public async Task Live_WriteField_PreTY21_ThrowsSimulationOnly_NotSilentlyMinusOne()
        {
            // P14-022/P14-031 — Pre-TY21 live writes need a ClientNumber → row
            // number lookup that is not yet wired (Phase 4 task #39 follow-up).
            // The chokepoint refuses the write with a structured SimulationOnly
            // error rather than silently fall through with rowNum=-1. This test
            // pins that contract — a regression that re-introduces the silent
            // -1 (or any partial bridge call) would fail here.
            EnsureWritableFieldSeeded();
            var fake = new RecordingBridge { CanUseNewApisResult = false };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\20data", 2020, WritableModule);

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteField(
                table: WritableTable,
                clientNumber: "JOHNS",
                field: WritableField,
                value: "test-value"));

            Assert.Equal(SyntheticStatusCodes.SimulationOnly, ex.StatusCode);
            Assert.Equal(0, fake.WriteFieldCalls);
            Assert.Equal(0, fake.UpdateTaxReturnCalls);
        }

        [Fact]
        public async Task Live_WriteField_DryRun_DoesNotCallBridge()
        {
            EnsureWritableFieldSeeded();
            var fake = new RecordingBridge { CanUseNewApisResult = true };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, WritableModule);

            var result = await mgr.WriteField(
                table: WritableTable,
                clientNumber: "JOHNS",
                field: WritableField,
                value: "test-value",
                dryRun: true);

            Assert.Equal(StatusCodes.Success, result);
            // dryRun short-circuits before the wire call — no bridge write.
            Assert.Equal(0, fake.WriteFieldCalls);
            Assert.Equal(0, fake.UpdateTaxReturnCalls);
        }

        // -----------------------------------------------------------------
        // Bridge wire-up: WriteDetail routes
        // -----------------------------------------------------------------

        [Fact]
        public async Task Live_WriteDetail_RoutesToBridge()
        {
            var fake = new RecordingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 100.0, "test");
            var result = await mgr.WriteDetail("JOHNS", row);

            Assert.Equal(StatusCodes.Success, result);
            Assert.Equal(1, fake.WriteDetailCalls);
        }

        [Fact]
        public async Task Live_WriteDetail_DryRun_DoesNotCallBridge()
        {
            var fake = new RecordingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var row = new DetailRow("S", "", "1", "", "", "", "", "", "", 100.0, "test");
            var result = await mgr.WriteDetail("JOHNS", row, dryRun: true);

            Assert.Equal(StatusCodes.Success, result);
            Assert.Equal(0, fake.WriteDetailCalls);
        }

        // -----------------------------------------------------------------
        // Bridge wire-up: CreateClient routes
        // -----------------------------------------------------------------

        [Fact]
        public async Task Live_CreateClient_RoutesToBridge()
        {
            var fake = new RecordingBridge
            {
                CreateClientResult = new BridgeCreateClientResult(StatusCodes.Success, "999"),
            };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var args = new CreateClientArgs("NEW01", "Individual", "Jane", "Doe", "jane@example.com");
            var result = await mgr.CreateClient(args);

            Assert.Equal(StatusCodes.Success, result.Status);
            Assert.Equal("999", result.ClientNumber);
            Assert.Equal(1, fake.CreateClientCalls);
        }

        [Fact]
        public async Task Live_CreateClient_DryRun_ShortCircuits()
        {
            var fake = new RecordingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var args = new CreateClientArgs("NEW01", "Individual", "Jane", "Doe", "jane@example.com");
            var result = await mgr.CreateClient(args, dryRun: true);

            Assert.Equal(StatusCodes.Success, result.Status);
            Assert.Null(result.ClientNumber); // synthetic dry-run result
            Assert.Equal(0, fake.CreateClientCalls);
        }

        // -----------------------------------------------------------------
        // Bridge wire-up: read chokepoints still throw BridgeUnavailable in
        // live mode (ODBC path not yet wired — Phase 4 task #39).
        // -----------------------------------------------------------------

        [Fact]
        public async Task Live_ListClients_StillThrowsBridgeUnavailable()
        {
            var fake = new RecordingBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.ListClients());
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // Hand-rolled fake bridge — counts calls and returns canned answers.
        // -----------------------------------------------------------------

        private sealed class RecordingBridge : ILacerteBridge
        {
            public bool CanUseNewApisResult { get; set; }
            public int CanUseNewApisCalls;
            public int WriteFieldCalls;
            public int UpdateTaxReturnCalls;
            public int WriteDetailCalls;
            public int CreateClientCalls;
            public BridgeCreateClientResult CreateClientResult { get; set; }
                = new BridgeCreateClientResult(StatusCodes.Success, "1");

            public int Register(string appName, string appId, int taxYear)
                => StatusCodes.AccessGranted;

            public bool CanUseNewApis(string appId, int year, char module)
            {
                System.Threading.Interlocked.Increment(ref CanUseNewApisCalls);
                return CanUseNewApisResult;
            }

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
                System.Threading.Interlocked.Increment(ref WriteFieldCalls);
                return StatusCodes.Success;
            }

            public int UpdateTaxReturn(string appId, string dataPath, int year, char module,
                int table, string clientNumber, string field, object? value,
                int? subClientID, string? stateAbbrev)
            {
                System.Threading.Interlocked.Increment(ref UpdateTaxReturnCalls);
                return StatusCodes.Success;
            }

            public int WriteDetail(string appId, string dataPath, int year, char module,
                string clientNumber, DetailRow row)
            {
                System.Threading.Interlocked.Increment(ref WriteDetailCalls);
                return StatusCodes.Success;
            }

            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module,
                string clientID, string taxType, string? firstName, string? lastName, string? emailID)
            {
                System.Threading.Interlocked.Increment(ref CreateClientCalls);
                return CreateClientResult;
            }

            public void Dispose() { }
        }
    }
}
