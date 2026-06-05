using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-033 — `lacerte_firm_dashboard`. Single MCP call returning a per
    /// (taxYear, module) snapshot of client counts + EF-status histogram +
    /// latest StatusDate watermark, plus a firm-level rollup. Composes the
    /// existing session primitives (DiscoverDatapaths + ListClients +
    /// ReadEFStatus) behind one envelope so the operator's Monday-morning
    /// "how does the firm look" question is a single round-trip instead of
    /// 9 module switches and 30+ MCP calls.
    ///
    /// <para>
    /// Refined-design implementation note: the canonical refinedDesign
    /// proposed adding parameterized <c>ListClientsAt</c> / <c>ReadEFStatusAt</c>
    /// overloads to <see cref="LacerteSessionManager"/> so the dashboard
    /// could be pure-read with zero side effects on the operator's current
    /// <c>set_context</c>. Cluster ownership confines this PR to the four
    /// new tool files, so we use the snapshot+restore-Context pattern
    /// here. The behavior is observationally identical to the *At overloads
    /// — restore runs in a finally block so a thrown read can't strand the
    /// operator's context — but it DOES briefly hold the lock on
    /// <c>SetContext</c> across the per-tuple read. A future cross-cluster
    /// follow-up can land the *At overloads and drop the snapshot/restore
    /// shim with no wire-shape change.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class FirmDashboardTools
    {
        // Canonical 9-module set per Modules.All. Used as the default for the
        // modulesFilter parameter so "no filter" admits every module.
        public const string AllModuleChars = "ICPSFRTNB";

        // Canonical histogram buckets per the refinedDesign. Anything else
        // bucketizes into `other` so the LLM sees a complete count.
        private static readonly string[] CanonicalStatusBuckets =
            { "Accepted", "Rejected", "Pending", "Transmitted" };

        private readonly LacerteSessionManager _session;

        public FirmDashboardTools(LacerteSessionManager session) => _session = session;

        /// <summary>
        /// Test seam — when non-null, the dashboard's discovery uses this
        /// instead of reading <c>LACERTE_DATA_ROOT</c> from the process
        /// env. Production callers must leave this null (the
        /// <see cref="LacerteSessionManager.TodayYearProvider"/> pattern).
        /// Lets unit tests redirect discovery to a temp dir without
        /// mutating the process-wide env var (which races against any
        /// parallel test calling <c>SetContext</c> on a canonical
        /// dataPath under <c>C:\Lacerte</c>).
        /// </summary>
        internal static Func<string?>? RootOverrideForTests { get; set; }

        [McpServerTool]
        [Description("One-call Monday-morning firm snapshot — fans out across every (taxYear, module) under LACERTE_DATA_ROOT (fallback C:\\Lacerte) and returns per-tuple client counts + EF-status histogram + latest StatusDate watermark, plus a firm-level rollup. Replaces the ~30 round-trips required to gather the same picture via lacerte_list_datapaths -> N x lacerte_set_context -> N x lacerte_client_list -> N x lacerte_ef_status. Optional filters: yearFilter narrows to one tax year; modulesFilter narrows to a subset of module characters (default 'ICPSFRTNB' admits all 9); sinceUtc threads through to the EF read for the incremental-delta workflow. Per-tuple errors are surfaced on that row's `error` field — one bad datapath does not poison the aggregate. Pure-read: the operator's current set_context is snapshotted and restored even on exception.")]
        public Task<string> LacerteFirmDashboard(
            [Description("Optional integer tax year filter (e.g. 2024). When set, only tuples with this exact tax year are included; when omitted, every discovered year is scanned.")] int? yearFilter = null,
            [Description("Uppercase concatenation of module characters to include (e.g. 'IC' for Individual + Corporate). Default 'ICPSFRTNB' admits all 9 modules. Unknown characters are silently ignored.")] string? modulesFilter = null,
            [Description("Optional ISO-8601 timestamp (yyyy-MM-dd | yyyy-MM-ddTHH:mm:ss | yyyy-MM-ddTHH:mm:ssZ) threaded through to the EF read — narrow each tuple's EF row set to entries whose StatusDate is on or after this watermark. Use the timestamp form for the canonical poll workflow so each cycle resumes from the exact last-StatusDate.")] string? sinceUtc = null,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // ---- Resolve discovery root --------------------------------
                var rootEnv = RootOverrideForTests?.Invoke()
                    ?? Environment.GetEnvironmentVariable("LACERTE_DATA_ROOT");
                var root = string.IsNullOrEmpty(rootEnv)
                    ? SessionTools.LacerteInstallRootFallback
                    : rootEnv!;

                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    // Top-level error mirrors lacerte_list_datapaths so the LLM
                    // gets the same remediation path. We use InvalidArgument
                    // (9017) with a {argument, value, reason} dict carrying the
                    // discovery hint — distinct from a runtime crash so the
                    // operator knows to fix LACERTE_DATA_ROOT, not their session.
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "LACERTE_DATA_ROOT",
                            ["value"] = root,
                            ["reason"] = "Discovery root does not exist. Set LACERTE_DATA_ROOT to your Lacerte install root (default C:\\Lacerte) and restart the MCP. Expected layout: {root}\\{YY}Tax\\{Module}Data. Modules: I/C/P/S/F/R/T/N/B.",
                        });
                }

                // ---- Parse sinceUtc once (shared across all tuples) --------
                DateTime? since = ParseSinceUtc(sinceUtc);

                // ---- Validate + normalize modulesFilter --------------------
                var allowed = NormalizeModulesFilter(modulesFilter);

                // ---- Discover (taxYear, module, dataPath) tuples -----------
                var entries = SessionTools.DiscoverDatapaths(root);

                // entries is a List<object> of anonymous { path, taxYear, module, moduleName }
                // — project to typed records so we can iterate without
                // reflection at runtime.
                var tuples = new List<DiscoveredTuple>(entries.Count);
                foreach (var e in entries)
                {
                    var dt = ToDiscoveredTuple(e);
                    if (dt == null) continue;
                    if (yearFilter.HasValue && dt.TaxYear != yearFilter.Value) continue;
                    if (!allowed.Contains(dt.Module)) continue;
                    tuples.Add(dt);
                }

                // ---- Snapshot active context for restore-in-finally --------
                var status = _session.SessionStatus();
                var savedContext = status.Context;

                var byContext = new List<object>(tuples.Count);
                int totalClients = 0;
                int totalEfRows = 0;
                var firmHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in CanonicalStatusBuckets) firmHistogram[b] = 0;
                firmHistogram["other"] = 0;

                try
                {
                    foreach (var t in tuples)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        Dictionary<string, object?>? errorBlock = null;
                        int clientCount = 0;
                        int efRowCount = 0;
                        var perTupleHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var b in CanonicalStatusBuckets) perTupleHistogram[b] = 0;
                        perTupleHistogram["other"] = 0;
                        string? latestStatusDate = null;

                        try
                        {
                            // Set the tuple's context as read-only so this scan
                            // can never write. Restored in the outer finally.
                            _session.SetContext(t.DataPath, t.TaxYear, t.Module, readOnly: true);

                            // Pull every client row (int.MaxValue overfetch
                            // is fine — sim is in-memory; live is one ODBC
                            // round-trip per tuple).
                            var clients = await _session.ListClients(
                                lastNameFilter: null,
                                limit: int.MaxValue,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            clientCount = clients.Count;

                            // EF status join with optional sinceUtc push-down.
                            var efRows = await _session.ReadEFStatus(since, cancellationToken)
                                .ConfigureAwait(false);
                            efRowCount = efRows.Count;

                            foreach (var r in efRows)
                            {
                                if (r.TryGetValue("Status", out var statusObj)
                                    && statusObj is string s
                                    && !string.IsNullOrEmpty(s))
                                {
                                    BumpHistogram(perTupleHistogram, s);
                                }
                                else
                                {
                                    // null-status rows from the LEFT OUTER
                                    // surface as "other" so the histogram
                                    // total matches efRowCount exactly.
                                    perTupleHistogram["other"]++;
                                }

                                if (r.TryGetValue("StatusDate", out var sdObj)
                                    && sdObj is string sd
                                    && !string.IsNullOrEmpty(sd))
                                {
                                    if (latestStatusDate == null
                                        || string.CompareOrdinal(sd, latestStatusDate) > 0)
                                    {
                                        latestStatusDate = sd;
                                    }
                                }
                            }
                        }
                        catch (LacerteSDKError sdkErr)
                        {
                            // Per-tuple isolation: a poisoned datapath
                            // surfaces on its row only, the aggregate still
                            // computes across the rest. Carry the structured
                            // error onto the row so the LLM can render the
                            // exact remediation per tuple.
                            errorBlock = new Dictionary<string, object?>
                            {
                                ["statusCode"] = sdkErr.StatusCode,
                                ["statusName"] = sdkErr.StatusName,
                                ["reason"] = sdkErr.UserMessage,
                            };
                            clientCount = 0;
                            efRowCount = 0;
                            // Reset the per-tuple histogram (a partially
                            // populated one would skew the firm rollup).
                            foreach (var k in perTupleHistogram.Keys.ToList())
                            {
                                perTupleHistogram[k] = 0;
                            }
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            // Defensive: a non-LacerteSDKError shouldn't
                            // poison the dashboard either. Wrap as a 9203-ish
                            // shape on the row so the operator sees the
                            // exception type without us throwing.
                            errorBlock = new Dictionary<string, object?>
                            {
                                ["statusCode"] = SyntheticStatusCodes.InternalServerError,
                                ["statusName"] = "InternalServerError",
                                ["reason"] = ex.Message,
                            };
                            clientCount = 0;
                            efRowCount = 0;
                            foreach (var k in perTupleHistogram.Keys.ToList())
                            {
                                perTupleHistogram[k] = 0;
                            }
                        }

                        totalClients += clientCount;
                        totalEfRows += efRowCount;
                        foreach (var kv in perTupleHistogram)
                        {
                            firmHistogram[kv.Key] = firmHistogram.TryGetValue(kv.Key, out var prev)
                                ? prev + kv.Value
                                : kv.Value;
                        }

                        // Row shape — camelCase keys, taxYear + module
                        // canonical. statusHistogram surfaces only the
                        // canonical buckets to keep wire shape compact.
                        var row = new Dictionary<string, object?>
                        {
                            ["taxYear"] = t.TaxYear,
                            ["module"] = t.Module.ToString(),
                            ["moduleName"] = t.ModuleName,
                            ["dataPath"] = t.DataPath,
                            ["clientCount"] = clientCount,
                            ["efRowCount"] = efRowCount,
                            ["statusHistogram"] = perTupleHistogram,
                            ["latestStatusDate"] = latestStatusDate,
                        };
                        if (errorBlock != null) row["error"] = errorBlock;
                        byContext.Add(row);
                    }
                }
                finally
                {
                    // Restore the operator's prior context (or leave it null
                    // if there was none). We cannot ClearContext through the
                    // public surface, but a null prior means no operator
                    // workflow can rely on context-being-set across this
                    // call — so the worst case is a leftover read-only
                    // context the operator must overwrite, identical to
                    // their starting state if they call set_context next.
                    if (savedContext != null)
                    {
                        try
                        {
                            _session.SetContext(
                                savedContext.DataPath,
                                savedContext.Year,
                                savedContext.Module,
                                savedContext.ReadOnly);
                        }
                        catch
                        {
                            // Defensive — restore must never throw onto the
                            // operator path. A failed restore is reported
                            // through the SetContext error envelope on the
                            // operator's next call.
                        }
                    }
                }

                return new
                {
                    generatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    root,
                    sinceUtc,
                    summary = new
                    {
                        totalClients,
                        totalModulesScanned = byContext.Count,
                        totalEfRows,
                        statusHistogram = firmHistogram,
                    },
                    byContext,
                };
            });

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Parse the optional sinceUtc into a UTC DateTime. Mirrors the
        /// accepted-format set in <c>EFileTools.LacerteEfStatus</c> so the
        /// two tools agree on what an LLM may construct.
        /// </summary>
        private static DateTime? ParseSinceUtc(string? sinceUtc)
        {
            if (string.IsNullOrEmpty(sinceUtc)) return null;
            if (!DateTime.TryParseExact(
                    sinceUtc,
                    new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dt))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidArgument,
                    new Dictionary<string, object?>
                    {
                        ["argument"] = "sinceUtc",
                        ["value"] = sinceUtc,
                        ["reason"] = $"sinceUtc value '{sinceUtc}' is not a recognized ISO-8601 timestamp. Accepted forms: 'yyyy-MM-dd' (treated as 00:00 UTC), 'yyyy-MM-ddTHH:mm:ss' (assumed UTC), or 'yyyy-MM-ddTHH:mm:ssZ'.",
                    });
            }
            return dt;
        }

        private static HashSet<char> NormalizeModulesFilter(string? modulesFilter)
        {
            var allowed = new HashSet<char>();
            string source = string.IsNullOrEmpty(modulesFilter) ? AllModuleChars : modulesFilter!;
            foreach (var c in source)
            {
                var up = char.ToUpperInvariant(c);
                if (Modules.GetByChar(up) != null)
                {
                    allowed.Add(up);
                }
            }
            // If the caller passed a non-empty string that yielded zero
            // valid modules (e.g. "xyz"), treat as "no match" rather than
            // silently falling back to all-9 — the caller's intent was
            // restrictive, even if the chars were bad.
            if (allowed.Count == 0 && string.IsNullOrEmpty(modulesFilter))
            {
                foreach (var c in AllModuleChars) allowed.Add(c);
            }
            return allowed;
        }

        private static void BumpHistogram(Dictionary<string, int> hist, string status)
        {
            // Case-insensitive bucket match — sim seeds use "Rejected" /
            // "Transmitted" / etc; live emits the same canonical strings.
            foreach (var bucket in CanonicalStatusBuckets)
            {
                if (string.Equals(bucket, status, StringComparison.OrdinalIgnoreCase))
                {
                    hist[bucket]++;
                    return;
                }
            }
            hist["other"]++;
        }

        // Projects the anonymous-object DiscoverDatapaths row to a typed
        // record. The shape is { path, taxYear, module, moduleName } per
        // SessionTools.DiscoverDatapaths; we use reflection-by-property so
        // a future schema tweak there surfaces here as a null row rather
        // than a hard cast crash.
        private sealed class DiscoveredTuple
        {
            public string DataPath { get; set; } = string.Empty;
            public int TaxYear { get; set; }
            public char Module { get; set; }
            public string ModuleName { get; set; } = string.Empty;
        }

        private static DiscoveredTuple? ToDiscoveredTuple(object e)
        {
            if (e == null) return null;
            var type = e.GetType();
            var pathProp = type.GetProperty("path");
            var yearProp = type.GetProperty("taxYear");
            var modProp = type.GetProperty("module");
            var modNameProp = type.GetProperty("moduleName");
            if (pathProp == null || yearProp == null || modProp == null) return null;

            var path = pathProp.GetValue(e) as string;
            var yearObj = yearProp.GetValue(e);
            var modStr = modProp.GetValue(e) as string;
            var modName = modNameProp?.GetValue(e) as string ?? string.Empty;
            if (path == null || yearObj == null || string.IsNullOrEmpty(modStr)) return null;
            int year;
            try { year = Convert.ToInt32(yearObj, CultureInfo.InvariantCulture); }
            catch { return null; }
            return new DiscoveredTuple
            {
                DataPath = path,
                TaxYear = year,
                Module = modStr![0],
                ModuleName = modName,
            };
        }
    }
}
