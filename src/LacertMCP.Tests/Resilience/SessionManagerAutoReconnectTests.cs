using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Resilience
{
    /// <summary>
    /// F12 — proves the session-manager wraps bridge calls in
    /// <see cref="LacertMCP.Server.Resilience.AutoReconnect.WithRetry"/>. A
    /// fake bridge throws a transient COMException on the first invocation
    /// and succeeds on the second; the chokepoint call must round-trip
    /// successfully even though the bridge would have failed without the
    /// retry wrapper.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionManagerAutoReconnectTests
    {
        private const char WritableModule = 'I';
        private const int WritableTable = 1;
        private const string WritableField = "C1_15";

        private static readonly object _seedLock = new object();

        private static void EnsureWritableFieldSeeded()
        {
            lock (_seedLock)
            {
                var t = typeof(FieldAccessMapLoader);
                var cacheField = t.GetField("_moduleCache", BindingFlags.NonPublic | BindingFlags.Static);
                var cache = cacheField?.GetValue(null) as ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>>;
                if (cache == null) return;

                var key = $"{WritableModule}|{WritableTable}|{WritableField}";
                if (cache.TryGetValue(WritableModule, out var existing) && existing.ContainsKey(key)) return;

                var seed = new Dictionary<string, FieldEntry>();
                if (existing != null)
                {
                    foreach (var kv in existing) seed[kv.Key] = kv.Value;
                }
                seed[key] = new FieldEntry(FieldAccess.ReadWrite, Source: "autoreconnect-seed");
                cache[WritableModule] = seed;
            }
        }

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

        [Fact]
        public async Task WriteField_TransientThenSuccess_RoundTripsViaAutoReconnect()
        {
            EnsureWritableFieldSeeded();
            // Fail-once-then-succeed UpdateTaxReturn. CanUseNewApis is a
            // separate call and gets its own retry budget — return true on
            // first try so the TY24 branch picks UpdateTaxReturn.
            var fake = new FailOnceBridge
            {
                CanUseNewApisResult = true,
                FailHResult = unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
            };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, WritableModule);

            var result = await mgr.WriteField(
                table: WritableTable,
                clientNumber: "JOHNS",
                field: WritableField,
                value: "after-retry");

            Assert.Equal(StatusCodes.Success, result);
            // The fake's UpdateTaxReturn must have been hit twice: once for
            // the transient throw, once for the successful retry.
            Assert.Equal(2, fake.UpdateTaxReturnCalls);
        }

        /// <summary>
        /// Bridge that throws a transient COMException on the FIRST
        /// UpdateTaxReturn call and succeeds on every subsequent call. Other
        /// methods are stubbed to satisfy the interface; they're not exercised
        /// by this test.
        /// </summary>
        private sealed class FailOnceBridge : ILacerteBridge
        {
            public bool CanUseNewApisResult { get; set; }
            public int UpdateTaxReturnCalls;
            public int FailHResult { get; set; }
            private bool _failedOnce;

            public int Register(string appName, string appId, int taxYear)
                => StatusCodes.AccessGranted;

            public bool CanUseNewApis(string appId, int year, char module)
                => CanUseNewApisResult;

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
                int? subClientID, string? stateAbbrev)
            {
                System.Threading.Interlocked.Increment(ref UpdateTaxReturnCalls);
                if (!_failedOnce)
                {
                    _failedOnce = true;
                    throw new COMException("RPC server unavailable (test)", FailHResult);
                }
                return StatusCodes.Success;
            }

            public int WriteDetail(string appId, string dataPath, int year, char module,
                string clientNumber, DetailRow row)
                => StatusCodes.Success;

            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module,
                string clientID, string taxType, string? firstName, string? lastName, string? emailID)
                => new BridgeCreateClientResult(StatusCodes.Success, "1");

            public void Dispose() { }
        }
    }
}
