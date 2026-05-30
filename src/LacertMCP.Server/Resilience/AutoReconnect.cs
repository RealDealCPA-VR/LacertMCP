using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
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
        /// </summary>
        public static async Task<T> WithRetry<T>(Func<Task<T>> op, ILogger? log = null)
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
                    log?.LogWarning(
                        ex,
                        "Transient COM/RPC failure on attempt {Attempt}/{Max}; retrying in {DelayMs}ms.",
                        attempt + 1, MaxRetries + 1, delay);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                // No catch for non-transient — they propagate.
            }

            // Out of retries. Annotate + rethrow the original.
            if (last != null)
            {
                last.Data["AutoReconnect.Attempts"] = MaxRetries + 1;
                last.Data["AutoReconnect.Exhausted"] = true;
                throw last;
            }
            // Unreachable — IsTransient was always true, so one of the catches
            // ran; defensive fallback for the compiler.
            throw new InvalidOperationException("AutoReconnect.WithRetry exhausted with no captured exception.");
        }

        /// <summary>
        /// Synchronous variant — used by COM callsites that don't await.
        /// </summary>
        public static T WithRetrySync<T>(Func<T> op, ILogger? log = null)
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
                    log?.LogWarning(
                        ex,
                        "Transient COM/RPC failure on attempt {Attempt}/{Max}; retrying in {DelayMs}ms.",
                        attempt + 1, MaxRetries + 1, delay);
                    System.Threading.Thread.Sleep(delay);
                }
            }

            if (last != null)
            {
                last.Data["AutoReconnect.Attempts"] = MaxRetries + 1;
                last.Data["AutoReconnect.Exhausted"] = true;
                throw last;
            }
            throw new InvalidOperationException("AutoReconnect.WithRetrySync exhausted with no captured exception.");
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
