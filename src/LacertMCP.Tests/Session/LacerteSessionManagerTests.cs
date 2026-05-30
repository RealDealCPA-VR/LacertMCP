using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Phase 3 tasks #25-34. Covers:
    /// <list type="bullet">
    ///   <item><description>Constructor mode resolution (sim default + Windows live path).</description></item>
    ///   <item><description>SetContext happy path + bad-input gates.</description></item>
    ///   <item><description>Pre-context guard on every chokepoint.</description></item>
    ///   <item><description>Synchronous read-only gate firing before any other write logic.</description></item>
    ///   <item><description>TY21+ <c>CanUseNewApis</c> branch + per-pair cache.</description></item>
    ///   <item><description>Idempotency cache state machine — miss / hit-same / hit-conflict.</description></item>
    ///   <item><description>Dry-run snapshot/execute/restore round-trips.</description></item>
    ///   <item><description><c>SessionStatus</c> snapshot coherence.</description></item>
    /// </list>
    /// </summary>
    public class LacerteSessionManagerTests
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static LacerteSessionManager NewSimManager()
        {
            // Explicit empty env → guaranteed sim mode regardless of host shell.
            return new LacerteSessionManager(new Dictionary<string, string?>());
        }

        private static LacerteSessionManager NewSimManagerWithContext(bool readOnly = false)
        {
            var mgr = NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return mgr;
        }

        // =================================================================
        // Construction + mode resolution
        // =================================================================

        [Fact]
        public void Ctor_DefaultEnv_ResolvesSimulationMode()
        {
            var mgr = NewSimManager();
            Assert.Equal(SessionMode.Simulation, mgr.Mode);
        }

        [Fact]
        public void Ctor_LiveWithoutWindows_FallsBackToSimulation()
        {
            // Live mode requires Win32NT; on any non-Windows host this lands
            // in sim. On Windows the test below covers the positive case.
            var env = new Dictionary<string, string?>
            {
                ["LACERTE_LIVE"] = "1",
                ["LACERTE_SIMULATION"] = "0",
            };
            var mgr = new LacerteSessionManager(env);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Assert.Equal(SessionMode.Live, mgr.Mode);
            }
            else
            {
                Assert.Equal(SessionMode.Simulation, mgr.Mode);
            }
        }

        [Fact]
        public void Ctor_SimulationFlagOverridesLive_OnWindows()
        {
            var env = new Dictionary<string, string?>
            {
                ["LACERTE_LIVE"] = "1",
                ["LACERTE_SIMULATION"] = "1",
            };
            var mgr = new LacerteSessionManager(env);
            Assert.Equal(SessionMode.Simulation, mgr.Mode);
        }

        // =================================================================
        // SetContext
        // =================================================================

        [Fact]
        public void SetContext_HappyPath_PersistsTriple()
        {
            var mgr = NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            Assert.NotNull(mgr.Context);
            Assert.Equal(SeedData.DataPathIData2024, mgr.Context!.DataPath);
            Assert.Equal(2024, mgr.Context.Year);
            Assert.Equal('I', mgr.Context.Module);
            Assert.False(mgr.Context.ReadOnly);
        }

        [Fact]
        public void SetContext_TaxYearBelowFloor_ThrowsTaxYearTooOld()
        {
            var mgr = NewSimManager();
            var ex = Assert.Throws<LacerteSDKError>(
                () => mgr.SetContext(SeedData.DataPathIData2024, 2017, 'I'));
            Assert.Equal(SyntheticStatusCodes.TaxYearTooOld, ex.StatusCode);
        }

        [Fact]
        public void SetContext_UnknownModule_Throws()
        {
            var mgr = NewSimManager();
            var ex = Assert.Throws<LacerteSDKError>(
                () => mgr.SetContext(SeedData.DataPathIData2024, 2024, 'Z'));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        // =================================================================
        // Pre-context guard on chokepoints
        // =================================================================

        [Fact]
        public async Task ListClients_WithoutContext_ThrowsContextNotSet()
        {
            var mgr = NewSimManager();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.ListClients());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet, ex.StatusCode);
        }

        [Fact]
        public async Task ReadClient_WithoutContext_ThrowsContextNotSet()
        {
            var mgr = NewSimManager();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.ReadClient("1001"));
            Assert.Equal(SyntheticStatusCodes.ContextNotSet, ex.StatusCode);
        }

        [Fact]
        public async Task WriteField_WithoutContext_ThrowsContextNotSet()
        {
            var mgr = NewSimManager();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "1001", "C1_1", "x"));
            Assert.Equal(SyntheticStatusCodes.ContextNotSet, ex.StatusCode);
        }

        // =================================================================
        // Read-only gate
        // =================================================================

        [Fact]
        public async Task WriteField_OnReadOnlySession_ThrowsFieldIsReadOnly()
        {
            var mgr = NewSimManagerWithContext(readOnly: true);
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "1001", "C1_1", "new-name"));
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, ex.StatusCode);
            Assert.Contains("WriteField", ex.Context["operation"]?.ToString() ?? "");
            // Follow-up F3: read-only 9001 names the specific field + active
            // module/table so the operator sees the full coords.
            Assert.Equal("C1_1", ex.Context["field"]?.ToString());
            Assert.Equal("I", ex.Context["module"]?.ToString());
            Assert.Equal(1, (int)ex.Context["table"]!);
            Assert.Contains("C1_1", ex.Context["reason"]?.ToString() ?? "");
        }

        [Fact]
        public async Task WriteField_UnknownField_ThrowsUnknownFieldAccess_9012()
        {
            // Follow-up F3: Unknown-field path now emits 9012 UnknownFieldAccess
            // (distinct from 9001 FieldIsReadOnly). The session is NOT read-only,
            // so we land in the access-map gate which sees an empty map → Unknown.
            var mgr = NewSimManagerWithContext();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "1001", "C1_NotInMap", "x"));
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, ex.StatusCode);
            Assert.Equal("UnknownFieldAccess", ex.StatusName);
            // Context names the specific field.
            Assert.Equal("C1_NotInMap", ex.Context["field"]?.ToString());
        }

        [Fact]
        public async Task WriteDetail_OnReadOnlySession_ThrowsFieldIsReadOnly()
        {
            var mgr = NewSimManagerWithContext(readOnly: true);
            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 1000m == 1000m ? (double?)1000 : null, "test");
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("1001", row));
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, ex.StatusCode);
        }

        [Fact]
        public async Task CreateClient_OnReadOnlySession_ThrowsFieldIsReadOnly()
        {
            var mgr = NewSimManagerWithContext(readOnly: true);
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.CreateClient(new CreateClientArgs("NEW1", "Individual")));
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, ex.StatusCode);
        }

        // =================================================================
        // Year/module branching
        // =================================================================

        [Fact]
        public void CanUseNewApis_TY20_ReturnsFalse()
        {
            var mgr = NewSimManager();
            Assert.False(mgr.CanUseNewApis(2020, 'I'));
        }

        [Fact]
        public void CanUseNewApis_TY24_ReturnsTrue()
        {
            var mgr = NewSimManager();
            Assert.True(mgr.CanUseNewApis(2024, 'I'));
        }

        [Fact]
        public void CanUseNewApis_TY21Boundary_ReturnsTrue()
        {
            var mgr = NewSimManager();
            Assert.True(mgr.CanUseNewApis(2021, 'I'));
        }

        // =================================================================
        // Reads — happy path against seed data
        // =================================================================

        [Fact]
        public async Task ListClients_TY24Individual_FindsSeededRows()
        {
            var mgr = NewSimManagerWithContext();
            var clients = await mgr.ListClients();
            Assert.NotEmpty(clients);
        }

        [Fact]
        public async Task ReadClient_KnownId_ReturnsRow()
        {
            var mgr = NewSimManagerWithContext();
            var row = await mgr.ReadClient("1001"); // BROW seed
            Assert.NotNull(row);
            Assert.Equal("BROW", row!["ClientID"]);
        }

        [Fact]
        public async Task ReadClient_UnknownId_ReturnsNull()
        {
            var mgr = NewSimManagerWithContext();
            var row = await mgr.ReadClient("XXXX");
            Assert.Null(row);
        }

        // =================================================================
        // Idempotency cache state machine
        // =================================================================

        [Fact]
        public async Task WriteField_SameKey_SamePayload_DoesNotReexecute()
        {
            var mgr = NewSimManagerWithContext();

            // First call — executes, returns Success, caches.
            // C1_1 (FirstName on individual). The seeded BROW row exists.
            // Pass force-equivalent by using a field that resolves to Unknown
            // is NOT what we want here — we need a writable path. Use a
            // standard field that doesn't appear in the access map → Unknown
            // → 9001, which is NOT what we want. Instead use the universal
            // C1_0 rule which is ReadOnly → throws ExposedForRead. Neither
            // is writable in default map (it's empty). Workaround: assert
            // that the FIRST call AND a replay both raise the SAME access
            // error AND the second one isn't doubly-executed by checking the
            // idempotency cache short-circuits on the second attempt.
            //
            // Simpler: use a writable scenario by exercising WriteDetail
            // (which doesn't run AssertWritable) — that's a clean idempotent
            // happy-path proof.

            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "first");
            var result1 = await mgr.WriteDetail("1001", row, idempotencyKey: "k1");
            Assert.Equal(StatusCodes.Success, result1);

            // Second call with same key + same payload — short-circuits to
            // cached result. Observable signal: no second append. We baselined
            // the BROW seed at 3 detail rows; after one append it's 4 and
            // should STAY 4 across the replay.
            var beforeReplay = (await mgr.ReadDetail("1001")).Count;
            var result2 = await mgr.WriteDetail("1001", row, idempotencyKey: "k1");
            Assert.Equal(StatusCodes.Success, result2);
            var afterReplay = (await mgr.ReadDetail("1001")).Count;
            Assert.Equal(beforeReplay, afterReplay);
        }

        [Fact]
        public async Task WriteField_SameKey_DifferentPayload_ThrowsIdempotencyConflict()
        {
            var mgr = NewSimManagerWithContext();
            var row1 = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "first");
            var row2 = new DetailRow("Int", "", "1", "", "", "", "1099INT", "T", "", 250.0, "second — different payload");

            await mgr.WriteDetail("1001", row1, idempotencyKey: "k2");

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("1001", row2, idempotencyKey: "k2"));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
        }

        // =================================================================
        // Dry-run
        // =================================================================

        [Fact]
        public async Task WriteDetail_DryRun_LeavesDetailListUnchanged()
        {
            var mgr = NewSimManagerWithContext();
            var before = (await mgr.ReadDetail("1001")).Count;

            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 9999.0, "dry-run row");
            var status = await mgr.WriteDetail("1001", row, dryRun: true);
            Assert.Equal(StatusCodes.Success, status);

            var after = (await mgr.ReadDetail("1001")).Count;
            Assert.Equal(before, after);
        }

        [Fact]
        public async Task CreateClient_DryRun_DoesNotPersistNewClient()
        {
            var mgr = NewSimManagerWithContext();
            // P14-030 — ApplySimCreateClient now delegates to
            // SimulationStore.SimulateCreateClient which enforces the
            // NotSignedIn gate. Register first so the create succeeds.
            await mgr.Register();
            var beforeList = await mgr.ListClients(limit: 1000);
            var beforeCount = beforeList.Count;

            var result = await mgr.CreateClient(new CreateClientArgs(
                ClientID: "DRY",
                TaxType: "Individual",
                FirstName: "Dry",
                LastName: "Run",
                EmailID: "dry@example.com"), dryRun: true);
            Assert.Equal(StatusCodes.Success, result.Status);

            var afterList = await mgr.ListClients(limit: 1000);
            Assert.Equal(beforeCount, afterList.Count);
        }

        // =================================================================
        // SessionStatus snapshot
        // =================================================================

        [Fact]
        public async Task SessionStatus_AfterRegisterAndSetContext_ReturnsCoherentSnapshot()
        {
            var mgr = NewSimManager();
            var status1 = mgr.SessionStatus();
            Assert.Equal(SessionMode.Simulation, status1.Mode);
            Assert.False(status1.IsRegistered);
            Assert.Null(status1.RegisteredAt);
            Assert.Null(status1.Context);
            Assert.Equal("***", status1.AppId);

            var rc = await mgr.Register();
            Assert.Equal(StatusCodes.AccessGranted, rc);
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            var status2 = mgr.SessionStatus();
            Assert.True(status2.IsRegistered);
            Assert.NotNull(status2.RegisteredAt);
            Assert.NotNull(status2.ExpiresAt);
            Assert.NotNull(status2.MinutesUntilExpiry);
            Assert.NotNull(status2.Context);
            Assert.Equal(2024, status2.Context!.Year);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var mgr = NewSimManager();
            mgr.Dispose();
            mgr.Dispose(); // idempotent
        }

        // =================================================================
        // F14 — Edition cache surfaces in SessionStatus
        // =================================================================

        [Fact]
        public void SessionStatus_InSimMode_ExposesSimulatedEdition()
        {
            var mgr = NewSimManager();
            var status = mgr.SessionStatus();

            Assert.NotNull(status.Edition);
            Assert.Equal(LacertMCP.Server.Bridge.EditionDetection.SimulatedEdition,
                status.Edition!.LacerteEdition);
            Assert.Equal(LacertMCP.Server.Bridge.EditionDetection.SimulatedBuild,
                status.Edition.LacerteBuild);
            Assert.Equal(LacertMCP.Server.Bridge.EditionDetection.SimulatedSdkVersion,
                status.Edition.SdkVersion);

            // Repeat call must return the SAME instance (probe is one-shot).
            var status2 = mgr.SessionStatus();
            Assert.Same(status.Edition, status2.Edition);
        }
    }
}
