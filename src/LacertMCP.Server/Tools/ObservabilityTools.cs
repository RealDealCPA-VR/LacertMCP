using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18 observability cluster — five LLM-facing diagnostic tools whose
    /// shared purpose is to let the operator-as-LLM-host workflow self-
    /// diagnose without requiring the operator to attach a debugger, grep
    /// the disk log, or restart the process to "see" state.
    ///
    /// <list type="bullet">
    ///   <item><description><c>lacerte_recent_log</c> (P18-026) — in-process WireLog tail, cid-filterable, always-on (~50 KB ring), independent of <c>LACERTE_DEBUG_WIRE</c>.</description></item>
    ///   <item><description><c>lacerte_request_stats</c> (P18-027) — uptime + per-op call/error/p50/p95/p99, idempotency hit-rate, AutoReconnect attempt/exhausted totals.</description></item>
    ///   <item><description><c>lacerte_cache_state</c> (P18-030) — idempotency / newApis / edition cache sizes + TTL + near-cap signal.</description></item>
    ///   <item><description><c>lacerte_resource_usage</c> (P18-031) — process memory + handle/thread count + GC stats. Survives bridge-unavailable.</description></item>
    ///   <item><description><c>lacerte_active_locks</c> (P18-032) — held idempotency semaphores so a 30-second hang turns into "key fa3e1c held with N seconds".</description></item>
    /// </list>
    ///
    /// <para>
    /// All five are pure-read, never take wire I/O, and are safe to call
    /// pre-Register — making them the LLM's natural reach for "is something
    /// up with the server?" before reaching for the wire-bound tools that
    /// would 9201 / 9006 / 9009 anyway.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class ObservabilityTools
    {
        private readonly LacerteSessionManager _session;

        public ObservabilityTools(LacerteSessionManager session) => _session = session;

        // -----------------------------------------------------------------
        // P18-026 — lacerte_recent_log
        // -----------------------------------------------------------------

        [McpServerTool]
        [Description("Return the in-process tail of WireLog records — the LLM's self-diagnostic surface for cid-correlated forensics on a just-failed call. Independent of LACERTE_DEBUG_WIRE (the disk log gate): the ring is always-on with a ~50KB cap (default 256 entries; override via LACERTE_RECENT_LOG_CAPACITY, clamped 16..4096). Filter by cid to recover the causal chain for a specific InternalServerError(9203). Returns { entries: [{ts, op, cid?, durationMs, error?}], totalSeen, droppedSinceProcessStart, capacity }.")]
        public Task<string> LacerteRecentLog(
            [Description("Optional 8-hex correlation ID from a prior error envelope's `cid` field. When provided, only entries matching this cid are returned.")] string? cid = null,
            [Description("Max entries to return (1..256). Default 50. Newest-first.")] int limit = 50)
            => WrapToolHandler.Run(() =>
            {
                if (limit < 1) limit = 1;
                if (limit > WireLogRing.MaxCapacity) limit = WireLogRing.MaxCapacity;

                var snap = WireLogRing.Snapshot();
                // The ring is oldest-first; for forensic triage the LLM
                // almost always wants newest-first ("what JUST happened?").
                // Build a filtered, projected, capped list in one pass.
                var entries = new List<JsonObject>();
                // Walk newest-to-oldest so we can stop as soon as we hit the limit.
                for (int i = snap.Lines.Length - 1; i >= 0 && entries.Count < limit; i--)
                {
                    JsonObject? projected = ProjectAndFilter(snap.Lines[i], cid);
                    if (projected != null) entries.Add(projected);
                }

                return Task.FromResult<object>(new
                {
                    entries,
                    totalSeen = snap.TotalSeen,
                    droppedSinceProcessStart = snap.Dropped,
                    capacity = snap.Capacity,
                    filteredByCid = cid,
                });
            });

        // Parse one ring line, optionally filter by cid, and project the
        // operationally useful keys onto a compact wire-redacted shape.
        // Returns null when the line doesn't match the cid filter or fails
        // to parse (never throws into the caller).
        private static JsonObject? ProjectAndFilter(string redactedLine, string? cidFilter)
        {
            try
            {
                // Re-redact at Wire scope before returning. The ring holds
                // Disk-scope-redacted JSON (broad keyset). Wire-scope is the
                // SUBSET of Disk-scope keys — re-running the narrower pass
                // over the broader output is a no-op for already-redacted
                // values (they stay "***"). Confirms the surface obeys the
                // wire-scope redaction policy even for entries pushed by
                // some hypothetical future caller that bypassed the
                // existing Disk-scope path.
                var rewrapped = WireLog.Redact(redactedLine, RedactionScope.Wire);
                var node = JsonNode.Parse(rewrapped);
                if (node is not JsonObject obj) return null;
                if (!string.IsNullOrEmpty(cidFilter))
                {
                    if (!obj.TryGetPropertyValue("cid", out var cidNode)
                        || cidNode is not JsonValue cidVal
                        || !cidVal.TryGetValue<string>(out var cidStr)
                        || !string.Equals(cidStr, cidFilter, StringComparison.Ordinal))
                    {
                        return null;
                    }
                }
                // Project to a compact envelope so a 5000-row result entry
                // in the ring doesn't bloat the response. SummarizeLargeResultArray
                // already ran on insert so `result` is at most {kind, count, sample}.
                var projected = new JsonObject();
                CopyIfPresent(obj, projected, "ts");
                CopyIfPresent(obj, projected, "op");
                CopyIfPresent(obj, projected, "cid");
                CopyIfPresent(obj, projected, "durationMs");
                CopyIfPresent(obj, projected, "error");
                return projected;
            }
            catch
            {
                return null;
            }
        }

        private static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
        {
            if (src.TryGetPropertyValue(key, out var node) && node != null)
            {
                // Re-parse via JSON text so the node is freshly rooted under
                // dst without disturbing src's tree.
                dst[key] = JsonNode.Parse(node.ToJsonString());
            }
        }

        // -----------------------------------------------------------------
        // P18-027 — lacerte_request_stats
        // -----------------------------------------------------------------

        [McpServerTool]
        [Description("Return process-lifetime aggregate counters — uptime, total tool calls / successes / errors, per-op count + p50/p95/p99 latency, idempotency cache hits/misses/conflicts, AutoReconnect attempts/exhausted totals. One-shot snapshot for 'how is the server doing today?' triage. Zero wire I/O. Counters reset only on process restart — by design, so the LLM can spot a slow degradation across an unattended day.")]
        public Task<string> LacerteRequestStats()
            => WrapToolHandler.Run(() => Task.FromResult<object>(StatsRegistry.Snapshot()));

        // -----------------------------------------------------------------
        // P18-030 — lacerte_cache_state
        // -----------------------------------------------------------------

        [McpServerTool]
        [Description("Return a snapshot of the three in-process caches the session manager owns — idempotency, new-APIs branch decisions, and the edition probe. Surfaces idempotency count/cap/TTL/nearCap/peakCount so the LLM can diagnose 'is a 9102 IdempotencyConflict happening near eviction pressure?', new-APIs per-(taxYear,module) memoized answers, and whether the edition probe has run AND whether it hit the P14-061 cached-failure sentinel. Safe to call pre-register (zero wire I/O).")]
        public Task<string> LacerteCacheState()
            => WrapToolHandler.Run(() =>
            {
                var s = _session.GetCacheSnapshot();
                return Task.FromResult<object>(new
                {
                    idempotency = new
                    {
                        count = s.IdempotencyCount,
                        cap = s.IdempotencyCap,
                        ttlHours = s.IdempotencyTtlHours,
                        nearCap = s.IdempotencyNearCap,
                        oldestCreatedAt = s.IdempotencyOldestCreatedAt,
                        newestCreatedAt = s.IdempotencyNewestCreatedAt,
                        peakCount = s.IdempotencyPeakCount,
                        persistEnabled = s.IdempotencyPersistEnabled,
                        persistPath = s.IdempotencyPersistPath,
                    },
                    newApis = new
                    {
                        // P16-079 — canonical wire names taxYear / module
                        // preserved on each entry.
                        entries = s.NewApisEntries.Select(e => new
                        {
                            taxYear = e.TaxYear,
                            module = e.Module,
                            value = e.Value,
                        }).ToArray(),
                        count = s.NewApisEntries.Count,
                    },
                    edition = new
                    {
                        probed = s.EditionProbed,
                        sentinelFailure = s.EditionSentinelFailure,
                        edition = s.Edition,
                        build = s.EditionBuild,
                        sdkVersion = s.EditionSdkVersion,
                    },
                    snapshotAtUtc = DateTimeOffset.UtcNow,
                });
            });

        // -----------------------------------------------------------------
        // P18-031 — lacerte_resource_usage
        // -----------------------------------------------------------------

        [McpServerTool]
        [Description("Return process telemetry — { pid, uptimeSeconds, workingSetMB, peakWorkingSetMB, privateMemoryMB, handleCount, threadCount, gcTotalMemoryMB, gcCollections:{gen0,gen1,gen2}, processorTimeMs, is64BitProcess, isServerGC }. Lets the LLM watch for a handle-leak or memory balloon across an unattended day. Pure in-process perf counters; no wire I/O, survives bridge-unavailable — the operator's most common 'is the MCP alive?' moment is the one this is built for.")]
        public Task<string> LacerteResourceUsage()
            => WrapToolHandler.Run(() =>
            {
                // Each call returns a fresh snapshot object whose properties
                // are populated on first access — no benefit to caching.
                using var proc = Process.GetCurrentProcess();
                long workingSet, peakWorkingSet, privateMem, handles;
                int threadCount;
                double processorTimeMs;
                DateTime startTime;
                int pid;
                try { pid = proc.Id; } catch { pid = -1; }
                try { workingSet = proc.WorkingSet64; } catch { workingSet = -1; }
                try { peakWorkingSet = proc.PeakWorkingSet64; } catch { peakWorkingSet = -1; }
                try { privateMem = proc.PrivateMemorySize64; } catch { privateMem = -1; }
                try { handles = proc.HandleCount; } catch { handles = -1; }
                try { threadCount = proc.Threads.Count; } catch { threadCount = -1; }
                try { processorTimeMs = proc.TotalProcessorTime.TotalMilliseconds; } catch { processorTimeMs = -1; }
                try { startTime = proc.StartTime.ToUniversalTime(); } catch { startTime = DateTime.UtcNow; }

                long gcTotal;
                try { gcTotal = GC.GetTotalMemory(forceFullCollection: false); } catch { gcTotal = -1; }

                int gen0, gen1, gen2;
                try { gen0 = GC.CollectionCount(0); } catch { gen0 = -1; }
                try { gen1 = GC.CollectionCount(1); } catch { gen1 = -1; }
                try { gen2 = GC.CollectionCount(2); } catch { gen2 = -1; }

                double uptimeSeconds;
                try { uptimeSeconds = (DateTime.UtcNow - startTime).TotalSeconds; }
                catch { uptimeSeconds = -1; }

                return Task.FromResult<object>(new
                {
                    pid,
                    uptimeSeconds,
                    workingSetMB = BytesToMb(workingSet),
                    peakWorkingSetMB = BytesToMb(peakWorkingSet),
                    privateMemoryMB = BytesToMb(privateMem),
                    handleCount = handles,
                    threadCount,
                    gcTotalMemoryMB = BytesToMb(gcTotal),
                    gcCollections = new { gen0, gen1, gen2 },
                    processorTimeMs,
                    is64BitProcess = Environment.Is64BitProcess,
                    isServerGC = System.Runtime.GCSettings.IsServerGC,
                });
            });

        private static double BytesToMb(long bytes)
        {
            if (bytes < 0) return -1;
            return Math.Round(bytes / 1024.0 / 1024.0, 2, MidpointRounding.AwayFromZero);
        }

        // -----------------------------------------------------------------
        // P18-032 — lacerte_active_locks
        // -----------------------------------------------------------------

        [McpServerTool]
        [Description("Snapshot of currently-held idempotency semaphores. Lists each held write-key (8-char SHA-256 prefix — operator-reproducible from the original key client-side), the seconds it has been held, and total held / total-tracked counts. Diagnostic surface for 'tool call hangs at 30+s — what is in flight?' Zero wire I/O; never blocks; safe to call even while a lock IS held (the introspection path never takes those locks).")]
        public Task<string> LacerteActiveLocks(
            [Description("Maximum held entries to include in the response (1..500). The `heldCount` field reports the full total regardless. Default 20.")] int limit = 20)
            => WrapToolHandler.Run(() =>
            {
                var s = _session.GetActiveLocks(cap: limit);
                return Task.FromResult<object>(new
                {
                    idempotency = s.Idempotency.Select(e => new
                    {
                        keyHashPrefix = e.KeyHashPrefix,
                        held = e.Held,
                        heldForSeconds = e.HeldForSeconds.HasValue
                            ? Math.Round(e.HeldForSeconds.Value, 3, MidpointRounding.AwayFromZero)
                            : (double?)null,
                    }).ToArray(),
                    totalLockEntries = s.TotalLockEntries,
                    heldCount = s.HeldCount,
                    returnedCap = s.ReturnedCap,
                    snapshotAtUtc = s.SnapshotAtUtc,
                });
            });
    }
}
