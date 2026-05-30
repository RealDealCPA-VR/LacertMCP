namespace LacertMCP.Server.Session
{
    /// <summary>
    /// The triple <c>(datapath, year, module)</c> plus a read-only flag that
    /// every chokepoint method needs. Set via
    /// <see cref="LacerteSessionManager.SetContext"/> and read on every tool
    /// call entry.
    ///
    /// <para>
    /// <c>DataPath</c> is the absolute path to a Lacerte data folder (e.g.
    /// <c>"C:\\Lacerte\\24Tax\\IData"</c>). <c>Year</c> is the four-digit tax
    /// year (>= 2018 per the SDK floor). <c>Module</c> is the single-character
    /// module code from <c>Schema.Modules</c> (one of I/C/P/S/F/R/T/N/B).
    /// </para>
    ///
    /// <para>
    /// <c>ReadOnly</c> — when true, every write chokepoint short-circuits
    /// to a synthetic 9001 (<c>FieldIsReadOnly</c>) before touching any
    /// access map, idempotency cache, dry-run snapshot, or wire call. Per
    /// agent 07 §8 (QB MCP read-only gate pattern).
    /// </para>
    /// </summary>
    public sealed record SessionContext(
        string DataPath,
        int Year,
        char Module,
        bool ReadOnly = false);
}
