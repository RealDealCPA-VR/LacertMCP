using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// Phase 12 task #84 â€” end-to-end full flow against the simulation
    /// backend. Exercises every chokepoint type the operator's day-1 workflow
    /// touches: Register â†’ SetContext â†’ ListClients â†’ ReadClient â†’ WriteField
    /// (success) â†’ CreateClient â†’ re-list and confirm new row.
    /// </summary>
    public class FullFlowTests
    {
        [Fact]
        public async Task FullFlow_Sim_RegisterContextReadWriteCreateVerify()
        {
            using var mgr = new LacerteSessionManager(new System.Collections.Generic.Dictionary<string, string?>());
            Assert.Equal(SessionMode.Simulation, mgr.Mode);

            // ----- 1. Register -----
            var regStatus = await mgr.Register();
            Assert.Equal(StatusCodes.AccessGranted, regStatus);
            Assert.True(mgr.IsRegistered);

            // ----- 2. SetContext -----
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var status = mgr.SessionStatus();
            Assert.NotNull(status.Context);
            Assert.Equal('I', status.Context!.Module);
            Assert.Equal(2024, status.Context.Year);

            // ----- 3. ListClients (initial roster) -----
            var beforeRows = await mgr.ListClients();
            var beforeCount = beforeRows.Count;
            Assert.True(beforeCount > 0, "seed should populate at least one client in TY24 IData");

            // Sanity-check that one of the seeded clients (BROW) is present.
            Assert.Contains(beforeRows, r =>
                r.TryGetValue("ClientNumber", out var v) && v is string s &&
                s == "1001");

            // ----- 4. ReadClient one row -----
            var smitRow = await mgr.ReadClient("1001");
            Assert.NotNull(smitRow);
            Assert.True(smitRow!.ContainsKey("ClientNumber"));

            // ----- 5. WriteField with no fixture seam â€” exercises the
            // fail-closed access-map gate. The map ships {} in v1; the
            // populated-map happy path is covered by
            // FieldWriteRoundTripTests (P15-011) and the unit-level happy
            // path tests in FieldWriteToolsTests, both of which use
            // AccessMapTestFixture to seed I|1|C1_15 as ReadWrite.
            //
            // Here we assert the structured error shape â€” proves the gate
            // fires before any wire side-effect. C1_1 is Unknown in the
            // empty v1 map â†’ 9012 UnknownFieldAccess (distinct from 9001
            // FieldIsReadOnly, which is for known non-writable fields or
            // read-only sessions).
            var writeErr = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_1", "NewFirst",
                    dryRun: true, idempotencyKey: "fullflow-field-1"));
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, writeErr.StatusCode);

            // ----- 6. CreateClient -----
            var createArgs = new CreateClientArgs(
                ClientId: "NEW1",
                TaxType: "1040",
                FirstName: "First",
                LastName: "Last",
                EmailId: "first.last@example.com");
            var createResult = await mgr.CreateClient(createArgs, idempotencyKey: "fullflow-create-1");
            Assert.Equal(StatusCodes.Success, createResult.Status);
            Assert.NotNull(createResult.ClientNumber);

            // ----- 7. Re-list and confirm the new client appears -----
            var afterRows = await mgr.ListClients();
            Assert.True(afterRows.Count == beforeCount + 1,
                $"expected count to grow by 1; before={beforeCount}, after={afterRows.Count}");
            Assert.Contains(afterRows, r =>
                r.TryGetValue("ClientID", out var v) && v is string s &&
                string.Equals(s, "NEW1", System.StringComparison.OrdinalIgnoreCase));

            // ----- 8. Idempotency replay returns the same ClientNumber -----
            var replay = await mgr.CreateClient(createArgs, idempotencyKey: "fullflow-create-1");
            Assert.Equal(createResult.ClientNumber, replay.ClientNumber);

            // ----- 9. SessionStatus snapshot remains coherent at the end -----
            var endStatus = mgr.SessionStatus();
            Assert.Equal(SessionMode.Simulation, endStatus.Mode);
            Assert.True(endStatus.IsRegistered);
        }
    }
}
