using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
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
    /// Proposal tools + commit against the real session chokepoints: simulation
    /// for the field / detail happy paths, the FP-2 live harness (fake ODBC +
    /// fake worker) for the dictionary gate, partial failure and duplicate
    /// protection.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class ProposalServiceTests : IDisposable
    {
        private const string Client = "1001";
        private readonly string _dir = NewTempDir();
        private readonly ProposalStore _store;

        public ProposalServiceTests()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_15");
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_71");
            _store = new ProposalStore(System.IO.Path.Combine(_dir, "proposals"));
        }

        public void Dispose() => TryDelete(_dir);

        private static async Task<LacerteSessionManager> NewSimAsync()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            return mgr;
        }

        private static JsonElement Ok(string json)
        {
            var root = JsonDocument.Parse(json).RootElement;
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            return root.GetProperty("result");
        }

        private static int Err(string json)
        {
            var root = JsonDocument.Parse(json).RootElement;
            Assert.False(root.GetProperty("success").GetBoolean(), json);
            return root.GetProperty("statusCode").GetInt32();
        }

        private static SourceCitationInput Src(string loc = "Box 1") => new SourceCitationInput { Document = "organizer.pdf", Page = 1, Location = loc, Excerpt = "x" };

        private static ProposalLineInput Field(string field, object value) => new ProposalLineInput
        {
            Kind = "field", Table = 1, Field = field, Value = JsonSerializer.SerializeToElement(value),
            Source = Src(), Confidence = "high", Reasoning = "from the organizer",
        };

        private static ProposalLineInput Detail(double amount, string description, string series = "11", string code = "1", string suffix = "1000") => new ProposalLineInput
        {
            Kind = "detail",
            Detail = new ProposalDetailInput { Series = series, Code = code, Suffix = suffix, Amount = amount, Description = description, Source = "Source", Tsj = "Taxpayer" },
            DictionaryId = "1040.w2_wages",
            Source = Src(), Confidence = "medium",
        };

        private static async Task<object?> ReadField(LacerteSessionManager mgr, string field)
        {
            var rows = await mgr.ReadTable(1, Client);
            return rows.Single().TryGetValue(field, out var v) ? v : null;
        }

        private static string Id(JsonElement view) => view.GetProperty("proposal").GetProperty("id").GetString()!;

        // ---- create / add lines ---------------------------------------------------------------

        [Fact]
        public async Task Create_UsesActiveContext_AndChecksClientExists()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);

            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Err(await tools.LacerteProposalCreate("NOPE99")));

            var view = Ok(await tools.LacerteProposalCreate(Client, "2024 organizer"));
            var p = view.GetProperty("proposal");
            Assert.Equal("Draft", p.GetProperty("status").GetString());
            Assert.Equal(1, p.GetProperty("version").GetInt32());
            Assert.Equal("stdio", p.GetProperty("author").GetString());
            Assert.Equal(2024, p.GetProperty("context").GetProperty("taxYear").GetInt32());
            Assert.Equal("I", p.GetProperty("context").GetProperty("module").GetString());
            Assert.True(view.GetProperty("state").GetProperty("agentEditable").GetBoolean());
        }

        [Fact]
        public async Task AddLines_DryRunsEachLine_InvalidLinesKeptButMarked()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            var before = await ReadField(mgr, "C1_15");

            var result = Ok(await tools.LacerteProposalAddLines(id, new[]
            {
                Field("C1_15", "PROPOSED"),
                Field("C1_NOPE", "x"),                 // not writable → 9012
                Detail(100, "ACME"),
                Detail(100, "BAD", series: "abc"),     // shape → 9008
            }));
            var added = result.GetProperty("added").EnumerateArray().ToArray();
            Assert.Equal(new[] { "L1", "L2", "L3", "L4" }, added.Select(a => a.GetProperty("lineId").GetString()).ToArray());
            Assert.True(added[0].GetProperty("ok").GetBoolean());
            Assert.False(added[1].GetProperty("ok").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, added[1].GetProperty("statusCode").GetInt32());
            Assert.True(added[2].GetProperty("ok").GetBoolean());
            Assert.False(added[3].GetProperty("ok").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, added[3].GetProperty("statusCode").GetInt32());
            Assert.Equal(2, result.GetProperty("invalidCount").GetInt32());
            Assert.Equal(2, result.GetProperty("version").GetInt32());

            // Dry runs only: nothing reached the return, and the caller's context is untouched.
            Assert.Equal(before, await ReadField(mgr, "C1_15"));
            Assert.Equal(SeedData.DataPathIData2024, mgr.Context!.DataPath);

            var stored = _store.Get(id)!;
            Assert.Equal(4, stored.Lines.Count);
            Assert.All(stored.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));
            Assert.Contains(stored.Lines[0].Warnings, w => w.Contains("No dictionaryId"));
        }

        [Fact]
        public async Task AddLines_MalformedInput_RejectsWholeCall()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));

            var noSource = Field("C1_15", "x");
            noSource.Source = null;
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Err(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "ok"), noSource })));
            var badConfidence = Field("C1_15", "x");
            badConfidence.Confidence = "certain";
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Err(await tools.LacerteProposalAddLines(id, new[] { badConfidence })));
            var badKind = Field("C1_15", "x");
            badKind.Kind = "note";
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Err(await tools.LacerteProposalAddLines(id, new[] { badKind })));

            Assert.Empty(_store.Get(id)!.Lines);
            Assert.Equal(1, _store.Get(id)!.Version);
        }

        [Fact]
        public async Task UpdateRemoveManualItemsQuestions_Submit_ThenAgentLockedOut()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_NOPE", "x"), Field("C1_71", "y") }));

            var view = Ok(await tools.LacerteProposalUpdateLine(id, "L1", Field("C1_15", "fixed")));
            Assert.True(view.GetProperty("proposal").GetProperty("lines")[0].GetProperty("preview").GetProperty("ok").GetBoolean());
            Ok(await tools.LacerteProposalRemoveLine(id, "L2"));
            Ok(await tools.LacerteProposalAddManualItems(id, new[] { new ManualItemInput { Description = "Depreciation schedule", Amount = 1200, Reason = "no verified mapping", Source = Src("Line 14") } }));
            view = Ok(await tools.LacerteProposalAddQuestions(id, new[] { new QuestionInput { Text = "Is the W-2 corrected?", LineIds = new List<string> { "l1" } } }));
            var p = view.GetProperty("proposal");
            Assert.Equal("M1", p.GetProperty("manualItems")[0].GetProperty("itemId").GetString());
            Assert.Equal("L1", p.GetProperty("questions")[0].GetProperty("lineIds")[0].GetString());
            Assert.Equal(1, p.GetProperty("lines").GetArrayLength());
            Assert.Equal(6, p.GetProperty("version").GetInt32());

            view = Ok(await tools.LacerteProposalSubmit(id));
            Assert.Equal("Submitted", view.GetProperty("proposal").GetProperty("status").GetString());

            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "late") })));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalUpdateLine(id, "L1", Field("C1_15", "late"))));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalRemoveLine(id, "L1")));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalAddQuestions(id, new[] { new QuestionInput { Text = "?" } })));
            // Not approved: no commit.
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalCommit(id)));

            var list = Ok(await tools.LacerteProposalList(Client, "submitted"));
            Assert.Equal(1, list.GetProperty("count").GetInt32());
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, Err(await tools.LacerteProposalList(null, "approvedish")));
        }

        // ---- commit (simulation) --------------------------------------------------------------

        [Fact]
        public async Task Commit_WritesOnlyApprovedLines_VerifiedByReadBack()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "APPROVED-VALUE"), Field("C1_71", "REJECTED-VALUE"), Detail(4321.5, "PROPOSAL W2") }));
            Ok(await tools.LacerteProposalSubmit(id));
            var rejectedBefore = await ReadField(mgr, "C1_71");
            var detailBefore = (await mgr.ReadDetail(Client)).Count;

            var p = _store.Get(id)!;
            p = _store.SetLineDecision(id, "L1", ReviewerDecision.Approved, null, p.Version);
            p = _store.SetLineDecision(id, "L2", ReviewerDecision.Rejected, "not this one", p.Version);
            p = _store.SetLineDecision(id, "L3", ReviewerDecision.Approved, null, p.Version);
            _store.Approve(id, "preparer", ProposalStore.ChannelControlPage, p.Version);

            var report = Ok(await tools.LacerteProposalCommit(id));
            Assert.Equal("Committed", report.GetProperty("status").GetString());
            Assert.Equal(2, report.GetProperty("ranCount").GetInt32());
            var lines = report.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("lineId").GetString()!);
            Assert.False(lines.ContainsKey("L2"));
            Assert.Equal("Succeeded", lines["L1"].GetProperty("result").GetProperty("status").GetString());
            Assert.True(lines["L1"].GetProperty("result").GetProperty("verified").GetBoolean());
            Assert.Equal("SucceededUnverified", lines["L3"].GetProperty("result").GetProperty("status").GetString()); // sim detail: no read-back

            Assert.Equal("APPROVED-VALUE", await ReadField(mgr, "C1_15"));
            Assert.Equal(rejectedBefore, await ReadField(mgr, "C1_71"));
            var rows = await mgr.ReadDetail(Client);
            Assert.Equal(detailBefore + 1, rows.Count);
            Assert.Single(rows, r => r.Description == "PROPOSAL W2");

            var stored = _store.Get(id)!;
            Assert.Equal(ProposalStatus.Committed, stored.Status);
            Assert.StartsWith("proposal-" + id + "-v", stored.Lines[0].Commit!.IdempotencyKey);
            Assert.Null(stored.Lines[1].Commit);
            Assert.NotNull(stored.CommitLease!.FinishedAt);

            // Nothing left: a second commit is refused and writes nothing.
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalCommit(id)));
            Assert.Equal(detailBefore + 1, (await mgr.ReadDetail(Client)).Count);
        }

        [Fact]
        public async Task Commit_RefusedWhenContentChangedAfterApproval()
        {
            using var mgr = await NewSimAsync();
            var service = new ProposalService(mgr, _store);
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "ORIGINAL") }));
            var before = await ReadField(mgr, "C1_15");
            SubmitAndApprove(_store, _store.Get(id)!);

            // Tampering that bypassed the version bump: the hash catches it.
            _store.Mutate(id, q => q.Lines[0].Value = JsonSerializer.SerializeToElement("TAMPERED"));
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(id));
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, ex.StatusCode);
            Assert.Contains("content hash", (string)ex.Context["reason"]!);

            // A version bump without re-approval is refused too.
            _store.Mutate(id, q => { q.Lines[0].Value = JsonSerializer.SerializeToElement("ORIGINAL"); q.Version++; });
            ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(id));
            Assert.Contains("version", (string)ex.Context["reason"]!);

            Assert.Equal(before, await ReadField(mgr, "C1_15"));
            Assert.Equal(ProposalStatus.Approved, _store.Get(id)!.Status);
        }

        [Fact]
        public async Task AgentEditAfterApproval_RevokesIt_CommitRefused()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "A") }));
            SubmitAndApprove(_store, _store.Get(id)!);

            var view = Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_71", "B") }));
            Assert.Equal("Draft", view.GetProperty("status").GetString());
            var stored = _store.Get(id)!;
            Assert.Null(stored.Approval);
            Assert.Equal(SyntheticStatusCodes.ProposalConflict, Err(await tools.LacerteProposalCommit(id)));
        }

        [Fact]
        public async Task PartialFailure_ReCommitRunsOnlyUnfinishedLines_VerifiedLineNotRewritten()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "FIRST"), Field("C1_71", "SECOND") }));
            var p = SubmitAndApprove(_store, _store.Get(id)!);

            // Make L1's commit key collide with an earlier, different write (9102):
            // L1 fails at commit while its dry run passed; L2 must still run.
            var l1Key = "proposal-" + id + "-v" + p.Version + "-L1";
            await mgr.WriteField(1, Client, "C1_15", "SOMETHING-ELSE", idempotencyKey: l1Key);

            var report = Ok(await tools.LacerteProposalCommit(id));
            Assert.Equal("PartiallyCommitted", report.GetProperty("status").GetString());
            Assert.Equal(1, report.GetProperty("failedCount").GetInt32());
            Assert.Equal(1, report.GetProperty("succeededCount").GetInt32());
            Assert.Equal("SECOND", await ReadField(mgr, "C1_71"));
            var stored = _store.Get(id)!;
            Assert.Equal(LineCommitStatus.Failed, stored.Lines[0].Commit!.Status);
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, stored.Lines[0].Commit!.StatusCode);

            // Someone changes L2's field in Lacerte afterwards; the re-commit must not touch it.
            await mgr.WriteField(1, Client, "C1_71", "CHANGED-IN-LACERTE");
            mgr.ForgetIdempotencyKey(l1Key);

            report = Ok(await tools.LacerteProposalCommit(id));
            Assert.Equal("Committed", report.GetProperty("status").GetString());
            Assert.Equal(1, report.GetProperty("ranCount").GetInt32());
            Assert.Equal(1, report.GetProperty("alreadyDoneCount").GetInt32());
            Assert.Equal("FIRST", await ReadField(mgr, "C1_15"));
            Assert.Equal("CHANGED-IN-LACERTE", await ReadField(mgr, "C1_71"));
            Assert.Equal(2, _store.Get(id)!.Lines[0].Commit!.Attempts);
        }

        [Fact]
        public async Task InterruptedCommit_IsRecovered_FieldRerunDetailUncertain()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "AFTER-CRASH"), Detail(5, "MAYBE") }));
            SubmitAndApprove(_store, _store.Get(id)!);
            // Simulate a server that died mid-commit: status Committing, lines Running, lease from a dead pid.
            _store.Mutate(id, q =>
            {
                q.Status = ProposalStatus.Committing;
                q.CommitLease = new CommitLease { By = "x", ProcessId = int.MaxValue, StartedAt = DateTimeOffset.UtcNow };
                foreach (var l in q.Lines) l.Commit = new LineCommitResult { Status = LineCommitStatus.Running, Attempts = 1 };
            });

            var report = Ok(await tools.LacerteProposalCommit(id));
            var stored = _store.Get(id)!;
            Assert.Equal(LineCommitStatus.Succeeded, stored.Lines[0].Commit!.Status);
            Assert.Equal(LineCommitStatus.Uncertain, stored.Lines[1].Commit!.Status);
            Assert.Equal(ProposalStatus.PartiallyCommitted, stored.Status);
            Assert.Equal("PartiallyCommitted", report.GetProperty("status").GetString());
            Assert.DoesNotContain(await mgr.ReadDetail(Client), r => r.Description == "MAYBE");
            Assert.Contains(stored.History, e => e.Action == "commitRecovered");
        }

        // ---- access control -------------------------------------------------------------------

        [Fact]
        public async Task ReadOnlyDevice_MayGetAndList_ButNotCreateEditOrCommit()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "X") }));
            SubmitAndApprove(_store, _store.Get(id)!);

            SessionScope.Current = new SessionScope { ReadOnlyAccess = true, DeviceName = "phone" };
            try
            {
                mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                Ok(await tools.LacerteProposalGet(id));
                Ok(await tools.LacerteProposalList());
                Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, Err(await tools.LacerteProposalCreate(Client)));
                Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, Err(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "Y") })));
                Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, Err(await tools.LacerteProposalAddQuestions(id, new[] { new QuestionInput { Text = "?" } })));
                Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, Err(await tools.LacerteProposalCommit(id)));
                // The service refuses a read-only caller too (defense in depth).
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => new ProposalService(mgr, _store).CommitAsync(id));
                Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, ex.StatusCode);
            }
            finally
            {
                SessionScope.Current = null;
            }
            Assert.Equal(ProposalStatus.Approved, _store.Get(id)!.Status);
        }

        [Fact]
        public async Task CommitFromControlPage_InHttpMode_RunsInItsOwnScope()
        {
            using var mgr = await NewSimAsync();
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "FROM-CONTROL-PAGE") }));
            SubmitAndApprove(_store, _store.Get(id)!);

            // HTTP mode, no connection scope (a control-page request): the
            // commit opens its own scope with the proposal's context.
            SessionScope.Required = true;
            try
            {
                var report = await new ProposalService(mgr, _store).CommitAsync(id, "owner (control page)");
                Assert.Equal(ProposalStatus.Committed, report.Status);
                Assert.Null(SessionScope.Current);
            }
            finally
            {
                SessionScope.Required = false;
            }
            Assert.Equal("FROM-CONTROL-PAGE", await ReadField(mgr, "C1_15"));
            Assert.Equal("owner (control page)", _store.Get(id)!.CommitLease!.By);
        }

        // ---- LACERTE_REQUIRE_APPROVAL --------------------------------------------------------

        [Fact]
        public async Task RequireApproval_BlocksDirectWrites_NotDryRunsNorProposalCommits()
        {
            using var mgr = await NewSimAsync();
            mgr.RequireApproval = true;

            Assert.Equal(SyntheticStatusCodes.ApprovalRequired, Err(await new FieldWriteTools(mgr).LacerteFieldWrite(1, Client, "C1_15", "DIRECT")));
            Assert.Equal(SyntheticStatusCodes.ApprovalRequired, Err(await new UpdateReturnTools(mgr).LacerteUpdateReturn(1, Client, "C1_15", "DIRECT")));
            Assert.Equal(SyntheticStatusCodes.ApprovalRequired, Err(await new DetailsTools(mgr).LacerteDetailWrite(Client, "11", "1", 1, "DIRECT", suffix: "1000")));
            Assert.Equal(SyntheticStatusCodes.ApprovalRequired, Err(await new FieldWriteBulkTools(mgr).LacerteFieldWriteBulk(1,
                new[] { new FieldWriteBulkTools.BulkWriteEntry { ClientNumber = Client, Field = "C1_15", Value = "DIRECT" } })));
            Assert.Equal(SyntheticStatusCodes.ApprovalRequired, Err(await new DetailWriteBulkTools(mgr).LacerteDetailWriteBulk(
                new[] { new DetailWriteBulkTools.BulkDetailEntry { ClientNumber = Client, Series = "11", Code = "1", Suffix = "1000", Amount = 1, Description = "DIRECT" } },
                previewToken: "whatever")));
            Assert.NotEqual("DIRECT", await ReadField(mgr, "C1_15"));

            // Dry runs still work.
            Ok(await new FieldWriteTools(mgr).LacerteFieldWrite(1, Client, "C1_15", "DIRECT", dryRun: true));
            Ok(await new DetailsTools(mgr).LacerteDetailWrite(Client, "11", "1", 1, "DIRECT", suffix: "1000", dryRun: true));
            // Client creation is not gated.
            var create = JsonDocument.Parse(await new CreateClientTools(mgr).LacerteClientCreate("APPR01", "Individual", "Ann", "Approval", "ann@example.com", dryRun: true)).RootElement;
            Assert.NotEqual(SyntheticStatusCodes.ApprovalRequired, create.TryGetProperty("statusCode", out var sc) ? sc.GetInt32() : 0);

            // The proposal path still dry-runs and commits.
            var tools = new ProposalTools(mgr, _store);
            var id = Id(Ok(await tools.LacerteProposalCreate(Client)));
            var added = Ok(await tools.LacerteProposalAddLines(id, new[] { Field("C1_15", "VIA-PROPOSAL") }));
            Assert.True(added.GetProperty("added")[0].GetProperty("ok").GetBoolean());
            SubmitAndApprove(_store, _store.Get(id)!);
            Assert.Equal("Committed", Ok(await tools.LacerteProposalCommit(id)).GetProperty("status").GetString());
            Assert.Equal("VIA-PROPOSAL", await ReadField(mgr, "C1_15"));

            // And the bypass doesn't leak out of the commit.
            Assert.Equal(SyntheticStatusCodes.ApprovalRequired, Err(await new FieldWriteTools(mgr).LacerteFieldWrite(1, Client, "C1_15", "DIRECT")));
        }

        [Fact]
        public void RequireApproval_ReadFromEnv_DefaultOff()
        {
            using var off = new LacerteSessionManager(new Dictionary<string, string?>());
            Assert.False(off.RequireApproval);
            using var on = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_REQUIRE_APPROVAL"] = "1" });
            Assert.True(on.RequireApproval);
            using var typo = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_REQUIRE_APPROVAL"] = "true" });
            Assert.False(typo.RequireApproval);
        }

        // ---- live detail lines (FP-2 harness) -------------------------------------------------

        private static DetailRow Row(double amount, string description)
            => new DetailRow("11", "", "1", "1000", "", "", "Source", "Taxpayer", "", amount, description);

        /// <summary>Runner that "inserts" the proposal row whose description is in the SQL, except the ones in <paramref name="lose"/> (reported OK, never stored → 9022).</summary>
        private static Func<string, DetailWriteRunResult> InsertByDescription(FakeDetailDb db, string client, IDictionary<string, DetailRow> rows, ISet<string> lose)
            => sql =>
            {
                foreach (var kv in rows)
                {
                    if (!sql.Contains(kv.Key)) continue;
                    if (!lose.Contains(kv.Key)) db.For(client).Add(kv.Value);
                    break;
                }
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1, ExitCode = 0 };
            };

        private Proposal NewLiveProposal() => _store.Create(
            new ProposalContext { DataPath = LiveDetailWriteHarness.DataPath, TaxYear = 2024, Module = "I" }, "SMIT", "live", null, "stdio");

        [Fact]
        public async Task Live_AddLines_DetailDryRun_EnforcesDictionaryAndSuffix()
        {
            var snap = LiveDetailWriteHarness.NewTempDir();
            try
            {
                var db = new FakeDetailDb();
                db.For("SMIT");
                var runner = new FakeDetailWriteRunner();
                using var mgr = LiveDetailWriteHarness.NewEnabled(db, runner, snap);
                var service = new ProposalService(mgr, _store);
                var p = NewLiveProposal();

                var (updated, ids) = await service.AddLinesAsync(p.Id, new[]
                {
                    Detail(100, "MAPPED"),
                    Detail(100, "UNMAPPED", code: "2"),     // not in the verified dictionary → 9012
                    Detail(100, "NOSUFFIX", suffix: ""),    // live needs Suffix
                }, CancellationToken.None);

                Assert.Equal(3, ids.Count);
                Assert.True(updated.Lines[0].Preview.Ok, updated.Lines[0].Preview.Message);
                Assert.Empty(updated.Lines[0].Warnings);
                Assert.False(updated.Lines[1].Preview.Ok);
                Assert.False(updated.Lines[2].Preview.Ok);
                // The named dictionary entry pins Code 1: a Code 2 line contradicts it.
                Assert.Contains("doesn't match dictionary entry", updated.Lines[1].Preview.Message);

                var noDictId = Detail(100, "UNMAPPED2", code: "2");
                noDictId.DictionaryId = null;
                (updated, _) = await service.AddLinesAsync(p.Id, new[] { noDictId }, CancellationToken.None);
                Assert.False(updated.Lines[3].Preview.Ok);
                Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, updated.Lines[3].Preview.StatusCode);

                // Dry runs: no worker, no backup.
                Assert.Empty(runner.Calls);
                Assert.Empty(System.IO.Directory.GetFiles(snap, "*.json"));
            }
            finally
            {
                LiveDetailWriteHarness.TryDelete(snap);
            }
        }

        [Fact]
        public async Task Live_Commit_PartialFailure_ReCommit_NoDuplicateDetailRows()
        {
            var snap = LiveDetailWriteHarness.NewTempDir();
            try
            {
                var db = new FakeDetailDb();
                db.For("SMIT");
                var runner = new FakeDetailWriteRunner();
                using var mgr = LiveDetailWriteHarness.NewEnabled(db, runner, snap);
                var service = new ProposalService(mgr, _store);
                var p = NewLiveProposal();
                await service.AddLinesAsync(p.Id, new[] { Detail(100, "ROW-A"), Detail(200, "ROW-B"), Detail(300, "ROW-C") }, CancellationToken.None);
                SubmitAndApprove(_store, _store.Get(p.Id)!);

                var rows = new Dictionary<string, DetailRow> { ["ROW-A"] = Row(100, "ROW-A"), ["ROW-B"] = Row(200, "ROW-B"), ["ROW-C"] = Row(300, "ROW-C") };
                var lose = new HashSet<string> { "ROW-B" };
                runner.Behavior = InsertByDescription(db, "SMIT", rows, lose);

                // ROW-B's read-back fails (9022: the row may be there) → Uncertain, commit stops, ROW-C skipped.
                var r1 = await service.CommitAsync(p.Id);
                Assert.Equal(ProposalStatus.PartiallyCommitted, r1.Status);
                Assert.True(r1.StoppedEarly);
                Assert.Equal(1, r1.SucceededCount);
                Assert.Equal(1, r1.UncertainCount);
                Assert.Equal(1, r1.SkippedCount);
                Assert.Equal(2, runner.Calls.Count);
                var stored = _store.Get(p.Id)!;
                Assert.Equal(LineCommitStatus.Succeeded, stored.Lines[0].Commit!.Status);
                Assert.True(stored.Lines[0].Commit!.Verified);
                Assert.NotNull(stored.Lines[0].Commit!.Snapshot);
                Assert.Equal(LineCommitStatus.Uncertain, stored.Lines[1].Commit!.Status);
                Assert.Equal(SyntheticStatusCodes.WriteNotVerified, stored.Lines[1].Commit!.StatusCode);
                Assert.Equal(LineCommitStatus.Skipped, stored.Lines[2].Commit!.Status);

                // Re-commit: only ROW-C runs. ROW-A (verified) and ROW-B (may have landed) are not touched.
                lose.Clear();
                var r2 = await service.CommitAsync(p.Id);
                Assert.Equal(1, r2.RanCount);
                Assert.Equal(3, runner.Calls.Count);
                Assert.Equal(ProposalStatus.PartiallyCommitted, r2.Status);
                Assert.Single(db.For("SMIT"), r => r.Description == "ROW-A");
                Assert.Single(db.For("SMIT"), r => r.Description == "ROW-C");
                Assert.Equal(new[] { "L2" }, ProposalStore.Evaluate(_store.Get(p.Id)!).UncertainLineIds);

                // Nothing else to run: refused until a person resolves L2.
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => service.CommitAsync(p.Id));
                Assert.Contains("L2", (string)ex.Context["reason"]!);

                // The operator checked Lacerte: ROW-B is missing → the next commit writes it once, with a new key.
                _store.ResolveUncertainLine(p.Id, "L2", rowIsInReturn: false, "preparer", ProposalStore.ChannelCli, "not there");
                var r3 = await service.CommitAsync(p.Id);
                Assert.Equal(ProposalStatus.Committed, r3.Status);
                Assert.Equal(4, runner.Calls.Count);
                Assert.Single(db.For("SMIT"), r => r.Description == "ROW-B");
                Assert.EndsWith("-L2-g1", _store.Get(p.Id)!.Lines[1].Commit!.IdempotencyKey);
            }
            finally
            {
                LiveDetailWriteHarness.TryDelete(snap);
            }
        }

        [Fact]
        public async Task Live_FieldFailure_Continues_ToDetailLines()
        {
            var snap = LiveDetailWriteHarness.NewTempDir();
            try
            {
                var db = new FakeDetailDb();
                db.For("SMIT");
                var runner = new FakeDetailWriteRunner();
                using var mgr = LiveDetailWriteHarness.NewEnabled(db, runner, snap);
                var service = new ProposalService(mgr, _store);
                var p = NewLiveProposal();
                // The fake DB has no DATA1 row: the field write is accepted but its read-back finds no row → 9022 → Failed.
                await service.AddLinesAsync(p.Id, new[] { Field("C1_15", "LIVE"), Detail(100, "ROW-A") }, CancellationToken.None);
                SubmitAndApprove(_store, _store.Get(p.Id)!);
                runner.Behavior = InsertByDescription(db, "SMIT", new Dictionary<string, DetailRow> { ["ROW-A"] = Row(100, "ROW-A") }, new HashSet<string>());

                var r = await service.CommitAsync(p.Id);
                Assert.False(r.StoppedEarly);
                Assert.Equal(1, r.FailedCount);
                Assert.Equal(1, r.SucceededCount);
                Assert.Equal(ProposalStatus.PartiallyCommitted, r.Status);
                Assert.Equal(SyntheticStatusCodes.WriteNotVerified, _store.Get(p.Id)!.Lines[0].Commit!.StatusCode);
                Assert.Single(db.For("SMIT"));
            }
            finally
            {
                LiveDetailWriteHarness.TryDelete(snap);
            }
        }
    }
}
