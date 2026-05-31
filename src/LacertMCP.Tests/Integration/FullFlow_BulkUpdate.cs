using System.Collections.Generic;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Tests.Parity;
using LacertMCP.Tests.Tools;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-039 — integration coverage for REQUIREMENTS §1 named operator
    /// flow #2: "Bulk-updating fields across many clients". Drives the
    /// real cross-tool path (ListClients → loop WriteField with distinct
    /// idempotency keys → re-read each client and confirm the new value
    /// round-tripped → replay one of the keys and assert no second
    /// mutation). Locks down P14-005 <c>ExecuteWithIdempotencyLock</c>
    /// under the bulk-write pattern the
    /// <c>lacerte_bulk_field_update</c> prompt scripts.
    ///
    /// <para>
    /// Uses <see cref="AccessMapTestFixture"/> to seed <c>I|1|C1_15</c>
    /// as ReadWrite. Marked with the <c>AccessMapMutation</c> collection
    /// so it serializes against other fixture-mutating classes.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FullFlow_BulkUpdate
    {
        [Theory, BothModesData]
        public async Task BulkUpdate_LoopWriteField_AllRoundTrip_AndReplayIsNoOp(SessionMode mode)
        {
            using var fixture = new AccessMapTestFixture();
            using var mgr = ParitySessionFactory.For(mode);
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            // Discover the roster the operator would iterate over.
            var clients = await mgr.ListClients();
            Assert.NotEmpty(clients);

            // Loop: for each client, write the writable field with a value
            // unique to the client + a unique idempotency key. Mirrors the
            // bulk-update prompt's documented sequencing.
            var writes = new List<(string client, string value, string key)>();
            foreach (var row in clients)
            {
                Assert.True(row.TryGetValue("ClientNumber", out var v),
                    "ListClients row must expose ClientNumber");
                var cn = v as string;
                Assert.False(string.IsNullOrEmpty(cn),
                    "ClientNumber must be a non-empty string");
                var value = $"Bulk-{cn}";
                var key = $"bulkflow-{cn}";
                var status = await mgr.WriteField(
                    table: AccessMapTestFixture.Table,
                    clientNumber: cn!,
                    field: AccessMapTestFixture.WritableField,
                    value: value,
                    idempotencyKey: key);
                Assert.Equal(StatusCodes.Success, status);
                writes.Add((cn!, value, key));
            }

            Assert.True(writes.Count >= 2,
                $"bulk-update flow needs >= 2 clients in the seed; got {writes.Count}");

            // Read-back: every value the operator wrote is visible verbatim.
            foreach (var (client, expectedValue, _) in writes)
            {
                var row = await mgr.ReadClient(client);
                Assert.NotNull(row);
                Assert.True(row!.TryGetValue(AccessMapTestFixture.WritableField, out var got),
                    $"client {client} missing the field we just wrote");
                Assert.Equal(expectedValue, got);
            }

            // Replay one key — must return cached Success WITHOUT issuing a
            // second mutation. P14-005 locks down this contract; if the
            // idempotency cache silently drops between calls or the lock is
            // released before the cache write, this assertion will trip.
            var replayTarget = writes[0];
            var replayStatus = await mgr.WriteField(
                table: AccessMapTestFixture.Table,
                clientNumber: replayTarget.client,
                field: AccessMapTestFixture.WritableField,
                value: replayTarget.value,
                idempotencyKey: replayTarget.key);
            Assert.Equal(StatusCodes.Success, replayStatus);

            // Same key, DIFFERENT value → IdempotencyConflict (9102). Proves
            // the cache hashes the full payload, not just the key.
            var conflictErr = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await mgr.WriteField(
                    table: AccessMapTestFixture.Table,
                    clientNumber: replayTarget.client,
                    field: AccessMapTestFixture.WritableField,
                    value: "different-value",
                    idempotencyKey: replayTarget.key));
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, conflictErr.StatusCode);

            // Value is still the original — the conflict short-circuited
            // before any mutation.
            var finalRow = await mgr.ReadClient(replayTarget.client);
            Assert.NotNull(finalRow);
            Assert.True(finalRow!.TryGetValue(AccessMapTestFixture.WritableField, out var finalVal));
            Assert.Equal(replayTarget.value, finalVal);
        }
    }
}
