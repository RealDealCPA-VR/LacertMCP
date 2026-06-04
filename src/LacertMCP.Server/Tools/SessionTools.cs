using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Session-level read tools. None of these touch the SDK wire — they read
    /// in-memory state on the session manager, the static schema catalogs, or
    /// the local filesystem (for <c>lacerte_list_datapaths</c>). Per
    /// <c>Todo.md</c> Phase 5 task #41.
    /// </summary>
    [McpServerToolType]
    public sealed class SessionTools
    {
        /// <summary>
        /// Fallback Lacerte install root used by <c>lacerte_list_datapaths</c>
        /// when <c>LACERTE_DATA_ROOT</c> is not set. Matches the SDK default.
        /// </summary>
        public const string LacerteInstallRootFallback = @"C:\Lacerte";

        // YY folder pattern: 2-digit year followed by "Tax" (e.g. "20Tax", "24Tax").
        private static readonly Regex YearFolderRegex =
            new Regex(@"^(\d{2})Tax$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // YY-module data folder pattern: single module character followed by "Data" (e.g. "IData").
        private static readonly Regex ModuleFolderRegex =
            new Regex(@"^([ICPSFRTNB])Data$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly LacerteSessionManager _session;

        public SessionTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Return a snapshot of the active session — mode, registration state, 24h expiry, active context (datapath/taxYear/module/readOnly), and wire-log diagnostic state (debugWireEnabled + wireLogPath, for the 9201/9203 'inspect logs/...' remediation). Zero wire I/O.")]
        public Task<string> LacerteSessionStatus()
            => WrapToolHandler.Run(() =>
            {
                var s = _session.SessionStatus();
                // P16-083 (boundary rounding) — clamp the human-rendered
                // minutes-until-expiry to one decimal so chat summaries don't
                // splat 13 fractional digits (e.g. 1439.9333333333334).
                double? minutesUntilExpiry = s.MinutesUntilExpiry.HasValue
                    ? Math.Round(s.MinutesUntilExpiry.Value, 1, MidpointRounding.AwayFromZero)
                    : (double?)null;
                return Task.FromResult<object>(new
                {
                    // Lowercase mode ("simulation"/"live") aligned with
                    // lacerte_health + HeartbeatWriter so every surface emits
                    // the same casing for the same conceptual field.
                    mode = s.Mode.ToString().ToLowerInvariant(),
                    isRegistered = s.IsRegistered,
                    registeredAt = s.RegisteredAt,
                    expiresAt = s.ExpiresAt,
                    minutesUntilExpiry,
                    context = s.Context == null ? null : new
                    {
                        dataPath = s.Context.DataPath,
                        // P16-069/P16-079 — canonical wire name `taxYear`
                        // (replaces `year`). Aligns input parameter +
                        // response field across the tool surface.
                        taxYear = s.Context.Year,
                        module = s.Context.Module.ToString(),
                        readOnly = s.Context.ReadOnly,
                    },
                    appName = s.AppName,
                    appId = s.AppId,
                    // P16-066 — surface debug-log state so the operator hitting
                    // the 9201/9203 remediation ('inspect logs/lacerte-...log')
                    // can see whether logging is on AND where the log lives
                    // in one tool call. wireLogPath is always populated — when
                    // logging is off it carries the would-be path so the
                    // operator can enable LACERTE_DEBUG_WIRE=1, restart, and
                    // grep at the exact location.
                    debugWireEnabled = s.DebugWireEnabled,
                    wireLogPath = s.WireLogPath,
                });
            });

        [McpServerTool]
        [Description("Set the active (dataPath, taxYear, module) triple used by every subsequent read/write tool. Validates year >= 2018 and module in I/C/P/S/F/R/T/N/B. Pass readOnly=true to lock the session against any write tool.")]
        public Task<string> LacerteSetContext(
            // P16-051/P16-078 — example renders as `C:\Lacerte\24Tax\IData`
            // on the wire (single backslashes), matching the rest of the
            // codebase. The pre-P16 form `C:\\Lacerte\\24Tax\\IData` was a
            // C#-source escape leak that smaller LLMs would mimic verbatim
            // into the call payload, triggering InvalidDataPath 9013.
            [Description("Absolute path to a Lacerte data folder (e.g. C:\\Lacerte\\24Tax\\IData).")] string dataPath,
            [Description("Four-digit tax year. SDK floor is 2018.")] int taxYear,
            [Description("Single-character module code: one of I, C, P, S, F, R, T, N, B.")] string module,
            [Description("If true, every write tool short-circuits to 9001 (FieldIsReadOnly) before any side effect. Default false.")] bool readOnly = false)
            => WrapToolHandler.Run(() =>
            {
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

                _session.SetContext(dataPath, taxYear, module[0], readOnly);
                var ctx = _session.SessionStatus().Context!;
                // P16-069/P16-079 — response field is `taxYear` to match the
                // input parameter (was `year` pre-P16; renamed for input/
                // output symmetry).
                return Task.FromResult<object>(new
                {
                    dataPath = ctx.DataPath,
                    taxYear = ctx.Year,
                    module = ctx.Module.ToString(),
                    readOnly = ctx.ReadOnly,
                });
            });

        [McpServerTool]
        [Description("Return whether the (taxYear, module) pair supports the TY21+ UpdateTaxReturn API path. Cached per pair. Sim returns year >= 2021.")]
        public Task<string> LacerteCanUseNewApis(
            [Description("Four-digit tax year (>= 2018).")] int taxYear,
            [Description("Single-character module code: one of I, C, P, S, F, R, T, N, B.")] string module)
            => WrapToolHandler.Run(() =>
            {
                if (string.IsNullOrEmpty(module) || module.Length != 1)
                {
                    throw new LacerteSDKError(
                        StatusCodes.InValidClientName,
                        new Dictionary<string, object?>
                        {
                            ["module"] = module,
                            ["reason"] = "module must be a single character.",
                        });
                }
                var canUseNew = _session.CanUseNewApis(taxYear, module[0]);
                return Task.FromResult<object>(new
                {
                    taxYear,
                    module,
                    canUseNewApis = canUseNew,
                    apiPath = canUseNew ? "UpdateTaxReturn" : "WriteToDBFDatabase",
                });
            });

        [McpServerTool]
        [Description("Return the 9-module Lacerte catalog (I/C/P/S/F/R/T/N/B) with names and ODBC ReturnType strings. Static schema; no wire I/O.")]
        public Task<string> LacerteListModules()
            => WrapToolHandler.Run(() =>
            {
                var rows = Modules.All
                    .Select(m => new
                    {
                        module = m.Char.ToString(),
                        name = m.Name,
                        returnType = m.ReturnType,
                    })
                    .ToList();
                // P16-024 — canonical `rows` key for cross-tool consistency.
                // `modules` retained as a v0.1 alias (removed in v0.2).
                return Task.FromResult<object>(new { count = rows.Count, rows, modules = rows });
            });

        [McpServerTool]
        [Description("Discover Lacerte data paths under the install root by scanning for {YY}Tax\\{Module}Data folders. Root is LACERTE_DATA_ROOT env var, falling back to C:\\Lacerte. Returns {path, taxYear, module} entries plus a `hint` field when the scan returns zero rows so the LLM can guide the operator to the correct env var or layout.")]
        public Task<string> LacerteListDatapaths()
            => WrapToolHandler.Run(() =>
            {
                var rootEnv = Environment.GetEnvironmentVariable("LACERTE_DATA_ROOT");
                var root = string.IsNullOrEmpty(rootEnv) ? LacerteInstallRootFallback : rootEnv!;
                return Task.FromResult(BuildListDatapathsPayload(root));
            });

        /// <summary>
        /// P16-030 — pure helper that builds the LacerteListDatapaths response
        /// payload for a given root. Extracted so tests can exercise the hint
        /// branches without mutating <c>LACERTE_DATA_ROOT</c> (which is
        /// process-wide and would race against parallel test fixtures).
        ///
        /// <para>
        /// Payload shape:
        /// <list type="bullet">
        ///   <item><c>root</c>, <c>rootExists</c>, <c>count</c></item>
        ///   <item><c>rows</c> / <c>datapaths</c> — canonical key + v0.1 alias</item>
        ///   <item><c>hint</c> — guided remediation string, null on the happy
        ///     path (count > 0); differentiates root-missing vs root-empty so
        ///     the LLM can suggest the right next step.</item>
        /// </list>
        /// </para>
        /// </summary>
        public static object BuildListDatapathsPayload(string root)
        {
            var rootExists = Directory.Exists(root);
            var entries = DiscoverDatapaths(root);
            string? hint = null;
            if (!rootExists)
            {
                hint = "Discovery root '" + root + "' does not exist. If Lacerte is installed elsewhere, set the LACERTE_DATA_ROOT env var to that path and restart the MCP. Expected layout: {root}\\{YY}Tax\\{Module}Data (e.g. C:\\Lacerte\\24Tax\\IData for Individual TY24, 23Tax\\CData for Corporate TY23). Modules: I/C/P/S/F/R/T/N/B.";
            }
            else if (entries.Count == 0)
            {
                hint = "Root '" + root + "' exists but contains no {YY}Tax\\{Module}Data subfolders. Either Lacerte is installed at a different root (set LACERTE_DATA_ROOT and restart), or this box uses the alternate shared-module layout C:\\Lacerte\\{YY}data\\ (in that case pass the leaf to lacerte_set_context directly — the scanner only finds the per-module shape).";
            }
            // P16-024 — canonical `rows` key for cross-tool consistency.
            // `datapaths` retained as a v0.1 alias (removed in v0.2).
            return new
            {
                root,
                rootExists,
                count = entries.Count,
                rows = entries,
                datapaths = entries,
                hint,
            };
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        /// <summary>
        /// Walk <paramref name="root"/> for <c>{YY}Tax\{Module}Data</c> folders.
        /// Returns an empty list if the root doesn't exist (graceful, no throw —
        /// the caller often just wants to know the box has no installs).
        /// Public so the tests in <c>SessionToolsTests</c> can drive it directly
        /// with a temp tree (no real Lacerte install needed).
        /// </summary>
        public static List<object> DiscoverDatapaths(string root)
        {
            var result = new List<object>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            string[] yearDirs;
            try
            {
                yearDirs = Directory.GetDirectories(root);
            }
            catch
            {
                // Permission / IO error — return empty rather than poisoning the tool call.
                return result;
            }

            foreach (var yearDir in yearDirs)
            {
                var yName = Path.GetFileName(yearDir);
                var yMatch = YearFolderRegex.Match(yName);
                if (!yMatch.Success) continue;

                // "20" → 2020. Lacerte's 2-digit convention is 21st century.
                if (!int.TryParse(yMatch.Groups[1].Value, out var yy)) continue;
                var year = 2000 + yy;

                string[] modDirs;
                try
                {
                    modDirs = Directory.GetDirectories(yearDir);
                }
                catch
                {
                    continue;
                }

                foreach (var modDir in modDirs)
                {
                    var mName = Path.GetFileName(modDir);
                    var mMatch = ModuleFolderRegex.Match(mName);
                    if (!mMatch.Success) continue;

                    var moduleChar = char.ToUpperInvariant(mMatch.Groups[1].Value[0]);
                    var moduleInfo = Modules.GetByChar(moduleChar);
                    if (moduleInfo == null) continue;

                    // P16-069/P16-079 — canonical wire field name `taxYear`
                    // (was `year`) — uniform with the rest of the surface.
                    result.Add(new
                    {
                        path = modDir,
                        taxYear = year,
                        module = moduleChar.ToString(),
                        moduleName = moduleInfo.Name,
                    });
                }
            }

            return result;
        }
    }
}
