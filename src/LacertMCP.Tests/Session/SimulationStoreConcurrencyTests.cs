using System.Collections.Generic;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Regression coverage for P14-028 — SimulationStore.WriteDetail and
    /// ReadDetail mutate/enumerate an inner List<DetailRow> that lives inside
    /// a ConcurrentDictionary; the outer map is thread-safe but the per-client
    /// list is not. The store now takes <c>lock (list)</c> on both paths to
    /// match LacerteSessionManager.ApplySimDetailWrite. These tests pound the
    /// list from many threads and assert no rows are lost, no exception is
    /// thrown by concurrent enumeration, and replaceIndex stays in-bounds.
    /// </summary>
    public class SimulationStoreConcurrencyTests
    {
        [Fact]
        public void WriteDetail_ParallelAppend_DoesNotLoseRows()
        {
            // ARRANGE — fresh store, known client. SeedData.DataPathIData2020
            // already provisions client 1001 with a detail file.
            var store = new SimulationStore();
            var beforeCount = store.ReadDetail(
                SeedData.DataPathIData2020, 2020, 'I', "1001").Count;

            const int threads = 16;
            const int perThread = 250;

            // ACT — every thread appends `perThread` rows in parallel. Without
            // the lock the underlying List<T>'s grow-the-array race will
            // occasionally drop a row or throw IndexOutOfRangeException
            // mid-Add. With the lock, the final count is deterministic.
            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    var row = new DetailRow(
                        Series: "Int", Prefix: "", Code: "2", Suffix: "",
                        StateAbbr: "", CityAbbr: "", Source: "1099INT",
                        TSJ: "T", ImportSrc: "",
                        Amount: t * 1000 + i,
                        Description: $"t{t}-i{i}");
                    var status = store.WriteDetail(
                        SeedData.DataPathIData2020, 2020, 'I', "1001", row);
                    Assert.Equal(StatusCodes.Success, status);
                }
            });

            // ASSERT — every appended row landed exactly once.
            var rows = store.ReadDetail(
                SeedData.DataPathIData2020, 2020, 'I', "1001");
            Assert.Equal(beforeCount + (threads * perThread), rows.Count);
        }

        [Fact]
        public async Task WriteDetail_ParallelAppend_AndConcurrentRead_DoesNotThrow()
        {
            // ARRANGE — interleave writes with reads. An unlocked List<T>
            // raises InvalidOperationException when an enumeration sees the
            // backing array swap; the lock makes ReadDetail snapshot safely.
            var store = new SimulationStore();
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
                        var row = new DetailRow(
                            Series: "Int", Prefix: "", Code: "2", Suffix: "",
                            StateAbbr: "", CityAbbr: "", Source: "1099INT",
                            TSJ: "T", ImportSrc: "",
                            Amount: local * 10000 + i,
                            Description: $"w{local}-i{i}");
                        store.WriteDetail(
                            SeedData.DataPathIData2020, 2020, 'I', "1001", row);
                    }
                }));
            }

            var readerTasks = new List<Task>();
            for (int r = 0; r < readers; r++)
            {
                readerTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perWriter; i++)
                    {
                        // Both the unfiltered and the filtered paths go through
                        // the same lock; exercise both.
                        _ = store.ReadDetail(
                            SeedData.DataPathIData2020, 2020, 'I', "1001");
                        _ = store.ReadDetail(
                            SeedData.DataPathIData2020, 2020, 'I', "1001",
                            r => r.Series == "Int");
                    }
                }));
            }

            // ACT + ASSERT — no exceptions, all writers complete.
            await Task.WhenAll(writerTasks);
            await Task.WhenAll(readerTasks);
        }
    }
}
