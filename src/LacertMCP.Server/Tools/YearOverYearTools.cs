using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-035 — `lacerte_year_over_year`. Diff one client across two tax
    /// years (e.g. 2023 vs 2024). Uses the active session context as the
    /// source of <c>dataPath</c> / <c>module</c> / <c>readOnly</c> snapshot;
    /// rewrites the path's <c>{YY}Tax</c> segment to point at each year,
    /// then reads the demographics + per-series detail totals from both
    /// and surfaces only changed/delta rows.
    ///
    /// <para>R6-D2 — the per-year reads go through the session manager's
    /// explicit-context <c>ReadClientAt</c> / <c>ReadDetailAt</c> overloads,
    /// which resolve (dataPath, taxYear, module) per call and never touch
    /// the active context. The active context is only READ (to derive the
    /// per-year paths and module) and never set or restored, so a thrown
    /// read cannot strand the operator and a concurrent tool call can never
    /// observe a scan-flipped tuple. (This retires the R6-09
    /// ContextScanGate shim and its documented limitation.)</para>
    /// </summary>
    [McpServerToolType]
    public sealed class YearOverYearTools
    {
        // Hard cap on (toYear - fromYear). Five years is enough to catch
        // the operator's typical "what changed in this client since 2020"
        // question without producing a 10-year envelope an LLM can't read.
        public const int MaxYearDelta = 5;
        public const int FloorTaxYear = 2018;

        // Mirrors SessionTools.YearFolderRegex — kept private here so we
        // don't entangle a Tools-cluster file with the SessionTools regex
        // (different ownership in some clusters).
        private static readonly Regex YearFolderRegex =
            new Regex(@"^(\d{2})Tax$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Canonical scope spellings. Matching is case-insensitive (R6-07d)
        // — 'detailtotals' and 'DETAILTOTALS' normalize to the canonical
        // camelCase 'detailTotals' before any branch compares against it.
        private static readonly string[] CanonicalScopes =
            { "demographics", "detailTotals", "all" };

        // Default series IDs to total — the common Individual-module
        // categories: Wages, Interest, Dividends, Income.
        private static readonly int[] DefaultSeriesIds = { 11, 12, 13, 20 };

        private readonly LacerteSessionManager _session;

        public YearOverYearTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Diff ONE client across two tax years. Uses the active context's dataPath/module to derive the per-year paths by rewriting the {YY}Tax segment (e.g. 24Tax -> 23Tax). Returns only changed/delta rows so the LLM doesn't have to filter a full two-year dump. scope='demographics' (DATA1 row diff only), 'detailTotals' (per-series Amount totals diff only), or 'all' (both). seriesIds defaults to [11, 12, 13, 20] (Wages, Interest, Dividends, Income). The active context is only read (for dataPath/module), never modified — per-year reads use explicit per-call contexts. Restrictions: fromYear must be < toYear, both >= 2018, (toYear - fromYear) <= 5. The active context's dataPath must follow the canonical {root}\\{YY}Tax\\{Module}Data layout — flat-share alternate layouts surface an actionable error.")]
        public Task<string> LacerteYearOverYear(
            [Description("ClientNumber (SDK-minted monotonic ID, e.g. '1001') to diff. Must exist in BOTH the fromYear and toYear data folders for full diff; missing in fromYear produces an 'all values are new' diff against toYear.")] string clientNumber,
            [Description("Earlier tax year (>= 2018; must be strictly less than toYear).")] int fromYear,
            [Description("Later tax year. (toYear - fromYear) must be <= 5 to keep the response shape compact.")] int toYear,
            [Description("Diff scope: 'demographics' (DATA1 row only), 'detailTotals' (per-series Amount sums only), or 'all' (both). Case-insensitive; default 'all'.")] string scope = "all",
            [Description("Series IDs to total. Default [11, 12, 13, 20] (Wages/Interest/Dividends/Income). Each value must be a positive integer. Ignored when scope='demographics'.")] int[]? seriesIds = null,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // ---- Argument validation ----------------------------------
                if (string.IsNullOrWhiteSpace(clientNumber))
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "clientNumber",
                            ["value"] = clientNumber,
                            ["reason"] = "clientNumber is required.",
                        });
                }
                if (fromYear < FloorTaxYear)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?>
                        {
                            ["taxYear"] = fromYear,
                            ["floor"] = FloorTaxYear,
                        });
                }
                if (toYear < FloorTaxYear)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?>
                        {
                            ["taxYear"] = toYear,
                            ["floor"] = FloorTaxYear,
                        });
                }
                if (fromYear >= toYear)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "fromYear",
                            ["value"] = fromYear,
                            ["reason"] = $"fromYear ({fromYear}) must be strictly less than toYear ({toYear}). Pass the EARLIER year as fromYear.",
                        });
                }
                if ((toYear - fromYear) > MaxYearDelta)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "toYear",
                            ["value"] = toYear,
                            ["fromYear"] = fromYear,
                            ["delta"] = toYear - fromYear,
                            ["maxDelta"] = MaxYearDelta,
                            ["reason"] = $"(toYear - fromYear) = {toYear - fromYear} exceeds the {MaxYearDelta}-year cap. Narrow the diff window — chained 5-year diffs preserve the same coverage with bounded response shape.",
                        });
                }
                // R6-07d — case-insensitive scope match, normalized to the
                // canonical camelCase spelling so downstream comparisons
                // ("detailTotals" / "demographics" / "all") stay exact.
                var trimmedScope = (scope ?? "all").Trim();
                string? normScope = null;
                foreach (var canonical in CanonicalScopes)
                {
                    if (string.Equals(trimmedScope, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        normScope = canonical;
                        break;
                    }
                }
                if (normScope == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "scope",
                            ["value"] = scope,
                            ["reason"] = "scope must be 'demographics', 'detailTotals', or 'all' (case-insensitive).",
                        });
                }

                int[] effectiveSeries;
                if (seriesIds == null || seriesIds.Length == 0)
                {
                    effectiveSeries = DefaultSeriesIds;
                }
                else
                {
                    foreach (var sid in seriesIds)
                    {
                        if (sid <= 0)
                        {
                            throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument,
                                new Dictionary<string, object?>
                                {
                                    ["argument"] = "seriesIds",
                                    ["value"] = sid,
                                    ["reason"] = "Every seriesId must be a positive integer (the Lacerte Series column is always >= 1).",
                                });
                        }
                    }
                    effectiveSeries = seriesIds;
                }

                // ---- Snapshot active context -------------------------------
                var status = _session.SessionStatus();
                var snapshotCtx = status.Context;
                if (snapshotCtx == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
                }

                // ---- Path rewrite ------------------------------------------
                var fromPath = RewriteYearPath(snapshotCtx.DataPath, fromYear);
                var toPath = RewriteYearPath(snapshotCtx.DataPath, toYear);
                if (fromPath == null || toPath == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.InvalidDataPath,
                        new Dictionary<string, object?>
                        {
                            ["dataPath"] = snapshotCtx.DataPath,
                            ["reason"] = "lacerte_year_over_year requires the canonical {root}\\{YY}Tax\\{Module}Data layout — alternate flat-share layouts (e.g. C:\\Lacerte\\24data\\) are not auto-rewritten. Set context per year manually with lacerte_set_context if you're on the alternate layout.",
                        });
                }

                // ---- Read both years --------------------------------------
                // R6-D2 — per-year reads use the explicit-context *At
                // overloads, so the active context is never mutated and no
                // restore / serialization gate is needed.
                var errors = new List<object>();
                YearResult? fromResult;
                YearResult? toResult;

                try
                {
                    fromResult = await ReadYear(
                        fromPath, fromYear, snapshotCtx.Module,
                        clientNumber, normScope, effectiveSeries, errors, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (LacerteSDKError sdkErr)
                {
                    // Capture into errors[] but keep going to toYear so
                    // we can still surface a partial diff.
                    // R6-07b/c — canonical `taxYear` wire key + the
                    // {statusCode, statusName, reason} error-row shape
                    // shared with lacerte_firm_dashboard.
                    errors.Add(new
                    {
                        taxYear = (int?)fromYear,
                        statusCode = sdkErr.StatusCode,
                        statusName = sdkErr.StatusName,
                        reason = sdkErr.UserMessage,
                    });
                    fromResult = new YearResult();
                }

                try
                {
                    toResult = await ReadYear(
                        toPath, toYear, snapshotCtx.Module,
                        clientNumber, normScope, effectiveSeries, errors, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (LacerteSDKError sdkErr)
                {
                    errors.Add(new
                    {
                        taxYear = (int?)toYear,
                        statusCode = sdkErr.StatusCode,
                        statusName = sdkErr.StatusName,
                        reason = sdkErr.UserMessage,
                    });
                    toResult = new YearResult();
                }

                // ---- Build the diff ----------------------------------------
                List<object> demographicsDiff = new List<object>();
                if (normScope == "demographics" || normScope == "all")
                {
                    demographicsDiff = BuildDemographicsDiff(
                        fromResult?.Client, toResult?.Client);
                }

                List<object> detailTotalsDiff = new List<object>();
                if (normScope == "detailTotals" || normScope == "all")
                {
                    detailTotalsDiff = BuildDetailTotalsDiff(
                        fromResult?.SeriesTotals ?? new Dictionary<int, double>(),
                        toResult?.SeriesTotals ?? new Dictionary<int, double>(),
                        effectiveSeries);
                }

                // R6-07a — never report a confident "no changes" for a
                // client that doesn't exist in either year. The found flags
                // make absence explicit; the hint fires when BOTH years
                // came back empty (the classic typo'd-clientNumber case).
                bool foundInFromYear = fromResult?.Client != null;
                bool foundInToYear = toResult?.Client != null;
                string? hint = null;
                if (!foundInFromYear && !foundInToYear)
                {
                    hint = $"ClientNumber '{clientNumber}' was not found in EITHER {fromYear} or {toYear} — "
                        + "the empty diff below means 'no such client', NOT 'nothing changed'. "
                        + "Check for a typo'd clientNumber (call lacerte_client_list per year to enumerate), "
                        + "and note that ClientNumber is the SDK-minted identifier, distinct from the operator-chosen ClientID.";
                }

                return new
                {
                    clientNumber,
                    fromYear,
                    toYear,
                    foundInFromYear,
                    foundInToYear,
                    scope = normScope,
                    seriesIds = effectiveSeries,
                    fromDataPath = fromPath,
                    toDataPath = toPath,
                    demographicsDiff,
                    detailTotalsDiff,
                    errors,
                    hint,
                };
            });

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Rewrite the <c>{YY}Tax</c> segment in <paramref name="dataPath"/>
        /// to the two-digit form of <paramref name="targetYear"/>. Returns
        /// null when the canonical layout isn't present (alternate flat-share
        /// shapes).
        /// </summary>
        public static string? RewriteYearPath(string dataPath, int targetYear)
        {
            if (string.IsNullOrEmpty(dataPath)) return null;
            // Split on both directory separators so the rewrite works on
            // Windows ('\') and any UNC / forward-slash mixed input.
            var sep = new[] { '\\', '/' };
            var parts = dataPath.Split(sep, StringSplitOptions.None);
            int matchIdx = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                if (YearFolderRegex.IsMatch(parts[i]))
                {
                    matchIdx = i;
                    break;
                }
            }
            if (matchIdx < 0) return null;
            // 2-digit year, zero-padded. (e.g. 2023 -> "23", 2099 -> "99")
            var yy = (targetYear % 100).ToString("D2", CultureInfo.InvariantCulture);
            parts[matchIdx] = yy + "Tax";
            // Re-join with the OS-canonical separator so the resulting
            // path round-trips through PathValidation cleanly.
            return string.Join("\\", parts);
        }

        private async Task<YearResult> ReadYear(
            string dataPath, int year, char module, string clientNumber,
            string scope, int[] seriesIds, List<object> errors,
            CancellationToken cancellationToken)
        {
            // R6-D2 — explicit per-call contexts; always read-only by the
            // *At contract, and the active context is never touched.
            var result = new YearResult();

            try
            {
                result.Client = await _session.ReadClientAt(
                        dataPath, year, module, clientNumber, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LacerteSDKError sdkErr)
            {
                // R6-07b/c — canonical `taxYear` wire key + the
                // {statusCode, statusName, reason} error-row shape.
                errors.Add(new
                {
                    taxYear = (int?)year,
                    statusCode = sdkErr.StatusCode,
                    statusName = sdkErr.StatusName,
                    reason = "ReadClient failed: " + sdkErr.UserMessage,
                });
            }

            if (scope != "demographics")
            {
                try
                {
                    var detail = await _session.ReadDetailAt(
                            dataPath, year, module, clientNumber,
                            seriesFilter: null, cancellationToken)
                        .ConfigureAwait(false);
                    // Group by Series, sum Amount; keep only the seriesIds
                    // the caller asked for so the response shape stays tight.
                    var totals = new Dictionary<int, double>();
                    var wanted = new HashSet<int>(seriesIds);
                    foreach (var r in detail)
                    {
                        if (int.TryParse(r.Series, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var sid)
                            && wanted.Contains(sid))
                        {
                            totals[sid] = totals.TryGetValue(sid, out var prior)
                                ? prior + (r.Amount ?? 0.0)
                                : (r.Amount ?? 0.0);
                        }
                    }
                    result.SeriesTotals = totals;
                }
                catch (LacerteSDKError sdkErr)
                {
                    errors.Add(new
                    {
                        taxYear = (int?)year,
                        statusCode = sdkErr.StatusCode,
                        statusName = sdkErr.StatusName,
                        reason = "ReadDetail failed: " + sdkErr.UserMessage,
                    });
                }
            }
            return result;
        }

        private static List<object> BuildDemographicsDiff(
            IReadOnlyDictionary<string, object?>? from,
            IReadOnlyDictionary<string, object?>? to)
        {
            var diff = new List<object>();
            var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (from != null) foreach (var k in from.Keys) allKeys.Add(k);
            if (to != null) foreach (var k in to.Keys) allKeys.Add(k);
            foreach (var key in allKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                object? fv = null;
                object? tv = null;
                from?.TryGetValue(key, out fv);
                to?.TryGetValue(key, out tv);
                if (!ScalarEquals(fv, tv))
                {
                    diff.Add(new
                    {
                        field = key,
                        fromValue = fv,
                        toValue = tv,
                        changed = true,
                    });
                }
            }
            return diff;
        }

        private static List<object> BuildDetailTotalsDiff(
            IReadOnlyDictionary<int, double> fromTotals,
            IReadOnlyDictionary<int, double> toTotals,
            int[] seriesIds)
        {
            var diff = new List<object>();
            foreach (var sid in seriesIds.Distinct().OrderBy(s => s))
            {
                fromTotals.TryGetValue(sid, out var ft);
                toTotals.TryGetValue(sid, out var tt);
                var delta = tt - ft;
                // Only include rows that actually changed (delta != 0) OR
                // that exist in at least one side, to keep the envelope
                // tight without dropping "appeared in toYear" rows.
                bool fromPresent = fromTotals.ContainsKey(sid);
                bool toPresent = toTotals.ContainsKey(sid);
                if (!fromPresent && !toPresent) continue;
                if (delta == 0.0 && fromPresent && toPresent) continue;
                diff.Add(new
                {
                    series = sid,
                    seriesName = SeriesName(sid),
                    fromTotal = ft,
                    toTotal = tt,
                    delta,
                });
            }
            return diff;
        }

        private static string SeriesName(int sid) => sid switch
        {
            11 => "Wages",
            12 => "Interest",
            13 => "Dividends",
            14 => "Dependents",
            20 => "Income",
            30 => "State",
            40 => "Partner",
            _ => "Series" + sid,
        };

        private static bool ScalarEquals(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            // Numeric tolerance — DBF round-trips can flip 1.0 vs 1 vs "1"
            // semantics; we compare as decimal where possible to avoid a
            // spurious "changed: 0.0 -> 0" diff row.
            if (IsNumber(a) && IsNumber(b))
            {
                try
                {
                    var da = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                    var db = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                    return da == db;
                }
                catch { /* fall through to string comparison */ }
            }
            var sa = Convert.ToString(a, CultureInfo.InvariantCulture);
            var sb = Convert.ToString(b, CultureInfo.InvariantCulture);
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }

        private static bool IsNumber(object o)
        {
            return o is byte || o is sbyte || o is short || o is ushort
                || o is int || o is uint || o is long || o is ulong
                || o is float || o is double || o is decimal;
        }

        private sealed class YearResult
        {
            public IReadOnlyDictionary<string, object?>? Client { get; set; }
            public IReadOnlyDictionary<int, double>? SeriesTotals { get; set; }
        }
    }
}
