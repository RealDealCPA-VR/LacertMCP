using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Tests.Parity;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-039 — integration coverage for REQUIREMENTS §1 named operator
    /// flow #4: "Watching e-file status across the firm". Asserts the
    /// EFStatus row envelope carries the documented contract columns
    /// (ClientId / EFilingId / EFId / InstanceId / Status / StatusDate)
    /// and that bucketing by Status — the work the
    /// <c>lacerte_review_ef_status</c> prompt scripts — actually composes
    /// against the wire shape. Locks down P14-022 (column-projection
    /// sim/live parity) at the cross-tool layer.
    /// </summary>
    public class FullFlow_EfStatusReview
    {
        [Theory, BothModesData]
        public async Task EfStatusReview_ReadEFStatus_EnvelopeCarriesContractColumns_AndBucketsByStatus(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var rows = await mgr.ReadEFStatus();
            Assert.NotNull(rows);
            // TY24 IData seeds PEND with EFStatus="Transmitted" → at least one
            // EFStatus row exists. If seed shrinks, the EFStatus flow has
            // nothing to discriminate; better to fail loudly than silently pass.
            Assert.True(rows.Count > 0,
                "TY24 IData seed must include at least one EFStatus row (PEND=Transmitted)");

            // Contract columns — the operator-facing keys the prompt names.
            foreach (var row in rows)
            {
                Assert.True(row.ContainsKey("EFilingId"), "row missing EFilingId");
                Assert.True(row.ContainsKey("ClientId"),  "row missing ClientId");
                Assert.True(row.ContainsKey("EFId"),      "row missing EFId");
                Assert.True(row.ContainsKey("InstanceId"),"row missing InstanceId");
                Assert.True(row.ContainsKey("Status"),    "row missing Status");
                Assert.True(row.ContainsKey("StatusDate"),"row missing StatusDate");
            }

            // Bucketing — the prompt instructs "group by Status". Drives the
            // same client-side filter the LLM applies to the raw envelope.
            var byStatus = rows
                .Where(r => r.TryGetValue("Status", out var v) && v is string)
                .GroupBy(r => (string)r["Status"]!)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.True(byStatus.Count >= 1,
                "expected at least one Status bucket from the seed");

            // PEND (ClientNumber 1002) is seeded with Transmitted in TY24 IData.
            Assert.Contains(rows, r =>
                r.TryGetValue("ClientId", out var c)  && c is string ci && ci == "1002" &&
                r.TryGetValue("Status",   out var s)  && s is string st && st == "Transmitted");

            // Drill into one client — the prompt's per-row follow-up.
            var first = rows[0];
            var clientNumber = (string)first["ClientId"]!;
            var clientRow = await mgr.ReadClient(clientNumber);
            Assert.NotNull(clientRow);
            Assert.True(clientRow!.ContainsKey("ClientNumber"));
            Assert.Equal(clientNumber, (string)clientRow["ClientNumber"]!);
        }
    }
}
