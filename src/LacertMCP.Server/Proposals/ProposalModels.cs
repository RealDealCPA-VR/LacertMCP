using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LacertMCP.Server.Proposals
{
    // ---------------------------------------------------------------------
    // Proposal model (DECISIONS 2026-11-18 "Approval model: Claude proposes,
    // a person approves, then the return is written").
    //
    // A proposal is everything Claude intends to enter for one client in one
    // (dataPath, taxYear, module) context: the lines it will write, the
    // source lines it couldn't map (manual items) and its questions for the
    // preparer. It is persisted as JSON by ProposalStore. Nothing in it is
    // written to the return until a PERSON approves the exact version
    // (ProposalStore.Approve, called only by the control page and the
    // `lacerte-mcp proposal` CLI — never by an MCP tool) and the commit runs
    // (ProposalService.CommitAsync).
    //
    // These files hold TAXPAYER DATA (amounts, descriptions, source excerpts).
    // ---------------------------------------------------------------------

    /// <summary>Lifecycle. Draft → Submitted → Approved | Rejected | ReturnedForChanges → Committing → Committed | PartiallyCommitted | CommitFailed.</summary>
    public enum ProposalStatus
    {
        Draft,
        Submitted,
        Approved,
        Rejected,
        ReturnedForChanges,
        Committing,
        Committed,
        PartiallyCommitted,
        CommitFailed,
    }

    public enum ProposalLineKind
    {
        Field,
        Detail,
    }

    public enum ProposalConfidence
    {
        High,
        Medium,
        Low,
    }

    public enum ReviewerDecision
    {
        Pending,
        Approved,
        Rejected,
    }

    /// <summary>Per-line commit outcome.</summary>
    public enum LineCommitStatus
    {
        /// <summary>The write was started and its result wasn't recorded (the server stopped mid-write). Resolved on the next commit.</summary>
        Running,
        /// <summary>Written and read back from Lacerte: the value / row is on the return.</summary>
        Succeeded,
        /// <summary>Written (accepted) but not confirmed by a read-back; check it in Lacerte. Never re-run (a detail row would be duplicated).</summary>
        SucceededUnverified,
        /// <summary>Not written. A re-commit runs it again.</summary>
        Failed,
        /// <summary>A detail row that may or may not be on the return (rowWritten yes/unknown, 9022). Never re-run automatically; a person checks Lacerte and resolves it.</summary>
        Uncertain,
        /// <summary>Not run because the commit stopped earlier. A re-commit runs it.</summary>
        Skipped,
    }

    /// <summary>The (dataPath, taxYear, module) the proposal was prepared in; the commit writes there.</summary>
    public sealed class ProposalContext
    {
        public string DataPath { get; set; } = string.Empty;
        public int TaxYear { get; set; }
        /// <summary>Single module letter (I, C, P, S, F, ...).</summary>
        public string Module { get; set; } = string.Empty;
    }

    /// <summary>
    /// Which client return the entries go into, captured from the client row when
    /// the proposal was created. Part of the content hash; the approval records it
    /// and the commit refuses when the client is gone or its name changed.
    /// </summary>
    public sealed class ProposalTargetReturn
    {
        public string ClientNumber { get; set; } = string.Empty;
        /// <summary>Individual: first + last name (C1_1, C1_2); other modules: the entity name (C1_1). Empty when the return has no name.</summary>
        public string ClientName { get; set; } = string.Empty;
        /// <summary>The client's ClientID when the row carries one (simulation); null otherwise.</summary>
        public string? ClientId { get; set; }
    }

    /// <summary>Where a value came from.</summary>
    public sealed class SourceCitation
    {
        /// <summary>Document name, e.g. "2024 W-2 ACME Corp.pdf".</summary>
        public string Document { get; set; } = string.Empty;
        public int? Page { get; set; }
        /// <summary>Box / line / cell, e.g. "Box 1".</summary>
        public string? Location { get; set; }
        /// <summary>Short quote of the source text.</summary>
        public string? Excerpt { get; set; }
    }

    /// <summary>A detail-file row (mirrors Session.DetailRow, with settable properties for JSON).</summary>
    public sealed class ProposalDetailRow
    {
        public string Series { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public double? Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Prefix { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
        public string StateAbbrev { get; set; } = string.Empty;
        public string CityAbbr { get; set; } = string.Empty;
        /// <summary>The SDK detail Source flag (blank / Source / Nonsource) — not the citation.</summary>
        public string Source { get; set; } = string.Empty;
        public string Tsj { get; set; } = string.Empty;
        public string ImportSrc { get; set; } = string.Empty;
    }

    /// <summary>What a line writes. Field lines use Table/Field (+ SubClientId / StateAbbrev); detail lines use Detail.</summary>
    public sealed class ProposalTarget
    {
        public int? Table { get; set; }
        public string? Field { get; set; }
        public int? SubClientId { get; set; }
        public string? StateAbbrev { get; set; }
        public ProposalDetailRow? Detail { get; set; }
    }

    /// <summary>The dry-run result recorded when the line was added or changed.</summary>
    public sealed class LinePreview
    {
        /// <summary>True when the dry run passed. A line with Ok=false can't be approved.</summary>
        public bool Ok { get; set; }
        public int? StatusCode { get; set; }
        public string? StatusName { get; set; }
        public string? Message { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    public sealed class LineCommitResult
    {
        public LineCommitStatus Status { get; set; }
        public int? StatusCode { get; set; }
        public string? StatusName { get; set; }
        /// <summary>true = read back and confirmed; null = not confirmed (see Note).</summary>
        public bool? Verified { get; set; }
        public string? Note { get; set; }
        /// <summary>Live detail rows: the client's pre-write backup file.</summary>
        public string? Snapshot { get; set; }
        public string? IdempotencyKey { get; set; }
        public DateTimeOffset At { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class ProposalLine
    {
        /// <summary>Stable id within the proposal ("L1", "L2", ...); never reused.</summary>
        public string LineId { get; set; } = string.Empty;
        public ProposalLineKind Kind { get; set; }
        public ProposalTarget Target { get; set; } = new ProposalTarget();
        /// <summary>Field lines: the value to write (JSON scalar). Null for detail lines (the amount is in Target.Detail).</summary>
        public JsonElement? Value { get; set; }
        /// <summary>The field-dictionary entry this line relies on (lacerte_field_dictionary id).</summary>
        public string? DictionaryId { get; set; }
        public SourceCitation Source { get; set; } = new SourceCitation();
        public string? Reasoning { get; set; }
        public ProposalConfidence Confidence { get; set; } = ProposalConfidence.Medium;
        public LinePreview Preview { get; set; } = new LinePreview();
        /// <summary>Non-blocking notes from validation (e.g. dictionary id not found).</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        public ReviewerDecision ReviewerDecision { get; set; } = ReviewerDecision.Pending;
        public string? ReviewerNote { get; set; }
        public LineCommitResult? Commit { get; set; }
        /// <summary>Bumped when a person resolves an Uncertain detail row as missing, so the re-commit uses a fresh idempotency key.</summary>
        public int KeyGeneration { get; set; }
    }

    /// <summary>A source line Claude could not map to a dictionary target; the preparer enters it by hand.</summary>
    public sealed class ManualItem
    {
        public string ItemId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double? Amount { get; set; }
        public SourceCitation? Source { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class ProposalQuestion
    {
        public string QuestionId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        /// <summary>Lines the question is about (optional).</summary>
        public List<string>? LineIds { get; set; }
    }

    /// <summary>The review record of a person's approval.</summary>
    public sealed class ProposalApproval
    {
        public string By { get; set; } = string.Empty;
        /// <summary>"control-page" or "cli".</summary>
        public string Channel { get; set; } = string.Empty;
        public DateTimeOffset At { get; set; }
        public int Version { get; set; }
        /// <summary>ProposalStore.ComputeContentHash at approval time; the commit refuses if it no longer matches.</summary>
        public string ContentHash { get; set; } = string.Empty;
        public Dictionary<string, ReviewerDecision> LineDecisions { get; set; } = new Dictionary<string, ReviewerDecision>();
        public string? Note { get; set; }
        /// <summary>The return the person approved the entries for (client number + name); the commit checks the return still matches.</summary>
        public ProposalTargetReturn? TargetReturn { get; set; }
        /// <summary>The (dataPath, taxYear, module) the person approved.</summary>
        public ProposalContext? TargetContext { get; set; }
        /// <summary>True when the approval PIN was verified for this approval.</summary>
        public bool PinVerified { get; set; }
    }

    /// <summary>One entry in the proposal's audit trail.</summary>
    public sealed class ProposalEvent
    {
        public DateTimeOffset At { get; set; }
        public string By { get; set; } = string.Empty;
        /// <summary>"mcp", "control-page", "cli", "commit".</summary>
        public string Channel { get; set; } = string.Empty;
        /// <summary>created, edited, submitted, lineDecision, approved, rejected, returnedForChanges, approvalRevoked, commitStarted, commitFinished, lineResolved.</summary>
        public string Action { get; set; } = string.Empty;
        public int Version { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>Who is (or was) committing, so an interrupted commit can be detected.</summary>
    public sealed class CommitLease
    {
        public string By { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
    }

    public sealed class Proposal
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        /// <summary>Device / connection name of the agent that created it ("stdio" outside HTTP mode).</summary>
        public string Author { get; set; } = string.Empty;
        public ProposalContext Context { get; set; } = new ProposalContext();
        public string ClientNumber { get; set; } = string.Empty;
        /// <summary>The client identity captured at create; null on proposals made before it was recorded (they can't be approved).</summary>
        public ProposalTargetReturn? TargetReturn { get; set; }
        public string Title { get; set; } = string.Empty;
        public ProposalStatus Status { get; set; } = ProposalStatus.Draft;
        /// <summary>Bumps on every content change. An approval is for one version.</summary>
        public int Version { get; set; } = 1;
        public List<ProposalLine> Lines { get; set; } = new List<ProposalLine>();
        public List<ManualItem> ManualItems { get; set; } = new List<ManualItem>();
        public List<ProposalQuestion> Questions { get; set; } = new List<ProposalQuestion>();
        public string? Notes { get; set; }
        public ProposalApproval? Approval { get; set; }
        /// <summary>The last reject / return-for-changes note from the reviewer.</summary>
        public ProposalEvent? LastReview { get; set; }
        public CommitLease? CommitLease { get; set; }
        public List<ProposalEvent> History { get; set; } = new List<ProposalEvent>();
        public int NextLineNumber { get; set; } = 1;
        public int NextItemNumber { get; set; } = 1;
        public int NextQuestionNumber { get; set; } = 1;
    }

    /// <summary>List filter (all optional; null = any).</summary>
    public sealed class ProposalFilter
    {
        public string? ClientNumber { get; set; }
        public ProposalStatus? Status { get; set; }
        public string? DataPath { get; set; }
        public int? TaxYear { get; set; }
        public string? Module { get; set; }
    }

    /// <summary>One row of ProposalStore.List.</summary>
    public sealed class ProposalSummary
    {
        public string Id { get; set; } = string.Empty;
        public string ClientNumber { get; set; } = string.Empty;
        /// <summary>The client's name captured at create (null when not recorded).</summary>
        public string? ClientName { get; set; }
        /// <summary>"client 1001 · John Smith · Individual (1040) 2024 · C:\..." — where the entries go.</summary>
        public string EntriesGoInto { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public ProposalStatus Status { get; set; }
        public int Version { get; set; }
        public string Author { get; set; } = string.Empty;
        public ProposalContext Context { get; set; } = new ProposalContext();
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int LineCount { get; set; }
        public int InvalidLineCount { get; set; }
        public int PendingLineCount { get; set; }
        public int ApprovedLineCount { get; set; }
        public int RejectedLineCount { get; set; }
        public int ManualItemCount { get; set; }
        public int QuestionCount { get; set; }
    }

    /// <summary>Computed state for a reviewer UI / the agent (ProposalStore.Evaluate).</summary>
    public sealed class ProposalState
    {
        public string ContentHash { get; set; } = string.Empty;
        /// <summary>"client 1001 · John Smith · Individual (1040) 2024 · C:\..." — the return the entries go into.</summary>
        public string EntriesGoInto { get; set; } = string.Empty;
        /// <summary>An approval exists and still matches the current version and content hash.</summary>
        public bool ApprovalCurrent { get; set; }
        /// <summary>Approve() would succeed (Submitted, every line decided, at least one approved, no approved invalid line).</summary>
        public bool ReadyToApprove { get; set; }
        /// <summary>The commit would run (approved + unchanged, and approved lines are left to write).</summary>
        public bool ReadyToCommit { get; set; }
        /// <summary>Agents may edit (Draft / ReturnedForChanges; Approved edits revoke the approval).</summary>
        public bool AgentEditable { get; set; }
        public int PendingLineCount { get; set; }
        public int InvalidLineCount { get; set; }
        public int ApprovedLineCount { get; set; }
        public int RejectedLineCount { get; set; }
        /// <summary>Approved lines still to write on the next commit (no result, Failed, Skipped).</summary>
        public int UnfinishedApprovedLineCount { get; set; }
        /// <summary>Approved detail lines that may be on the return; a person must resolve them.</summary>
        public List<string> UncertainLineIds { get; set; } = new List<string>();
        /// <summary>Why ReadyToApprove / ReadyToCommit is false (empty when both are true or not applicable).</summary>
        public List<string> Blockers { get; set; } = new List<string>();
    }

    /// <summary>Result of ProposalService.CommitAsync.</summary>
    public sealed class ProposalCommitReport
    {
        public string ProposalId { get; set; } = string.Empty;
        public ProposalStatus Status { get; set; }
        public int Version { get; set; }
        public int ApprovedLineCount { get; set; }
        /// <summary>Lines run by THIS commit call.</summary>
        public int RanCount { get; set; }
        public int SucceededCount { get; set; }
        public int SucceededUnverifiedCount { get; set; }
        public int FailedCount { get; set; }
        public int UncertainCount { get; set; }
        public int SkippedCount { get; set; }
        /// <summary>Lines already finished by an earlier commit and not run again.</summary>
        public int AlreadyDoneCount { get; set; }
        /// <summary>True when a detail row may have landed after an error, so the commit stopped.</summary>
        public bool StoppedEarly { get; set; }
        public List<CommitLineReport> Lines { get; set; } = new List<CommitLineReport>();
        public string Message { get; set; } = string.Empty;
    }

    public sealed class CommitLineReport
    {
        public string LineId { get; set; } = string.Empty;
        public ProposalLineKind Kind { get; set; }
        public bool Ran { get; set; }
        public LineCommitResult? Result { get; set; }
    }
}
