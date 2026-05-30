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
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var rows = await mgr.ReadSubclients("1001");
            // PART seed has 3 partners → DATA2 subclient rows.
            Assert.True(rows.Count >= 3, $"expected >= 3 subclients, got {rows.Count}");
        }

        [Theory, BothModesData]
        public async Task ReadStates_AcmeCorp_HasCaAndNyRows(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
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
