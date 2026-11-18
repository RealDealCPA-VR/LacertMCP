using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// FP-3 — <c>lacerte_field_dictionary</c>: the verified map from tax
    /// concepts (e.g. "1120 gross receipts", K-1 boxes) to Lacerte DATA1–4
    /// columns or detail Series/Code, read from
    /// <c>Schema/FieldDictionary/&lt;module&gt;.json</c>.
    ///
    /// <para>
    /// Read-only and mode-independent (pure schema metadata, no session or
    /// wire I/O), so simulation and live answer identically. Entries are only
    /// added after being observed on a real Lacerte install with
    /// <c>lacerte-mcp map-diff</c>; until then a module's dictionary is empty
    /// and the response says so explicitly — the model must not fill the gap
    /// by guessing field or detail codes, which would write wrong cells on a
    /// real return.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class FieldDictionaryTools
    {
        internal const string EmptyNote =
            "No verified mappings for this module yet. Mappings are added on the Lacerte PC with " +
            "`lacerte-mcp map-diff` (snapshot a test client, enter one known value on one Lacerte input screen, " +
            "snapshot again, record the change). Do not guess Lacerte field names (C{table}_{n}) or detail " +
            "Series/Code numbers — a guessed code writes the wrong cell on a real return. Ask the operator to " +
            "enter the value in Lacerte, or to record the mapping first.";

        internal const string NoMatchNote =
            "No verified mapping matches this search. Do not guess a field or detail code; ask the operator, " +
            "or have them record the mapping with `lacerte-mcp map-diff`.";

        [McpServerTool]
        [Description("Look up the verified field dictionary for a Lacerte module: which DATA1-4 column (kind=field: table + field, e.g. C1_37) or detail-file Series/Code (kind=detail) holds a tax concept such as '1120 gross receipts' or a K-1 box. Read-only, no session needed. Every entry was observed on a real Lacerte install (`verifiedOn`); `taxYears` (empty = all years) limits where it applies, `needsSubclient` / `needsState` say whether a write needs a subclient number / state. A field listed here with no literal access-map entry is classified by the SDK range rules (see lacerte_field_access_map). When the module has no entries the response carries a `note`: do NOT guess field or detail codes — mappings are added on the Lacerte PC with `lacerte-mcp map-diff`.")]
        public Task<string> LacerteFieldDictionary(
            [Description("Module: a single character I, C, P, S, F, R, T, N or B, or a return-type alias such as 1120, 1120S, 1065, 1041, Corporate, Partnership.")] string module,
            [Description("Optional case-insensitive text matched against each entry's id, concept, form, line and notes (e.g. \"gross receipts\", \"K-1\", \"1120\"). Omit to list every entry.")] string? search = null,
            [Description("Optional tax year (>= 2018): keep only entries verified for that year (entries without taxYears apply to every year).")] int? taxYear = null)
        {
            return WrapToolHandler.Run(() =>
            {
                var info = ResolveModule(module);
                if (taxYear.HasValue && taxYear.Value < 2018)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?> { ["taxYear"] = taxYear.Value, ["floor"] = 2018 });
                }

                var loaded = FieldDictionaryLoader.Load(info.Char);
                var all = loaded.Entries;
                var term = string.IsNullOrWhiteSpace(search) ? null : search!.Trim();

                var matches = all
                    .Where(e => e.AppliesToYear(taxYear))
                    .Where(e => term == null || Matches(e, term))
                    .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(e => (object)e.ToDictionary())
                    .ToList();

                var result = new Dictionary<string, object?>
                {
                    ["module"] = info.Char.ToString(),
                    ["moduleName"] = info.Name,
                    ["taxYear"] = taxYear,
                    ["search"] = term,
                    ["count"] = matches.Count,
                    ["totalEntries"] = all.Count,
                    ["entries"] = matches,
                };
                if (loaded.Error != null)
                {
                    // Fail-closed load: the file exists but was rejected, so it
                    // grants nothing. Surface why so the operator can fix it.
                    result["loadError"] = loaded.Error;
                    result["note"] = "The module's dictionary file was rejected and is treated as empty: " + EmptyNote;
                }
                else if (all.Count == 0)
                {
                    result["note"] = EmptyNote;
                }
                else if (matches.Count == 0)
                {
                    result["note"] = NoMatchNote;
                }
                return Task.FromResult<object>(result);
            });
        }

        private static bool Matches(FieldDictionaryEntry e, string term)
        {
            bool Has(string? s) => s != null && s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(e.Id) || Has(e.Concept) || Has(e.Form) || Has(e.Line) || Has(e.Notes);
        }

        private static ModuleInfo ResolveModule(string? module)
        {
            var raw = module?.Trim() ?? string.Empty;
            ModuleInfo? info = null;
            if (raw.Length == 1)
            {
                info = Modules.GetByChar(char.ToUpperInvariant(raw[0]));
            }
            else if (raw.Length > 1)
            {
                info = Modules.All.FirstOrDefault(m =>
                    string.Equals(m.Name, raw, StringComparison.OrdinalIgnoreCase)
                    || m.TaxTypeAliases.Any(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase)));
            }
            if (info == null)
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidArgument,
                    new Dictionary<string, object?>
                    {
                        ["argument"] = "module",
                        ["module"] = module,
                        ["validModules"] = string.Join(",", Modules.All.Select(m => m.Char)),
                        ["validAliases"] = string.Join(",", Modules.All.SelectMany(m => m.TaxTypeAliases)),
                    });
            }
            return info;
        }
    }
}
