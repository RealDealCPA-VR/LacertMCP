using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Tools;
using LacertMCP.Tests.Session;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// FP-2 — the detail-write tools surface the live outcome (verified,
    /// verificationNote, snapshot) on success and the structured error on a
    /// read-back mismatch, through the fake ODBC + fake worker harness.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class DetailWriteLiveToolsTests : IDisposable
    {
        private readonly string _dir = LiveDetailWriteHarness.NewTempDir();

        public void Dispose() => LiveDetailWriteHarness.TryDelete(_dir);

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public async Task DetailWrite_Live_ReturnsVerifiedAndSnapshot()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            var runner = new FakeDetailWriteRunner();
            runner.Behavior = LiveDetailWriteHarness.AppendOnRun(db, "SMIT", LiveDetailWriteHarness.W2(1200, "ACME"));
            using var mgr = LiveDetailWriteHarness.NewEnabled(db, runner, _dir);
            var tool = new DetailsTools(mgr);

            var root = Parse(await tool.LacerteDetailWrite("SMIT", "11", "1", 1200, "ACME", suffix: "1000", source: "Source", tsj: "T"));

            Assert.True(root.GetProperty("success").GetBoolean(), root.ToString());
            var result = root.GetProperty("result");
            Assert.Equal(StatusCodes.Success, result.GetProperty("statusCode").GetInt32());
            Assert.True(result.GetProperty("verified").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("verificationNote").ValueKind);
            Assert.True(File.Exists(result.GetProperty("snapshot").GetString()));
        }

        [Fact]
        public async Task DetailWrite_Live_Mismatch_Is9022Envelope()
        {
            var db = new FakeDetailDb();
            db.For("SMIT");
            using var mgr = LiveDetailWriteHarness.NewEnabled(db, new FakeDetailWriteRunner(), _dir);
            var tool = new DetailsTools(mgr);

            var root = Parse(await tool.LacerteDetailWrite("SMIT", "11", "1", 1200, "ACME", suffix: "1000"));

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DetailWriteBulk_Live_PerRowVerifiedAndSnapshot_AndDisabledIs9202()
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
            using var mgr = LiveDetailWriteHarness.NewEnabled(db, runner, _dir);
            var tool = new DetailWriteBulkTools(mgr);
            var entries = new[]
            {
                new DetailWriteBulkTools.BulkDetailEntry { ClientNumber = "SMIT", Series = "11", Code = "1", Suffix = "1000", Amount = 100, Description = "row1", Source = "Source", Tsj = "T" },
                new DetailWriteBulkTools.BulkDetailEntry { ClientNumber = "SMIT", Series = "11", Code = "1", Suffix = "1000", Amount = 200, Description = "row2", Source = "Source", Tsj = "T" },
            };

            var root = Parse(await tool.LacerteDetailWriteBulk(entries, idempotencyKey: "fp2-bulk"));
            Assert.True(root.GetProperty("success").GetBoolean(), root.ToString());
            var result = root.GetProperty("result");
            Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());
            var rows = result.GetProperty("rows");
            string? snap = null;
            foreach (var r in rows.EnumerateArray())
            {
                Assert.True(r.GetProperty("verified").GetBoolean());
                var s = r.GetProperty("snapshot").GetString();
                snap ??= s;
                Assert.Equal(snap, s); // one backup per client
            }
            Assert.Single(Directory.GetFiles(_dir, "*.json"));

            mgr.LiveDetailWritesEnabled = false;
            var off = Parse(await tool.LacerteDetailWriteBulk(entries, idempotencyKey: "fp2-bulk-off"));
            var offRows = off.GetProperty("result").GetProperty("rows");
            Assert.Equal(SyntheticStatusCodes.SimulationOnly, offRows[0].GetProperty("statusCode").GetInt32());
            Assert.Equal(2, runner.Calls.Count);
        }
    }
}
