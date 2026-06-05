using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18 observability cluster — five tools on <see cref="ObservabilityTools"/>:
    /// lacerte_recent_log (P18-026), lacerte_request_stats (P18-027),
    /// lacerte_cache_state (P18-030), lacerte_resource_usage (P18-031),
    /// lacerte_active_locks (P18-032).
    ///
    /// <para>
    /// Tests run sim-only (no Windows / Lacerte SDK dependency) and reset
    /// the process-lifetime registries between facts to keep assertions
    /// hermetic.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class ObservabilityToolsTests
    {
        private static (ObservabilityTools tools, LacerteSessionManager mgr) NewFixture()
        {
            // Manager construction + auto-Register both push WireLog entries
            // into the always-on ring, so we reset AFTER Register() to keep
            // tests hermetic on the ring contents. Stats are likewise reset
            // after fixture setup so the per-tool counters are zero.
            var mgr = ToolTestHelpers.NewSimManager();
            WireLogRing.ResetForTests();
            StatsRegistry.ResetForTests();
            return (new ObservabilityTools(mgr), mgr);
        }

        // -----------------------------------------------------------------
        // P18-026 — lacerte_recent_log
        // -----------------------------------------------------------------

        [Fact]
        public async Task RecentLog_EmptyRing_ReturnsZeroEntries()
        {
            var (tools, _) = NewFixture();
            var root = ToolTestHelpers.Parse(await tools.LacerteRecentLog());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("entries").GetArrayLength());
            Assert.True(result.GetProperty("capacity").GetInt32() >= WireLogRing.MinCapacity);
            Assert.Equal(0, result.GetProperty("totalSeen").GetInt64());
            Assert.Equal(0, result.GetProperty("droppedSinceProcessStart").GetInt64());
        }

        [Fact]
        public async Task RecentLog_PicksUpRingPushesEvenWithDebugWireOff()
        {
            // Independence from LACERTE_DEBUG_WIRE is the load-bearing
            // promise of P18-026 — the ring works when the disk log is off.
            var (tools, _) = NewFixture();
            // WireLog.IsEnabled is false by default in tests (no env var).
            await WireLog.Record<bool>("test-op-A", new { taxYear = 2024 }, () => Task.FromResult(true));
            await WireLog.Record<bool>("test-op-B", new { taxYear = 2024 }, () => Task.FromResult(true));

            var root = ToolTestHelpers.Parse(await tools.LacerteRecentLog(limit: 50));
            var result = root.GetProperty("result");
            var entries = result.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            // Newest-first: index 0 is test-op-B.
            Assert.Equal("test-op-B", entries[0].GetProperty("op").GetString());
            Assert.Equal("test-op-A", entries[1].GetProperty("op").GetString());
            Assert.Equal(2, result.GetProperty("totalSeen").GetInt64());
        }

        [Fact]
        public async Task RecentLog_CidFilter_ReturnsOnlyMatchingEntries()
        {
            var (tools, _) = NewFixture();
            // Push two entries under one cid, one under another.
            WireLog.CurrentCorrelationId.Value = "abc12345";
            try
            {
                await WireLog.Record<bool>("op-1", null, () => Task.FromResult(true));
                await WireLog.Record<bool>("op-2", null, () => Task.FromResult(true));
            }
            finally { WireLog.CurrentCorrelationId.Value = null; }
            WireLog.CurrentCorrelationId.Value = "deadbeef";
            try
            {
                await WireLog.Record<bool>("op-3", null, () => Task.FromResult(true));
            }
            finally { WireLog.CurrentCorrelationId.Value = null; }

            var root = ToolTestHelpers.Parse(await tools.LacerteRecentLog(cid: "abc12345"));
            var result = root.GetProperty("result");
            var entries = result.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            foreach (var entry in entries.EnumerateArray())
            {
                Assert.Equal("abc12345", entry.GetProperty("cid").GetString());
            }
        }

        [Fact]
        public async Task RecentLog_LimitCapsResult()
        {
            var (tools, _) = NewFixture();
            for (int i = 0; i < 10; i++)
            {
                await WireLog.Record<bool>($"op-{i}", null, () => Task.FromResult(true));
            }
            var root = ToolTestHelpers.Parse(await tools.LacerteRecentLog(limit: 3));
            var entries = root.GetProperty("result").GetProperty("entries");
            Assert.Equal(3, entries.GetArrayLength());
        }

        // -----------------------------------------------------------------
        // P18-027 — lacerte_request_stats
        // -----------------------------------------------------------------

        [Fact]
        public async Task RequestStats_EmitsCanonicalShape()
        {
            var (tools, _) = NewFixture();
            var root = ToolTestHelpers.Parse(await tools.LacerteRequestStats());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.TryGetProperty("uptime", out var uptime));
            Assert.True(uptime.TryGetProperty("startedAt", out _));
            Assert.True(uptime.GetProperty("seconds").GetInt64() >= 0);
            Assert.True(result.TryGetProperty("totals", out var totals));
            Assert.True(totals.TryGetProperty("calls", out _));
            Assert.True(totals.TryGetProperty("errorRate", out _));
            Assert.True(result.TryGetProperty("perOp", out _));
            Assert.True(result.TryGetProperty("idempotency", out _));
            Assert.True(result.TryGetProperty("autoReconnect", out _));
        }

        [Fact]
        public async Task RequestStats_CallsIncrementTotals()
        {
            var (tools, _) = NewFixture();
            // Fire one tool call (LacerteCacheState is cheap + always succeeds).
            await tools.LacerteCacheState();
            await tools.LacerteCacheState();

            var root = ToolTestHelpers.Parse(await tools.LacerteRequestStats());
            var totals = root.GetProperty("result").GetProperty("totals");
            // At least 2 cache-state calls + the request-stats call itself.
            Assert.True(totals.GetProperty("calls").GetInt64() >= 2);
            Assert.True(totals.GetProperty("successes").GetInt64() >= 2);
        }

        // -----------------------------------------------------------------
        // P18-030 — lacerte_cache_state
        // -----------------------------------------------------------------

        [Fact]
        public async Task CacheState_EmptyCaches_ReturnsWellFormedEnvelope()
        {
            var (tools, _) = NewFixture();
            var root = ToolTestHelpers.Parse(await tools.LacerteCacheState());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            var idemp = result.GetProperty("idempotency");
            Assert.Equal(0, idemp.GetProperty("count").GetInt32());
            Assert.Equal(10000, idemp.GetProperty("cap").GetInt32());
            Assert.Equal(24, idemp.GetProperty("ttlHours").GetInt32());
            Assert.False(idemp.GetProperty("nearCap").GetBoolean());
            Assert.True(result.TryGetProperty("newApis", out _));
            Assert.True(result.TryGetProperty("edition", out _));
            Assert.True(result.TryGetProperty("snapshotAtUtc", out _));
        }

        [Fact]
        public async Task CacheState_SeededIdempotencyEntry_SurfacesCount()
        {
            var (tools, mgr) = NewFixture();
            // Seed an entry via the existing test seam.
            mgr.SeedIdempotencyEntryForTest("test-key-1", DateTimeOffset.UtcNow.AddMinutes(-5));
            mgr.SeedIdempotencyEntryForTest("test-key-2", DateTimeOffset.UtcNow.AddMinutes(-2));

            var root = ToolTestHelpers.Parse(await tools.LacerteCacheState());
            var idemp = root.GetProperty("result").GetProperty("idempotency");
            Assert.Equal(2, idemp.GetProperty("count").GetInt32());
            Assert.True(idemp.TryGetProperty("oldestCreatedAt", out var oldest));
            Assert.NotEqual(JsonValueKind.Null, oldest.ValueKind);
            Assert.True(idemp.TryGetProperty("newestCreatedAt", out var newest));
            Assert.NotEqual(JsonValueKind.Null, newest.ValueKind);
        }

        // -----------------------------------------------------------------
        // P18-031 — lacerte_resource_usage
        // -----------------------------------------------------------------

        [Fact]
        public async Task ResourceUsage_EmitsCanonicalShape()
        {
            var (tools, _) = NewFixture();
            var root = ToolTestHelpers.Parse(await tools.LacerteResourceUsage());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            // pid > 0 on any healthy process; -1 only if Process.Id throws.
            Assert.True(result.GetProperty("pid").GetInt32() > 0);
            Assert.True(result.GetProperty("workingSetMB").GetDouble() > 0);
            Assert.True(result.GetProperty("handleCount").GetInt32() > 0);
            Assert.True(result.GetProperty("threadCount").GetInt32() > 0);
            // GC counters: all three gens must be present (>= 0).
            var gc = result.GetProperty("gcCollections");
            Assert.True(gc.GetProperty("gen0").GetInt32() >= 0);
            Assert.True(gc.GetProperty("gen1").GetInt32() >= 0);
            Assert.True(gc.GetProperty("gen2").GetInt32() >= 0);
            Assert.True(result.TryGetProperty("is64BitProcess", out _));
            Assert.True(result.TryGetProperty("isServerGC", out _));
        }

        // -----------------------------------------------------------------
        // P18-032 — lacerte_active_locks
        // -----------------------------------------------------------------

        [Fact]
        public async Task ActiveLocks_NoActiveWrites_ReturnsEmptyList()
        {
            var (tools, _) = NewFixture();
            var root = ToolTestHelpers.Parse(await tools.LacerteActiveLocks());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("idempotency").GetArrayLength());
            Assert.Equal(0, result.GetProperty("heldCount").GetInt32());
            Assert.Equal(0, result.GetProperty("totalLockEntries").GetInt32());
        }

        [Fact]
        public async Task ActiveLocks_HasReturnedCap()
        {
            var (tools, _) = NewFixture();
            var root = ToolTestHelpers.Parse(await tools.LacerteActiveLocks(limit: 5));
            var result = root.GetProperty("result");
            Assert.Equal(5, result.GetProperty("returnedCap").GetInt32());
        }
    }
}
