using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace LacertMCP.Server.Logging
{
    /// <summary>
    /// P18-026 — Bounded in-process ring buffer of wire-log lines, always-on
    /// (independent of <c>LACERTE_DEBUG_WIRE</c>) so the LLM can self-diagnose
    /// a just-failed call via <c>lacerte_recent_log</c> without first asking
    /// the operator to enable disk logging.
    ///
    /// <para>
    /// Lines are pushed in their <see cref="RedactionScope.Disk"/>-redacted
    /// JSON form (the same line that would land on disk when
    /// <c>LACERTE_DEBUG_WIRE=1</c>). The tool re-runs the lines through
    /// <see cref="RedactionScope.Wire"/> redaction before returning so any
    /// envelope-scope leaks are double-scrubbed.
    /// </para>
    ///
    /// <para>
    /// Capacity defaults to 256 entries (~50 KB at typical line length) and
    /// is overridable via <c>LACERTE_RECENT_LOG_CAPACITY</c> (clamped 16..4096
    /// so a runaway env value can't blow the budget or shrink to uselessness).
    /// </para>
    /// </summary>
    internal static class WireLogRing
    {
        public const int DefaultCapacity = 256;
        public const int MinCapacity = 16;
        public const int MaxCapacity = 4096;
        public const string CapacityEnvName = "LACERTE_RECENT_LOG_CAPACITY";

        private static readonly ConcurrentQueue<string> _ring = new ConcurrentQueue<string>();
        private static long _totalSeen;
        private static long _droppedSinceStart;
        private static readonly int _capacity = ResolveCapacity();

        /// <summary>
        /// Effective in-process capacity (clamped, resolved once at class
        /// init from <see cref="CapacityEnvName"/>).
        /// </summary>
        public static int Capacity => _capacity;

        /// <summary>
        /// Push a redacted JSON line into the ring. Never throws — a push
        /// failure must not poison the wire call site. Safe to call from
        /// any thread.
        /// </summary>
        public static void Push(string redactedLine)
        {
            if (string.IsNullOrEmpty(redactedLine)) return;
            try
            {
                _ring.Enqueue(redactedLine);
                Interlocked.Increment(ref _totalSeen);
                // Trim down to capacity — concurrent pushes may briefly
                // overshoot the cap; the loop drains until we're back
                // under. Each evicted entry bumps the drop counter so the
                // tool surface can report eviction pressure.
                while (_ring.Count > _capacity && _ring.TryDequeue(out _))
                {
                    Interlocked.Increment(ref _droppedSinceStart);
                }
            }
            catch
            {
                // Defensive — never propagate logging faults.
            }
        }

        /// <summary>
        /// Take a snapshot of the current ring contents plus the bookkeeping
        /// counters. Returns the lines in oldest-first order.
        /// </summary>
        public static RingSnapshot Snapshot()
        {
            // ConcurrentQueue.ToArray is documented as an atomic snapshot.
            var lines = _ring.ToArray();
            return new RingSnapshot(
                lines,
                Interlocked.Read(ref _totalSeen),
                Interlocked.Read(ref _droppedSinceStart),
                _capacity);
        }

        /// <summary>
        /// Test-only reset — clears the ring + counters so a regression test
        /// can start from a known-empty state.
        /// </summary>
        internal static void ResetForTests()
        {
            while (_ring.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _totalSeen, 0);
            Interlocked.Exchange(ref _droppedSinceStart, 0);
        }

        private static int ResolveCapacity()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(CapacityEnvName);
                if (!string.IsNullOrEmpty(raw)
                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    if (v < MinCapacity) return MinCapacity;
                    if (v > MaxCapacity) return MaxCapacity;
                    return v;
                }
            }
            catch
            {
                // Env access failure — fall through to default.
            }
            return DefaultCapacity;
        }

        /// <summary>
        /// Snapshot payload returned by <see cref="Snapshot"/>.
        /// </summary>
        public readonly struct RingSnapshot
        {
            public RingSnapshot(string[] lines, long totalSeen, long dropped, int capacity)
            {
                Lines = lines;
                TotalSeen = totalSeen;
                Dropped = dropped;
                Capacity = capacity;
            }

            public string[] Lines { get; }
            public long TotalSeen { get; }
            public long Dropped { get; }
            public int Capacity { get; }
        }
    }
}
