using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using LacertMCP.Tests.Session;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Filing-preparation decision 2 — <c>lacerte_detail_write_bulk</c> commits
    /// only a batch a dry run previewed. A passing dry run issues a
    /// previewToken bound to mode + context + canonical entries +
    /// continueOnError; a real call without a matching, unexpired token from
    /// the same connection is refused with 9017 before any row runs. Covered
    /// in simulation and through the live harness (two-mode discipline).
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class DetailWriteBulkPreviewTokenTests : IDisposable
    {
        private readonly string _dir = LiveDetailWriteHarness.NewTempDir();

        public void Dispose() => LiveDetailWriteHarness.TryDelete(_dir);

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        private static (LacerteSessionManager mgr, DetailWriteBulkTools tool) NewSim()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            return (mgr, new DetailWriteBulkTools(mgr));
        }

        private static DetailWriteBulkTools.BulkDetailEntry W2(string client, double amount, string description, string tsj = "T") =>
            new DetailWriteBulkTools.BulkDetailEntry
            {
                ClientNumber = client,
                Series = SeedData.SeriesIds.Wages,
                Code = "1",
                Amount = amount,
                Description = description,
                Source = "Source",
                Tsj = tsj,
            };

        private static DetailWriteBulkTools.BulkDetailEntry[] Batch() => new[]
        {
            W2("1001", 100.0, "row 0"),
            W2("1001", 200.0, "row 1"),
        };

        private static void AssertRefused(string json, string problem)
        {
            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, root.GetProperty("statusCode").GetInt32());
            var ctx = root.GetProperty("context");
            Assert.Equal("previewToken", ctx.GetProperty("argument").GetString());
            Assert.Equal(problem, ctx.GetProperty("previewTokenProblem").GetString());
            Assert.Contains("dryRun:true", ctx.GetProperty("reason").GetString());
            Assert.Equal(0, ctx.GetProperty("rowsWritten").GetInt32());
        }

        // ---- token issuance ---------------------------------------------------

        [Fact]
        public async Task DryRun_AllRowsPass_IssuesUrlSafe128BitToken_With30MinuteExpiry()
        {
            var (mgr, tool) = NewSim();
            var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
            mgr.DetailBulkPreviews.Clock = () => now;

            var result = Parse(await tool.LacerteDetailWriteBulk(Batch(), dryRun: true)).GetProperty("result");

            var token = result.GetProperty("previewToken").GetString();
            Assert.Matches(new Regex("^[A-Za-z0-9_-]{22}$"), token);
            Assert.Equal(now.AddMinutes(30), DateTimeOffset.Parse(result.GetProperty("previewExpiresUtc").GetString()!));
            Assert.Contains("previewToken", result.GetProperty("previewNote").GetString());

            // Fresh token per dry run.
            var again = Parse(await tool.LacerteDetailWriteBulk(Batch(), dryRun: true))
                .GetProperty("result").GetProperty("previewToken").GetString();
            Assert.NotEqual(token, again);
        }

        [Fact]
        public async Task DryRun_AnyRowFails_NoToken_AndCommitIsRefused()
        {
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;
            var entries = Batch();
            entries[1].Code = "x1"; // 9008

            var result = Parse(await tool.LacerteDetailWriteBulk(entries, dryRun: true, continueOnError: true))
                .GetProperty("result");
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("previewToken").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("previewExpiresUtc").ValueKind);
            Assert.Contains("No previewToken", result.GetProperty("previewNote").GetString());

            AssertRefused(await tool.LacerteDetailWriteBulk(entries, continueOnError: true), "missing");
            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task DryRun_Cancelled_NoToken()
        {
            var (_, tool) = NewSim();
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            var result = Parse(await tool.LacerteDetailWriteBulk(Batch(), dryRun: true, cancellationToken: cts.Token))
                .GetProperty("result");
            Assert.True(result.GetProperty("stoppedEarly").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("previewToken").ValueKind);
        }

        // ---- refusals: nothing written ------------------------------------------

        [Fact]
        public async Task Commit_WithoutToken_Refused_NothingWritten()
        {
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;

            AssertRefused(await tool.LacerteDetailWriteBulk(Batch()), "missing");
            AssertRefused(await tool.LacerteDetailWriteBulk(Batch(), previewToken: "   "), "missing");

            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Commit_UnknownToken_Refused_NothingWritten()
        {
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;
            await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

            AssertRefused(await tool.LacerteDetailWriteBulk(Batch(), previewToken: "AAAAAAAAAAAAAAAAAAAAAA"), "unknown");

            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Commit_TokenFromAnotherManager_IsUnknown()
        {
            var (_, toolA) = NewSim();
            var (mgrB, toolB) = NewSim();
            var before = (await mgrB.ReadDetail("1001")).Count;
            var token = await DetailWriteBulkToolsTests.PreviewToken(toolA, Batch());

            AssertRefused(await toolB.LacerteDetailWriteBulk(Batch(), previewToken: token), "unknown");
            Assert.Equal(before, (await mgrB.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Commit_ExpiredToken_Refused_ButValidJustBeforeExpiry()
        {
            var (mgr, tool) = NewSim();
            var now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
            mgr.DetailBulkPreviews.Clock = () => now;
            var before = (await mgr.ReadDetail("1001")).Count;
            var token = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

            now = now.AddMinutes(30).AddSeconds(1);
            var json = await tool.LacerteDetailWriteBulk(Batch(), previewToken: token);
            AssertRefused(json, "expired");
            Assert.True(Parse(json).GetProperty("context").TryGetProperty("previewExpiresUtc", out _));
            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);

            // A new preview gives a working token; it is accepted at 29:59.
            var token2 = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());
            now = now.AddMinutes(29).AddSeconds(59);
            var ok = Parse(await tool.LacerteDetailWriteBulk(Batch(), previewToken: token2));
            Assert.True(ok.GetProperty("success").GetBoolean(), ok.ToString());
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Commit_EntriesChangedAfterPreview_Refused_NothingWritten()
        {
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;
            var token = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

            // Changed amount.
            var changed = Batch();
            changed[1].Amount = 2000.0;
            var json = await tool.LacerteDetailWriteBulk(changed, previewToken: token);
            AssertRefused(json, "mismatched");
            Assert.Equal("entries",
                Parse(json).GetProperty("context").GetProperty("changedSincePreview")[0].GetString());

            // Reordered.
            var reordered = Enumerable.Reverse(Batch()).ToArray(); // explicit LINQ: C# 14 binds array.Reverse() to the void span overload
            AssertRefused(await tool.LacerteDetailWriteBulk(reordered, previewToken: token), "mismatched");

            // A row dropped.
            AssertRefused(await tool.LacerteDetailWriteBulk(Batch().Take(1).ToArray(), previewToken: token), "mismatched");

            // A row added.
            var added = Batch().Concat(new[] { W2("1001", 300.0, "row 2") }).ToArray();
            AssertRefused(await tool.LacerteDetailWriteBulk(added, previewToken: token), "mismatched");

            // continueOnError flipped.
            var flipped = await tool.LacerteDetailWriteBulk(Batch(), continueOnError: true, previewToken: token);
            AssertRefused(flipped, "mismatched");
            Assert.Equal("continueOnError",
                Parse(flipped).GetProperty("context").GetProperty("changedSincePreview")[0].GetString());

            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);

            // The untouched batch still commits with the same token.
            var ok = Parse(await tool.LacerteDetailWriteBulk(Batch(), previewToken: token));
            Assert.True(ok.GetProperty("success").GetBoolean(), ok.ToString());
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Commit_ContextChangedAfterPreview_Refused_NothingWritten()
        {
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;
            var token = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            var json = await tool.LacerteDetailWriteBulk(Batch(), previewToken: token);
            AssertRefused(json, "mismatched");
            var changed = Parse(json).GetProperty("context").GetProperty("changedSincePreview")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("dataPath", changed);
            Assert.Contains("taxYear", changed);

            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Commit_EquivalentSpelling_SameCanonicalRows_Accepted()
        {
            // The binding hashes the rows as the session writes them: trimmed
            // client number, TSJ T → Taxpayer, Source case-normalized.
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;
            var token = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

            var spelled = Batch();
            foreach (var e in spelled)
            {
                e.ClientNumber = " 1001 ";
                e.Tsj = "Taxpayer";
                e.Source = "source";
            }
            var ok = Parse(await tool.LacerteDetailWriteBulk(spelled, previewToken: token));
            Assert.True(ok.GetProperty("success").GetBoolean(), ok.ToString());
            Assert.Equal(2, ok.GetProperty("result").GetProperty("succeededCount").GetInt32());
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        // ---- valid tokens --------------------------------------------------------

        [Fact]
        public async Task Commit_ValidToken_Writes_AndEchoesTokenAndExpiry()
        {
            var (mgr, tool) = NewSim();
            var before = (await mgr.ReadDetail("1001")).Count;
            var token = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

            var root = Parse(await tool.LacerteDetailWriteBulk(Batch(), previewToken: token));
            Assert.True(root.GetProperty("success").GetBoolean(), root.ToString());
            var result = root.GetProperty("result");
            Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(token, result.GetProperty("previewToken").GetString());
            Assert.Equal(JsonValueKind.String, result.GetProperty("previewExpiresUtc").ValueKind);
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task Token_ReusableWithinWindow_ForResubmissionOfHalfFailedBatch()
        {
            // The first commit fails (the context was re-set read-only after
            // the preview — the same triple, so the token still matches; the
            // batch stops at row 0 and skips row 1). Once writable again, the
            // resubmission with the SAME entries, key and token is accepted
            // without a new preview and writes both rows; a further
            // resubmission replays from cache (no duplicate appends).
            var (mgr, tool) = NewSim();
            var now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
            mgr.DetailBulkPreviews.Clock = () => now;
            var before = (await mgr.ReadDetail("1001")).Count;
            var entries = Batch();
            var token = await DetailWriteBulkToolsTests.PreviewToken(tool, entries);

            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly: true);
            var first = Parse(await tool.LacerteDetailWriteBulk(entries,
                idempotencyKey: "resub", previewToken: token)).GetProperty("result");
            Assert.Equal(0, first.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, first.GetProperty("failedCount").GetInt32());
            Assert.Equal(1, first.GetProperty("skippedCount").GetInt32());
            Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);

            // 20 minutes later, writable again: same entries + key + token.
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            now = now.AddMinutes(20);
            var second = Parse(await tool.LacerteDetailWriteBulk(entries,
                idempotencyKey: "resub", previewToken: token));
            Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
            Assert.Equal(2, second.GetProperty("result").GetProperty("succeededCount").GetInt32());
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);

            now = now.AddMinutes(9);
            var third = Parse(await tool.LacerteDetailWriteBulk(entries,
                idempotencyKey: "resub", previewToken: token));
            Assert.Equal(2, third.GetProperty("result").GetProperty("succeededCount").GetInt32());
            Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
        }

        [Fact]
        public async Task EmptyBatch_NeedsNoToken()
        {
            var (_, tool) = NewSim();
            var root = Parse(await tool.LacerteDetailWriteBulk(Array.Empty<DetailWriteBulkTools.BulkDetailEntry>()));
            Assert.True(root.GetProperty("success").GetBoolean(), root.ToString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("result").GetProperty("previewToken").ValueKind);
        }

        // ---- HTTP connection binding --------------------------------------------

        [Fact]
        public async Task HttpScope_TokenOnlyWorksOnTheIssuingConnection()
        {
            var (mgr, tool) = NewSim();
            var a = new SessionScope { DeviceName = "a" };
            var b = new SessionScope { DeviceName = "b" };
            try
            {
                SessionScope.Current = a;
                mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                var before = (await mgr.ReadDetail("1001")).Count;
                var token = await DetailWriteBulkToolsTests.PreviewToken(tool, Batch());

                SessionScope.Current = b;
                mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                AssertRefused(await tool.LacerteDetailWriteBulk(Batch(), previewToken: token), "wrongConnection");

                SessionScope.Current = null; // stdio-style caller
                AssertRefused(await tool.LacerteDetailWriteBulk(Batch(), previewToken: token), "wrongConnection");

                SessionScope.Current = a;
                Assert.Equal(before, (await mgr.ReadDetail("1001")).Count);
                var ok = Parse(await tool.LacerteDetailWriteBulk(Batch(), previewToken: token));
                Assert.True(ok.GetProperty("success").GetBoolean(), ok.ToString());
                Assert.Equal(before + 2, (await mgr.ReadDetail("1001")).Count);
            }
            finally
            {
                SessionScope.Current = null;
            }
        }

        // ---- live harness ----------------------------------------------------------

        private static DetailWriteBulkTools.BulkDetailEntry[] LiveBatch() => new[]
        {
            new DetailWriteBulkTools.BulkDetailEntry { ClientNumber = "SMIT", Series = "11", Code = "1", Suffix = "1000", Amount = 100, Description = "row1", Source = "Source", Tsj = "T" },
            new DetailWriteBulkTools.BulkDetailEntry { ClientNumber = "SMIT", Series = "11", Code = "1", Suffix = "1000", Amount = 200, Description = "row2", Source = "Source", Tsj = "T" },
        };

        private (LacerteSessionManager mgr, DetailWriteBulkTools tool, FakeDetailDb db, FakeDetailWriteRunner runner) NewLive()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            int n = 0;
            runner.Behavior = _ =>
            {
                n++;
                db.For("SMIT").Add(LiveDetailWriteHarness.W2(n * 100.0, "row" + n));
                return new DetailWriteRunResult { Status = DetailWriteRunStatus.Ok, RowsAffected = 1 };
            };
            var mgr = LiveDetailWriteHarness.NewEnabled(db, runner, _dir);
            return (mgr, new DetailWriteBulkTools(mgr), db, runner);
        }

        [Fact]
        public async Task Live_CommitWithoutOrWithWrongToken_Refused_NoWorkerRun_NoBackup()
        {
            var (mgr, tool, db, runner) = NewLive();
            using (mgr)
            {
                AssertRefused(await tool.LacerteDetailWriteBulk(LiveBatch(), idempotencyKey: "live-none"), "missing");
                AssertRefused(await tool.LacerteDetailWriteBulk(LiveBatch(), idempotencyKey: "live-bad",
                    previewToken: "not-a-real-token"), "unknown");

                Assert.Empty(runner.Calls);
                Assert.Empty(db.For("SMIT"));
                Assert.Empty(Directory.GetFiles(_dir));
            }
        }

        [Fact]
        public async Task Live_ExpiredToken_Refused()
        {
            var (mgr, tool, db, runner) = NewLive();
            using (mgr)
            {
                var now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
                mgr.DetailBulkPreviews.Clock = () => now;
                var token = await DetailWriteBulkToolsTests.PreviewToken(tool, LiveBatch());
                now = now.AddMinutes(31);

                AssertRefused(await tool.LacerteDetailWriteBulk(LiveBatch(), previewToken: token), "expired");
                Assert.Empty(runner.Calls);
                Assert.Empty(db.For("SMIT"));
            }
        }

        [Fact]
        public async Task Live_EntriesOrContextChangedAfterPreview_Refused_NoWorkerRun()
        {
            var (mgr, tool, db, runner) = NewLive();
            using (mgr)
            {
                var token = await DetailWriteBulkToolsTests.PreviewToken(tool, LiveBatch());

                var changed = LiveBatch();
                changed[0].Description = "edited after preview";
                AssertRefused(await tool.LacerteDetailWriteBulk(changed, previewToken: token), "mismatched");

                mgr.SetContext(LiveDetailWriteHarness.DataPath, 2023, 'I');
                AssertRefused(await tool.LacerteDetailWriteBulk(LiveBatch(), previewToken: token), "mismatched");
                mgr.SetContext(LiveDetailWriteHarness.DataPath, 2024, 'I');

                Assert.Empty(runner.Calls);
                Assert.Empty(db.For("SMIT"));
                Assert.Empty(Directory.GetFiles(_dir));
            }
        }

        [Fact]
        public async Task Live_ValidToken_Commits_AndIsReusableForResubmission()
        {
            var (mgr, tool, db, runner) = NewLive();
            using (mgr)
            {
                var token = await DetailWriteBulkToolsTests.PreviewToken(tool, LiveBatch());

                var root = Parse(await tool.LacerteDetailWriteBulk(LiveBatch(), idempotencyKey: "live-ok", previewToken: token));
                Assert.True(root.GetProperty("success").GetBoolean(), root.ToString());
                Assert.Equal(2, root.GetProperty("result").GetProperty("succeededCount").GetInt32());
                Assert.Equal(2, runner.Calls.Count);

                // Resubmission (same key + token): replays, no new worker run.
                var again = Parse(await tool.LacerteDetailWriteBulk(LiveBatch(), idempotencyKey: "live-ok", previewToken: token));
                Assert.True(again.GetProperty("success").GetBoolean(), again.ToString());
                Assert.Equal(2, again.GetProperty("result").GetProperty("succeededCount").GetInt32());
                Assert.Equal(2, runner.Calls.Count);
                Assert.Equal(2, db.For("SMIT").Count);
            }
        }

        // ---- store -------------------------------------------------------------------

        private static BulkPreviewBinding Binding(string hash = "h") =>
            new BulkPreviewBinding("Simulation", @"C:\Lacerte\24Tax\IData", 2024, 'I', hash, 1, false);

        [Fact]
        public void Store_CappedAtCapacity_OldestEvicted()
        {
            var store = new BulkPreviewTokenStore { Capacity = 3 };
            var t0 = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
            var now = t0;
            store.Clock = () => now;

            var tokens = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                now = t0.AddSeconds(i);
                tokens.Add(store.Issue(Binding(), null).Token);
            }

            Assert.Equal(3, store.Count);
            Assert.Equal(BulkPreviewTokenProblem.Unknown, store.Check(tokens[0], Binding(), null).Problem);
            Assert.Equal(BulkPreviewTokenProblem.Unknown, store.Check(tokens[1], Binding(), null).Problem);
            Assert.True(store.Check(tokens[4], Binding(), null).Accepted);
        }

        [Fact]
        public void Store_DefaultCapacityIs1000_AndExpiredTokensArePruned()
        {
            Assert.Equal(1000, BulkPreviewTokenStore.DefaultCapacity);
            Assert.Equal(TimeSpan.FromMinutes(30), BulkPreviewTokenStore.DefaultLifetime);

            var store = new BulkPreviewTokenStore();
            var now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
            store.Clock = () => now;
            var old = store.Issue(Binding(), null).Token;

            // Just expired: still reported as "expired" (kept for one extra lifetime).
            now = now.AddMinutes(31);
            Assert.Equal(BulkPreviewTokenProblem.Expired, store.Check(old, Binding(), null).Problem);

            // Long expired: pruned on the next call.
            now = now.AddMinutes(30);
            store.Issue(Binding(), null);
            Assert.Equal(1, store.Count);
            Assert.Equal(BulkPreviewTokenProblem.Unknown, store.Check(old, Binding(), null).Problem);
        }

        [Fact]
        public void Store_MismatchNamesTheChangedParts()
        {
            var store = new BulkPreviewTokenStore();
            var token = store.Issue(Binding(), null).Token;

            var check = store.Check(token, Binding() with { Mode = "Live", Module = 'C', EntriesHash = "other" }, null);
            Assert.Equal(BulkPreviewTokenProblem.Mismatched, check.Problem);
            Assert.Equal(new[] { "mode", "module", "entries" }, check.Mismatches);
        }
    }
}
