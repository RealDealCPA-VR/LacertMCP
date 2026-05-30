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
        // Module char → friendly basename — mirrors the loader's internal map
        // so the metadata response can label the source shard.
        private static readonly IReadOnlyDictionary<char, string> _moduleFileName = new Dictionary<char, string>
        {
            { 'I', "individual" },
            { 'C', "corporate" },
            { 'P', "partnership" },
            { 'S', "scorp" },
            { 'F', "fiduciary" },
            { 'R', "exempt" },
            { 'T', "estate" },
            { 'N', "gift" },
            { 'B', "benefit" },
        };

        [McpServerTool]
        [Description("Return the resolved field access map for (module, table, year). Includes (a) the universal C{t}_0 -> ReadOnly rule, and (b) every cataloged field for the module/table from the loaded JSON shard. Useful before attempting a field write — the same gate runs server-side via AssertWritable.")]
        public Task<string> LacerteFieldAccessMap(
            [Description("Single-character module code: I/C/P/S/F/R/T/N/B.")] char module,
            [Description("DBF table number 1..7.")] int table,
            [Description("Tax year (>= 2018). Overlay deltas for the year are applied on top of the base map.")] int year)
        {
            return WrapToolHandler.Run(() =>
            {
                if (!_moduleFileName.ContainsKey(module))
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["module"] = module.ToString(),
                            ["validModules"] = string.Join(",", _moduleFileName.Keys),
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
                if (year < 2018)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?> { ["taxYear"] = year, ["floor"] = 2018 });
                }

                // Build the result map. We always include the universal
                // C{t}_0 -> ReadOnly entry so callers can rely on it being
                // present even when the shard is empty (the v1 shards are
                // mostly {} pending the introspection follow-up F5).
                var entries = new Dictionary<string, string>();
                var universal = "C" + table + "_0";
                entries[universal] = FieldAccess.ReadOnly.ToString();

                // Enumerate every cataloged field by walking a small canonical
                // probe set + the loader's resolved access for each. The
                // loader's per-shard internals aren't public, so we probe
                // GetFieldAccess for a documented field grid; that covers v1
                // surface (empty shards) and grows naturally as shards land.
                //
                // Probe grid: C{table}_0..C{table}_25 covers the standard
                // numeric-suffix space; add common letter-suffix variants used
                // in DATA3/4 group fields (e.g. C4_G3).
                for (int i = 0; i <= 25; i++)
                {
                    var f = "C" + table + "_" + i;
                    var access = FieldAccessMapLoader.GetFieldAccess(module, table, f, year);
                    // Skip Unknown (the empty shards return Unknown for every
                    // probe except the universal C{t}_0 rule) — surfacing all
                    // 26 Unknown rows would just be noise. Callers who want
                    // raw per-field probing have GetFieldAccess via the
                    // session manager.
                    if (access == FieldAccess.Unknown && f != universal) continue;
                    entries[f] = access.ToString();
                }

                // Also probe a few common group-suffix fields for DATA3/4 so
                // callers see at least one cataloged group entry in shards
                // that ship them; harmless when absent (Unknown skipped).
                foreach (var suffix in new[] { "_G1", "_G2", "_G3" })
                {
                    var f = "C" + table + suffix;
                    var access = FieldAccessMapLoader.GetFieldAccess(module, table, f, year);
                    if (access == FieldAccess.Unknown) continue;
                    entries[f] = access.ToString();
                }

                return Task.FromResult<object>(new
                {
                    module = module.ToString(),
                    moduleName = _moduleFileName[module],
                    table,
                    year,
                    count = entries.Count,
                    fields = entries,
                });
            });
        }

        [McpServerTool]
        [Description("Return the full Lacerte SDK return-code table — TAPIStatus 0..28 + TPSDKStatus 200..204 + synthetic 9xxx — with name, severity, user message, and remediation. Discoverable resource so the model can quote canonical error guidance.")]
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
                    });
                }

                return Task.FromResult<object>(new
                {
                    count = rows.Count,
                    codes = rows,
                });
            });
        }
    }
}
