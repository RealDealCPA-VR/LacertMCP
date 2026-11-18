using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Proposals;

namespace LacertMCP.Server.Web
{
    /// <summary>
    /// Control-page API for the proposal-then-approve workflow (DECISIONS
    /// 2026-11-18 "Approval model: Claude proposes, a person approves").
    ///
    /// <list type="bullet">
    ///   <item><c>GET  /api/proposals?status=&amp;client=</c> — summaries + computed state.</item>
    ///   <item><c>GET  /api/proposals/{id}</c> — the full proposal + state.</item>
    ///   <item><c>POST /api/proposals/{id}/line</c> {lineId, decision: approved|rejected|pending, note, expectedVersion}</item>
    ///   <item><c>POST /api/proposals/{id}/approve</c> {expectedVersion, note}</item>
    ///   <item><c>POST /api/proposals/{id}/reject</c> {note} (note required)</item>
    ///   <item><c>POST /api/proposals/{id}/return</c> {note} (note required)</item>
    ///   <item><c>POST /api/proposals/{id}/resolve</c> {lineId, rowIsInReturn, note}</item>
    ///   <item><c>POST /api/proposals/{id}/commit</c> {expectedVersion}</item>
    /// </list>
    ///
    /// <para><b>Who may call them.</b> Every endpoint here — the reads too, since
    /// proposals hold taxpayer data — is for a PERSON: this computer or the
    /// owner's own (untagged, same-login) tailnet devices, exactly the callers
    /// that may open the control page. A device's Read / Read &amp; write grant
    /// is for the agents running on it and never allows reviewing (agents run on
    /// those devices). Every request must also come from the control page itself
    /// (X-Lacerte-Admin header + same-origin), reads included. Bodies are capped
    /// by <see cref="LimitedReadStream"/>.</para>
    ///
    /// <para>The reviewer is recorded as the caller's identity (owner login + device,
    /// or "this computer (&lt;Windows user&gt;)") with channel "control-page". Approve,
    /// reject, return, resolve and commit are recorded in the activity log with
    /// ids, the (masked) client number and counts only — never values, notes or
    /// source text.</para>
    /// </summary>
    public sealed partial class HttpServer
    {
        private const string ProposalsApiPath = "/api/proposals";

        internal static bool IsProposalPath(string path)
            => path == ProposalsApiPath || path.StartsWith(ProposalsApiPath + "/", StringComparison.Ordinal);

        /// <summary>
        /// Same rule as the control page and /api/access: this computer, or a
        /// tailnet device owned by the same (untagged) Tailscale login as this PC.
        /// Device access grants are NOT consulted.
        /// </summary>
        internal static bool MayReview(Caller caller) => caller.IsLocal || caller.IsOwner;

        /// <summary>Who the approval / commit is recorded as.</summary>
        internal static string ReviewerIdentity(Caller caller)
        {
            if (caller.IsLocal) return "this computer (" + Environment.UserName + ")";
            return string.IsNullOrWhiteSpace(caller.Login) ? caller.Name : caller.Login + " on " + caller.Name;
        }

        private ProposalService? ProposalServiceOrNull() => _services.GetService(typeof(ProposalService)) as ProposalService;

        /// <summary>Counts for the tab badge (no taxpayer data); null when proposals aren't available.</summary>
        private object? ProposalCounts()
        {
            var service = ProposalServiceOrNull();
            if (service == null) return null;
            try
            {
                var all = service.Store.List();
                return new
                {
                    awaitingReview = all.Count(s => s.Status == ProposalStatus.Submitted),
                    toCommit = all.Count(s => s.Status == ProposalStatus.Approved || s.Status == ProposalStatus.PartiallyCommitted || s.Status == ProposalStatus.CommitFailed),
                    total = all.Count,
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task HandleProposalApiAsync(HttpListenerContext ctx, Caller caller, string path)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            if (!MayReview(caller))
            {
                _activity.RecordThrottled("proposal-refused:" + caller.StableId, RepeatEventWindow, "warn", "security",
                    "Refused a proposal request from a device that isn't this computer or the owner's", caller.Name);
                await SendJson(res, 403, new { error = "Only this computer and the owner's own tailnet devices can see, approve or commit proposals. A device's Read & write access is for its agents and does not allow reviewing." }).ConfigureAwait(false);
                return;
            }
            if (!IsFromControlPage(req))
            {
                await SendJson(res, 403, new { error = "Refused: this request did not come from the control page." }).ConfigureAwait(false);
                return;
            }
            var service = ProposalServiceOrNull();
            if (service == null)
            {
                await SendJson(res, 503, new { error = "Proposals aren't available: the server has no folder to keep them in. Set LACERTE_HTTP_DATA_DIR (or make sure APPDATA is set) for the server and restart it." }).ConfigureAwait(false);
                return;
            }
            var store = service.Store;

            var rest = path.Substring(ProposalsApiPath.Length).Trim('/');
            var parts = rest.Length == 0 ? new string[0] : rest.Split('/');
            if (parts.Length > 2 || (parts.Length >= 1 && !ProposalStore.IsValidId(parts[0])))
            {
                await SendJson(res, 404, new { error = "No such proposal." }).ConfigureAwait(false);
                return;
            }

            try
            {
                if (req.HttpMethod == "GET")
                {
                    if (parts.Length == 0) await ListProposalsAsync(req, res, store).ConfigureAwait(false);
                    else if (parts.Length == 1) await GetProposalAsync(res, store, parts[0]).ConfigureAwait(false);
                    else await SendJson(res, 405, new { error = "Method not allowed." }).ConfigureAwait(false);
                    return;
                }
                if (req.HttpMethod != "POST" || parts.Length != 2)
                {
                    await SendJson(res, 405, new { error = "Method not allowed." }).ConfigureAwait(false);
                    return;
                }

                var (body, bodyStatus, bodyError) = await ReadJsonObjectAsync(req).ConfigureAwait(false);
                if (bodyError != null)
                {
                    await SendJson(res, bodyStatus, new { error = bodyError }).ConfigureAwait(false);
                    return;
                }
                await HandleProposalActionAsync(res, service, caller, parts[0], parts[1], body).ConfigureAwait(false);
            }
            catch (LacerteSDKError ex)
            {
                await SendProposalErrorAsync(res, store, ex).ConfigureAwait(false);
            }
        }

        private async Task ListProposalsAsync(HttpListenerRequest req, HttpListenerResponse res, ProposalStore store)
        {
            var filter = new ProposalFilter();
            var status = (req.QueryString["status"] ?? string.Empty).Trim();
            if (status.Length > 0)
            {
                if (!Enum.TryParse<ProposalStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(typeof(ProposalStatus), parsed))
                {
                    await SendJson(res, 400, new { error = "status must be one of: " + string.Join(", ", Enum.GetNames(typeof(ProposalStatus))) + "." }).ConfigureAwait(false);
                    return;
                }
                filter.Status = parsed;
            }
            var client = (req.QueryString["client"] ?? string.Empty).Trim();
            if (client.Length > 64) client = client.Substring(0, 64);
            if (client.Length > 0) filter.ClientNumber = client;

            var items = new List<object>();
            foreach (var s in store.List(filter))
            {
                Proposal? p;
                try { p = store.Get(s.Id); }
                catch (Exception ex) when (ex is System.IO.IOException || ex is JsonException || ex is UnauthorizedAccessException) { p = null; }
                items.Add(new
                {
                    summary = ProposalStore.ToJsonElement(s),
                    state = p == null ? (JsonElement?)null : ProposalStore.ToJsonElement(ProposalStore.Evaluate(p)),
                });
            }
            await SendJson(res, 200, new { count = items.Count, folder = store.DirectoryPath, proposals = items }).ConfigureAwait(false);
        }

        private async Task GetProposalAsync(HttpListenerResponse res, ProposalStore store, string id)
        {
            var p = store.Get(id);
            if (p == null)
            {
                await SendJson(res, 404, new { error = "No such proposal." }).ConfigureAwait(false);
                return;
            }
            await SendJson(res, 200, ProposalView(p)).ConfigureAwait(false);
        }

        private static object ProposalView(Proposal p) => new
        {
            proposal = ProposalStore.ToJsonElement(p),
            state = ProposalStore.ToJsonElement(ProposalStore.Evaluate(p)),
        };

        private async Task HandleProposalActionAsync(HttpListenerResponse res, ProposalService service, Caller caller, string id, string action, JsonElement body)
        {
            var store = service.Store;
            var by = ReviewerIdentity(caller);
            var channel = ProposalStore.ChannelControlPage;
            var note = NullIfBlank(Str(body, "note"));
            switch (action)
            {
                case "line":
                {
                    var lineId = Str(body, "lineId").Trim();
                    if (lineId.Length == 0) { await BadRequest(res, "lineId is required.").ConfigureAwait(false); return; }
                    ReviewerDecision decision;
                    switch (Str(body, "decision").Trim().ToLowerInvariant())
                    {
                        case "approved": decision = ReviewerDecision.Approved; break;
                        case "rejected": decision = ReviewerDecision.Rejected; break;
                        case "pending": decision = ReviewerDecision.Pending; break;
                        default: await BadRequest(res, "decision must be approved, rejected or pending.").ConfigureAwait(false); return;
                    }
                    if (!TryInt(body, "expectedVersion", out var version)) { await BadRequest(res, "expectedVersion is required (the version you reviewed).").ConfigureAwait(false); return; }
                    var p = store.SetLineDecision(id, lineId, decision, note, version);
                    await SendJson(res, 200, ProposalView(p)).ConfigureAwait(false);
                    return;
                }
                case "approve":
                {
                    if (!TryInt(body, "expectedVersion", out var version)) { await BadRequest(res, "expectedVersion is required (the version you reviewed).").ConfigureAwait(false); return; }
                    var p = store.Approve(id, by, channel, version, note);
                    var approved = p.Lines.Count(l => l.ReviewerDecision == ReviewerDecision.Approved);
                    var rejected = p.Lines.Count(l => l.ReviewerDecision == ReviewerDecision.Rejected);
                    _activity.Record("success", "proposal", "Approved proposal " + p.Id + " for client " + ToolActivity.Plain(p.ClientNumber), caller.Name,
                        approved + " line(s) approved, " + rejected + " rejected; version " + p.Version);
                    await SendJson(res, 200, ProposalView(p)).ConfigureAwait(false);
                    return;
                }
                case "reject":
                case "return":
                {
                    if (note == null)
                    {
                        await BadRequest(res, action == "reject"
                            ? "Say why the proposal is rejected (a note is required)."
                            : "Say what the agent should change (a note is required).").ConfigureAwait(false);
                        return;
                    }
                    var p = action == "reject" ? store.Reject(id, by, channel, note) : store.ReturnForChanges(id, by, channel, note);
                    _activity.Record(action == "reject" ? "warn" : "info", "proposal",
                        (action == "reject" ? "Rejected proposal " : "Returned proposal ") + p.Id + (action == "reject" ? "" : " for changes") + " (client " + ToolActivity.Plain(p.ClientNumber) + ")",
                        caller.Name, "version " + p.Version);
                    await SendJson(res, 200, ProposalView(p)).ConfigureAwait(false);
                    return;
                }
                case "resolve":
                {
                    var lineId = Str(body, "lineId").Trim();
                    if (lineId.Length == 0) { await BadRequest(res, "lineId is required.").ConfigureAwait(false); return; }
                    if (!TryBool(body, "rowIsInReturn", out var inReturn)) { await BadRequest(res, "rowIsInReturn is required: true if you found the row in Lacerte, false if it is missing.").ConfigureAwait(false); return; }
                    var p = store.ResolveUncertainLine(id, lineId, inReturn, by, channel, note);
                    var resolved = p.Lines.FirstOrDefault(l => string.Equals(l.LineId, lineId, StringComparison.OrdinalIgnoreCase))?.LineId ?? "?";
                    _activity.Record("info", "proposal", "Resolved line " + ToolActivity.Plain(resolved) + " of proposal " + p.Id + " (client " + ToolActivity.Plain(p.ClientNumber) + ")",
                        caller.Name, inReturn ? "found in the return" : "not in the return; the next commit writes it");
                    await SendJson(res, 200, ProposalView(p)).ConfigureAwait(false);
                    return;
                }
                case "commit":
                {
                    if (!TryInt(body, "expectedVersion", out var version)) { await BadRequest(res, "expectedVersion is required (the version you approved).").ConfigureAwait(false); return; }
                    var current = store.Get(id);
                    if (current == null)
                    {
                        await SendJson(res, 404, new { error = "No such proposal." }).ConfigureAwait(false);
                        return;
                    }
                    if (current.Version != version) throw ProposalStore.Conflict(current, "The proposal is at version " + current.Version + ", not " + version + ": it changed since you loaded it. Reload and review it again.");
                    ProposalCommitReport report;
                    try
                    {
                        report = await service.CommitAsync(id, by, _stop.Token).ConfigureAwait(false);
                    }
                    catch (LacerteSDKError ex)
                    {
                        _activity.Record("warn", "write", "Commit of proposal " + current.Id + " refused (client " + ToolActivity.Plain(current.ClientNumber) + ")",
                            caller.Name, ex.StatusName);
                        throw;
                    }
                    RecordCommit(report, current.ClientNumber, caller);
                    var after = store.Get(id) ?? current;
                    await SendJson(res, 200, new
                    {
                        report = ProposalStore.ToJsonElement(report),
                        proposal = ProposalStore.ToJsonElement(after),
                        state = ProposalStore.ToJsonElement(ProposalStore.Evaluate(after)),
                    }).ConfigureAwait(false);
                    return;
                }
                default:
                    await SendJson(res, 404, new { error = "Not found." }).ConfigureAwait(false);
                    return;
            }
        }

        /// <summary>A commit in the activity log: counts only; verified when every line it ran was read back.</summary>
        private void RecordCommit(ProposalCommitReport report, string clientNumber, Caller caller)
        {
            var written = report.SucceededCount + report.SucceededUnverifiedCount;
            var level = report.Status == ProposalStatus.Committed ? "success"
                : report.Status == ProposalStatus.PartiallyCommitted ? "warn"
                : "error";
            var verb = report.Status == ProposalStatus.Committed ? "Committed proposal "
                : report.Status == ProposalStatus.PartiallyCommitted ? "Partly committed proposal "
                : "Commit failed for proposal ";
            var detail = written + " of " + report.RanCount + " line(s) written (" + report.SucceededCount + " verified)"
                + (report.FailedCount > 0 ? ", " + report.FailedCount + " failed" : "")
                + (report.UncertainCount > 0 ? ", " + report.UncertainCount + " may be in the return (check and resolve)" : "")
                + (report.SkippedCount > 0 ? ", " + report.SkippedCount + " skipped" : "")
                + "; version " + report.Version;
            _activity.Append(new ActivityEvent
            {
                At = DateTime.UtcNow,
                Level = level,
                Category = "write",
                Message = verb + report.ProposalId + " (client " + ToolActivity.Plain(clientNumber) + ")",
                Device = caller.Name,
                Detail = detail,
                Verified = report.Status == ProposalStatus.Committed && report.RanCount > 0 && report.SucceededCount == report.RanCount,
            });
        }

        /// <summary>
        /// 9024 ProposalConflict → 409 (with the proposal's current status, version and
        /// blockers), 9017 InvalidArgument → 400 (404 for an unknown proposal), anything
        /// else → 500. The reason is the store's own text (ids and statuses, no values).
        /// </summary>
        private static async Task SendProposalErrorAsync(HttpListenerResponse res, ProposalStore store, LacerteSDKError ex)
        {
            var reason = ex.Context.TryGetValue("reason", out var r) ? r as string : null;
            int http;
            if (ex.StatusCode == SyntheticStatusCodes.ProposalConflict) http = 409;
            else if (ex.StatusCode == SyntheticStatusCodes.InvalidArgument)
                http = ex.Context.TryGetValue("argument", out var a) && (a as string) == "proposalId" ? 404 : 400;
            else http = 500;

            List<string>? blockers = null;
            int? version = null;
            string? current = null;
            if (http == 409 && ex.Context.TryGetValue("proposalId", out var pid) && pid is string id)
            {
                try
                {
                    var p = store.Get(id);
                    if (p != null)
                    {
                        blockers = ProposalStore.Evaluate(p).Blockers;
                        version = p.Version;
                        current = p.Status.ToString();
                    }
                }
                catch
                {
                    // best effort: the reason already says what went wrong
                }
            }
            await SendJson(res, http, new
            {
                error = string.IsNullOrWhiteSpace(reason) ? ex.UserMessage : reason,
                statusCode = ex.StatusCode,
                statusName = ex.StatusName,
                proposalStatus = current,
                version,
                blockers,
            }).ConfigureAwait(false);
        }

        private static Task BadRequest(HttpListenerResponse res, string message) => SendJson(res, 400, new { error = message });

        /// <summary>Reads a capped JSON object body. Returns (body, 0, null) or (default, status, error).</summary>
        private static async Task<(JsonElement Body, int Status, string? Error)> ReadJsonObjectAsync(HttpListenerRequest req)
        {
            if (req.ContentLength64 > MaxApiBodyBytes) return (default, 413, "Request too large.");
            try
            {
                using var limited = new LimitedReadStream(req.InputStream, MaxApiBodyBytes);
                using var doc = await JsonDocument.ParseAsync(limited).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (default, 400, "The body must be a JSON object.");
                return (doc.RootElement.Clone(), 0, null);
            }
            catch (RequestTooLargeException)
            {
                return (default, 413, "Request too large.");
            }
            catch (JsonException)
            {
                return (default, 400, "The body must be a JSON object.");
            }
        }

        private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static bool TryInt(JsonElement e, string name, out int value)
        {
            value = 0;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                   && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
        }

        private static bool TryBool(JsonElement e, string name, out bool value)
        {
            value = false;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) { value = true; return true; }
            return v.ValueKind == JsonValueKind.False;
        }
    }
}
