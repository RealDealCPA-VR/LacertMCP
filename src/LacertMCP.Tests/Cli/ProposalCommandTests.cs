using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LacertMCP.Cli.Commands;
using LacertMCP.Server.Proposals;
using Xunit;
using static LacertMCP.Tests.Proposals.ProposalTestSupport;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// <c>lacerte-mcp proposal list | show | approve | reject | return | resolve</c>
    /// against a temp store (the same files the server writes). The interactive
    /// prompts (client number, approval PIN) are driven through ConsoleInput's test hooks.
    /// </summary>
    [Collection(ConsoleInputHooksCollection.Name)]
    public sealed class ProposalCommandTests : IDisposable
    {
        private readonly string _dir = NewTempDir();
        private readonly ProposalStore _store;
        private readonly StringWriter _out = new StringWriter();
        private readonly StringWriter _err = new StringWriter();
        private string? _typedPin = TestPin;
        private string? _typedLine = "1001";
        private bool _redirected;
        private readonly List<string> _prompts = new List<string>();

        public ProposalCommandTests()
        {
            _store = NewStore(_dir);
            ConsoleInput.InputRedirectedForTests = () => _redirected;
            ConsoleInput.PinReaderForTests = prompt => { _prompts.Add(prompt); return _typedPin; };
            ConsoleInput.LineReaderForTests = prompt => { _prompts.Add(prompt); return _typedLine; };
        }

        public void Dispose()
        {
            ConsoleInput.InputRedirectedForTests = null;
            ConsoleInput.PinReaderForTests = null;
            ConsoleInput.LineReaderForTests = null;
            TryDelete(_dir);
        }

        private void AssertPinNeverPrinted()
        {
            Assert.DoesNotContain(TestPin, _out.ToString());
            Assert.DoesNotContain(TestPin, _err.ToString());
            Assert.DoesNotContain(TestPin, File.ReadAllText(Path.Combine(_dir, ApprovalPin.FileName)));
        }

        private Proposal Submitted()
        {
            var p = NewProposal(_store, FieldLine("C1_15", "Acme LLC"), DetailLine(52000.5, "ACME W-2"), FieldLine("C1_99", "bad", ok: false));
            _store.EditContent(p.Id, "stdio", "items", q =>
            {
                q.ManualItems.Add(new ManualItem { ItemId = "M1", Description = "Section 179 election", Amount = 2500, Reason = "no verified mapping" });
                q.Questions.Add(new ProposalQuestion { QuestionId = "Q1", Text = "Is the W-2 the corrected one?", LineIds = new List<string> { "L2" } });
            });
            return _store.Submit(p.Id, "stdio");
        }

        [Fact]
        public void OpenStore_ResolvesLikeTheServer()
        {
            var store = ProposalCommand.OpenStore(null, _err, new Dictionary<string, string?> { ["LACERTE_HTTP_DATA_DIR"] = _dir });
            Assert.Equal(Path.Combine(_dir, "proposals"), store!.DirectoryPath);
            Assert.Null(ProposalCommand.OpenStore(null, _err, new Dictionary<string, string?>()));
            Assert.Equal(Path.GetFullPath(_dir), ProposalCommand.OpenStore(_dir, _err)!.DirectoryPath);
        }

        [Fact]
        public void List_And_Show_PrintAReadableReview()
        {
            var p = Submitted();
            Assert.Equal(0, ProposalCommand.RunList(_store, null, null, _out, _err));
            var list = _out.ToString();
            Assert.Contains(p.Id, list);
            Assert.Contains("Submitted", list);
            Assert.Contains("lines 3", list);
            Assert.Contains("failed dry run 1", list);

            Assert.Equal(1, ProposalCommand.RunList(_store, null, "bogus", _out, _err));
            _out.GetStringBuilder().Clear();
            Assert.Equal(0, ProposalCommand.RunList(_store, "9999", null, _out, _err));
            Assert.Contains("(no proposals)", _out.ToString());

            _out.GetStringBuilder().Clear();
            Assert.Equal(0, ProposalCommand.RunShow(_store, p.Id, _out, _err));
            var show = _out.ToString();
            Assert.Contains("Client 1001", show);
            Assert.Contains("Entries go into: client 1001 · John Smith · Individual (1040) 2024 · C:\\Lacerte\\24Tax\\IData", show);
            Assert.Contains("table 1 C1_15 = \"Acme LLC\"", show);
            Assert.Contains("Series 11 Code 1 Suffix 1000", show);
            Assert.Contains("52,000.50", show);
            Assert.Contains("organizer.pdf, page 2, Line 7", show);
            Assert.Contains("reasoning:  because", show);
            Assert.Contains("confidence: High", show);
            Assert.Contains("dry run:    FAILED", show);
            Assert.Contains("Section 179 election", show);
            Assert.Contains("Is the W-2 the corrected one?", show);
            Assert.Contains("(lines L2)", show);
            Assert.Contains("Content hash " + ProposalStore.ComputeContentHash(p), show);

            Assert.Equal(1, ProposalCommand.RunShow(_store, "doesnotexist00", _out, _err));
        }

        [Fact]
        public void Approve_WithoutConfirm_ChangesNothing()
        {
            var p = Submitted();
            Assert.Equal(0, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, null, false, "me@pc", _out, _err));
            var text = _out.ToString();
            Assert.Contains("L1   Approved", text);
            Assert.Contains("L3   Rejected  (dry run failed", text);
            Assert.Contains("--confirm", text);
            var back = _store.Get(p.Id)!;
            Assert.Equal(ProposalStatus.Submitted, back.Status);
            Assert.All(back.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));
        }

        [Fact]
        public void Approve_Confirm_RecordsCliApproval_WithRejectedLines()
        {
            var p = Submitted();
            Assert.Equal(0, ProposalCommand.RunApprove(_store, p.Id, new[] { "l2" }, "checked against source", p.Version, true, "me@pc", _out, _err));
            var back = _store.Get(p.Id)!;
            Assert.Equal(ProposalStatus.Approved, back.Status);
            Assert.Equal("cli", back.Approval!.Channel);
            Assert.Equal("me@pc", back.Approval.By);
            Assert.Equal(p.Version, back.Approval.Version);
            Assert.Equal("checked against source", back.Approval.Note);
            Assert.Equal(ReviewerDecision.Approved, back.Approval.LineDecisions["L1"]);
            Assert.Equal(ReviewerDecision.Rejected, back.Approval.LineDecisions["L2"]);
            Assert.Equal(ReviewerDecision.Rejected, back.Approval.LineDecisions["L3"]);
            Assert.True(back.Approval.PinVerified);
            Assert.Equal("John Smith", back.Approval.TargetReturn!.ClientName);
            Assert.Equal("1001", back.Approval.TargetReturn.ClientNumber);
            Assert.Equal(2024, back.Approval.TargetContext!.TaxYear);
            Assert.True(ProposalStore.Evaluate(back).ReadyToCommit);
            var text = _out.ToString();
            Assert.Contains("Entries go into: client 1001 · John Smith", text);
            Assert.Contains("Nothing is written yet", text);
            Assert.Equal(new[] { "Type the client number to confirm: ", "Approval PIN: " }, _prompts.ToArray());
            AssertPinNeverPrinted();
        }

        [Fact]
        public void Approve_Refusals()
        {
            var p = Submitted();
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, p.Version + 1, true, "me", _out, _err));
            Assert.Contains("version", _err.ToString());
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, new[] { "L9" }, null, null, true, "me", _out, _err));
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, new[] { "L1", "L2" }, null, null, true, "me", _out, _err)); // nothing approvable left
            Assert.Equal(ProposalStatus.Submitted, _store.Get(p.Id)!.Status);

            var draft = NewProposal(_store, FieldLine("C1_15", "x"));
            Assert.Equal(1, ProposalCommand.RunApprove(_store, draft.Id, Array.Empty<string>(), null, null, true, "me", _out, _err));
            Assert.Contains("Only a Submitted proposal", _err.ToString());
            Assert.Empty(_prompts); // refused before asking for the PIN
        }

        [Fact]
        public void Approve_WrongPin_OrWrongClient_ChangesNothing_NotEvenLineDecisions()
        {
            var p = Submitted();
            _typedPin = "not-the-pin-99";
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, p.Version, true, "me", _out, _err));
            Assert.Contains("Wrong approval PIN. 4 attempts left", _err.ToString());
            Assert.Equal(1, _store.Pin.Status().FailedAttempts);

            _typedPin = TestPin;
            _typedLine = "1002";
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, p.Version, true, "me", _out, _err));
            Assert.Contains("not this proposal's client", _err.ToString());

            var back = _store.Get(p.Id)!;
            Assert.Equal(ProposalStatus.Submitted, back.Status);
            Assert.Null(back.Approval);
            Assert.All(back.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));
            Assert.DoesNotContain("not-the-pin-99", _out.ToString() + _err.ToString());
            AssertPinNeverPrinted();
        }

        [Fact]
        public void Approve_RedirectedInput_OrNoPinSet_IsRefused()
        {
            var p = Submitted();
            _redirected = true;
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, p.Version, true, "me", _out, _err));
            Assert.Contains("run it yourself in a console", _err.ToString());
            Assert.Empty(_prompts);
            // Without --confirm (a preview) redirected input is fine.
            Assert.Equal(0, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, p.Version, false, "me", _out, _err));

            _redirected = false;
            _store.Pin.Reset();
            Assert.Equal(1, ProposalCommand.RunApprove(_store, p.Id, Array.Empty<string>(), null, p.Version, true, "me", _out, _err));
            Assert.Contains("lacerte-mcp approval-pin set", _err.ToString());
            Assert.Equal(ProposalStatus.Submitted, _store.Get(p.Id)!.Status);
        }

        [Fact]
        public void Reject_And_Return_RoundTrip()
        {
            var p = Submitted();
            Assert.Equal(1, ProposalCommand.RunReturn(_store, p.Id, "  ", "me", _out, _err));
            Assert.Equal(0, ProposalCommand.RunReturn(_store, p.Id, "use the corrected W-2", "me@pc", _out, _err));
            var back = _store.Get(p.Id)!;
            Assert.Equal(ProposalStatus.ReturnedForChanges, back.Status);
            Assert.Equal("use the corrected W-2", back.LastReview!.Note);
            Assert.Equal("cli", back.LastReview.Channel);

            // The agent fixes and resubmits; then the reviewer rejects it.
            _store.Submit(p.Id, "stdio");
            Assert.Equal(1, ProposalCommand.RunReject(_store, p.Id, null, "me", _out, _err));
            Assert.Equal(0, ProposalCommand.RunReject(_store, p.Id, "wrong client", "me@pc", _out, _err));
            Assert.Equal(ProposalStatus.Rejected, _store.Get(p.Id)!.Status);
            // Terminal: returning a rejected proposal is refused.
            Assert.Equal(1, ProposalCommand.RunReturn(_store, p.Id, "again", "me", _out, _err));
            Assert.Contains("Refused (9024", _err.ToString());
        }

        [Fact]
        public void Resolve_UncertainLine()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, DetailLine(1, "a")));
            _store.Mutate(p.Id, q =>
            {
                q.Lines[0].Commit = new LineCommitResult { Status = LineCommitStatus.Uncertain, Note = "9022" };
                q.Status = ProposalStatus.PartiallyCommitted;
            });
            Assert.Equal(1, ProposalCommand.RunResolve(_store, p.Id, "L1", true, true, null, true, "me", _out, _err));
            Assert.Equal(0, ProposalCommand.RunResolve(_store, p.Id, "L1", true, false, null, false, "me", _out, _err));
            Assert.Equal(LineCommitStatus.Uncertain, _store.Get(p.Id)!.Lines[0].Commit!.Status);

            // Redirected input and a wrong PIN are refused; nothing changes.
            _redirected = true;
            Assert.Equal(1, ProposalCommand.RunResolve(_store, p.Id, "L1", true, false, null, true, "me", _out, _err));
            _redirected = false;
            _typedPin = "wrong-pin-123";
            Assert.Equal(1, ProposalCommand.RunResolve(_store, p.Id, "L1", true, false, null, true, "me", _out, _err));
            Assert.Contains("Wrong approval PIN", _err.ToString());
            Assert.Equal(LineCommitStatus.Uncertain, _store.Get(p.Id)!.Lines[0].Commit!.Status);

            _typedPin = TestPin;
            Assert.Equal(0, ProposalCommand.RunResolve(_store, p.Id, "L1", true, false, "seen in Lacerte", true, "me", _out, _err));
            var back = _store.Get(p.Id)!;
            Assert.Equal(LineCommitStatus.Succeeded, back.Lines[0].Commit!.Status);
            Assert.Equal(ProposalStatus.Committed, back.Status);
            Assert.Contains("Entries go into: client 1001", _out.ToString());
            AssertPinNeverPrinted();
        }

        [Fact]
        public void Build_RegistersSubcommands()
        {
            var cmd = ProposalCommand.Build();
            Assert.Equal("proposal", cmd.Name);
            Assert.Equal(new[] { "approve", "list", "reject", "resolve", "return", "show" },
                cmd.Subcommands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }
}
