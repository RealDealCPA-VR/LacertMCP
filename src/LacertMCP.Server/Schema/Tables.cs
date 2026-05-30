using System.Collections.Generic;

namespace LacertMCP.Server.Schema
{
    /// <summary>
    /// One entry in the Lacerte DBF table catalog.
    ///
    /// <para>
    /// <c>Number</c> is the table number 1..8 (matches the digit in the DBF
    /// filename — e.g. <c>DATA1I18.DBF</c> is table 1). <c>DbfName</c> is the
    /// short identifier (DATA1..DATA8). <c>Contents</c> is the human-readable
    /// description from spec §2. <c>AccessNote</c> is the spec's
    /// "Access in MCP" column verbatim. <c>Reserved</c> flags DATA8 which
    /// the spec marks as "RESERVED" — no read or write surface.
    /// </para>
    /// </summary>
    public sealed record TableInfo(
        int Number,
        string DbfName,
        string Contents,
        string AccessNote,
        bool Reserved);

    /// <summary>
    /// Static catalog of the 8 DBF tables per module/year.
    ///
    /// Loaded directly from <c>Lacerte_MCP_Structure_Spec.md</c> §2
    /// "The 8 DBF tables (per module, per year)" table.
    /// </summary>
    public static class Tables
    {
        public static readonly IReadOnlyList<TableInfo> All = new[]
        {
            new TableInfo(1, "DATA1", "Client Info (contact/demographic)",
                "Read all; write per field map", false),
            new TableInfo(2, "DATA2", "Subclient Info (dependents, partners, shareholders…)",
                "Read/write; needs SubClientID", false),
            new TableInfo(3, "DATA3", "State Client Info",
                "Read/write; needs StateAbbrev", false),
            new TableInfo(4, "DATA4", "State Subclient Info",
                "Read/write; needs SubClientID + StateAbbrev", false),
            new TableInfo(5, "DATA5", "Status Info (F4 status)",
                "Read only (non-global); some globals R/W", false),
            new TableInfo(6, "DATA6", "Display Info (client-list display)",
                "Read only", false),
            new TableInfo(7, "DATA7", "Form Info (forms in return)",
                "Read only", false),
            new TableInfo(8, "DATA8", "RESERVED",
                "n/a", true),
        };
    }
}
