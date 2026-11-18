using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Proposals;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using LacertMCP.Tests.Session;
using Xunit;
using static LacertMCP.Tests.Proposals.ProposalTestSupport;

namespace LacertMCP.Tests.Proposals
{
    /// <summary>
    /// Approve / resolve need the approval PIN and the target client; the commit
    /// re-checks the target return before writing anything.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class ProposalPinApprovalTests : IDisposable
    {
        private const string Client = "1001";
        private readonly string _dir = NewTempDir();
        private readonly ProposalStore _store;

        public ProposalPinApprovalTests()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_15");
            _store = new ProposalStore(Path.Combine(_dir, "proposals"), NewPin(_dir));
        }

        public void Dispose() => TryDelete(_dir);

        private Proposal Decided(params ProposalLine[] lines)
        {
            var p = _store.Submit(NewProposal(_store, lines.Length == 0 ? new[] { FieldLine("C1_15", "x") } : lines).Id, "stdio");
            foreach (var l in p.Lines) p = _store.SetLineDecision(p.Id, l.LineId, ReviewerDecision.Approved, null, p.Version);
            return p;
        }

        private static LacerteSDKError Refused(Action a) => Assert.Throws<LacerteSDKError>(a);

        // ---- store: approve -----------------------------------------------------------------

        [Fact]
        public void Approve_NoPinSet_Refused_NothingChanges()
        {
            _store.Pin.Reset();
            var p = Decided();
            var ex = Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, Client));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, ex.StatusCode);
            Assert.Equal("NotSet", ex.Context["pinCheck"]);
            Assert.Contains("lacerte-mcp approval-pin set", (string)ex.Context["reason"]!);
            Assert.Equal(ProposalStatus.Submitted, _store.Get(p.Id)!.Status);
            Assert.Null(_store.Get(p.Id)!.Approval);
        }

        [Fact]
        public void Approve_WrongPin_CountsDown_ThenLocks_NeverEchoesThePin()
        {
            var p = Decided();
            for (int left = 4; left >= 1; left--)
            {
                var ex = Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, "WRONG-PIN-42", Client));
                Assert.Equal("Wrong", ex.Context["pinCheck"]);
                Assert.Equal(left, ex.Context["remainingAttempts"]);
                Assert.DoesNotContain("WRONG-PIN-42", string.Join("|", ex.Context.Values) + ex.Message);
            }
            var locking = Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, "WRONG-PIN-42", Client));
            Assert.Equal("Locked", locking.Context["pinCheck"]);
            Assert.IsType<DateTimeOffset>(locking.Context["lockedUntil"]);
            var locked = Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, Client));
            Assert.Equal("Locked", locked.Context["pinCheck"]);

            var back = _store.Get(p.Id)!;
            Assert.Equal(ProposalStatus.Submitted, back.Status);
            Assert.Null(back.Approval);
            Assert.DoesNotContain(back.History, e => e.Action == "approved");
            Assert.DoesNotContain(TestPin, File.ReadAllText(Path.Combine(_store.DirectoryPath, p.Id + ".json")));
        }

        [Fact]
        public void Approve_ConfirmClientNumber_MustBeTheProposals()
        {
            var p = Decided();
            Assert.Contains("Type the client number", (string)Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, "  ")).Context["reason"]!);
            var ex = Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, "1002"));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, ex.StatusCode);
            Assert.Contains("client 1001 · John Smith", (string)ex.Context["reason"]!);
            Assert.Equal(0, _store.Pin.Status().FailedAttempts); // refused before the PIN check
            Assert.Null(_store.Get(p.Id)!.Approval);
        }

        [Fact]
        public void Approve_Success_RecordsTargetAndPinVerified()
        {
            var p = Decided();
            var a = _store.Approve(p.Id, "me", "control-page", p.Version, TestPin, " 1001 ", "ok").Approval!;
            Assert.True(a.PinVerified);
            Assert.Equal("1001", a.TargetReturn!.ClientNumber);
            Assert.Equal("John Smith", a.TargetReturn.ClientName);
            Assert.Equal("SMIT", a.TargetReturn.ClientId);
            Assert.Equal(@"C:\Lacerte\24Tax\IData", a.TargetContext!.DataPath);
            Assert.Equal(2024, a.TargetContext.TaxYear);
            Assert.Equal("I", a.TargetContext.Module);
            var ev = _store.Get(p.Id)!.History.Last(e => e.Action == "approved");
            Assert.StartsWith("Entries go into: client 1001 · John Smith · Individual (1040) 2024", ev.Note);
            Assert.True(ProposalStore.Evaluate(_store.Get(p.Id)!).ReadyToCommit);
        }

        [Fact]
        public void Approve_LineDecisionsInTheSameStep_OnlyAfterThePin()
        {
            var p = _store.Submit(NewProposal(_store, FieldLine("C1_15", "x"), FieldLine("C1_16", "y")).Id, "stdio");
            var decisions = new Dictionary<string, ReviewerDecision> { ["L1"] = ReviewerDecision.Approved, ["L2"] = ReviewerDecision.Rejected };
            Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, "nope-nope", Client, null, decisions));
            Assert.All(_store.Get(p.Id)!.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));

            var approved = _store.Approve(p.Id, "me", "cli", p.Version, TestPin, Client, null, decisions);
            Assert.Equal(ReviewerDecision.Approved, approved.Approval!.LineDecisions["L1"]);
            Assert.Equal(ReviewerDecision.Rejected, approved.Approval.LineDecisions["L2"]);
        }

        [Fact]
        public void ProposalWithoutIdentity_CannotBeApproved()
        {
            var p = _store.Create(Ctx(), Client, "old", null, "stdio", null);
            _store.EditContent(p.Id, "stdio", "lines", q => { var l = FieldLine("C1_15", "x"); l.LineId = "L1"; q.Lines.Add(l); });
            p = _store.Submit(p.Id, "stdio");
            p = _store.SetLineDecision(p.Id, "L1", ReviewerDecision.Approved, null, p.Version);
            var state = ProposalStore.Evaluate(p);
            Assert.False(state.ReadyToApprove);
            Assert.Contains(state.Blockers, b => b.Contains("create a new proposal"));
            Assert.Contains("(client name not recorded)", state.EntriesGoInto);
            var ex = Refused(() => _store.Approve(p.Id, "me", "cli", p.Version, TestPin, Client));
            Assert.Contains("create a new proposal", (string)ex.Context["reason"]!);
        }

        [Fact]
        public void ApprovalWithoutRecordedTarget_IsNotCommittable()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, FieldLine("C1_15", "x")));
            p = _store.Mutate(p.Id, q => { q.Approval!.TargetReturn = null; });
            var state = ProposalStore.Evaluate(p);
            Assert.False(state.ReadyToCommit);
            Assert.Contains(state.Blockers, b => b.Contains("doesn't record which return"));
        }

        // ---- store: resolve -------------------------------------------------------------------

        [Fact]
        public void Resolve_RequiresThePin_RejectAndReturnDoNot()
        {
            var p = SubmitAndApprove(_store, NewProposal(_store, DetailLine(1, "a")));
            _store.Mutate(p.Id, q =>
            {
                q.Lines[0].Commit = new LineCommitResult { Status = LineCommitStatus.Uncertain };
                q.Status = ProposalStatus.PartiallyCommitted;
            });
            Assert.Equal("Wrong", Refused(() => _store.ResolveUncertainLine(p.Id, "L1", false, "me", "cli", "bad-pin-1", null)).Context["pinCheck"]);
            Assert.Equal("Wrong", Refused(() => _store.ResolveUncertainLine(p.Id, "L1", false, "me", "cli", null, null)).Context["pinCheck"]);
            var back = _store.Get(p.Id)!;
            Assert.Equal(LineCommitStatus.Uncertain, back.Lines[0].Commit!.Status);
            Assert.Equal(0, back.Lines[0].KeyGeneration);

            _store.Pin.Reset();
            Assert.Equal("NotSet", Refused(() => _store.ResolveUncertainLine(p.Id, "L1", false, "me", "cli", TestPin, null)).Context["pinCheck"]);

            // Reject / return / line decisions don't need it.
            var q2 = _store.Submit(NewProposal(_store, FieldLine("C1_15", "x")).Id, "stdio");
            q2 = _store.SetLineDecision(q2.Id, "L1", ReviewerDecision.Approved, null, q2.Version);
            Assert.Equal(ProposalStatus.ReturnedForChanges, _store.ReturnForChanges(q2.Id, "me", "cli", "fix").Status);
            Assert.Equal(ProposalStatus.Rejected, _store.Reject(q2.Id, "me", "cli", "no").Status);
        }

        // ---- service: identity + commit -------------------------------------------------------

        private static async Task<LacerteSessionManager> NewSimAsync()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            return mgr;
        }

        private static ProposalLineInput Field(string field, object value) => new ProposalLineInput
        {
            Kind = "field", Table = 1, Field = field, Value = System.Text.Json.JsonSerializer.SerializeToElement(value),
            Source = new SourceCitationInput { Document = "organizer.pdf", Page = 1 }, Confidence = "high",
        };

        private async Task<(LacerteSessionManager Mgr, ProposalService Service, string Id)> ApprovedSimProposalAsync()
        {
            var mgr = await NewSimAsync();
            var service = new ProposalService(mgr, _store);
            var p = await service.CreateAsync(Client, null, null, default);
            await service.AddLinesAsync(p.Id, new[] { Field("C1_15", "PIN-COMMIT") }, default);
            SubmitAndApprove(_store, _store.Get(p.Id)!);
            return (mgr, service, p.Id);
        }

        [Fact]
        public async Task Create_CapturesTheClientsIdentity_IndividualFirstPlusLast()
        {
            using var mgr = await NewSimAsync();
            var service = new ProposalService(mgr, _store);
            var p = await service.CreateAsync(Client, null, null, default);
            Assert.Equal("1001", p.TargetReturn!.ClientNumber);
            Assert.Equal("Bill Brown", p.TargetReturn.ClientName);
            Assert.Equal("BROW", p.TargetReturn.ClientId);
            Assert.Equal("client 1001 · Bill Brown · Individual (1040) 2024 · " + SeedData.DataPathIData2024, ProposalStore.Evaluate(p).EntriesGoInto);
            Assert.Equal("Bill Brown", _store.List().Single().ClientName);
        }

        [Fact]
        public async Task Create_EntityModules_UseTheEntityName()
        {
            using var mgr = await NewSimAsync();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var p = await new ProposalService(mgr, _store).CreateAsync(Client, null, null, default);
            Assert.Equal("Partner Ventures LP", p.TargetReturn!.ClientName);
            Assert.StartsWith("client 1001 · Partner Ventures LP · Partnership (1065) 2024", ProposalStore.Evaluate(p).EntriesGoInto);
        }

        [Fact]
        public void TargetFromRow_LiveShapedRow_TrimsAndIgnoresKeyCase()
        {
            var row = new Dictionary<string, object?> { ["clientNumber"] = "SMIT", ["c1_1"] = "  John   ", ["C1_2"] = "Smith    " };
            var t = ProposalService.TargetFromRow("smit", 'I', row);
            Assert.Equal("SMIT", t.ClientNumber);
            Assert.Equal("John Smith", t.ClientName);
            Assert.Null(t.ClientId);
            Assert.Equal("Acme Corp", ProposalService.TargetFromRow("1", 'C', new Dictionary<string, object?> { ["C1_1"] = "Acme  Corp", ["C1_2"] = "ignored" }).ClientName);
            Assert.True(ProposalStore.SameClientName(" john  SMITH", "John Smith"));
            Assert.False(ProposalStore.SameClientName("John Smith", "John Smyth"));
        }

        [Fact]
        public async Task Commit_RefusesWhenTheClientWasRenamed_WritesNothing_StatusUnchanged()
        {
            var (mgr, service, id) = await ApprovedSimProposalAsync();
            using (mgr)
            {
                var row = mgr.SimulationStoreForTest!.TryGetModule(SeedData.DataPathIData2024, 2024, 'I')!.Clients[Client];
                var before = row.Fields.TryGetValue("C1_15", out var b) ? b : null;
                row.Fields["C1_1"] = "William";

                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(id));
                Assert.Equal(SyntheticStatusCodes.ProposalConflict, ex.StatusCode);
                var reason = (string)ex.Context["reason"]!;
                Assert.Contains("Approved for client 1001 · Bill Brown", reason);
                Assert.Contains("now holds client 1001 · William Brown", reason);
                Assert.Equal(before, row.Fields.TryGetValue("C1_15", out var a) ? a : null);
                var stored = _store.Get(id)!;
                Assert.Equal(ProposalStatus.Approved, stored.Status);
                Assert.Null(stored.Lines[0].Commit);

                // Only spacing / case differ: the same name, the commit runs.
                row.Fields["C1_1"] = "  BILL ";
                var report = await service.CommitAsync(id);
                Assert.Equal(ProposalStatus.Committed, report.Status);
            }
        }

        [Fact]
        public async Task Commit_RefusesWhenTheClientIsGone()
        {
            var (mgr, service, id) = await ApprovedSimProposalAsync();
            using (mgr)
            {
                mgr.SimulationStoreForTest!.TryGetModule(SeedData.DataPathIData2024, 2024, 'I')!.Clients.TryRemove(Client, out _);
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(id));
                Assert.Equal(SyntheticStatusCodes.ProposalConflict, ex.StatusCode);
                Assert.Contains("there is no client 1001", (string)ex.Context["reason"]!);
                Assert.Equal(ProposalStatus.Approved, _store.Get(id)!.Status);
            }
        }

        [Fact]
        public async Task Commit_RefusesWhenTheApprovalWasForAnotherTarget()
        {
            var (mgr, service, id) = await ApprovedSimProposalAsync();
            using (mgr)
            {
                // An edited approval record (not the proposal content, so the hash still matches).
                _store.Mutate(id, q => q.Approval!.TargetReturn!.ClientName = "Someone Else");
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(id));
                Assert.Contains("The approval was for client 1001 · Someone Else", (string)ex.Context["reason"]!);
                Assert.Null(_store.Get(id)!.Lines[0].Commit);
            }
        }

        [Fact]
        public async Task ReCommit_OfAPartlyCommittedProposal_RechecksTheTarget()
        {
            var (mgr, service, id) = await ApprovedSimProposalAsync();
            using (mgr)
            {
                // As if an earlier pass had failed this line.
                _store.Mutate(id, q =>
                {
                    q.Lines[0].Commit = new LineCommitResult { Status = LineCommitStatus.Failed };
                    q.Status = ProposalStatus.CommitFailed;
                });
                mgr.SimulationStoreForTest!.TryGetModule(SeedData.DataPathIData2024, 2024, 'I')!.Clients[Client].Fields["C1_2"] = "Brownstone";
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(id));
                Assert.Contains("Bill Brownstone", (string)ex.Context["reason"]!);
                Assert.Equal(ProposalStatus.CommitFailed, _store.Get(id)!.Status);
                Assert.Equal(LineCommitStatus.Failed, _store.Get(id)!.Lines[0].Commit!.Status);
            }
        }
    }
}
