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
        [Description("Return a snapshot of the active session — mode, registration state, 24h expiry, active context (datapath/year/module/readOnly). Zero wire I/O.")]
        public Task<string> LacerteSessionStatus()
            => WrapToolHandler.Run(() =>
            {
                var s = _session.SessionStatus();
                return Task.FromResult<object>(new
                {
                    mode = s.Mode.ToString(),
                    isRegistered = s.IsRegistered,
                    registeredAt = s.RegisteredAt,
                    expiresAt = s.ExpiresAt,
                    minutesUntilExpiry = s.MinutesUntilExpiry,
                    context = s.Context == null ? null : new
                    {
                        dataPath = s.Context.DataPath,
                        year = s.Context.Year,
                        module = s.Context.Module.ToString(),
                        readOnly = s.Context.ReadOnly,
                    },
                    appName = s.AppName,
                    appId = s.AppId,
                });
            });

        [McpServerTool]
        [Description("Set the active (dataPath, taxYear, module) triple used by every subsequent read/write tool. Validates year >= 2018 and module in I/C/P/S/F/R/T/N/B. Pass readOnly=true to lock the session against any write tool.")]
        public Task<string> LacerteSetContext(
            [Description("Absolute path to a Lacerte data folder (e.g. \"C:\\\\Lacerte\\\\24Tax\\\\IData\").")] string dataPath,
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
                return Task.FromResult<object>(new
                {
                    dataPath = ctx.DataPath,
                    year = ctx.Year,
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
                return Task.FromResult<object>(new { count = rows.Count, modules = rows });
            });

        [McpServerTool]
        [Description("Discover Lacerte data paths under the install root by scanning for {YY}Tax\\{Module}Data folders. Root is LACERTE_DATA_ROOT env var, falling back to C:\\Lacerte. Returns {path, year, module} entries.")]
        public Task<string> LacerteListDatapaths()
            => WrapToolHandler.Run(() =>
            {
                var rootEnv = Environment.GetEnvironmentVariable("LACERTE_DATA_ROOT");
                var root = string.IsNullOrEmpty(rootEnv) ? LacerteInstallRootFallback : rootEnv!;
                var entries = DiscoverDatapaths(root);
                return Task.FromResult<object>(new
                {
                    root,
                    rootExists = Directory.Exists(root),
                    count = entries.Count,
                    datapaths = entries,
                });
            });

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

                    result.Add(new
                    {
                        path = modDir,
                        year,
                        module = moduleChar.ToString(),
                        moduleName = moduleInfo.Name,
                    });
                }
            }

            return result;
        }
    }
}
