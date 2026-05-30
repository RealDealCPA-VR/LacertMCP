using System.Collections.Generic;
using System.Linq;

namespace LacertMCP.Server.Schema
{
    /// <summary>
    /// One entry in the Lacerte module catalog: a module-character (used in
    /// DBF filenames like <c>DATA1{Char}{YY}.DBF</c>) paired with the
    /// human-readable module name and the matching ODBC connection
    /// <c>ReturnType</c> string.
    ///
    /// Source: <c>Lacerte_MCP_Structure_Spec.md</c> §2 "Modules" table.
    /// </summary>
    public sealed record ModuleInfo(char Char, string Name, string ReturnType);

    /// <summary>
    /// Static catalog of the 9 Lacerte tax modules — Individual, Corporate,
    /// Partnership, S-Corporation, Fiduciary, Exempt, Estate, Gift, Benefit.
    ///
    /// Loaded directly from <c>Lacerte_MCP_Structure_Spec.md</c> §2; the
    /// <c>Char</c> column appears verbatim in DBF filenames and the
    /// <c>ReturnType</c> column is the literal value that goes into the ODBC
    /// connection string's <c>ReturnType=</c> parameter (per agent 03 §2).
    /// </summary>
    public static class Modules
    {
        public static readonly IReadOnlyList<ModuleInfo> All = new[]
        {
            new ModuleInfo('I', "Individual",    "Individual"),
            new ModuleInfo('C', "Corporate",     "Corporate"),
            new ModuleInfo('P', "Partnership",   "Partnership"),
            new ModuleInfo('S', "S-Corporation", "SCorporate"),
            new ModuleInfo('F', "Fiduciary",     "Fiduciary"),
            new ModuleInfo('R', "Exempt",        "Exempt"),
            new ModuleInfo('T', "Estate",        "Estate"),
            new ModuleInfo('N', "Gift",          "Gift"),
            new ModuleInfo('B', "Benefit",       "Benefit"),
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
    }
}
