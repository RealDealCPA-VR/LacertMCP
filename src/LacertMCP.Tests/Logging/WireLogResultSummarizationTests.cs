using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// P15-072 — large read results balloon the wire log when LACERTE_DEBUG_WIRE
    /// is on: a 5000-row <c>ReadTable(1)</c> writes ~2 MB per log line and
    /// gives the operator no quick "this returned N rows" summary. WireLog
    /// now post-processes the serialized record so a top-level <c>result</c>
    /// JSON array longer than <c>ResultSampleLimit</c> is replaced with
    /// <c>{kind:"collection", count:N, sample:[…]}</c>. Opt out with
    /// <c>LACERTE_DEBUG_LOG_FULL_RESULTS=1</c>.
    ///
    /// <para>
    /// Uses the AccessMapMutationCollection because WireLog's static
    /// _testForceConfig + cached-writer state is mutated by every test
    /// here; without serialization the rotation/retention tests in the
    /// same collection would race the EnableForTests calls.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class WireLogResultSummarizationTests : IDisposable
    {
        private readonly string _logDir;

        public WireLogResultSummarizationTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(),
                "lacertmcp-wirelog-summary-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_logDir);
        }

        public void Dispose()
        {
            try { WireLog.DisableForTests(); } catch { /* swallow */ }
            try { WireLog.FlushForTests(_logDir); } catch { /* swallow */ }
            try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); }
            catch { /* swallow */ }
        }

        private string[] ReadLogLines()
        {
            WireLog.FlushForTests(_logDir);
            var files = Directory.GetFiles(_logDir, "lacerte-*.log");
            if (files.Length == 0) return Array.Empty<string>();
            return files.SelectMany(f => File.ReadAllLines(f)).ToArray();
        }

        // ----------------------------------------------------------------
        // Pure helper coverage — SummarizeLargeResultArray
        // ----------------------------------------------------------------

        [Fact]
        public void SummarizeLargeResultArray_SmallArray_ReturnsUnchanged()
        {
            var input = "{\"op\":\"ReadTable\",\"result\":[{\"id\":1},{\"id\":2}]}";
            var output = WireLog.SummarizeLargeResultArray(input, sampleLimit: 5);
            Assert.Equal(input, output);
        }

        [Fact]
        public void SummarizeLargeResultArray_LargeArray_RewritesAsCollectionSummary()
        {
            // 10-element array, sample limit 3 → result becomes
            // {kind: "collection", count: 10, sample: [first 3 items]}.
            var arr = string.Join(",", Enumerable.Range(0, 10).Select(i => $"{{\"id\":{i}}}"));
            var input = "{\"op\":\"ReadTable\",\"result\":[" + arr + "]}";
            var output = WireLog.SummarizeLargeResultArray(input, sampleLimit: 3);
            using var doc = JsonDocument.Parse(output);
            var resultEl = doc.RootElement.GetProperty("result");
            Assert.Equal(JsonValueKind.Object, resultEl.ValueKind);
            Assert.Equal("collection", resultEl.GetProperty("kind").GetString());
            Assert.Equal(10, resultEl.GetProperty("count").GetInt32());
            Assert.Equal(3, resultEl.GetProperty("sample").GetArrayLength());
        }

        [Fact]
        public void SummarizeLargeResultArray_NonArrayResult_ReturnsUnchanged()
        {
            // A scalar/object result is left alone — only arrays get the
            // collection summary treatment.
            var input = "{\"op\":\"ReadClient\",\"result\":{\"C1_0\":\"1001\"}}";
            var output = WireLog.SummarizeLargeResultArray(input, sampleLimit: 3);
            Assert.Equal(input, output);
        }

        [Fact]
        public void SummarizeLargeResultArray_NoResultKey_ReturnsUnchanged()
        {
            // An error record has no "result" key — must pass through.
            var input = "{\"op\":\"WriteField\",\"error\":{\"type\":\"X\"}}";
            var output = WireLog.SummarizeLargeResultArray(input, sampleLimit: 3);
            Assert.Equal(input, output);
        }

        [Fact]
        public void SummarizeLargeResultArray_MalformedJson_ReturnsInput()
        {
            // Defensive: summarization must NEVER poison the log line.
            var input = "{not valid json";
            var output = WireLog.SummarizeLargeResultArray(input, sampleLimit: 3);
            Assert.Equal(input, output);
        }

        // ----------------------------------------------------------------
        // End-to-end via WireLog.Record
        // ----------------------------------------------------------------

        [Fact]
        public async Task Record_LargeArrayResult_LogsSummary()
        {
            WireLog.EnableForTests(_logDir, resultSampleLimit: 2);
            // Return a 6-row "result" so the summarizer fires (6 > 2).
            await WireLog.Record<object>(
                "ReadTableTest",
                new { table = 1 },
                () => Task.FromResult<object>(
                    Enumerable.Range(0, 6).Select(i => new { id = i }).ToList()));

            var lines = ReadLogLines();
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            var result = doc.RootElement.GetProperty("result");
            Assert.Equal(JsonValueKind.Object, result.ValueKind);
            Assert.Equal("collection", result.GetProperty("kind").GetString());
            Assert.Equal(6, result.GetProperty("count").GetInt32());
            Assert.Equal(2, result.GetProperty("sample").GetArrayLength());
        }

        [Fact]
        public async Task Record_SmallArrayResult_LogsAsIs()
        {
            WireLog.EnableForTests(_logDir, resultSampleLimit: 5);
            await WireLog.Record<object>(
                "SmallReadTest",
                new { table = 1 },
                () => Task.FromResult<object>(
                    Enumerable.Range(0, 3).Select(i => new { id = i }).ToList()));

            var lines = ReadLogLines();
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            var result = doc.RootElement.GetProperty("result");
            // Below threshold — stays as full array.
            Assert.Equal(JsonValueKind.Array, result.ValueKind);
            Assert.Equal(3, result.GetArrayLength());
        }

        [Fact]
        public async Task Record_FullResultsOptOut_RestoresVerbosePayload()
        {
            // P15-072 — LACERTE_DEBUG_LOG_FULL_RESULTS=1 (modeled here via the
            // EnableForTests fullResults overload) disables the summarizer
            // for the rare deep-debugging session.
            WireLog.EnableForTests(_logDir, resultSampleLimit: 2, fullResults: true);
            await WireLog.Record<object>(
                "VerboseReadTest",
                new { table = 1 },
                () => Task.FromResult<object>(
                    Enumerable.Range(0, 10).Select(i => new { id = i }).ToList()));

            var lines = ReadLogLines();
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            var result = doc.RootElement.GetProperty("result");
            Assert.Equal(JsonValueKind.Array, result.ValueKind);
            Assert.Equal(10, result.GetArrayLength());
        }
    }
}
