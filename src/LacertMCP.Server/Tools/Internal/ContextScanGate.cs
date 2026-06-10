using System.Threading;

namespace LacertMCP.Server.Tools.Internal
{
    /// <summary>
    /// R6-09 — process-wide gate serializing the two scan tools that
    /// temporarily mutate the GLOBAL session context across awaits
    /// (<c>lacerte_firm_dashboard</c> and <c>lacerte_year_over_year</c>).
    /// Each holds this semaphore across its whole save-set-restore span so
    /// the two scans can never interleave each other's <c>SetContext</c>
    /// flips (which produced wrong-year/wrong-module reads and corrupted
    /// restores when both ran concurrently).
    ///
    /// <para>
    /// LIMITATION (documented, not solved here): the gate only serializes
    /// the tools that opt into it. A concurrent NON-scan tool call (e.g.
    /// <c>lacerte_field_write</c>, <c>lacerte_client_read</c>) still reads
    /// whatever tuple the in-flight scan last set — including the scan's
    /// <c>readOnly:true</c> flag, which fails a concurrent write with 9001
    /// FieldIsReadOnly. The real fix is session-manager-side read-at-context
    /// (*At) overloads that take (dataPath, taxYear, module) per call and
    /// never touch the active context; those do not exist yet and the
    /// session manager is owned by another cluster. When they land, both
    /// scan tools should switch to them and this gate can be deleted.
    /// </para>
    /// </summary>
    internal static class ContextScanGate
    {
        /// <summary>
        /// Single-slot semaphore. Acquire with <c>WaitAsync</c> before the
        /// context snapshot, release in a <c>finally</c> AFTER the restore.
        /// </summary>
        public static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    }
}
