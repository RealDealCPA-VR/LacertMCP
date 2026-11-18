using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Proposals;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Proposal-then-approve (DECISIONS 2026-11-18): the agent prepares a
    /// proposal — every intended entry with target, value, source citation,
    /// reasoning and confidence, dry-run validated on the spot — plus manual
    /// items and questions, submits it, and a PERSON approves it on the control
    /// page or with <c>lacerte-mcp proposal approve</c>. Only then does
    /// <c>lacerte_proposal_commit</c> write the approved lines.
    ///
    /// <para><b>No tool here approves, rejects, returns or decides lines</b>
    /// (ProposalStore's reviewer methods); a reflection test pins that.
    /// Read-only devices may get / list but not create, edit or commit.</para>
    /// </summary>
    [McpServerToolType]
    public sealed class ProposalTools
    {
        private readonly LacerteSessionManager _session;
        private readonly ProposalStore? _store;

        public ProposalTools(LacerteSessionManager session, ProposalStore? store = null)
        {
            _session = session;
            _store = store ?? ProposalStore.Shared;
        }

        private ProposalService Service()
        {
            var store = _store ?? throw new LacerteSDKError(SyntheticStatusCodes.InternalServerError, new Dictionary<string, object?>
            {
                ["reason"] = "No proposal folder: set LACERTE_HTTP_DATA_DIR or APPDATA for the server.",
            });
            return new ProposalService(_session, store);
        }

        [McpServerTool]
        [Description("Start a proposal for one client in the ACTIVE context (dataPath, taxYear, module): the place to put every entry you intend to make, for the operator to review and approve before anything is written. Checks the client exists and records its number and name as the target return (state.entriesGoInto). Returns the proposal (id, status Draft, version).")]
        public Task<string> LacerteProposalCreate(
            [Description("Lacerte ClientNumber in the active context.")] string clientNumber,
            [Description("Short title, e.g. \"2024 1065 from trial balance\". Default: client + year + module.")] string? title = null,
            [Description("Optional notes for the reviewer (scope, documents used).")] string? notes = null,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                var p = await Service().CreateAsync(clientNumber, title, notes, cancellationToken).ConfigureAwait(false);
                return (object)View(p);
            });
        }

        [McpServerTool]
        [Description("Add lines to a Draft / ReturnedForChanges proposal. Each line is either a field write ({kind:\"field\", table, field, value, subClientId?, stateAbbrev?}) or a detail row ({kind:\"detail\", detail:{series, code, amount, description, suffix, prefix?, ...}}), with dictionaryId, source {document, page?, location?, excerpt?} (required), reasoning? and confidence (high|medium|low, required). Every line is dry-run validated immediately through the real write path (live detail rows: Suffix and the verified field dictionary are enforced); a line that fails is kept with preview.ok=false and can't be approved — fix it with lacerte_proposal_update_line or remove it. Nothing is written to the return. Changing an Approved proposal revokes its approval.")]
        public Task<string> LacerteProposalAddLines(
            [Description("The proposal id.")] string proposalId,
            [Description("Lines to add (at most 200 per call).")] ProposalLineInput[] lines,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                var (p, ids) = await Service().AddLinesAsync(proposalId, lines, cancellationToken).ConfigureAwait(false);
                var added = p.Lines.Where(l => ids.Contains(l.LineId)).Select(l => new
                {
                    lineId = l.LineId,
                    ok = l.Preview.Ok,
                    statusCode = l.Preview.StatusCode,
                    statusName = l.Preview.StatusName,
                    message = l.Preview.Message,
                    warnings = l.Warnings,
                }).ToList();
                return (object)new
                {
                    proposalId = p.Id,
                    status = p.Status.ToString(),
                    version = p.Version,
                    added,
                    invalidCount = added.Count(a => !a.ok),
                    note = added.Any(a => !a.ok)
                        ? "Some lines failed their dry run (ok=false): they can't be approved. Fix them with lacerte_proposal_update_line, remove them, or move them to manual items."
                        : "All lines passed their dry run. Nothing has been written to the return.",
                };
            });
        }

        [McpServerTool]
        [Description("Replace one line of a Draft / ReturnedForChanges proposal with new content (same shape as in lacerte_proposal_add_lines). The line is dry-run validated again and its reviewer decision goes back to Pending.")]
        public Task<string> LacerteProposalUpdateLine(
            [Description("The proposal id.")] string proposalId,
            [Description("The line id (e.g. \"L3\").")] string lineId,
            [Description("The line's new content.")] ProposalLineInput line,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                var p = await Service().UpdateLineAsync(proposalId, lineId, line, cancellationToken).ConfigureAwait(false);
                return (object)View(p);
            });
        }

        [McpServerTool]
        [Description("Remove a line from a Draft / ReturnedForChanges proposal.")]
        public Task<string> LacerteProposalRemoveLine(
            [Description("The proposal id.")] string proposalId,
            [Description("The line id (e.g. \"L3\").")] string lineId)
        {
            return WrapToolHandler.Run(() => Task.FromResult((object)View(Service().RemoveLine(proposalId, lineId))));
        }

        [McpServerTool]
        [Description("Record source lines you could NOT map to a verified target (no dictionary entry, unsupported screen, judgement needed): the preparer enters them by hand in Lacerte. Each item: {description, amount?, source?, reason}.")]
        public Task<string> LacerteProposalAddManualItems(
            [Description("The proposal id.")] string proposalId,
            [Description("Items for hand entry.")] ManualItemInput[] items)
        {
            return WrapToolHandler.Run(() => Task.FromResult((object)View(Service().AddManualItems(proposalId, items))));
        }

        [McpServerTool]
        [Description("Add open questions for the preparer to the proposal. Each: {text, lineIds?}.")]
        public Task<string> LacerteProposalAddQuestions(
            [Description("The proposal id.")] string proposalId,
            [Description("Questions for the preparer.")] QuestionInput[] questions)
        {
            return WrapToolHandler.Run(() => Task.FromResult((object)View(Service().AddQuestions(proposalId, questions))));
        }

        [McpServerTool]
        [Description("Submit a Draft / ReturnedForChanges proposal for review. Locks it against agent edits until the operator approves, rejects or returns it. Then tell the operator to review it on the control page (Proposals) or with `lacerte-mcp proposal show <id>` — they approve it with their approval PIN; you cannot approve it yourself.")]
        public Task<string> LacerteProposalSubmit(
            [Description("The proposal id.")] string proposalId)
        {
            return WrapToolHandler.Run(() => Task.FromResult((object)View(Service().Submit(proposalId))));
        }

        [McpServerTool]
        [Description("Read a proposal: lines with targets, values, sources, dry-run previews, reviewer decisions and commit results; manual items; questions; the approval record; and state (contentHash, approvalCurrent, readyToCommit, blockers). Works on read-only devices.")]
        public Task<string> LacerteProposalGet(
            [Description("The proposal id.")] string proposalId)
        {
            return WrapToolHandler.Run(() =>
            {
                var store = _store ?? throw ProposalStore.NotFound(proposalId);
                return Task.FromResult((object)View(store.GetRequired(proposalId)));
            });
        }

        [McpServerTool]
        [Description("List proposals (newest first), optionally for one client and/or one status (Draft, Submitted, Approved, Rejected, ReturnedForChanges, Committing, Committed, PartiallyCommitted, CommitFailed). Works on read-only devices.")]
        public Task<string> LacerteProposalList(
            [Description("Only this ClientNumber.")] string? clientNumber = null,
            [Description("Only this status.")] string? status = null)
        {
            return WrapToolHandler.Run(() =>
            {
                ProposalStatus? s = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    if (!Enum.TryParse<ProposalStatus>(status!.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(typeof(ProposalStatus), parsed))
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, new Dictionary<string, object?>
                        {
                            ["argument"] = "status",
                            ["value"] = status,
                            ["reason"] = "status must be one of: " + string.Join(", ", Enum.GetNames(typeof(ProposalStatus))) + ".",
                        });
                    }
                    s = parsed;
                }
                var rows = _store?.List(new ProposalFilter { ClientNumber = clientNumber, Status = s }) ?? Array.Empty<ProposalSummary>();
                return Task.FromResult((object)new { count = rows.Count, proposals = ProposalStore.ToJsonElement(rows) });
            });
        }

        [McpServerTool]
        [Description("Write an APPROVED proposal to the return. Refused unless a person approved it and it hasn't changed since (version + content hash), and unless the client still exists under the approved name (9024; nothing written). Writes only the approved lines, in the proposal's own context, through the guarded write paths (read-back, dictionary gate, backup, duplicate protection). Field failures are recorded and the commit continues; a detail-row error after which the row may be on the return stops it (that line is Uncertain: never retry it — the operator checks Lacerte and resolves it). A PartiallyCommitted / CommitFailed proposal can be committed again: only unfinished lines run. Report each line's result to the operator; verified=null means not confirmed by a read-back.")]
        public Task<string> LacerteProposalCommit(
            [Description("The proposal id.")] string proposalId,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                ProposalService.RequireAgentWriteAccess("ProposalCommit");
                if (_session.Context?.ReadOnly == true)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.FieldIsReadOnly, new Dictionary<string, object?>
                    {
                        ["operation"] = "ProposalCommit",
                        ["reason"] = "The active session context is read-only (lacerte_set_context readOnly=true); commits are refused.",
                    });
                }
                var report = await Service().CommitAsync(proposalId, null, cancellationToken).ConfigureAwait(false);
                return (object)ProposalStore.ToJsonElement(report);
            });
        }

        /// <summary>Tool response for a proposal: the stored record plus computed state.</summary>
        internal static object View(Proposal p) => ProposalStore.ToJsonElement(new
        {
            proposal = p,
            state = ProposalStore.Evaluate(p),
        });
    }
}
