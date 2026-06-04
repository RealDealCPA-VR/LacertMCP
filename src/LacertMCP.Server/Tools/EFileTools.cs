using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// E-file status read tool. TY19+ — joins the EFilings and EFStatuses
    /// tables to produce a flat per-filing status row. Per <c>Todo.md</c>
    /// Phase 5 task #49.
    ///
    /// <para>
    /// Audit P15-010 — added the optional <c>sinceUtc</c> filter so the
    /// REQUIREMENTS §1.4 "watch e-file status across the firm" poll
    /// workflow can fetch only rows that ticked since the last poll. The
    /// session-manager push-down dispatches <see cref="LacertMCP.Server.Odbc.SqlTemplates.EfStatusJoinSince"/>
    /// on live and filters the in-memory LINQ join on sim.
    /// </para>
    ///
    /// <para>
    /// Audit P16-044/P16-048 — bad <c>sinceUtc</c> parse now throws
    /// <see cref="SyntheticStatusCodes.InvalidArgument"/> (9017) instead of
    /// the path-flavored 9013 InvalidDataPath, with a {argument, value,
    /// reason} context dict so the LLM doesn't rerun lacerte_set_context for
    /// a date-string typo. Audit P16-077 — error message and description
    /// strings now match the full accepted format set
    /// (<c>yyyy-MM-dd</c> / <c>yyyy-MM-ddTHH:mm:ss</c> / <c>yyyy-MM-ddTHH:mm:ssZ</c>).
    /// Audit P16-047 — paginated envelope (limit/offset/total/hasMore/
    /// nextOffset) so a busy practice's module-wide filing list doesn't blow
    /// the MCP message budget.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class EFileTools
    {
        private readonly LacerteSessionManager _session;

        public EFileTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read the per-client e-file status by joining EFilings and EFStatuses in the active context. TY19+ only. Optional `sinceUtc` push-down narrows to rows whose StatusDate is on or after the supplied ISO-8601 timestamp — use for the canonical poll workflow so each cycle returns only the delta instead of the full firm-history every time. Supports limit/offset pagination so a module-wide pull doesn't blow the MCP message budget. Returns the paginated envelope {rows, total, limit, offset, hasMore, nextOffset} with flat row fields EFilingId, ClientId, EFId, InstanceId, Status, StatusDate.")]
        public Task<string> LacerteEfStatus(
            [Description("Optional ISO-8601 timestamp (yyyy-MM-dd | yyyy-MM-ddTHH:mm:ss | yyyy-MM-ddTHH:mm:ssZ) — return only rows whose StatusDate is on or after this threshold. Drives the incremental-delta poll workflow; prefer the timestamp form so each cycle resumes from the exact last-StatusDate watermark instead of rounding to the day. Date-only values are normalized to 00:00 UTC.")] string? sinceUtc = null,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the filtered result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                DateTime? since = null;
                if (!string.IsNullOrEmpty(sinceUtc))
                {
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
                                ["reason"] = $"sinceUtc value '{sinceUtc}' is not a recognized ISO-8601 timestamp. Accepted forms: 'yyyy-MM-dd' (treated as 00:00 UTC), 'yyyy-MM-ddTHH:mm:ss' (assumed UTC), or 'yyyy-MM-ddTHH:mm:ssZ'. Common gotchas: fractional seconds (.123) and timezone offsets (+00:00) are NOT accepted — round to whole seconds and use the Z suffix.",
                            });
                    }
                    since = dt;
                }
                var rows = await _session.ReadEFStatus(since, cancellationToken).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);
                // P16-024 — canonical `rows` key for cross-tool consistency.
                // `efStatuses` retained as a v0.1 alias (removed in v0.2).
                // P16-047 — paginated envelope; legacy `count` reflects the
                // slice size; new consumers should read `total`.
                return new
                {
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    efStatuses = page.Rows,
                };
            });
    }
}
