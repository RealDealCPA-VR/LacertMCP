using System;
using System.Collections.Generic;
using System.Linq;

namespace LacertMCP.Server.Schema
{
    /// <summary>
    /// One entry in the Lacerte module catalog: a module-character (used in
    /// DBF filenames like <c>DATA1{Char}{YY}.DBF</c>) paired with the
    /// human-readable module name, the matching ODBC connection
    /// <c>ReturnType</c> string, the per-module access-map shard basename
    /// (e.g. <c>"individual"</c> → <c>Schema/FieldAccessMap/base/individual.json</c>),
    /// and the case-insensitive set of TaxType aliases the SDK accepts
    /// for <c>IClient(TaxType, ...)</c> on this module.
    ///
    /// Source: <c>Lacerte_MCP_Structure_Spec.md</c> §2 "Modules" table.
    /// </summary>
    public sealed record ModuleInfo(
        char Char,
        string Name,
        string ReturnType,
        string FileBasename,
        IReadOnlyList<string> TaxTypeAliases);

    /// <summary>
    /// Static catalog of the 9 Lacerte tax modules — Individual, Corporate,
    /// Partnership, S-Corporation, Fiduciary, Exempt, Estate, Gift, Benefit.
    ///
    /// Loaded directly from <c>Lacerte_MCP_Structure_Spec.md</c> §2; the
    /// <c>Char</c> column appears verbatim in DBF filenames and the
    /// <c>ReturnType</c> column is the literal value that goes into the ODBC
    /// connection string's <c>ReturnType=</c> parameter (per agent 03 §2).
    ///
    /// The <c>FileBasename</c> column is the single source of truth for the
    /// per-module access-map shard filename — formerly duplicated in three
    /// places (FieldAccessMapLoader, MetadataTools, FieldAccessCommand).
    /// The <c>TaxTypeAliases</c> column is the single source of truth for
    /// the per-module SDK TaxType accept-list used by CreateClient — formerly
    /// duplicated between SimulationStore and CreateClientTools.
    /// </summary>
    public static class Modules
    {
        public static readonly IReadOnlyList<ModuleInfo> All = new[]
        {
            new ModuleInfo('I', "Individual",    "Individual", "individual",  new[] { "Individual", "1040" }),
            new ModuleInfo('C', "Corporate",     "Corporate",  "corporate",   new[] { "Corporate", "1120" }),
            new ModuleInfo('P', "Partnership",   "Partnership","partnership", new[] { "Partnership", "1065" }),
            new ModuleInfo('S', "S-Corporation", "SCorporate", "scorp",       new[] { "SCorporate", "1120S", "S-Corporation", "SCorp" }),
            new ModuleInfo('F', "Fiduciary",     "Fiduciary",  "fiduciary",   new[] { "Fiduciary", "1041" }),
            new ModuleInfo('R', "Exempt",        "Exempt",     "exempt",      new[] { "Exempt", "990" }),
            new ModuleInfo('T', "Estate",        "Estate",     "estate",      new[] { "Estate", "706" }),
            new ModuleInfo('N', "Gift",          "Gift",       "gift",        new[] { "Gift", "709" }),
            new ModuleInfo('B', "Benefit",       "Benefit",    "benefit",     new[] { "Benefit", "5500" }),
        };

        /// <summary>Lookup a module by its single-character DBF code (case-sensitive).</summary>
        public static ModuleInfo? GetByChar(char c)
        {
            foreach (var m in All)
            {
                if (m.Char == c) return m;
            }
            return null;
        }

        /// <summary>Lookup a module by its ODBC <c>ReturnType</c> string (case-sensitive).</summary>
        public static ModuleInfo? GetByReturnType(string rt)
        {
            if (rt == null) return null;
            foreach (var m in All)
            {
                if (m.ReturnType == rt) return m;
            }
            return null;
        }

        /// <summary>
        /// Return the per-module access-map shard basename for the given
        /// module character (e.g. <c>'I'</c> → <c>"individual"</c>), or
        /// <c>null</c> if the character is not a recognized module.
        /// Single source of truth for the file-basename mapping used by
        /// <see cref="FieldAccessMapLoader"/> and the CLI / metadata tools.
        /// </summary>
        public static string? FileBasenameFor(char c)
        {
            return GetByChar(c)?.FileBasename;
        }

        /// <summary>
        /// True when <paramref name="taxType"/> is one of the SDK TaxType
        /// aliases accepted for the given module (case-insensitive). Used
        /// by both the simulator (<c>SimulationStore.ModuleAcceptsTaxType</c>)
        /// and the live-side pre-flight (<c>CreateClientTools</c>) so the
        /// two stay observationally identical per the two-mode discipline.
        /// Returns <c>false</c> for unknown modules or null/empty TaxTypes.
        /// </summary>
        public static bool AcceptsTaxType(char c, string? taxType)
        {
            if (string.IsNullOrEmpty(taxType)) return false;
            var info = GetByChar(c);
            if (info == null) return false;
            foreach (var a in info.TaxTypeAliases)
            {
                if (string.Equals(a, taxType, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Return the accepted TaxType alias list for the given module, or
        /// <c>null</c> if the character is not a recognized module. Exposed
        /// so callers can include the accepted set in error context.
        /// </summary>
        public static IReadOnlyList<string>? TaxTypeAliasesFor(char c)
        {
            return GetByChar(c)?.TaxTypeAliases;
        }

        /// <summary>
        /// P15-059 — Normalize an operator-supplied <paramref name="taxType"/>
        /// (which may be a colloquial form-number alias like <c>"1040"</c>,
        /// <c>"1120S"</c>, <c>"S-Corporation"</c>) to the canonical
        /// SDK-accepted <see cref="ModuleInfo.ReturnType"/> string
        /// (<c>"Individual"</c>, <c>"SCorporate"</c>, ...).
        ///
        /// <para>
        /// Why this matters — the spec (<c>Lacerte_MCP_Structure_Spec.md</c> §2)
        /// lists only 9 canonical <c>ReturnType</c> strings as valid for the
        /// ODBC connection string AND for the live SDK's
        /// <c>IClient(TaxType, ...)</c> constructor. The colloquial form-number
        /// aliases are useful operator ergonomics but the SDK will reject
        /// them with TaxModuleMismatch (14). Callers should canonicalize at
        /// the tool boundary BEFORE handing the value to the bridge or the
        /// simulator, so sim and live stay observationally identical per the
        /// two-mode discipline.
        /// </para>
        ///
        /// <para>
        /// Returns <c>null</c> for unknown modules or when
        /// <paramref name="taxType"/> is not in the module's alias set
        /// (matching <see cref="AcceptsTaxType"/>'s contract). Returns the
        /// already-canonical string when the operator already supplied it.
        /// Case-insensitive match.
        /// </para>
        /// </summary>
        public static string? CanonicalizeTaxType(char c, string? taxType)
        {
            if (string.IsNullOrEmpty(taxType)) return null;
            var info = GetByChar(c);
            if (info == null) return null;
            foreach (var a in info.TaxTypeAliases)
            {
                if (string.Equals(a, taxType, StringComparison.OrdinalIgnoreCase)) return info.ReturnType;
            }
            return null;
        }
    }
}
