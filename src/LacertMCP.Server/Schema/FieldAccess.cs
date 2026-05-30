using System.Collections.Generic;

namespace LacertMCP.Server.Schema
{
    /// <summary>
    /// Per-field access classification, ported verbatim from
    /// <c>docs/research/agents/04-field-access-map.md</c> §1 (the TS
    /// <c>FieldAccess</c> union). The <c>Unknown</c> member makes the
    /// lookup function total — uncatalogued fields default-refuse on
    /// write unless the caller passes <c>force: true</c>.
    /// </summary>
    public enum FieldAccess
    {
        /// <summary>Safe to write.</summary>
        ReadWrite,
        /// <summary>Read-only (e.g. <c>C{t}_0</c> client number, computed fields).</summary>
        ReadOnly,
        /// <summary>Exists in the DBF but the SDK refuses writes (returns TAPIStatus 7 NotExposed).</summary>
        Hidden,
        /// <summary>Not surfaced by the SDK at all.</summary>
        NotExposed,
        /// <summary>TY21+ deprecation overlay — field was removed for this tax year.</summary>
        NotSupported,
        /// <summary>Not in the access map yet; lookup fallback.</summary>
        Unknown,
    }

    /// <summary>
    /// A single row in a per-module access-map JSON file.
    ///
    /// Ported from agent 04 §1 (TS <c>FieldEntry</c> interface). Defaults
    /// keep base/overlay JSON files lean: most fields only need
    /// <c>Access</c>; the optional metadata (<c>DataType</c>,
    /// <c>MaxLength</c>, <c>Description</c>) is populated during v3
    /// validator work.
    /// </summary>
    public sealed record FieldEntry(
        FieldAccess Access,
        string? DataType = null,
        int? MaxLength = null,
        string? Description = null,
        string Source = "spec",
        string AddedAt = "2026-05-30");

    /// <summary>
    /// A tax-year overlay file ("not supported in TY{n}" list). Only stores
    /// access deltas relative to the base map — typically promotions to
    /// <c>NotSupported</c> when Intuit drops a field for a given tax year.
    ///
    /// Lookup order in <see cref="FieldAccessMapLoader.GetFieldAccess"/>:
    /// overlay first, then base. A TY22 overlay does NOT inherit from TY21
    /// (per agent 04 §6).
    /// </summary>
    public sealed record TaxYearOverlay(
        int TaxYear,
        IReadOnlyDictionary<string, OverlayEntry> Overrides);

    /// <summary>
    /// One row inside a tax-year overlay file. Only carries the fields that
    /// actually change per year — almost always <see cref="FieldAccess"/>
    /// flipped to <see cref="FieldAccess.NotSupported"/>.
    /// </summary>
    public sealed record OverlayEntry(FieldAccess Access, string Source);
}
