using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-036 — `lacerte_field_history`. Audit log for one (clientNumber,
    /// field) pair sourced from the on-disk <c>WireLog</c> JSONL records.
    ///
    /// <para>Reads only the OUTER <c>op == "WriteField"</c> records emitted
    /// by <see cref="LacerteSessionManager"/> so each logical write counts
    /// once (the inner <c>Bridge.WriteField</c> / <c>Bridge.UpdateTaxReturn</c>
    /// / <c>Bridge.WriteToDBFDatabase</c> records emitted by the bridge
    /// layer are silently skipped to avoid double-counting).</para>
    ///
    /// <para>The <c>value</c> column on each row will almost always be
    /// <c>"***"</c> because the disk-scope WireLog redaction is unconditional
    /// per the P15-058 contract. This is by design — disk logs may be
    /// shared off-host and the operator's original write payload is
    /// recoverable from their input record, not from this tool.</para>
    /// </summary>
    [McpServerToolType]
    public sealed class FieldHistoryTools
    {
        public const int DefaultLimit = 100;
        // 14-day default window covers a typical operator's review cadence
        // without scanning months of stale logs.
        public const int DefaultWindowDays = 14;

        // Bridge-layer op names that mirror the outer WriteField — skipped
        // so we count each logical write once.
        private static readonly HashSet<string> InnerOpsToSkip =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Bridge.WriteField",
                "Bridge.UpdateTaxReturn",
                "Bridge.WriteToDBFDatabase",
            };

        [McpServerTool]
        [Description("Read the audit history for one (clientNumber, field) pair from the WireLog JSONL records. Requires LACERTE_DEBUG_WIRE=1 — when disabled, returns an empty rows list with a hint pointing at the env var. Counts only the OUTER op='WriteField' records (skips the inner Bridge.* mirrors to avoid double-counting). Each row carries succeeded=true/false — failed writes (WireLog error records) surface with succeeded=false plus the error text, so a rejected write is distinguishable from a landed one. Rows are returned oldest-first; when total exceeds limit the NEWEST rows are kept (the truncated older tail is the recoverable-from-archives part). limit defaults to 100, clamped to Pagination.MaxLimit (1000). The `value` column is the redacted '***' sentinel for every row by design (disk-scope redaction is unconditional per the WireLog policy) — the operator's original write payload is recoverable from their input record, not from this tool. sinceUtc defaults to (now - 14 days). Malformed JSONL lines are skipped silently; a missing log dir surfaces the same disabled-WireLog hint.")]
        public Task<string> LacerteFieldHistory(
            [Description("ClientNumber (SDK-minted monotonic ID, e.g. '1001') whose write history to retrieve.")] string clientNumber,
            [Description("Lacerte field code (e.g. 'C1_3' for email). The history is keyed by EXACT field name — not by friendly-alias.")] string field,
            [Description("Optional ISO-8601 timestamp (yyyy-MM-dd | yyyy-MM-ddTHH:mm:ss | yyyy-MM-ddTHH:mm:ssZ). Default = (now - 14 days). Rows with ts < sinceUtc are skipped.")] string? sinceUtc = null,
            [Description("Maximum rows to return after filtering. Default 100, clamped to Pagination.MaxLimit (1000). When total exceeds limit, the NEWEST rows are kept (display order stays oldest-first). <= 0 returns rows=[] with the unfiltered total and truncated=true so an LLM can still see the count.")] int limit = DefaultLimit,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(clientNumber))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "clientNumber",
                            ["value"] = clientNumber,
                            ["reason"] = "clientNumber is required.",
                        });
                }
                if (string.IsNullOrWhiteSpace(field))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "field",
                            ["value"] = field,
                            ["reason"] = "field is required.",
                        });
                }

                var logPath = WireLog.ResolvedOrWouldBeLogPath();
                var logDir = string.IsNullOrEmpty(logPath)
                    ? null
                    : Path.GetDirectoryName(logPath);

                // WireLog off → return the disabled hint shape.
                if (!WireLog.IsEnabled)
                {
                    return Task.FromResult<object>(new
                    {
                        clientNumber,
                        field,
                        rows = Array.Empty<object>(),
                        total = 0,
                        truncated = false,
                        logPath,
                        hint = "history unavailable — set LACERTE_DEBUG_WIRE=1 and restart to begin recording. Existing writes from BEFORE this flag was set are NOT recoverable; the audit trail begins at the first write after the restart.",
                        redactionNote = "value is '***' for every write — disk-scope redaction is unconditional per WireLog policy",
                    });
                }

                // Log dir missing → empty + hint variant.
                if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
                {
                    return Task.FromResult<object>(new
                    {
                        clientNumber,
                        field,
                        rows = Array.Empty<object>(),
                        total = 0,
                        truncated = false,
                        logPath,
                        hint = "WireLog directory does not exist yet — no writes have been recorded since LACERTE_DEBUG_WIRE was set. Trigger at least one write and re-call.",
                        redactionNote = "value is '***' for every write — disk-scope redaction is unconditional per WireLog policy",
                    });
                }

                // Parse sinceUtc with default = now - 14d.
                DateTime since;
                if (string.IsNullOrEmpty(sinceUtc))
                {
                    since = DateTime.UtcNow.AddDays(-DefaultWindowDays);
                }
                else
                {
                    if (!DateTime.TryParseExact(
                            sinceUtc,
                            new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss" },
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
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
                    since = parsed;
                }

                // Enumerate eligible log files within the window.
                var sinceDate = since.Date;
                var nowDate = DateTime.UtcNow.Date;
                string[] files;
                try
                {
                    // Glob covers per-day (lacerte-YYYYMMDD.log) + rotated
                    // (lacerte-YYYYMMDD-HHmmss[-salt].log) shapes.
                    files = Directory.GetFiles(logDir, "lacerte-*.log");
                }
                catch
                {
                    files = Array.Empty<string>();
                }

                var inWindow = new List<string>(files.Length);
                foreach (var f in files)
                {
                    if (TryParseLogFileDate(f, out var dt))
                    {
                        if (dt >= sinceDate && dt <= nowDate) inWindow.Add(f);
                    }
                }
                // Sort by filename so we read oldest first (lexicographic on
                // YYYYMMDD[-HHmmss] gives chronological order).
                inWindow.Sort(StringComparer.Ordinal);

                var allMatches = new List<RowParsed>();
                foreach (var path in inWindow)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScanFile(path, clientNumber, field, since, allMatches);
                }

                // Sort by ts ascending (the file scan order is mostly
                // chronological, but interleaved retries within one file
                // can be out of order).
                allMatches.Sort((a, b) => string.CompareOrdinal(a.Ts, b.Ts));

                var total = allMatches.Count;
                // R6-21b — clamp to the project-wide pagination ceiling so a
                // hallucinated limit=999999 can't blow the wire budget.
                int effectiveLimit = Math.Min(limit, Pagination.Pagination.MaxLimit);
                List<RowParsed> capped;
                if (effectiveLimit <= 0)
                {
                    // limit<=0 returns rows=[] with total + truncated=true so
                    // an LLM can still see the count without blowing the
                    // wire budget. Matches the audit-finding r3-line 903
                    // "limit=0 returns total but no rows" precedent.
                    capped = new List<RowParsed>();
                }
                else
                {
                    // R6-21a — keep the NEWEST rows on truncation: the list
                    // is sorted ascending by ts, so take the LAST
                    // effectiveLimit rows (display stays oldest-first). The
                    // old GetRange(0, N) kept the oldest window, making the
                    // most recent writes — the ones an auditor actually
                    // wants — unreachable with no offset parameter.
                    capped = total > effectiveLimit
                        ? allMatches.GetRange(total - effectiveLimit, effectiveLimit)
                        : allMatches;
                }

                var rows = capped.Select(r => (object)new
                {
                    ts = r.Ts,
                    cid = r.Cid,
                    value = r.Value,
                    dryRun = r.DryRun,
                    // R6-21c — failed writes are first-class: WireLog error
                    // records surface with succeeded=false + the error text
                    // so a rejected write is distinguishable from a landed
                    // one (both used to render as statusCode:null).
                    succeeded = r.Succeeded,
                    statusCode = r.StatusCode,
                    error = r.Error,
                    durationMs = r.DurationMs,
                }).ToList();

                return Task.FromResult<object>(new
                {
                    clientNumber,
                    field,
                    rows,
                    total,
                    truncated = total > rows.Count,
                    logPath,
                    redactionNote = "value is '***' for every write — disk-scope redaction is unconditional per WireLog policy",
                });
            });

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static void ScanFile(
            string path, string clientNumber, string field,
            DateTime since, List<RowParsed> sink)
        {
            // Open with FileShare.ReadWrite to match the live writer's
            // share-mode contract — otherwise a busy live process would
            // block our read.
            FileStream? fs = null;
            StreamReader? reader = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!TryParseRow(line, clientNumber, field, since, out var parsed)) continue;
                    sink.Add(parsed);
                }
            }
            catch
            {
                // Defensive — any IO/format failure on one file is silently
                // skipped so a hostile log file doesn't poison the query.
            }
            finally
            {
                try { reader?.Dispose(); } catch { /* swallow */ }
                try { fs?.Dispose(); } catch { /* swallow */ }
            }
        }

        private static bool TryParseRow(
            string line, string clientNumber, string field,
            DateTime since, out RowParsed parsed)
        {
            parsed = default!;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { return false; }
            if (node is not JsonObject obj) return false;

            // op must equal "WriteField" — NOT any of the inner Bridge.*
            // mirrors. Anything else is silently filtered.
            var op = obj["op"]?.GetValue<string>();
            if (string.IsNullOrEmpty(op) || InnerOpsToSkip.Contains(op!)) return false;
            if (!string.Equals(op, "WriteField", StringComparison.Ordinal)) return false;

            // args.clientNumber + args.field must match exactly.
            var args = obj["args"] as JsonObject;
            if (args == null) return false;
            var ac = args["clientNumber"]?.GetValue<string>();
            var af = args["field"]?.GetValue<string>();
            if (!string.Equals(ac, clientNumber, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(af, field, StringComparison.Ordinal)) return false;

            // ts filter.
            var tsStr = obj["ts"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tsStr)) return false;
            if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var ts))
            {
                return false;
            }
            if (ts < since) return false;

            // Project the visible fields onto the row.
            string? cid = obj["cid"]?.GetValue<string>();
            object? rawValue = args.ContainsKey("value")
                ? (args["value"] is JsonValue jv ? jv.ToString() : args["value"]?.ToJsonString())
                : null;
            // Default to the redacted sentinel when the value slot is
            // missing (shouldn't happen on a WriteField record, but
            // defensive).
            string redactedValue = rawValue == null ? "***" : Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? "***";

            bool dryRun = false;
            var dr = args["dryRun"];
            if (dr is JsonValue drv && drv.TryGetValue<bool>(out var drBool)) dryRun = drBool;

            int? statusCode = null;
            var result = obj["result"];
            if (result is JsonValue rv && rv.TryGetValue<int>(out var rvInt)) statusCode = rvInt;

            // R6-21c — WireLog writes an `error` object (instead of
            // `result`) when the write threw. Parse it so failed writes are
            // distinguishable from successes: succeeded=false + the error
            // type/message (message may itself be the redacted sentinel —
            // that's the disk-scope policy, not a parse failure).
            bool succeeded = true;
            string? errorText = null;
            if (obj["error"] is JsonObject errObj)
            {
                succeeded = false;
                var errType = errObj["type"]?.GetValue<string>();
                var errMessage = (errObj["message"] as JsonValue)?.ToString();
                errorText = string.IsNullOrEmpty(errType)
                    ? (errMessage ?? "unknown error")
                    : (string.IsNullOrEmpty(errMessage) ? errType : errType + ": " + errMessage);
            }

            long? durationMs = null;
            var dur = obj["durationMs"];
            if (dur is JsonValue dv && dv.TryGetValue<long>(out var dvLong)) durationMs = dvLong;

            parsed = new RowParsed
            {
                Ts = tsStr!,
                Cid = cid,
                Value = redactedValue,
                DryRun = dryRun,
                Succeeded = succeeded,
                StatusCode = statusCode,
                Error = errorText,
                DurationMs = durationMs,
            };
            return true;
        }

        // Mirrors WireLog.TryParseLogFileDate — kept private here so we
        // stay independent of the WireLog internals.
        private static bool TryParseLogFileDate(string fullPath, out DateTime date)
        {
            date = default;
            try
            {
                var name = Path.GetFileNameWithoutExtension(fullPath);
                if (string.IsNullOrEmpty(name) || !name.StartsWith("lacerte-", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var tail = name.Substring("lacerte-".Length);
                if (tail.Length < 8) return false;
                var datePart = tail.Substring(0, 8);
                return DateTime.TryParseExact(
                    datePart,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out date);
            }
            catch
            {
                return false;
            }
        }

        private sealed class RowParsed
        {
            public string Ts { get; set; } = string.Empty;
            public string? Cid { get; set; }
            public string Value { get; set; } = "***";
            public bool DryRun { get; set; }
            public bool Succeeded { get; set; } = true;
            public int? StatusCode { get; set; }
            public string? Error { get; set; }
            public long? DurationMs { get; set; }
        }
    }
}
