using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// P18-022 — Regression coverage for the simulated DBF-lock latch.
    /// The operator's normal workflow has Lacerte open on the desktop while
    /// using the MCP server; when the same return is open in Lacerte's UI,
    /// the underlying DATA{n}{Mod}{YY}.DBF is held with read-share/write-deny
    /// per FoxPro/xBase semantics and the live SDK returns TAPIStatus 2
    /// <see cref="StatusCodes.DBFNotAccessable"/>. Until now the sim could not
    /// produce code 2, so the agent's recovery path (close in UI + retry) was
    /// un-rehearsable. <see cref="SimulationStore.ToggleLock(int, char, int, bool)"/>
    /// makes that scenario testable; these tests pin the contract.
    ///
    /// Seeds a ReadWrite field via <see cref="AccessMapInMemorySeed"/> so the
    /// SimulateFieldWrite happy-path (after unlock) actually reaches the
    /// row mutation rather than short-circuiting at the access-map check.
    /// </summary>
    [Collection(AccessMapMutationCollection.Name)]
    public class SimulationStoreLockTests
    {
        private const char SeedModule = 'I';
        private const int SeedTable = 1;
        private const string SeedField = "C1_15";

        public SimulationStoreLockTests()
        {
            // Make C1_15 ReadWrite on module I/table 1 so writes can reach the
            // happy path. Sibling pattern to the other SessionManager tests
            // that mutate the access-map cache.
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(
                module: SeedModule, table: SeedTable, field: SeedField);
        }

        [Fact]
        public void SimulateFieldWrite_AfterToggleLock_ReturnsDBFNotAccessable()
        {
            var store = new SimulationStore();
            store.Register();

            // Sanity: write succeeds before the lock is engaged (seeded
            // ReadWrite field reaches the row mutation).
            int before = store.SimulateFieldWrite(
                SeedModule, SeedTable, "1001", SeedField, value: "ok",
                year: 2020, dataPath: SeedData.DataPathIData2020);
            Assert.Equal(StatusCodes.Success, before);

            store.ToggleLock(2020, SeedModule, SeedTable, locked: true);

            // P18-022 — the lock check fires before the access-map check, so
            // it returns DBFNotAccessable regardless of whether the field is
            // ReadWrite. This mirrors the live SDK behavior: a locked DBF
            // can't even surface row metadata.
            int locked = store.SimulateFieldWrite(
                SeedModule, SeedTable, "1001", SeedField, value: "blocked",
                year: 2020, dataPath: SeedData.DataPathIData2020);
            Assert.Equal(StatusCodes.DBFNotAccessable, locked);
        }

        [Fact]
        public void SimulateFieldWrite_AfterUnlock_Succeeds()
        {
            var store = new SimulationStore();
            store.Register();

            store.ToggleLock(2020, SeedModule, SeedTable, locked: true);
            store.ToggleLock(2020, SeedModule, SeedTable, locked: false);

            int result = store.SimulateFieldWrite(
                SeedModule, SeedTable, "1001", SeedField, value: "Jane",
                year: 2020, dataPath: SeedData.DataPathIData2020);
            Assert.Equal(StatusCodes.Success, result);
        }

        [Fact]
        public void ToggleLock_IsScopedPerYearModuleTable_DoesNotBlockOtherCells()
        {
            // Locking DATA1 must not produce DBFNotAccessable on a different
            // (year, module, table). The latch is a triple-set. We assert the
            // negative (no DBFNotAccessable) — non-seeded fields will fall
            // through to FieldNotSupported, which is fine; we're confirming
            // the lock didn't fire across cells.
            var store = new SimulationStore();
            store.Register();

            store.ToggleLock(2020, 'I', 1, locked: true);

            // Different table on the locked cell — must NOT return DBFNotAccessable.
            int otherTable = store.SimulateFieldWrite(
                'I', 5, "1001", "C5_5", value: "ok",
                year: 2020, dataPath: SeedData.DataPathIData2020);
            Assert.NotEqual(StatusCodes.DBFNotAccessable, otherTable);

            // Different year — must NOT return DBFNotAccessable.
            int otherYear = store.SimulateFieldWrite(
                'I', 1, "1001", "C1_5", value: "ok",
                year: 2024, dataPath: SeedData.DataPathIData2024);
            Assert.NotEqual(StatusCodes.DBFNotAccessable, otherYear);

            // Different module — must NOT return DBFNotAccessable.
            int otherModule = store.SimulateFieldWrite(
                'C', 1, "1001", "C1_5", value: "ok",
                year: 2024, dataPath: SeedData.DataPathCData2024);
            Assert.NotEqual(StatusCodes.DBFNotAccessable, otherModule);
        }

        [Fact]
        public void WriteDetail_AfterToggleLockTable0_ReturnsDBFNotAccessable()
        {
            // Detail files ({CLIENT}.{Mod}{YY}) are not numbered DATA1-7; the
            // convention is table=0 for the detail-file lock latch.
            var store = new SimulationStore();
            store.Register();

            var detail = new DetailRow(
                Series: "Int", Prefix: "", Code: "2", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "1099INT",
                TSJ: "T", ImportSrc: "", Amount: 100.0, Description: "test");

            store.ToggleLock(2020, 'I', 0, locked: true);

            int result = store.WriteDetail(
                SeedData.DataPathIData2020, 2020, 'I', "1001", detail);
            Assert.Equal(StatusCodes.DBFNotAccessable, result);
        }

        [Fact]
        public void SimulateCreateClient_AfterToggleLockTable1_ReturnsDBFNotAccessable()
        {
            // CreateClient writes a new DATA1 row by definition; locking DATA1
            // must surface as DBFNotAccessable.
            var store = new SimulationStore();
            store.Register();

            store.ToggleLock(2024, 'I', 1, locked: true);

            var args = new SimulateCreateClientArgs(
                DataPath: SeedData.DataPathIData2024,
                Year: 2024,
                Module: 'I',
                ClientId: "NEWLOCK",
                TaxType: "Individual",
                FirstName: "New",
                LastName: "Client");
            var (status, cn) = store.SimulateCreateClient(args);

            Assert.Equal(StatusCodes.DBFNotAccessable, status);
            Assert.Null(cn);
        }

        [Fact]
        public void Snapshot_PreservesLockState_RoundTrip()
        {
            // The lock latch participates in the Snapshot/Restore contract
            // alongside Registered + SessionExpired, so dry-run rollback
            // doesn't leak test state across snapshots.
            var store = new SimulationStore();
            store.Register();
            store.ToggleLock(2020, SeedModule, SeedTable, locked: true);

            var snap = store.Snapshot();

            // Flip the lock off and confirm writes succeed in the mutated state.
            store.ToggleLock(2020, SeedModule, SeedTable, locked: false);
            int afterUnlock = store.SimulateFieldWrite(
                SeedModule, SeedTable, "1001", SeedField, value: "ok",
                year: 2020, dataPath: SeedData.DataPathIData2020);
            Assert.Equal(StatusCodes.Success, afterUnlock);

            // Restore should bring the lock back.
            store.Restore(snap);
            int afterRestore = store.SimulateFieldWrite(
                SeedModule, SeedTable, "1001", SeedField, value: "blocked",
                year: 2020, dataPath: SeedData.DataPathIData2020);
            Assert.Equal(StatusCodes.DBFNotAccessable, afterRestore);
        }
    }
}
