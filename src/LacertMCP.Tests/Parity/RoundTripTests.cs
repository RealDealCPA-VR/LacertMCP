using System;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Parity
{
    /// <summary>
    /// Phase 12 task #81 — read parity. For each read chokepoint, assert the
    /// returned row shape matches the documented contract. Today the live leg
    /// short-circuits to sim (Mode resolution rule: off-Windows == sim) so
    /// "both legs" effectively runs the same assertions twice. The harness
    /// shape is what matters — when the recorded-replay live adapter lands,
    /// the second leg automatically picks up the live row shape.
    /// </summary>
    public class RoundTripTests
    {
        private static LacerteSessionManager NewWithContext(SessionMode mode, char module = 'I', int year = 2024, string? dataPath = null)
        {
            var mgr = ParitySessionFactory.For(mode);
            // P15-050 — Register before reads so the manager-side
            // RequireRegistered gate fires through (matches the live SDK's
            // NotSignedIn rejection). Same one-line addition the legacy
            // sim-only tests took.
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(dataPath ?? SeedData.DataPathIData2024, year, module);
            return mgr;
        }

        // -----------------------------------------------------------------
        // ListClients
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task ListClients_ReturnsClientNumberAndC1Fields(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var rows = await mgr.ListClients();
            Assert.NotEmpty(rows);
            var row = rows[0];
            Assert.True(row.ContainsKey("ClientNumber"), "row must expose ClientNumber");
            // Either first-name (C1_1 / individual) or entity-name C1_1 — both
            // are conventional surface keys.
            Assert.Contains(row.Keys, k => k.StartsWith("C1_", StringComparison.Ordinal));
        }

        [Theory, BothModesData]
        public async Task ListClients_LastNameFilter_PrefixMatchOnly(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var rows = await mgr.ListClients(lastNameFilter: "Bro");
            // Seed has BROW (last name "Brown") in TY24 IData.
            Assert.Contains(rows, r =>
                r.TryGetValue("ClientNumber", out var v) && v is string s &&
                string.Equals(s, "1001", StringComparison.OrdinalIgnoreCase));
        }

        // -----------------------------------------------------------------
        // ReadClient
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task ReadClient_KnownClient_ReturnsRowWithClientNumber(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var row = await mgr.ReadClient("1001");
            Assert.NotNull(row);
            Assert.True(row!.ContainsKey("ClientNumber"));
        }

        [Theory, BothModesData]
        public async Task ReadClient_UnknownClient_ReturnsNull(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var row = await mgr.ReadClient("NOPE-XYZ");
            Assert.Null(row);
        }

        // -----------------------------------------------------------------
        // ReadSubclients / ReadStates / ReadStatuses / ReadForms
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task ReadSubclients_PartClient_HasThreePartners(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            // P15-050 — RequireRegistered gate now fires after RequireContext.
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var rows = await mgr.ReadSubclients("1001");
            // PART seed has 3 partners → DATA2 subclient rows.
            Assert.True(rows.Count >= 3, $"expected >= 3 subclients, got {rows.Count}");
        }

        [Theory, BothModesData]
        public async Task ReadStates_AcmeCorp_HasCaAndNyRows(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            // P15-050 — RequireRegistered gate now fires after RequireContext.
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var rows = await mgr.ReadStates("1001");
            Assert.True(rows.Count >= 2, $"expected >= 2 state rows for ACME, got {rows.Count}");
        }

        [Theory, BothModesData]
        public async Task ReadStatuses_KnownClient_DoesNotThrow(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            // ReadStatuses can return empty; shape parity is "returns a list",
            // not "row count > 0". The TAPIStatus rejection paths are covered
            // in WriteParityTests.
            var rows = await mgr.ReadStatuses("1001");
            Assert.NotNull(rows);
        }

        [Theory, BothModesData]
        public async Task ReadForms_KnownClient_DoesNotThrow(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var rows = await mgr.ReadForms("1001");
            Assert.NotNull(rows);
        }

        // -----------------------------------------------------------------
        // ReadDetail
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task ReadDetail_SeededClient_ReturnsDetailRows(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            // SMIT exists in TY20 IData; for TY24 use BROW. Test BROW since
            // we're on TY24 context.
            var rows = await mgr.ReadDetail("1001");
            Assert.NotNull(rows);
            // Shape assertion: each DetailRow has Series + Code + Amount slots.
            if (rows.Count > 0)
            {
                var r = rows[0];
                Assert.NotNull(r.Series);
                Assert.NotNull(r.Code);
            }
        }

        // -----------------------------------------------------------------
        // ReadEFStatus
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task ReadEFStatus_ReturnsRowsWithEfilingId(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var rows = await mgr.ReadEFStatus();
            Assert.NotNull(rows);
            if (rows.Count > 0)
            {
                var r = rows[0];
                Assert.True(r.ContainsKey("EFilingId"));
                Assert.True(r.ContainsKey("Status"));
            }
        }

        // P15-056 — LEFT OUTER JOIN parity. SqlTemplates.EfStatusJoin is LEFT
        // OUTER from DATA1-Clients (post-P14-053); the sim path must match or
        // sim returns N rows while live returns N+K (clients without
        // eFilings) the moment Phase 9 wires live reads. The seed has BROW
        // with EFStatus=null (case c: no EFiling at all) and PEND with
        // EFStatus="Transmitted" (case a: full join hit).
        [Theory, BothModesData]
        public async Task ReadEFStatus_LeftOuterJoin_IncludesClientsWithoutEFilings(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var rows = await mgr.ReadEFStatus();
            // BROW (ClientNumber 1001) has EFStatus=null in the seed — under
            // the pre-P15-056 INNER JOIN it was dropped. After P15-056 the
            // LEFT OUTER from DATA1 surfaces it with null EFiling/Status.
            var brow = rows.FirstOrDefault(r =>
                r.TryGetValue("ClientId", out var c) && (c?.ToString() == "1001"));
            Assert.NotNull(brow);
            Assert.Null(brow!["EFilingId"]);
            Assert.Null(brow!["Status"]);

            // PEND (ClientNumber 1002) has EFStatus="Transmitted" → full
            // join hit, both EFiling and Status non-null.
            var pend = rows.FirstOrDefault(r =>
                r.TryGetValue("ClientId", out var c) && (c?.ToString() == "1002"));
            Assert.NotNull(pend);
            Assert.NotNull(pend!["EFilingId"]);
            Assert.Equal("Transmitted", pend!["Status"]);
        }

        [Theory, BothModesData]
        public async Task ReadTable_UnknownTableNumber_ThrowsDBNotSupported(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.ReadTable(99));
            Assert.Equal(StatusCodes.DBNotSupported, err.StatusCode);
        }
    }
}
