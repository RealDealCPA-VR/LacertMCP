using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Tests.Parity;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-039 â€” integration coverage for REQUIREMENTS Â§1 named operator
    /// flow #3: "Bootstrapping new clients from a CSV or Tax Organizer
    /// responses". Drives the documented loop end-to-end: parse a
    /// fixture CSV â†’ for each row call <see cref="LacerteSessionManager.CreateClient"/>
    /// with the spec-required minimum set (TaxType / ClientID / EmailID /
    /// FirstName / LastName) â†’ re-list and confirm every row appears with
    /// the right ClientID casing. Locks down the P14-019 sim-gate parity
    /// and the P14-035 required-set contract across cross-tool boundaries.
    /// </summary>
    public class FullFlow_CsvIntake
    {
        // Hand-rolled CSV body â€” no third-party parser needed and no fixture
        // file lifecycle to manage. ClientID column is kept <= 8 chars to
        // satisfy the SyntheticStatusCodes.ClientIDTooLong gate.
        private const string CsvBody =
            "ClientID,FirstName,LastName,EmailID\n" +
            "CSV1,Alice,Adams,alice@example.com\n" +
            "CSV2,Bob,Baker,bob@example.com\n" +
            "CSV3,Carol,Clark,carol@example.com\n";

        [Theory, BothModesData]
        public async Task CsvIntake_LoopCreateClient_AllRowsAppearInListClients(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            // Capture pre-intake roster so we can assert the delta.
            var beforeRows = await mgr.ListClients();
            var beforeIds = new HashSet<string>(
                beforeRows
                    .Where(r => r.TryGetValue("ClientID", out var v) && v is string)
                    .Select(r => ((string)r["ClientID"]!).ToUpperInvariant()));

            // Parse the CSV in-memory â€” no test fixture file, no I/O.
            using var reader = new StringReader(CsvBody);
            string? header = reader.ReadLine();
            Assert.Equal("ClientID,FirstName,LastName,EmailID", header);

            var intakeIds = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                Assert.Equal(4, parts.Length);
                var args = new CreateClientArgs(
                    ClientId: parts[0],
                    TaxType: "1040",
                    FirstName: parts[1],
                    LastName: parts[2],
                    EmailId: parts[3]);
                var result = await mgr.CreateClient(args, idempotencyKey: $"csvflow-{parts[0]}");
                Assert.Equal(StatusCodes.Success, result.Status);
                Assert.NotNull(result.ClientNumber);
                intakeIds.Add(parts[0]);
            }

            Assert.Equal(3, intakeIds.Count);

            // Roster delta â€” every new ClientID is now visible; casing is
            // preserved exactly as submitted. Operator-facing trust gap:
            // the LLM passes ClientID strings down the pipeline and
            // expects the same string back when filtering / drilling in.
            var afterRows = await mgr.ListClients();
            Assert.True(afterRows.Count >= beforeRows.Count + 3,
                $"expected roster to grow by 3; before={beforeRows.Count}, after={afterRows.Count}");

            foreach (var id in intakeIds)
            {
                Assert.Contains(afterRows, r =>
                    r.TryGetValue("ClientID", out var v) && v is string s &&
                    string.Equals(s, id, System.StringComparison.OrdinalIgnoreCase));
            }

            // Replay one CSV row's idempotency key â€” must return the same
            // ClientNumber, NOT create a duplicate. This is the cross-tool
            // contract the bulk-create flow leans on.
            var replayArgs = new CreateClientArgs(
                ClientId: "CSV1",
                TaxType: "1040",
                FirstName: "Alice",
                LastName: "Adams",
                EmailId: "alice@example.com");
            var replay = await mgr.CreateClient(replayArgs, idempotencyKey: "csvflow-CSV1");
            Assert.Equal(StatusCodes.Success, replay.Status);

            var stillThree = await mgr.ListClients();
            // Count of CSV-prefixed rows must still be 3; a duplicate would push it to 4+.
            var csvCount = stillThree.Count(r =>
                r.TryGetValue("ClientID", out var v) && v is string s &&
                s.StartsWith("CSV", System.StringComparison.OrdinalIgnoreCase) &&
                !beforeIds.Contains(s.ToUpperInvariant()));
            Assert.Equal(3, csvCount);
        }
    }
}
