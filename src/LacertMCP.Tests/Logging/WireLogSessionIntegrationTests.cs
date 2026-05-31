using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
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
        // recorded sim write. Uses the shared in-memory helper —
        // additive-only, race-safe with the on-disk Tools/* tests via
        // [Collection(AccessMapMutationCollection.Name)].
        // -----------------------------------------------------------------

        private const char WritableModule = 'I';
        private const int WritableTable = 1;
        private const string WritableField = "C1_15";

        private static void EnsureWritableFieldSeeded()
            => LacertMCP.Tests.AccessMapInMemorySeed.EnsureWritableFieldSeeded(
                WritableModule, WritableTable, WritableField, source: "wirelog-integration-seed");

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

                // Assert: the logfile(s) exist and contain one line per op.
                // P14-012: WireLog caches the StreamWriter per logfile path
                // across records. FlushForTests closes the cached handle so
                // File.ReadAllLines can open with FileShare.Read.
                //
                // P15-078: date-roll-agnostic enumeration. A test that spans
                // UTC midnight produces two files; concatenating all matching
                // files preserves the semantic assertion ("at least one file
                // exists with these ops") without the date-boundary flake.
                WireLog.FlushForTests(dir);
                var files = Directory.GetFiles(dir, "lacerte-*.log");
                Assert.NotEmpty(files);

                var lines = files.SelectMany(File.ReadAllLines).ToArray();
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
                // writer open. Flush again before re-reading. The probe may
                // land in either day's file when we span midnight, so re-glob.
                WireLog.FlushForTests(dir);
                var afterFiles = Directory.GetFiles(dir, "lacerte-*.log");
                var afterLines = afterFiles.SelectMany(File.ReadAllLines).ToArray();
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
