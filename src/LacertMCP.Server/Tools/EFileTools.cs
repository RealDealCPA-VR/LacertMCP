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
    /// </summary>
    [McpServerToolType]
    public sealed class EFileTools
    {
        private readonly LacerteSessionManager _session;

        public EFileTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read the per-client e-file status by joining EFilings and EFStatuses in the active context. TY19+ only. Optional `sinceUtc` push-down (audit P15-010) narrows to rows whose StatusDate is on or after the supplied ISO-8601 date (yyyy-MM-dd) — use for the canonical poll workflow so each cycle returns only the delta instead of the full firm-history every time. Returns flat rows with EFilingId, ClientId, EFId, InstanceId, Status, StatusDate.")]
        public Task<string> LacerteEfStatus(
            [Description("Optional ISO-8601 date (yyyy-MM-dd) — return only rows whose StatusDate is on or after this threshold. Audit P15-010 — drives the REQUIREMENTS §1.4 poll workflow's incremental delta.")] string? sinceUtc = null,
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
                            SyntheticStatusCodes.InvalidDataPath,
                            new Dictionary<string, object?>
                            {
                                ["sinceUtc"] = sinceUtc,
                                ["reason"] = "sinceUtc must be an ISO-8601 date (yyyy-MM-dd).",
                            });
                    }
                    since = dt;
                }
                var rows = await _session.ReadEFStatus(since, cancellationToken).ConfigureAwait(false);
                return new { count = rows.Count, efStatuses = rows };
            });
    }
}
