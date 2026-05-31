using System.Collections.Generic;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Tests.Tools;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-011 — closes the operator's #1 trust gap: "if I write X, did it
    /// actually land?". Prior to this test, every <c>WriteField</c> test in
    /// the suite asserted either a rejection code or the dry-run no-op path
    /// — none drove a successful happy-path write through the chokepoint and
    /// then read the value back to confirm it persisted verbatim.
    ///
    /// <para>
    /// Uses <see cref="AccessMapTestFixture"/> to seed <c>I|1|C1_15</c> as
    /// ReadWrite so the access-map gate doesn't fail-closed on the write
    /// (the v1 shipped shards are all <c>{}</c>). Marked with the
    /// <c>AccessMapMutation</c> collection so it serializes against other
    /// fixture-mutating classes — parallel execution would otherwise race
    /// the shared loader cache + on-disk shard.
    /// </para>
    ///
    /// <para>
    /// Regression coverage: a bug in
    /// <c>SimulationStore.ApplySimFieldWrite</c> that corrupts field-name
    /// casing, drops a key during <c>CloneRow</c>, or mutates the field
    /// dictionary under an active dry-run snapshot would be invisible to
    /// every other test today; this round-trip would catch it immediately.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldWriteRoundTripTests
    {
        [Fact]
        public async Task FieldWrite_HappyPath_ValueReadsBackVerbatim()
        {
            using var fixture = new AccessMapTestFixture();
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var regStatus = await mgr.Register();
            Assert.Equal(StatusCodes.AccessGranted, regStatus);
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            // First write through the chokepoint — exercises the full
            // (context, idempotency lock, access-map gate, wire-log,
            // simulation-store mutation) pipeline.
            var status = await mgr.WriteField(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "RoundTripValue");
            Assert.Equal(StatusCodes.Success, status);

            // Read it back through the SAME chokepoint shape the operator uses.
            var row = await mgr.ReadClient("1001");
            Assert.NotNull(row);
            Assert.True(row!.TryGetValue(AccessMapTestFixture.WritableField, out var v),
                $"row missing the field we just wrote: {AccessMapTestFixture.WritableField}");
            Assert.Equal("RoundTripValue", v);

            // Second write — confirms overwrite (not append) semantics. A
            // bug in the store that accumulates writes (list/dict-of-list)
            // would leave the first value visible and fail this assertion.
            var status2 = await mgr.WriteField(
                table: AccessMapTestFixture.Table,
                clientNumber: "1001",
                field: AccessMapTestFixture.WritableField,
                value: "OverwriteValue");
            Assert.Equal(StatusCodes.Success, status2);

            var row2 = await mgr.ReadClient("1001");
            Assert.NotNull(row2);
            Assert.True(row2!.TryGetValue(AccessMapTestFixture.WritableField, out var v2));
            Assert.Equal("OverwriteValue", v2);

            // Sanity: ClientNumber (C1_0) is untouched — proves the write
            // didn't smear across fields.
            Assert.True(row2.TryGetValue("ClientNumber", out var cn));
            Assert.Equal("1001", cn);
        }
    }
}
