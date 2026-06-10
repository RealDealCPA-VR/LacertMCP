using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// P18-028 — <c>lacerte_client_summary</c>. One-call, high-signal snapshot
    /// of a client in the active <c>(dataPath, taxYear, module)</c>, fanning
    /// out the existing <c>session.Read{Client,Subclients,States,Statuses,Forms,EFStatus}</c>
    /// chokepoints in a single envelope.
    ///
    /// <para>
    /// Replaces the 5–7 MCP round-trips the LLM pays to answer "what's the
    /// deal with client X?" with a compact projection: identity (DATA1) +
    /// counts (subclients / states / forms / status / detail?) + the resolved
    /// states list + derived flags + the latest e-file status row + drill-down
    /// cursors for the heavy detail-row reads. Identity field set is FIXED
    /// per the verdict scope note — additions need their own task.
    /// </para>
    ///
    /// <para>
    /// Fan-out runs sequentially through the session manager — the live ODBC
    /// bridge is not guaranteed concurrent-safe on a single connection (see
    /// the design comment in the audit verdict). The win is still the wire-
    /// envelope + LLM-context-token reduction: 1 envelope instead of 6, and
    /// the LLM no longer parses 4000 low-signal DATA1 columns to surface the
    /// 4 facts it actually needed.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class ClientSummaryTools
    {
        private readonly LacerteSessionManager _session;

        public ClientSummaryTools(LacerteSessionManager session) => _session = session;

        /// <summary>Stable version stamp so a future field-set bump is observable.</summary>
        public const int SummaryVersion = 1;

        /// <summary>Cap on the inline states list before truncating + flagging overflow.</summary>
        public const int StatesCap = 10;

        /// <summary>Cap on the per-state breakdown rows in the eFile sub-envelope.</summary>
        public const int PerStateBreakdownCap = 10;

        [McpServerTool]
        [Description("One-call high-level snapshot of a client — DATA1 identity + counts (subclients/states/forms/status) + derived flags (completed/eFileReady/locked/hasErrors) + latest e-file status + cursors for drill-down. Replaces the 5-7 round-trip chain LLMs run today for 'what's the deal with client X?' questions. Pass includeDetail=true to also project DATA6 detail-row counts (heavier — extra session read). Returns 9009 ContextNotSet if no active context; {found:false, clientNumber, hint} if clientNumber unknown. Identity field set is FIXED per module — to grow it, open a separate task; do not let it drift into return-everything.")]
        public Task<string> LacerteClientSummary(
            [Description("The ClientNumber (sim-assigned monotonic identifier; e.g. \"1001\"). NOTE: ClientNumber is the SDK-minted identifier (sim's monotonic counter), distinct from the operator-chosen ClientID — these are not interchangeable. If you only have a name, call lacerte_client_find first.")] string clientNumber,
            [Description("When true, also fan out to ReadDetail and surface detailRows in counts. Default false — detail-row reads can be heavy on large returns.")] bool includeDetail = false,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(clientNumber))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "clientNumber",
                            ["value"] = clientNumber,
                            ["reason"] = "clientNumber must be a non-empty string. Call lacerte_client_list to enumerate available ClientNumbers, or lacerte_client_find to resolve from a name.",
                        });
                }

                // First read serves both as the existence check and as the
                // session-context / registration gate (ReadClient throws
                // 9009 ContextNotSet / NotSignedIn / LoginExpired before we
                // ever reach the fan-out). On {found:false} short-circuit
                // with a directly-actionable hint instead of paying the
                // remaining 5 round trips for an empty envelope.
                var clientRow = await _session.ReadClient(clientNumber, cancellationToken).ConfigureAwait(false);
                if (clientRow == null)
                {
                    return (object)new
                    {
                        clientNumber,
                        found = false,
                        hint = $"ClientNumber '{clientNumber}' not present in the active (datapath, year, module). Call lacerte_client_list to enumerate available ClientNumbers, or lacerte_client_find to resolve from a name. Note: ClientNumber is the SDK-minted identifier (sim's monotonic counter), distinct from the operator-chosen ClientID — these are not interchangeable.",
                    };
                }

                // Sequential fan-out — see class comment for the live-bridge
                // concurrency caveat. Pass the CancellationToken through so a
                // mid-fan-out abort surfaces cleanly.
                var subclients = await _session.ReadSubclients(clientNumber, cancellationToken).ConfigureAwait(false);
                var states = await _session.ReadStates(clientNumber, cancellationToken).ConfigureAwait(false);
                var statuses = await _session.ReadStatuses(clientNumber, cancellationToken).ConfigureAwait(false);
                var forms = await _session.ReadForms(clientNumber, cancellationToken).ConfigureAwait(false);
                var efStatusRows = await _session.ReadEFStatus(null, cancellationToken).ConfigureAwait(false);

                int? detailRows = null;
                if (includeDetail)
                {
                    var detail = await _session.ReadDetail(clientNumber, cancellationToken).ConfigureAwait(false);
                    detailRows = detail.Count;
                }

                // ----- Context echo (from the session) ------------------
                // The Context here is safe to dereference — every Read* above
                // would have thrown 9009 if it were null.
                var ctx = _session.Context!;
                var moduleInfo = Modules.GetByChar(ctx.Module);
                var moduleName = moduleInfo?.Name ?? ctx.Module.ToString();
                var returnType = moduleInfo?.ReturnType ?? ctx.Module.ToString();
                // R6-18 — the `module` wire key carries the canonical
                // single-character DBF code everywhere ("I"), matching
                // SessionTools/FirmDashboard so cross-tool joins work; the
                // human-readable name lives under `moduleName`.
                var contextEcho = new
                {
                    dataPath = ctx.DataPath,
                    taxYear = ctx.Year,
                    module = ctx.Module.ToString(),
                    moduleName,
                    returnType,
                };

                // ----- Identity projection (FIXED set per module) -------
                var identity = ProjectIdentity(clientRow, ctx);

                // ----- States projection --------------------------------
                var stateAbbrevs = states
                    .Select(r => r.TryGetValue("stateAbbrev", out var sa) ? sa as string : null)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!.ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var statesTruncated = stateAbbrevs.Count > StatesCap;
                var statesProjected = statesTruncated
                    ? stateAbbrevs.Take(StatesCap).ToList()
                    : stateAbbrevs;

                // ----- Status / derived flags --------------------------
                int statusRowsSet = statuses.Count;
                var flags = DeriveFlags(statuses);

                // ----- EF status (latest only + per-state breakdown) ---
                var efView = ProjectEfStatus(efStatusRows, clientNumber);

                // ----- Final envelope ----------------------------------
                return new
                {
                    summaryVersion = SummaryVersion,
                    clientNumber,
                    found = true,
                    context = contextEcho,
                    identity,
                    counts = new
                    {
                        subclients = subclients.Count,
                        statesFiled = stateAbbrevs.Count,
                        formsCount = forms.Count,
                        statusRowsSet,
                        detailRows,
                    },
                    states = statesProjected,
                    statesTruncated,
                    flags,
                    efStatus = efView,
                    cursors = new
                    {
                        subclientReadOffset = 0,
                        detailReadOffset = 0,
                        fullStateList = statesTruncated
                            ? "call lacerte_state_read for the full state list"
                            : null,
                    },
                };
            });

        // ===================================================================
        // Internals
        // ===================================================================

        /// <summary>
        /// Project the DATA1 row into a small, stable identity envelope. The
        /// field set is FIXED per the verdict — additions need their own
        /// task. Individual modules surface firstName/lastName; non-Individual
        /// modules carry the entity name in firstName (C1_1) with lastName
        /// null. taxpayerId stays NULL for v1: the seed fixture does not
        /// carry an SSN/EIN column, and surfacing one would need a wire-scope
        /// redaction pass that the existing WireLog.Redact already covers at
        /// the envelope boundary — but emitting a placeholder field invites
        /// LLM hallucination ("the SSN is null, that means …"). When a real
        /// SDK column lands, add it here AND extend WireLog.Redact's Wire
        /// scope to cover the new key.
        /// </summary>
        public static object ProjectIdentity(
            IReadOnlyDictionary<string, object?> row, SessionContext ctx)
        {
            row.TryGetValue("clientId", out var clientId);
            row.TryGetValue("C1_1", out var c1_1);
            row.TryGetValue("C1_2", out var c1_2);
            row.TryGetValue("C1_3", out var c1_3);
            row.TryGetValue("C1_4", out var c1_4); // filing status for Individual

            var moduleInfo = Modules.GetByChar(ctx.Module);
            var moduleName = moduleInfo?.Name ?? ctx.Module.ToString();
            var returnType = moduleInfo?.ReturnType ?? ctx.Module.ToString();

            return new
            {
                clientId,
                name = ProjectName(c1_1, c1_2),
                firstName = c1_1,
                lastName = c1_2,
                email = c1_3,
                filingStatus = c1_4,
                taxYear = ctx.Year,
                // R6-18 — canonical single-char module code; friendly name
                // under `moduleName`.
                module = ctx.Module.ToString(),
                moduleName,
                returnType,
            };
        }

        private static string? ProjectName(object? c1_1, object? c1_2)
        {
            var first = c1_1 as string;
            var last = c1_2 as string;
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last))
            {
                return $"{first} {last}";
            }
            if (!string.IsNullOrEmpty(first)) return first;
            if (!string.IsNullOrEmpty(last)) return last;
            return null;
        }

        /// <summary>
        /// Derive the four canonical boolean flags from the DATA5 status rows.
        /// DATA5 column convention (per the seed fixture): C5_1 carries the
        /// e-file readiness string ("InProgress" | "Rejected" | "Transmitted"
        /// | "Accepted"). The flag projection is conservative — if a row's
        /// C5_1 cannot be resolved as one of the documented strings the flag
        /// defaults to false rather than throwing, so a future schema bump
        /// doesn't crash the summary tool.
        ///
        /// <para>
        /// hasErrors is true when ANY status row reports "Rejected" — surfacing
        /// the failure mode without requiring the LLM to enumerate all rows.
        /// </para>
        /// </summary>
        public static object DeriveFlags(IReadOnlyList<IReadOnlyDictionary<string, object?>> statuses)
        {
            bool completed = false;
            bool eFileReady = false;
            bool locked = false;
            bool hasErrors = false;
            foreach (var row in statuses)
            {
                if (row.TryGetValue("C5_1", out var c5_1) && c5_1 is string s)
                {
                    if (string.Equals(s, "Rejected", StringComparison.OrdinalIgnoreCase))
                    {
                        hasErrors = true;
                    }
                    if (string.Equals(s, "Transmitted", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "Accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        eFileReady = true;
                    }
                    if (string.Equals(s, "Completed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "Accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        completed = true;
                    }
                }
                // C5_2 reserved for the "locked" indicator in future schema —
                // safely defaults to false when absent.
                if (row.TryGetValue("C5_2", out var c5_2) && c5_2 is bool b && b)
                {
                    locked = true;
                }
            }
            return new { completed, eFileReady, locked, hasErrors };
        }

        /// <summary>
        /// Reduce the full EF-status join into a compact view: the latest
        /// status row for this client (by StatusDate, descending) plus a
        /// per-state breakdown capped at <see cref="PerStateBreakdownCap"/>.
        /// The full join is N-clients-wide; we filter to the requested
        /// clientNumber up front so multistate partnerships don't drag in
        /// every sibling client.
        /// </summary>
        public static object ProjectEfStatus(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> efRows,
            string clientNumber)
        {
            var forClient = efRows
                .Where(r => r.TryGetValue("ClientId", out var cid)
                            && string.Equals(cid as string, clientNumber, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? latestStatus = null;
            string? latestStatusDate = null;
            DateTime? bestDate = null;
            string? fallbackStatus = null;
            string? fallbackStatusDate = null;
            foreach (var r in forClient)
            {
                if (!r.TryGetValue("Status", out var st) || st is not string sStr) continue;
                r.TryGetValue("StatusDate", out var sd);
                // R6-33a — shared parse helper so the dashboard watermark
                // and this "latest" projection can never disagree.
                if (sd is string dStr && Internal.EfStatusDates.TryParse(dStr, out var dt))
                {
                    if (bestDate == null || dt > bestDate)
                    {
                        bestDate = dt;
                        latestStatus = sStr;
                        latestStatusDate = dStr;
                    }
                }
                else
                {
                    // R6-32b — track the LAST undateable row (overwritten on
                    // every hit) as the fallback: the EF join is append-
                    // ordered, so "last appended" is the best available
                    // proxy for "latest" when no row carries a parseable
                    // date. (The old code froze on the FIRST undateable
                    // row.)
                    fallbackStatus = sStr;
                    fallbackStatusDate = sd as string;
                }
            }
            if (bestDate == null && latestStatus == null)
            {
                latestStatus = fallbackStatus;
                latestStatusDate = fallbackStatusDate;
            }

            // R6-32a — truncation is computed against the FILTERED count
            // (rows that actually carry a string Status — the same predicate
            // that feeds perStateBreakdown), not forClient.Count. Otherwise
            // null-status join rows could flag truncation when nothing was
            // dropped (or mask a real drop).
            var withStatus = forClient
                .Where(r => r.TryGetValue("Status", out var st) && st is string)
                .ToList();
            var perStateRaw = withStatus
                .Take(PerStateBreakdownCap)
                .Select(r => new
                {
                    efId = r.TryGetValue("EFId", out var ef) ? ef as string : null,
                    instanceId = r.TryGetValue("InstanceId", out var inst) ? inst as string : null,
                    status = r.TryGetValue("Status", out var st) ? st as string : null,
                    statusDate = r.TryGetValue("StatusDate", out var sd) ? sd as string : null,
                })
                .ToList();

            return new
            {
                latestStatus,
                latestStatusDate,
                perStateBreakdown = perStateRaw,
                perStateBreakdownTruncated = withStatus.Count > PerStateBreakdownCap,
            };
        }
    }
}
