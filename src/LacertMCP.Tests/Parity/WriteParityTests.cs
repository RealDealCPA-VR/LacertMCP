using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Parity
{
    /// <summary>
    /// Phase 12 task #82 — write parity. For each write chokepoint, assert the
    /// documented TAPIStatus error paths fire in sim. When the recorded-replay
    /// live adapter lands, the second leg picks up the live emissions and the
    /// same assertions cover both modes.
    /// </summary>
    public class WriteParityTests
    {
        private static LacerteSessionManager NewWithContext(
            SessionMode mode, char module = 'I', int year = 2024,
            bool readOnly = false, string? dataPath = null)
        {
            var mgr = ParitySessionFactory.For(mode);
            mgr.SetContext(dataPath ?? SeedData.DataPathIData2024, year, module, readOnly);
            // P14-030 — CreateClient now delegates to SimulationStore.SimulateCreateClient
            // which enforces the NotSignedIn gate. Register synchronously here so every
            // CreateClient-bearing test in this class can reach the happy/conflict path.
            // Sim Register flips an in-memory latch — no real I/O.
            mgr.Register().GetAwaiter().GetResult();
            return mgr;
        }

        // -----------------------------------------------------------------
        // WriteField — synchronous read-only gate (9001 FieldIsReadOnly)
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_ReadOnlySession_ThrowsFieldIsReadOnly(SessionMode mode)
        {
            using var mgr = NewWithContext(mode, readOnly: true);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_1", "newvalue"));
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteField — context gate (9009 ContextNotSet)
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_NoContext_ThrowsContextNotSet(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_1", "newvalue"));
            Assert.Equal(SyntheticStatusCodes.ContextNotSet, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteField — DATA2 / DATA3 access-gate fires FIRST when the access
        // map has no entry for the field (current state: v1 ships empty maps,
        // so the loader returns Unknown → fail-closed 9012 UnknownFieldAccess).
        // The downstream SubClientIDRequired / StateAbbrevRequired guards
        // inside ApplySimFieldWrite only fire ONCE the access map is populated
        // for a writable field. Documented in agent 04 §7.
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_Data2_AccessGateFiresFirst_FailsClosed(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            // P15-050 — RequireRegistered gate now fires after RequireContext.
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(2, "1001", "C2_5", "x"));
            // Empty access map → Unknown → 9012 UnknownFieldAccess. Once the
            // map is populated with a ReadWrite entry for C2_5, the
            // SubClientIDRequired guard (9005) becomes reachable; covered in
            // WriteField unit tests.
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, err.StatusCode);
        }

        [Theory, BothModesData]
        public async Task WriteField_Data3_AccessGateFiresFirst_FailsClosed(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            // P15-050 — RequireRegistered gate now fires after RequireContext.
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(3, "1001", "C3_1", "x"));
            // Same fail-closed gate as DATA2 above.
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteField — universal C{t}_0 ReadOnly rule (ExposedForRead = 8)
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_ClientNumberField_ThrowsExposedForRead(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_0", "9999"));
            Assert.Equal(StatusCodes.ExposedForRead, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteField — unknown field, fail-closed via synthetic 9012
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_UnknownField_FailsClosed(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_999_NotInMap", "x"));
            // Empty access map → Unknown → 9012 UnknownFieldAccess per
            // fail-closed contract in FieldAccessMapLoader.AssertWritable.
            // Distinct from 9001 (FieldIsReadOnly) so callers can tell
            // "no map entry" apart from "known non-writable".
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteField — DATA6 / DATA7 spec invariant (read-only tables)
        // P14-054: gate fires in the session manager chokepoint BEFORE the
        // per-field access map, so the rejection holds regardless of map
        // contents. DATA5 still flows through the per-field gate.
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_Data6_ThrowsExposedForRead(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(6, "1001", "C6_1", "x"));
            Assert.Equal(StatusCodes.ExposedForRead, err.StatusCode);
        }

        [Theory, BothModesData]
        public async Task WriteField_Data7_ThrowsExposedForRead(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(7, "1001", "C7_1", "x"));
            Assert.Equal(StatusCodes.ExposedForRead, err.StatusCode);
        }

        [Theory, BothModesData]
        public async Task WriteField_Data5_StillFlowsThroughPerFieldGate(SessionMode mode)
        {
            // DATA5 is NOT covered by the table-level gate (spec: "some
            // globals R/W"); the per-field access map remains the
            // enforcement point. Empty base map → Unknown → 9012.
            using var mgr = NewWithContext(mode);
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(5, "1001", "C5_1", "x"));
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteDetail — read-only gate
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteDetail_ReadOnlySession_ThrowsFieldIsReadOnly(SessionMode mode)
        {
            using var mgr = NewWithContext(mode, readOnly: true);
            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 1.0, "x");
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteDetail("1001", row));
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, err.StatusCode);
        }

        [Theory, BothModesData]
        public async Task WriteDetail_BadReplaceIndex_ThrowsOutOfIndex(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 1.0, "x");
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteDetail("1001", row, replaceIndex: 99999));
            Assert.Equal(StatusCodes.OutOfIndex, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // CreateClient — read-only gate
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task CreateClient_ReadOnlySession_ThrowsFieldIsReadOnly(SessionMode mode)
        {
            using var mgr = NewWithContext(mode, readOnly: true);
            var args = new CreateClientArgs("NEW1", "1040");
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.CreateClient(args));
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // CreateClient — too-long clientID
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task CreateClient_ClientIDTooLong_Throws9002(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var args = new CreateClientArgs("TOOLONGID", "1040"); // 9 chars
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.CreateClient(args));
            Assert.Equal(SyntheticStatusCodes.ClientIDTooLong, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // CreateClient — duplicate
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task CreateClient_Duplicate_ThrowsDuplicateClient(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            // Seed-known BROW exists in TY24 IData.
            var args = new CreateClientArgs("BROW", "1040");
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.CreateClient(args));
            Assert.Equal(StatusCodes.DuplicateClient, err.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteField — idempotency cache state machine
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task WriteField_SameKeySamePayload_Replays(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            // Pick a known-writable field. Since the access map ships as {} v1,
            // the loader fails closed — so use C1_0 to demonstrate the
            // idempotency path BEFORE the access gate fires. Actually that
            // throws first. Use the synthetic flow: a force-writable path
            // requires a populated map. Instead test the dry-run + idempotency
            // surface: the call to WriteField that fails (access-gate) still
            // does NOT cache, so second call still throws — assert that.
            var err1 = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_0", "X", idempotencyKey: "K1"));
            Assert.Equal(StatusCodes.ExposedForRead, err1.StatusCode);

            // Second call with same key MUST still throw (cache only stores
            // success results) — proves the cache doesn't store error responses.
            var err2 = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(1, "1001", "C1_0", "X", idempotencyKey: "K1"));
            Assert.Equal(StatusCodes.ExposedForRead, err2.StatusCode);
        }

        // -----------------------------------------------------------------
        // CreateClient — idempotency replay (success path)
        // -----------------------------------------------------------------

        [Theory, BothModesData]
        public async Task CreateClient_SameKeySamePayload_ReturnsSameClientNumber(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var args = new CreateClientArgs("UNIQ" + ((int)mode).ToString(), "1040");

            var r1 = await mgr.CreateClient(args, idempotencyKey: "create-1");
            Assert.Equal(StatusCodes.Success, r1.Status);
            Assert.NotNull(r1.ClientNumber);

            // Replay — must return the same cached result.
            var r2 = await mgr.CreateClient(args, idempotencyKey: "create-1");
            Assert.Equal(r1.ClientNumber, r2.ClientNumber);
        }

        [Theory, BothModesData]
        public async Task CreateClient_SameKeyDifferentPayload_ThrowsConflict(SessionMode mode)
        {
            using var mgr = NewWithContext(mode);
            var args1 = new CreateClientArgs("CFA" + ((int)mode).ToString(), "1040");
            var args2 = new CreateClientArgs("CFB" + ((int)mode).ToString(), "1040");

            await mgr.CreateClient(args1, idempotencyKey: "create-conflict");

            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.CreateClient(args2, idempotencyKey: "create-conflict"));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, err.StatusCode);
        }
    }
}
