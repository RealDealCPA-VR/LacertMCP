using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Proposals;
using Xunit;
using static LacertMCP.Tests.Proposals.ProposalTestSupport;

namespace LacertMCP.Tests.Proposals
{
    /// <summary>
    /// Proposal store: persistence, content hash, status machine and the human
    /// approval rules (DECISIONS 2026-11-18 "Approval model").
    /// </summary>
    public sealed class ProposalStoreTests : IDisposable
    {
        private readonly string _dir = NewTempDir();
        private readonly ProposalStore _store;

        public ProposalStoreTests()
        {
            _store = NewStore(_dir);
        }

        public void Dispose() => TryDelete(_dir);

        private static int Code(Action a) => Assert.Throws<LacerteSDKError>(a).StatusCode;

        // ---- persistence ----------------------------------------------------------------

        [Fact]
        public void Create_PersistsJson_WithReadmeAndUrlSafeId()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"));
            Assert.Matches("^[a-z0-9]{24}$", p.Id);
            var path = Path.Combine(_dir, p.Id + ".json");
            Assert.True(File.Exists(path));
            Assert.Contains("TAXPAYER DATA", File.ReadAllText(Path.Combine(_dir, "README.txt")));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            Assert.Empty(Directory.GetFiles(_dir, "*.lock"));

            var json = File.ReadAllText(path);
            Assert.Contains("\"status\": \"Draft\"", json);
            Assert.Contains("\"clientNumber\": \"1001\"", json);

            var back = _store.Get(p.Id)!;
            Assert.Equal(ProposalStatus.Draft, back.Status);
            Assert.Equal(2, back.Version); // created v1, one content edit
            Assert.Equal("L1", back.Lines.Single().LineId);
            Assert.Equal("\"x\"", back.Lines[0].Value!.Value.GetRawText());
            Assert.Equal(ProposalStore.ComputeContentHash(p), ProposalStore.ComputeContentHash(back));
        }

        [Fact]
        public void Get_InvalidOrUnknownId_IsNull_AndRequiredIs9017()
        {
            Assert.Null(_store.Get("..\\..\\etc"));
            Assert.Null(_store.Get("abcdefabcdef"));
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Code(() => _store.GetRequired("nope-not-valid")));
        }

        [Fact]
        public void List_FiltersByClientAndStatus()
        {
            var a = NewProposal(_store, FieldLine("C1_15", "x"));
            var b = _store.Create(Ctx(), "2002", "other", null, "stdio", Target("2002"));
            _store.Submit(a.Id, "stdio");
            Assert.Equal(2, _store.List().Count);
            Assert.Equal(a.Id, _store.List(new ProposalFilter { Status = ProposalStatus.Submitted }).Single().Id);
            Assert.Equal(b.Id, _store.List(new ProposalFilter { ClientNumber = "2002" }).Single().Id);
            Assert.Empty(_store.List(new ProposalFilter { TaxYear = 2023 }));
            var s = _store.List(new ProposalFilter { ClientNumber = "1001" }).Single();
            Assert.Equal(1, s.LineCount);
            Assert.Equal(1, s.PendingLineCount);
        }

        [Fact]
        public void ResolveDirectory_UsesHttpDataDir_ElseAppData()
        {
            Assert.Equal(Path.Combine(@"D:\data", "proposals"),
                ProposalStore.ResolveDirectory(new Dictionary<string, string?> { ["LACERTE_HTTP_DATA_DIR"] = @"D:\data", ["APPDATA"] = @"C:\x" }));
            Assert.Equal(Path.Combine(@"C:\x", "lacerte-mcp", "proposals"),
                ProposalStore.ResolveDirectory(new Dictionary<string, string?> { ["APPDATA"] = @"C:\x" }));
            Assert.Null(ProposalStore.ResolveDirectory(new Dictionary<string, string?>()));
        }

        // ---- content hash -----------------------------------------------------------------

        [Fact]
        public void ContentHash_IsStable_AndIgnoresReviewAndCommitState()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"), DetailLine(100.5, "ACME"));
            var h = ProposalStore.ComputeContentHash(p);
            Assert.Matches("^[0-9a-f]{64}$", h);
            Assert.Equal(h, ProposalStore.ComputeContentHash(_store.Get(p.Id)!));

            p.Lines[0].ReviewerDecision = ReviewerDecision.Approved;
            p.Lines[0].ReviewerNote = "fine";
            p.Lines[0].Preview = new LinePreview { Ok = false, Message = "changed" };
            p.Lines[1].Commit = new LineCommitResult { Status = LineCommitStatus.Succeeded };
            p.Status = ProposalStatus.Committed;
            p.History.Add(new ProposalEvent { Action = "x" });
            p.UpdatedAt = DateTimeOffset.UtcNow.AddDays(1);
            Assert.Equal(h, ProposalStore.ComputeContentHash(p));
        }

        [Fact]
        public void ContentHash_ChangesWithAnyApprovedContent()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"), DetailLine(100.5, "ACME"));
            var h = ProposalStore.ComputeContentHash(p);

            string With(Action<Proposal> change)
            {
                var copy = _store.Get(p.Id)!;
                change(copy);
                return ProposalStore.ComputeContentHash(copy);
            }

            Assert.NotEqual(h, With(q => q.Lines[0].Value = JsonSerializer.SerializeToElement("y")));
            Assert.NotEqual(h, With(q => q.Lines[0].Value = JsonSerializer.SerializeToElement(1)));
            Assert.NotEqual(h, With(q => q.Lines[0].Target.Field = "C1_16"));
            Assert.NotEqual(h, With(q => q.Lines[1].Target.Detail!.Amount = 100.51));
            Assert.NotEqual(h, With(q => q.Lines[1].Target.Detail!.Suffix = "1001"));
            Assert.NotEqual(h, With(q => q.Lines[1].Source.Location = "Box 2"));
            Assert.NotEqual(h, With(q => q.ClientNumber = "1002"));
            Assert.NotEqual(h, With(q => q.Context.TaxYear = 2023));
            Assert.NotEqual(h, With(q => q.TargetReturn!.ClientName = "Jane Smith"));
            Assert.NotEqual(h, With(q => q.TargetReturn!.ClientNumber = "1002"));
            Assert.NotEqual(h, With(q => q.TargetReturn!.ClientId = "OTHR"));
            Assert.NotEqual(h, With(q => q.TargetReturn = null));
            Assert.NotEqual(h, With(q => q.ManualItems.Add(new ManualItem { ItemId = "M1", Description = "x", Reason = "y" })));
            Assert.NotEqual(h, With(q => q.Lines.RemoveAt(1)));
        }

        // ---- status machine ---------------------------------------------------------------

        [Fact]
        public void HappyPath_Draft_Submitted_Approved_RecordsApproval()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"), FieldLine("C1_16", "y"));
            var approved = SubmitAndApprove(_store, p, "L2");

            Assert.Equal(ProposalStatus.Approved, approved.Status);
            var a = approved.Approval!;
            Assert.Equal("reviewer@pc", a.By);
            Assert.Equal("cli", a.Channel);
            Assert.Equal(approved.Version, a.Version);
            Assert.Equal(ProposalStore.ComputeContentHash(approved), a.ContentHash);
            Assert.Equal(ReviewerDecision.Approved, a.LineDecisions["L1"]);
            Assert.Equal(ReviewerDecision.Rejected, a.LineDecisions["L2"]);
            Assert.Equal("looks right", a.Note);
            Assert.Contains(approved.History, e => e.Action == "approved" && e.Channel == "cli");

            var state = ProposalStore.Evaluate(approved);
            Assert.True(state.ApprovalCurrent);
            Assert.True(state.ReadyToCommit);
            Assert.Equal(1, state.UnfinishedApprovedLineCount);
        }

        [Fact]
        public void IllegalTransitions_Are9024()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"));
            // Draft: no decisions, no approval, no reject / return.
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version)));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, "1001")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.Reject(p.Id, "me", "cli", "no")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.ReturnForChanges(p.Id, "me", "cli", "no")));

            p = _store.Submit(p.Id, "stdio");
            // Submitted: locked against agent edits, can't submit twice.
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.EditContent(p.Id, "stdio", "x", q => q.Notes = "n")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.Submit(p.Id, "stdio")));

            p = _store.Reject(p.Id, "me", "control-page", "wrong client");
            Assert.Equal(ProposalStatus.Rejected, p.Status);
            Assert.Equal("wrong client", p.LastReview!.Note);
            // Rejected is terminal.
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.EditContent(p.Id, "stdio", "x", q => q.Notes = "n")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.Submit(p.Id, "stdio")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.ReturnForChanges(p.Id, "me", "cli", "x")));
        }

        [Fact]
        public void Submit_EmptyProposal_Is9024()
        {
            var p = _store.Create(Ctx(), "1001", "t", null, "stdio", Target("1001"));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.Submit(p.Id, "stdio")));
        }

        [Fact]
        public void ReturnForChanges_ClearsApproval_AgentMayEditAndResubmit()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, FieldLine("C1_15", "x")));
            p = _store.ReturnForChanges(p.Id, "me", "control-page", "use the corrected W-2");
            Assert.Equal(ProposalStatus.ReturnedForChanges, p.Status);
            Assert.Null(p.Approval);
            Assert.Equal("use the corrected W-2", p.LastReview!.Note);

            var v = p.Version;
            p = _store.EditContent(p.Id, "stdio", "fix", q => q.Lines[0].Value = JsonSerializer.SerializeToElement("z"));
            Assert.Equal(v + 1, p.Version);
            Assert.Equal(ProposalStatus.ReturnedForChanges, p.Status);
            p = _store.Submit(p.Id, "stdio");
            Assert.Equal(ProposalStatus.Submitted, p.Status);
        }

        // ---- approval rules ---------------------------------------------------------------

        [Fact]
        public void Approve_RequiresEveryLineDecided()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"), FieldLine("C1_16", "y"));
            p = _store.Submit(p.Id, "stdio");
            p = _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version);
            var ex = Assert.Throws<LacerteSDKError>(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, "1001"));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, ex.StatusCode);
            Assert.Contains("L2", (string)ex.Context["reason"]!);
        }

        [Fact]
        public void Approve_RequiresAtLeastOneApprovedLine()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"));
            p = _store.Submit(p.Id, "stdio");
            p = _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Rejected, "no", p.Version);
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, "1001")));
        }

        [Fact]
        public void Approve_RequiresMatchingVersion()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"));
            p = _store.Submit(p.Id, "stdio");
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version - 1)));
            p = _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version);
            var ex = Assert.Throws<LacerteSDKError>(() => _store.Approve(p.Id, "me", "cli", p.Version + 1, TestPin, "1001"));
            Assert.Contains("version", (string)ex.Context["reason"]!);
            Assert.Equal(ProposalStatus.Submitted, _store.Get(p.Id)!.Status);
        }

        [Fact]
        public void LineThatFailedItsDryRun_CannotBeApproved()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"), FieldLine("C1_99", "y", ok: false));
            p = _store.Submit(p.Id, "stdio");
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.SetLineDecision(p.Id, "L2", ReviewerDecision.Approved, null, p.Version)));
            p = _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version);
            p = _store.SetLineDecision(p.Id, "L2", ReviewerDecision.Rejected, null, p.Version);
            Assert.Equal(ProposalStatus.Approved, _store.Approve(p.Id, "me", "cli", p.Version, TestPin, "1001").Status);
        }

        [Fact]
        public void Approve_OnlyFromHumanChannels_WithAReviewer()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "x"));
            p = _store.Submit(p.Id, "stdio");
            p = _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version);
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Code(() => _store.Approve(p.Id, "me", "mcp", p.Version, TestPin, "1001")));
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Code(() => _store.Approve(p.Id, "me", "agent", p.Version, TestPin, "1001")));
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Code(() => _store.Approve(p.Id, "  ", "cli", p.Version, TestPin, "1001")));
            Assert.Equal(ProposalStatus.Approved, _store.Approve(p.Id, "me", "control-page", p.Version, TestPin, "1001").Status);
        }

        [Fact]
        public void EditAfterApprove_RevokesApproval_AndReturnsToDraft()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, FieldLine("C1_15", "x")));
            var approvedVersion = p.Version;
            p = _store.EditContent(p.Id, "stdio", "changed value", q => q.Lines[0].Value = JsonSerializer.SerializeToElement("other"));

            Assert.Equal(ProposalStatus.Draft, p.Status);
            Assert.Null(p.Approval);
            Assert.Equal(approvedVersion + 1, p.Version);
            Assert.Contains(p.History, e => e.Action == "approvalRevoked");
            Assert.False(ProposalStore.Evaluate(p).ReadyToCommit);
        }

        [Fact]
        public void Evaluate_DetectsTamperedContent()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, FieldLine("C1_15", "x")));
            // An out-of-band edit (file tampering) that skipped the version bump.
            p = _store.Mutate(p.Id, q => q.Lines[0].Value = JsonSerializer.SerializeToElement("tampered"));
            var state = ProposalStore.Evaluate(p);
            Assert.False(state.ApprovalCurrent);
            Assert.False(state.ReadyToCommit);
            Assert.Contains(state.Blockers, b => b.Contains("changed after it was approved"));
        }

        [Fact]
        public void ConcurrentMutations_AreSerialized_NoLostUpdates()
        {
            var p = _store.Create(Ctx(), "1001", "t", null, "stdio", Target("1001"));
            System.Threading.Tasks.Parallel.For(0, 20, i =>
                _store.EditContent(p.Id, "stdio", "q" + i, q => q.Questions.Add(new ProposalQuestion { QuestionId = "Q" + i, Text = "t" + i })));
            var back = _store.Get(p.Id)!;
            Assert.Equal(20, back.Questions.Count);
            Assert.Equal(21, back.Version);
        }

        [Fact]
        public void ResolveUncertainLine_OnlyForUncertain_MissingBumpsKeyGeneration()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, DetailLine(1, "a")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Code(() => _store.ResolveUncertainLine(p.Id, "L1", true, "me", "cli", TestPin, null)));

            _store.Mutate(p.Id, q =>
            {
                q.Lines[0].Commit = new LineCommitResult { Status = LineCommitStatus.Uncertain };
                q.Status = ProposalStatus.PartiallyCommitted;
            });
            p = _store.ResolveUncertainLine(p.Id, "L1", rowIsInReturn: false, "me", "cli", TestPin, "not in Lacerte");
            Assert.Equal(LineCommitStatus.Failed, p.Lines[0].Commit!.Status);
            Assert.Equal(1, p.Lines[0].KeyGeneration);
            Assert.Equal(ProposalStatus.CommitFailed, p.Status);

            _store.Mutate(p.Id, q => q.Lines[0].Commit!.Status = LineCommitStatus.Uncertain);
            p = _store.ResolveUncertainLine(p.Id, "L1", rowIsInReturn: true, "me", "control-page", TestPin, null);
            Assert.Equal(LineCommitStatus.Succeeded, p.Lines[0].Commit!.Status);
            Assert.Equal(ProposalStatus.Committed, p.Status);
        }
    }
}
