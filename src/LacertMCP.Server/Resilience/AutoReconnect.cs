using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using Microsoft.Extensions.Logging;

namespace LacertMCP.Server.Resilience
{
    /// <summary>
    /// Wraps a wire call in exponential-backoff retry for transient COM/RPC
    /// failures. Mirrors the QB MCP <c>manager.ts</c> <c>isTransientLiveError</c>
    /// gate per Todo.md task #76 + agent 07 §11.
    ///
    /// <para>
    /// Transient-HRESULT whitelist (NEVER retried on anything else):
    /// <list type="bullet">
    ///   <item><description><c>0x800706BA</c> — "The RPC server is unavailable".</description></item>
    ///   <item><description><c>0x80010108</c> — "The object invoked has disconnected from its clients".</description></item>
    ///   <item><description><c>0x8001010A</c> — "The message filter indicated that the application is busy".</description></item>
    ///   <item><description><c>0x80010001</c> — "Call was canceled by the message filter".</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Backoff schedule: <c>[250, 500, 1000]ms</c>, max 3 retries. Non-transient
    /// errors (including any <see cref="LacerteSDKError"/> with a TAPIStatus
    /// like <c>NotSignedIn</c> or <c>ConsentDenied</c>) propagate unchanged.
    /// </para>
    /// </summary>
    public static class AutoReconnect
    {
        // Backoff in ms — keep as a static readonly array so callers can read
        // the schedule (test asserts, doctor probes).
        public static readonly IReadOnlyList<int> BackoffMs = new[] { 250, 500, 1000 };

        public const int MaxRetries = 3;

        // HRESULT whitelist. The set is small + tight on purpose — anything not
        // on this list propagates without retry.
        public static readonly ISet<int> TransientHResults = new HashSet<int>
        {
            unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
            unchecked((int)0x80010108), // RPC_E_DISCONNECTED
            unchecked((int)0x8001010A), // RPC_E_SERVERCALL_RETRYLATER
            unchecked((int)0x80010001), // RPC_E_CALL_CANCELED
        };

        /// <summary>
        /// Run <paramref name="op"/>; on transient HRESULTs retry per
        /// <see cref="BackoffMs"/>. On final failure rethrow the LAST exception
        /// (the original SDK / COM error), with retry metadata attached to the
        /// <see cref="Exception.Data"/> bag for diagnostics.
        ///
        /// <para>
        /// P18 — <paramref name="opName"/> is the name of the wrapped wire
        /// operation (e.g. <c>"WriteDetail"</c>, <c>"CreateClient"</c>) and
        /// flows into the AutoReconnect.retry / AutoReconnect.exhausted
        /// WireLog records so a forensic grep can distinguish a retried
        /// read (safe to ignore) from a retried append-only write
        /// (DECISIONS.md treats this as REQUIRES post-call verification —
        /// see the AutoReconnect-unsafe-for-append-only entry). Optional
        /// for backward-compat with existing test fixtures.
        /// </para>
        /// </summary>
        public static async Task<T> WithRetry<T>(Func<Task<T>> op, ILogger? log = null, string? opName = null)
        {
            if (op == null) throw new ArgumentNullException(nameof(op));

            Exception? last = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await op().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    last = ex;
                    if (attempt >= MaxRetries) break;
                    var delay = BackoffMs[attempt];
                    // P15-014 — unconditional WireLog emission so retries are
                    // visible in the same lacerte-YYYYMMDD.log stream as the
                    // wrapped op. Without this, a 2nd-attempt success is
                    // OBSERVATIONALLY INVISIBLE: the wrapped op produces ONE
                    // record showing elevated durationMs and the operator has
                    // no way to know the wire just retried. The ILogger seam
                    // stays for future DI plumbing but every existing call
                    // site passes null.
                    EmitRetryLog(attempt + 1, MaxRetries + 1, delay, ex, opName);
                    log?.LogWarning(
                        ex,
                        "Transient COM/RPC failure on attempt {Attempt}/{Max} ({Op}); retrying in {DelayMs}ms.",
                        attempt + 1, MaxRetries + 1, opName ?? "(unnamed)", delay);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                // No catch for non-transient — they propagate.
            }

            // Out of retries. Annotate + rethrow the original.
            if (last != null)
            {
                last.Data["AutoReconnect.Attempts"] = MaxRetries + 1;
                last.Data["AutoReconnect.Exhausted"] = true;
                if (opName != null) last.Data["AutoReconnect.OpName"] = opName;
                // P15-014 — exhausted-retries log. Pairs with the per-retry
                // lines so a forensic grep ("AutoReconnect.exhausted") finds
                // every wire that hit the cliff.
                EmitExhaustedLog(MaxRetries + 1, last, opName);
                throw last;
            }
            // Unreachable — IsTransient was always true, so one of the catches
            // ran; defensive fallback for the compiler.
            throw new InvalidOperationException("AutoReconnect.WithRetry exhausted with no captured exception.");
        }

        /// <summary>
        /// Synchronous variant — used by COM callsites that don't await.
        /// </summary>
        public static T WithRetrySync<T>(Func<T> op, ILogger? log = null, string? opName = null)
        {
            if (op == null) throw new ArgumentNullException(nameof(op));

            Exception? last = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return op();
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    last = ex;
                    if (attempt >= MaxRetries) break;
                    var delay = BackoffMs[attempt];
                    EmitRetryLog(attempt + 1, MaxRetries + 1, delay, ex, opName);
                    log?.LogWarning(
                        ex,
                        "Transient COM/RPC failure on attempt {Attempt}/{Max} ({Op}); retrying in {DelayMs}ms.",
                        attempt + 1, MaxRetries + 1, opName ?? "(unnamed)", delay);
                    System.Threading.Thread.Sleep(delay);
                }
            }

            if (last != null)
            {
                last.Data["AutoReconnect.Attempts"] = MaxRetries + 1;
                last.Data["AutoReconnect.Exhausted"] = true;
                if (opName != null) last.Data["AutoReconnect.OpName"] = opName;
                EmitExhaustedLog(MaxRetries + 1, last, opName);
                throw last;
            }
            throw new InvalidOperationException("AutoReconnect.WithRetrySync exhausted with no captured exception.");
        }

        // P15-014 — shared WireLog emitters for the retry + exhausted events.
        // P18 — opName surfaces in the record so an operator can distinguish a
        // retried read (safe to ignore) from a retried append-only write
        // (REQUIRES post-call verification; see DECISIONS.md entry).
        // Wrapped in try/catch so a logging failure (closed writer, transient
        // disk hiccup) never poisons the wire path itself; WireLog.RecordSync
        // is already null-safe on the disabled-log fast path.
        private static void EmitRetryLog(int attempt, int max, int delayMs, Exception ex, string? opName)
        {
            // P18-027 — bump the aggregate counter so lacerte_request_stats
            // can answer "are we flaking today?" without grepping the disk
            // log. Bumped per retry (NOT per call) so the count reflects
            // attempt-pressure, which is the operationally interesting signal.
            try { StatsRegistry.BumpAutoReconnectAttempt(); } catch { /* never poison */ }
            try
            {
                WireLog.RecordSync<bool>(
                    "AutoReconnect.retry",
                    new
                    {
                        opName,
                        attempt,
                        max,
                        hresult = ex.HResult.ToString("X8"),
                        exType = ex.GetType().Name,
                        delayMs,
                    },
                    () => true);
            }
            catch { /* logging is best-effort */ }
        }

        private static void EmitExhaustedLog(int attempts, Exception last, string? opName)
        {
            try { StatsRegistry.BumpAutoReconnectExhausted(); } catch { /* never poison */ }
            try
            {
                WireLog.RecordSync<bool>(
                    "AutoReconnect.exhausted",
                    new
                    {
                        opName,
                        attempts,
                        hresult = last.HResult.ToString("X8"),
                        exType = last.GetType().Name,
                    },
                    () => true);
            }
            catch { /* logging is best-effort */ }
        }

        /// <summary>
        /// True iff <paramref name="ex"/> is one of the whitelisted transient
        /// COM/RPC errors. Public for tests.
        /// </summary>
        public static bool IsTransient(Exception ex)
        {
            if (ex == null) return false;

            // LacerteSDKError carries a TAPIStatus — those are NEVER transient
            // (NotSignedIn, ConsentDenied, etc. need re-auth, not retry).
            if (ex is LacerteSDKError) return false;

            // COMException carries an HRESULT directly.
            if (ex is COMException comEx)
            {
                return TransientHResults.Contains(comEx.ErrorCode);
            }

            // ExternalException covers some marshaled COM errors that didn't
            // come through as COMException (e.g. SEH-converted on x64).
            if (ex is ExternalException extEx)
            {
                return TransientHResults.Contains(extEx.ErrorCode);
            }

            // Catch HResult on the base Exception too — some COM interop paths
            // populate it without re-typing the exception.
            if (TransientHResults.Contains(ex.HResult))
            {
                return true;
            }

            return false;
        }
    }
}
