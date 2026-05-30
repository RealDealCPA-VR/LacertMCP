using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// F11 integration test — wires <see cref="WireLog"/> on via
    /// <c>EnableForTests</c>, drives the full <c>ListClients → WriteField →
    /// CreateClient</c> sequence through the session manager in sim mode, then
    /// asserts the on-disk log file contains the right ops with redacted args.
    /// Exercises every WireLog wrap added under F11 except the live-only
    /// CanUseNewApis branch (sim's CanUseNewApis is a compile-time bool, not
    /// logged on purpose).
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class WireLogSessionIntegrationTests
    {
        // -----------------------------------------------------------------
        // Access-map seed: WriteField runs AssertWritable BEFORE WireLog
        // records, so we need at least one ReadWrite entry to reach the
        // recorded sim write. Mirrors the seed in
        // SessionManagerBridgeWireupTests.
        // -----------------------------------------------------------------

        private const char WritableModule = 'I';
        private const int WritableTable = 1;
        private const string WritableField = "C1_15";

        private static readonly object _seedLock = new object();

        private static void EnsureWritableFieldSeeded()
        {
            lock (_seedLock)
            {
                var t = typeof(FieldAccessMapLoader);
                var cacheField = t.GetField("_moduleCache", BindingFlags.NonPublic | BindingFlags.Static);
                var cache = cacheField?.GetValue(null) as ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>>;
                if (cache == null) return;

                var key = $"{WritableModule}|{WritableTable}|{WritableField}";
                if (cache.TryGetValue(WritableModule, out var existing) && existing.ContainsKey(key)) return;

                var seed = new Dictionary<string, FieldEntry>();
                if (existing != null)
                {
                    foreach (var kv in existing) seed[kv.Key] = kv.Value;
                }
                seed[key] = new FieldEntry(FieldAccess.ReadWrite, Source: "wirelog-integration-seed");
                cache[WritableModule] = seed;
            }
        }

        [Fact]
        public async Task FullSequence_WritesOpsToLogfile_WithRedactedArgs()
        {
            EnsureWritableFieldSeeded();
            var dir = Path.Combine(Path.GetTempPath(), "wirelog-int-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                WireLog.EnableForTests(dir);
                Assert.True(WireLog.IsEnabled);

                var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
                mgr.SetContext(SeedData.DataPathIData2024, 2024, WritableModule);
                // P14-030 — CreateClient delegates to SimulateCreateClient which
                // requires Register first (NotSignedIn gate).
                await mgr.Register();

                // 1) ListClients — should write op="ListClients".
                var clients = await mgr.ListClients(limit: 100);
                Assert.NotEmpty(clients);

                // 2) WriteField — write a value that contains an "SSN" key
                //    inside the value object would be ideal, but the value is
                //    a flat string here; the redaction surface for WriteField
                //    args is the value itself. Use a string value to keep the
                //    test focused on op-name + redaction-of-other-args.
                var writeRc = await mgr.WriteField(
                    table: WritableTable,
                    clientNumber: "1001",
                    field: WritableField,
                    value: "wire-log-int-test");
                Assert.Equal(StatusCodes.Success, writeRc);

                // 3) CreateClient — args.EmailID is logged; the redaction
                //    layer redacts SSN/EIN/BankAccountNumber/RoutingNumber, so
                //    pass an SSN field on the args object via field name to
                //    show the redaction works. CreateClientArgs has no SSN
                //    property, so we assert presence of CreateClient op only
                //    and assert no plain-text "***" sentinels are missing for
                //    a separately-written-record with secret args.
                var createResult = await mgr.CreateClient(new CreateClientArgs(
                    ClientID: "WLOG",
                    TaxType: "Individual",
                    FirstName: "Wire",
                    LastName: "Log",
                    EmailID: "wirelog@example.com"));
                Assert.Equal(StatusCodes.Success, createResult.Status);

                // Assert: the logfile exists and contains one line per op.
                // P14-012: WireLog now caches the StreamWriter per logfile
                // path across records (perf — avoids per-call open/close).
                // FlushForTests closes the cached handle so File.ReadAllLines
                // can open the file with its default FileShare.Read mode.
                WireLog.FlushForTests(dir);
                var files = Directory.GetFiles(dir, "lacerte-*.log");
                Assert.Single(files);

                var lines = File.ReadAllLines(files[0]);
                var ops = lines.Select(line =>
                {
                    using var doc = JsonDocument.Parse(line);
                    return doc.RootElement.GetProperty("op").GetString();
                }).ToList();

                Assert.Contains("ListClients", ops);
                Assert.Contains("WriteField", ops);
                Assert.Contains("CreateClient", ops);

                // Drive a redacted-args record explicitly (the wrapped
                // chokepoints above don't expose SSN/EIN-shaped args, so we
                // assert the redaction pipeline end-to-end by writing one
                // record with a sensitive key through the same logfile).
                var sensitive = new { SSN = "111-22-3333", BankAccountNumber = "9999" };
                WireLog.WriteForTest(dir, "Redaction.Probe", sensitive, new { ok = true }, durationMs: 1);

                // WriteForTest flushes its own writer on exit, but the
                // earlier Record/RecordSync calls may have left a cached
                // writer open. Flush again before the second ReadAllLines.
                WireLog.FlushForTests(dir);
                var afterLines = File.ReadAllLines(files[0]);
                var probeLine = afterLines.Last(l => l.Contains("Redaction.Probe"));
                Assert.DoesNotContain("111-22-3333", probeLine);
                Assert.DoesNotContain("9999", probeLine);
                Assert.Contains("\"SSN\":\"***\"", probeLine);
                Assert.Contains("\"BankAccountNumber\":\"***\"", probeLine);
            }
            finally
            {
                WireLog.DisableForTests();
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
