using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// P18-001 — Regression coverage for the <see cref="DbfRow.Fields"/>
    /// concurrent-mutation hazard. The field bag was a plain
    /// <see cref="Dictionary{TKey,TValue}"/>; 10 parallel WriteField calls on
    /// the same client (10 distinct idempotency keys) all reached
    /// <c>row.Fields[field] = value</c> simultaneously, corrupting the bucket
    /// array. The fix converts <see cref="DbfRow.Fields"/> to
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>;
    /// these tests pound the field bag from many threads and assert no rows
    /// are lost, no exception is thrown by concurrent enumeration, and the
    /// duplicate-ClientId scan stays safe under concurrent mutation.
    ///
    /// Sibling of <see cref="SimulationStoreConcurrencyTests"/> (which covers
    /// the P14-028 per-list lock for <c>List&lt;DetailRow&gt;</c>).
    ///
    /// The SimulateFieldWrite-driven tests need a ReadWrite access-map entry
    /// to reach the row mutation; <see cref="AccessMapInMemorySeed"/> seeds
    /// the needed entries and the <see cref="AccessMapMutationCollection"/>
    /// serializes us with other tests that mutate the same static cache.
    /// </summary>
    [Collection(AccessMapMutationCollection.Name)]
    public class DbfRowFieldsConcurrencyTests
    {
        public DbfRowFieldsConcurrencyTests()
        {
            // Pre-seed the entire C1_100..C1_415 range as ReadWrite so every
            // SimulateFieldWrite in these tests reaches the dictionary write.
            // Without the seed, the access map returns Unknown → FieldNotSupported
            // and the row.Fields[field] = value site is never exercised.
            for (int i = 100; i < 416; i++)
            {
                AccessMapInMemorySeed.EnsureWritableFieldSeeded(
                    module: 'I', table: 1, field: $"C1_{i}");
            }
        }

        [Fact]
        public void SimulateFieldWrite_ParallelDistinctFields_SameClient_DoesNotCorruptOrLoseWrites()
        {
            // ARRANGE — fresh store, known client. SeedData.DataPathIData2020
            // already provisions client 1001 (SMIT) with a DATA1 row.
            var store = new SimulationStore();
            store.Register();

            const int threads = 16;
            const int perThread = 250;
            // Wide-enough field set so multiple writers always hit the same
            // dictionary buckets simultaneously rather than per-thread lanes.
            string[] fields = Enumerable.Range(0, 16).Select(i => $"C1_{100 + i}").ToArray();

            // ACT — each thread hammers its own field with `perThread` writes
            // on the SAME ClientNumber. Without the ConcurrentDictionary fix
            // the dictionary's grow-the-array race throws InvalidOperationException
            // or corrupts the bucket array within a few hundred iterations.
            Parallel.For(0, threads, t =>
            {
                var field = fields[t];
                for (int i = 0; i < perThread; i++)
                {
                    int status = store.SimulateFieldWrite(
                        'I', 1, "1001", field,
                        value: $"t{t}-i{i}",
                        year: 2020,
                        dataPath: SeedData.DataPathIData2020);
                    Assert.Equal(StatusCodes.Success, status);
                }
            });

            // ASSERT — every field has its last-written value. With the fix,
            // each thread's final write is the one observed. Without the fix,
            // we'd more likely have thrown above.
            var module = store.TryGetModule(SeedData.DataPathIData2020, 2020, 'I');
            Assert.NotNull(module);
            Assert.True(module!.Clients.TryGetValue("1001", out var row));
            for (int t = 0; t < threads; t++)
            {
                var field = fields[t];
                Assert.True(row!.Fields.TryGetValue(field, out var v),
                    $"Field {field} should have been written by thread {t}");
                Assert.Equal($"t{t}-i{perThread - 1}", v);
            }
        }

        [Fact]
        public async Task SimulateFieldWrite_ParallelWritesAndReads_DoesNotThrowOnEnumeration()
        {
            // ARRANGE — 8 writer threads + 8 reader threads on the same client.
            // Without the ConcurrentDictionary fix, the readers enumerate the
            // backing bucket array during a writer-driven reallocation and throw
            // "Collection was modified" / NullReferenceException.
            var store = new SimulationStore();
            store.Register();

            const int writers = 8;
            const int readers = 8;
            const int perWriter = 500;

            var writerTasks = new List<Task>();
            for (int w = 0; w < writers; w++)
            {
                int local = w;
                writerTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perWriter; i++)
                    {
                        store.SimulateFieldWrite(
                            'I', 1, "1001", $"C1_{200 + local}",
                            value: local * 10000 + i,
                            year: 2020,
                            dataPath: SeedData.DataPathIData2020);
                    }
                }));
            }

            var readerTasks = new List<Task>();
            for (int r = 0; r < readers; r++)
            {
                readerTasks.Add(Task.Run(() =>
                {
                    var module = store.TryGetModule(SeedData.DataPathIData2020, 2020, 'I');
                    if (module == null || !module.Clients.TryGetValue("1001", out var row)) return;
                    for (int i = 0; i < perWriter; i++)
                    {
                        // Enumerate Fields keys + values — this is the path
                        // CloneRow / wire-emission takes. With the fix, no
                        // exception; without, occasional "Collection was
                        // modified during enumeration".
                        foreach (var kv in row!.Fields)
                        {
                            _ = kv.Key;
                            _ = kv.Value;
                        }
                    }
                }));
            }

            // ACT + ASSERT — no exceptions thrown by either side.
            await Task.WhenAll(writerTasks);
            await Task.WhenAll(readerTasks);
        }

        [Fact]
        public async Task SimulateCreateClient_DuplicateScan_ConcurrentWithFieldWrites_DoesNotThrow()
        {
            // ARRANGE — SimulateCreateClient walks `store.Clients.Values` and
            // for each existing row reads `existing.Fields.TryGetValue("ClientID", ...)`.
            // Concurrent field writes on the existing clients used to throw
            // mid-enumeration; with ConcurrentDictionary they don't.
            var store = new SimulationStore();
            store.Register();

            const int writers = 4;
            const int creators = 4;
            const int perWriter = 500;

            var writerTasks = new List<Task>();
            for (int w = 0; w < writers; w++)
            {
                int local = w;
                writerTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perWriter; i++)
                    {
                        store.SimulateFieldWrite(
                            'I', 1, "1001", $"C1_{300 + local}",
                            value: local * 10000 + i,
                            year: 2024,
                            dataPath: SeedData.DataPathIData2024);
                    }
                }));
            }

            var creatorTasks = new List<Task>();
            for (int c = 0; c < creators; c++)
            {
                int local = c;
                creatorTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        // Unique ClientId per (creator, iteration), <=8 chars,
                        // alphanumeric only (sim regex). The goal is to make the
                        // duplicate-scan enumerate Fields under load.
                        var args = new SimulateCreateClientArgs(
                            DataPath: SeedData.DataPathIData2024,
                            Year: 2024,
                            Module: 'I',
                            ClientId: $"X{local}{i:D5}", // 7 chars, fits in 8
                            TaxType: "Individual",
                            FirstName: "Test",
                            LastName: "Concurrent");
                        store.SimulateCreateClient(args);
                    }
                }));
            }

            await Task.WhenAll(writerTasks);
            await Task.WhenAll(creatorTasks);
        }

        [Fact]
        public async Task Snapshot_DuringConcurrentWrites_DoesNotThrow()
        {
            // ARRANGE — CloneRow in Snapshot iterates `src.Fields`. Concurrent
            // writers used to break this enumeration; ConcurrentDictionary
            // gives Snapshot a moment-in-time view that's safe to walk.
            var store = new SimulationStore();
            store.Register();

            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    store.SimulateFieldWrite(
                        'I', 1, "1001", $"C1_{i % 16 + 400}",
                        value: i,
                        year: 2020,
                        dataPath: SeedData.DataPathIData2020);
                }
            });

            // ACT — take 50 snapshots concurrently with the writer loop. None
            // should throw.
            for (int s = 0; s < 50; s++)
            {
                var snap = store.Snapshot();
                Assert.NotNull(snap);
            }

            await writer;
        }
    }
}
