using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LacertMCP.Server.Diagnostics
{
    /// <summary>
    /// P18-027 — Process-lifetime aggregate counters for the
    /// <c>lacerte_request_stats</c> MCP tool.
    ///
    /// <para>
    /// All counters are <see cref="long"/> mutated with
    /// <see cref="Interlocked.Increment(ref long)"/> so the hot path
    /// (<c>WrapToolHandler.Run</c>, <c>AutoReconnect.EmitRetryLog</c>,
    /// idempotency Hit/Miss/Conflict sites) pays a single uncontended
    /// atomic per increment and never blocks.
    /// </para>
    ///
    /// <para>
    /// Per-op latency is captured in a fixed-size 256-slot ring of int
    /// milliseconds; percentile math (p50/p95/p99) runs on
    /// <see cref="Snapshot"/>, never on the hot path. A torn read of a
    /// ring slot is one stale int which doesn't move the percentile
    /// after the 256-sample sort.
    /// </para>
    ///
    /// <para>
    /// Process-lifetime semantics — no reset tool. The operator restarts
    /// the host to clear counters.
    /// </para>
    /// </summary>
    internal static class StatsRegistry
    {
        public static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;

        private static readonly ConcurrentDictionary<string, OpStats> _perOp =
            new ConcurrentDictionary<string, OpStats>(StringComparer.Ordinal);

        private static long _idempotencyHits;
        private static long _idempotencyMisses;
        private static long _idempotencyConflicts;
        private static long _autoReconnectAttemptsTotal;
        private static long _autoReconnectExhaustedTotal;

        /// <summary>
        /// Record a tool invocation result. Called from
        /// <c>WrapToolHandler.Run</c> on every entry. Never throws —
        /// catch the call site in defensive try/catch.
        /// </summary>
        public static void RecordOp(string opName, bool success, long latencyMs)
        {
            if (string.IsNullOrEmpty(opName)) opName = "unknown";
            var stats = _perOp.GetOrAdd(opName, _ => new OpStats());
            stats.Record(success, latencyMs);
        }

        public static void BumpIdempotencyHit() => Interlocked.Increment(ref _idempotencyHits);
        public static void BumpIdempotencyMiss() => Interlocked.Increment(ref _idempotencyMisses);
        public static void BumpIdempotencyConflict() => Interlocked.Increment(ref _idempotencyConflicts);

        public static void BumpAutoReconnectAttempt() => Interlocked.Increment(ref _autoReconnectAttemptsTotal);
        public static void BumpAutoReconnectExhausted() => Interlocked.Increment(ref _autoReconnectExhaustedTotal);

        /// <summary>
        /// Compose a snapshot envelope for the <c>lacerte_request_stats</c>
        /// tool. Percentile math runs here (off the hot path).
        /// </summary>
        public static object Snapshot()
        {
            var now = DateTimeOffset.UtcNow;
            long totalCalls = 0, totalSuccesses = 0, totalErrors = 0;
            var perOpList = new List<object>(_perOp.Count);
            // Snapshot the dictionary atomically so a concurrent insert
            // doesn't show up half-built.
            var snapshot = _perOp.ToArray();
            foreach (var kv in snapshot)
            {
                var s = kv.Value.Snapshot();
                totalCalls += s.Count;
                totalSuccesses += s.SuccessCount;
                totalErrors += s.ErrorCount;
                perOpList.Add(new
                {
                    op = kv.Key,
                    count = s.Count,
                    errorCount = s.ErrorCount,
                    p50ms = s.P50,
                    p95ms = s.P95,
                    p99ms = s.P99,
                });
            }
            // Sort by count desc so the noisiest tools rise to the top.
            perOpList.Sort((a, b) =>
            {
                long ca = (long)a.GetType().GetProperty("count")!.GetValue(a)!;
                long cb = (long)b.GetType().GetProperty("count")!.GetValue(b)!;
                return cb.CompareTo(ca);
            });

            double errorRate = totalCalls > 0
                ? (double)totalErrors / (double)totalCalls
                : 0.0;

            return new
            {
                uptime = new
                {
                    startedAt = StartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    seconds = (long)(now - StartedAtUtc).TotalSeconds,
                },
                totals = new
                {
                    calls = totalCalls,
                    successes = totalSuccesses,
                    errors = totalErrors,
                    errorRate,
                },
                perOp = perOpList,
                idempotency = new
                {
                    hits = Interlocked.Read(ref _idempotencyHits),
                    misses = Interlocked.Read(ref _idempotencyMisses),
                    conflicts = Interlocked.Read(ref _idempotencyConflicts),
                },
                autoReconnect = new
                {
                    totalAttempts = Interlocked.Read(ref _autoReconnectAttemptsTotal),
                    exhausted = Interlocked.Read(ref _autoReconnectExhaustedTotal),
                },
                snapshotAtUtc = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        /// <summary>
        /// Test-only — clear all counters so a regression test can start
        /// from a known-empty state without depending on process-lifetime
        /// memo from prior tests.
        /// </summary>
        internal static void ResetForTests()
        {
            _perOp.Clear();
            Interlocked.Exchange(ref _idempotencyHits, 0);
            Interlocked.Exchange(ref _idempotencyMisses, 0);
            Interlocked.Exchange(ref _idempotencyConflicts, 0);
            Interlocked.Exchange(ref _autoReconnectAttemptsTotal, 0);
            Interlocked.Exchange(ref _autoReconnectExhaustedTotal, 0);
        }

        /// <summary>
        /// Per-tool aggregate. Counters are Interlocked longs; the latency
        /// ring is 256 ints written via Interlocked.Increment on a head
        /// index modulo 256.
        /// </summary>
        private sealed class OpStats
        {
            private const int RingSize = 256;
            private long _count;
            private long _successCount;
            private long _errorCount;
            private int _head;
            private readonly int[] _latencyRing = new int[RingSize];

            public void Record(bool success, long latencyMs)
            {
                Interlocked.Increment(ref _count);
                if (success) Interlocked.Increment(ref _successCount);
                else Interlocked.Increment(ref _errorCount);

                // Clamp latency into int range — anything over 24 days is
                // an obvious overflow signal, not a real value.
                int clampedMs = latencyMs > int.MaxValue ? int.MaxValue
                    : latencyMs < 0 ? 0
                    : (int)latencyMs;
                int slot = (int)(((uint)Interlocked.Increment(ref _head) - 1) % RingSize);
                _latencyRing[slot] = clampedMs;
            }

            public OpSnapshot Snapshot()
            {
                long count = Interlocked.Read(ref _count);
                long successes = Interlocked.Read(ref _successCount);
                long errors = Interlocked.Read(ref _errorCount);

                // Copy the latency ring into a local buffer. Bounded
                // sample size = min(count, RingSize) so we don't dilute
                // percentiles with zero-init slots before the first
                // RingSize ops have landed.
                int sampleCount = (int)Math.Min(count, (long)RingSize);
                var sample = new int[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    sample[i] = _latencyRing[i];
                }
                Array.Sort(sample);
                return new OpSnapshot
                {
                    Count = count,
                    SuccessCount = successes,
                    ErrorCount = errors,
                    P50 = Percentile(sample, 0.50),
                    P95 = Percentile(sample, 0.95),
                    P99 = Percentile(sample, 0.99),
                };
            }

            private static int Percentile(int[] sortedSample, double p)
            {
                if (sortedSample.Length == 0) return 0;
                int idx = (int)Math.Ceiling(p * sortedSample.Length) - 1;
                if (idx < 0) idx = 0;
                if (idx >= sortedSample.Length) idx = sortedSample.Length - 1;
                return sortedSample[idx];
            }
        }

        internal struct OpSnapshot
        {
            public long Count;
            public long SuccessCount;
            public long ErrorCount;
            public int P50;
            public int P95;
            public int P99;
        }
    }
}
