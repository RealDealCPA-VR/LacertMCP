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
    /// Phase 8 tasks #60-61: discoverable metadata tools that mirror the
    /// MCP "resource" idea — they let the model introspect the schema
    /// catalog without making a wire call.
    ///
    /// <para>
    /// <c>lacerte_field_access_map(module, table, year)</c> returns the
    /// resolved <c>{ field: access }</c> map by enumerating the loaded
    /// JSON shard for the module + applying the universal <c>C{t}_0 → ReadOnly</c>
    /// rule. <c>lacerte_return_codes</c> dumps the full TAPIStatus +
    /// synthetic-code table so the model can quote remediation text back
    /// at the operator without guessing.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class MetadataTools
    {
        // Per-module file-basename + accepted-module-set both live on
        // Schema.Modules now (single source of truth).

        [McpServerTool]
        [Description("Return the resolved field access map for (module, table, taxYear). Includes (a) the universal C{t}_0 -> ReadOnly rule, and (b) every cataloged field for the module/table from the loaded JSON shard. Useful before attempting a field write — the same gate runs server-side via AssertWritable.")]
        public Task<string> LacerteFieldAccessMap(
            [Description("Single-character module code: one of I, C, P, S, F, R, T, N, B.")] string module,
            [Description("DBF table number 1..7.")] int table,
            [Description("Tax year (>= 2018). Overlay deltas for the year are applied on top of the base map.")] int taxYear)
        {
            return WrapToolHandler.Run(() =>
            {
                // P16-027 — module typed as string for JSON-binding parity
                // with SessionTools.SetContext / CanUseNewApis. Length-1
                // guard mirrors those tools so binding errors surface the
                // same way across the trio.
                if (string.IsNullOrEmpty(module) || module.Length != 1)
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["module"] = module,
                            ["reason"] = "module must be a single character (I/C/P/S/F/R/T/N/B).",
                        });
                }
                var moduleChar = module[0];
                var moduleInfo = Modules.GetByChar(moduleChar);
                if (moduleInfo == null)
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["module"] = module,
                            ["validModules"] = string.Join(",", Modules.All.Select(m => m.Char)),
                        });
                }
                if (table < 1 || table > 7)
                {
                    throw new LacerteSDKError(
                        StatusCodes.DBNotSupported,
                        new Dictionary<string, object?>
                        {
                            ["table"] = table,
                            ["validTables"] = "1..7",
                        });
                }
                if (taxYear < 2018)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?> { ["taxYear"] = taxYear, ["floor"] = 2018 });
                }

                // Build the result map. We always include the universal
                // C{t}_0 -> ReadOnly entry so callers can rely on it being
                // present even when the shard is empty (the v1 shards are
                // mostly {} pending the introspection follow-up F5).
                var entries = new Dictionary<string, string>();
                var universal = "C" + table + "_0";
                entries[universal] = FieldAccess.ReadOnly.ToString();

                // R7-24 — enumerate EVERY cataloged field (base shard + the
                // taxYear overlay) instead of probing a fixed C{t}_0..25 +
                // _G1..G3 grid. The grid could never surface real codes like
                // C1_71, and the BulkFieldUpdate / CsvIntake prompts treat a
                // field ABSENT from `fields` as the STOP signal. Access is
                // resolved through GetFieldAccess so overlay precedence and
                // the universal C{t}_0 rule apply exactly as AssertWritable
                // applies them.
                foreach (var f in FieldAccessMapLoader.GetCatalogedFields(moduleChar, table, taxYear))
                {
                    var access = FieldAccessMapLoader.GetFieldAccess(moduleChar, table, f, taxYear);
                    if (access == FieldAccess.Unknown && f != universal) continue;
                    entries[f] = access.ToString();
                }

                return Task.FromResult<object>(new
                {
                    module,
                    moduleName = moduleInfo.Name,
                    table,
                    // P16-069/P16-079 — canonical `taxYear` (replaces `year`)
                    // so the wire name matches the input parameter.
                    taxYear,
                    count = entries.Count,
                    fields = entries,
                });
            });
        }

        [McpServerTool]
        [Description("Return the full Lacerte SDK return-code table — TAPIStatus 0..28 + TPSDKStatus 200..204 + synthetic 9xxx — with name, severity, user message, and remediation. Discoverable resource so the model can quote canonical error guidance. Each row carries `origin` (TAPIStatus / TPSDKStatus / Synthetic — Lacerte's own SDK subdivision) and `originGroup` (sdk / synthetic — matches the wire-envelope `origin` field for direct filtering).")]
        public Task<string> LacerteReturnCodes()
        {
            return WrapToolHandler.Run(() =>
            {
                var rows = new List<object>();

                // SDK codes — drive from the StatusCodes.StatusTable keys so we
                // pick up every cataloged code automatically.
                foreach (var kv in StatusCodes.StatusTable.OrderBy(kv => kv.Key))
                {
                    var info = StatusCodes.GetStatusInfo(kv.Key);
                    rows.Add(new
                    {
                        code = kv.Key,
                        name = info.Name,
                        severity = info.Severity.ToString().ToLowerInvariant(),
                        userMessage = info.UserMessage,
                        remediation = info.Remediation,
                        origin = kv.Key >= 200 ? "TPSDKStatus" : "TAPIStatus",
                        // P16-053 — low-cardinality wire-envelope-matching
                        // `originGroup` (sdk / synthetic). LLMs can filter
                        // `originGroup == envelope.origin` to locate the
                        // catalog row for a structured error.
                        originGroup = kv.Key >= LacerteSDKError.SyntheticCodeThreshold ? "synthetic" : "sdk",
                    });
                }

                // Synthetic 9xxx codes — drive from the synthetic table.
                foreach (var kv in SyntheticStatusCodes.StatusTable.OrderBy(kv => kv.Key))
                {
                    rows.Add(new
                    {
                        code = kv.Key,
                        name = kv.Value.Name,
                        severity = kv.Value.Severity.ToString().ToLowerInvariant(),
                        userMessage = kv.Value.UserMessage,
                        remediation = kv.Value.Remediation,
                        origin = "Synthetic",
                        originGroup = "synthetic",
                    });
                }

                return Task.FromResult<object>(new
                {
                    count = rows.Count,
                    // P16-024 — canonical `rows` key for cross-tool consistency.
                    // `codes` retained as a v0.1 alias (removed in v0.2).
                    rows,
                    codes = rows,
                });
            });
        }
    }
}
