using System;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// One row in the session manager's idempotency cache. Keyed externally by
    /// the caller-supplied <c>idempotencyKey</c>; the entry remembers the
    /// payload fingerprint so a replay with the SAME key but a DIFFERENT
    /// payload trips the 9102 (<c>IdempotencyConflict</c>) gate.
    ///
    /// <para>
    /// Cache state machine (per agent 07 §9):
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Miss</b> — execute, store, return.</description></item>
    ///   <item><description><b>Hit + same fingerprint</b> — return cached result, no wire call.</description></item>
    ///   <item><description><b>Hit + different fingerprint</b> — throw 9102.</description></item>
    /// </list>
    ///
    /// <para>
    /// <c>PayloadFingerprint</c> is the SHA-256 of a canonicalized payload
    /// (operation name + arguments). <c>Result</c> is the boxed return value
    /// from the original successful call — replayed verbatim on a cache hit.
    /// </para>
    /// </summary>
    public sealed record IdempotencyCacheEntry(
        string PayloadFingerprint,
        object? Result,
        DateTimeOffset CreatedAt);
}
