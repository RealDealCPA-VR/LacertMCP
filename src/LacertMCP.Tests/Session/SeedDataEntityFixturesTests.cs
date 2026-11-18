using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using LacertMCP.Tests.Tools;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// FP-5 — S-Corporation (TY24 SData: ORCH, HOLW) and Fiduciary (TY24
    /// FData: MAPL, BIRC) simulation fixtures. Pins the seeded shape the
    /// pre-filing catalog relies on (DATA2 owner rows, DATA5 status rows,
    /// DATA7 main-form rows) and smoke-tests set_context + client_list on
    /// the new datapaths through the real tool surface.
    /// </summary>
    public class SeedDataEntityFixturesTests
    {
        [Theory]
        [InlineData(SeedData.DataPathSData2024, "S", "ORCH", "HOLW")]
        [InlineData(SeedData.DataPathFData2024, "F", "MAPL", "BIRC")]
        public async Task SetContextThenClientList_NewDatapath_ListsBothSeededClients(
            string dataPath, string module, string firstId, string secondId)
        {
            var mgr = ToolTestHelpers.NewSimManager();
            var session = new SessionTools(mgr);
            var setRoot = ToolTestHelpers.Parse(
                await session.LacerteSetContext(dataPath, module: module, taxYear: 2024));
            Assert.True(setRoot.GetProperty("success").GetBoolean());
            Assert.Equal(dataPath, setRoot.GetProperty("result").GetProperty("dataPath").GetString());
            Assert.Equal(module, setRoot.GetProperty("result").GetProperty("module").GetString());

            var clients = new ClientsTools(mgr);
            var listRoot = ToolTestHelpers.Parse(await clients.LacerteClientList());
            Assert.True(listRoot.GetProperty("success").GetBoolean());
            var result = listRoot.GetProperty("result");
            Assert.Equal(2, result.GetProperty("count").GetInt32());
            var ids = result.GetProperty("clients").EnumerateArray()
                .Select(c => c.GetProperty("clientId").GetString())
                .ToArray();
            Assert.Contains(firstId, ids);
            Assert.Contains(secondId, ids);
        }

        [Fact]
        public void SCorpSeed_OrchHasTwoShareholders_HolwHasNone()
        {
            var store = new SimulationStore(sessionExpired: false);
            var mod = store.TryGetModule(SeedData.DataPathSData2024, 2024, 'S');
            Assert.NotNull(mod);
            Assert.Equal(2, mod!.Clients.Count);
            Assert.Equal(1003, mod.NextClientNumber);

            Assert.Equal(2, SubClientCount(mod, SeedData.ORCH.ClientNumber));
            Assert.Equal(0, SubClientCount(mod, SeedData.HOLW.ClientNumber));

            // Main-form row in the column the pre-filing rule reads (C7_1).
            foreach (var c in new[] { SeedData.ORCH, SeedData.HOLW })
            {
                Assert.Equal("1120S", mod.Forms[c.ClientNumber].Single().Fields["C7_1"]);
                Assert.True(mod.Statuses.ContainsKey(c.ClientNumber));
                Assert.NotEmpty(mod.Details[c.ClientNumber]);
            }
            Assert.True(mod.States.ContainsKey((SeedData.ORCH.ClientNumber, "CA")));
            Assert.True(mod.States.ContainsKey((SeedData.ORCH.ClientNumber, "NV")));
            Assert.True(mod.States.ContainsKey((SeedData.HOLW.ClientNumber, "TX")));
        }

        [Fact]
        public void FiduciarySeed_MaplHasTwoBeneficiaries_BircHasNone()
        {
            var store = new SimulationStore(sessionExpired: false);
            var mod = store.TryGetModule(SeedData.DataPathFData2024, 2024, 'F');
            Assert.NotNull(mod);
            Assert.Equal(2, mod!.Clients.Count);
            Assert.Equal(1003, mod.NextClientNumber);

            Assert.Equal(2, SubClientCount(mod, SeedData.MAPL.ClientNumber));
            Assert.Equal(0, SubClientCount(mod, SeedData.BIRC.ClientNumber));

            foreach (var c in new[] { SeedData.MAPL, SeedData.BIRC })
            {
                Assert.Equal("1041", mod.Forms[c.ClientNumber].Single().Fields["C7_1"]);
                Assert.True(mod.Statuses.ContainsKey(c.ClientNumber));
                Assert.NotEmpty(mod.Details[c.ClientNumber]);
            }
        }

        [Fact]
        public void EntitySeeds_DetailRowsReuseExistingSeriesIdsOnly()
        {
            // FP-5 guard: the new detail rows must not invent Series meanings
            // — only the pre-existing SeriesIds constants are allowed.
            var known = new HashSet<string>
            {
                SeedData.SeriesIds.Wages, SeedData.SeriesIds.Int, SeedData.SeriesIds.Div,
                SeedData.SeriesIds.Dep, SeedData.SeriesIds.Inc, SeedData.SeriesIds.State,
                SeedData.SeriesIds.Partner,
            };
            var rows = SeedData.OrchDetails()
                .Concat(SeedData.HolwDetails())
                .Concat(SeedData.MaplDetails())
                .Concat(SeedData.BircDetails());
            Assert.All(rows, r => Assert.Contains(r.Series, known));
        }

        [Fact]
        public void AllSeeds_ClientIdsAreUniqueAndAtMostEightAlphanumeric()
        {
            var ids = new[]
            {
                SeedData.SMIT.ClientID, SeedData.JONE.ClientID, SeedData.REJE.ClientID,
                SeedData.BROW.ClientID, SeedData.PEND.ClientID,
                SeedData.ACME.ClientID, SeedData.WIDG.ClientID,
                SeedData.PART.ClientID,
                SeedData.ORCH.ClientID, SeedData.HOLW.ClientID,
                SeedData.MAPL.ClientID, SeedData.BIRC.ClientID,
            };
            Assert.Equal(12, ids.Length);
            Assert.Equal(ids.Length, ids.Distinct().Count());
            Assert.All(ids, id =>
            {
                Assert.InRange(id.Length, 1, 8);
                Assert.True(id.All(char.IsLetterOrDigit), "ClientID must be alphanumeric: " + id);
            });
        }

        [Fact]
        public void ContextNotSetRemediation_ListsEverySeededSimPath()
        {
            var remediation = SyntheticStatusCodes.StatusTable[SyntheticStatusCodes.ContextNotSet].Remediation;
            Assert.NotNull(remediation);
            foreach (var path in new[]
            {
                SeedData.DataPathIData2020, SeedData.DataPathIData2024,
                SeedData.DataPathCData2024, SeedData.DataPathPData2024,
                SeedData.DataPathSData2024, SeedData.DataPathFData2024,
            })
            {
                Assert.Contains(path, remediation);
            }
        }

        private static int SubClientCount(ModuleStore mod, string clientNumber)
            => mod.SubClients.Keys.Count(k => k.Client == clientNumber);
    }
}
