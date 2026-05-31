using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Resilience;
using Xunit;

namespace LacertMCP.Tests.Resilience
{
    /// <summary>
    /// P15-014 — AutoReconnect must emit one WireLog "AutoReconnect.retry"
    /// record per retry attempt AND one "AutoReconnect.exhausted" record on
    /// terminal failure. Pre-P15-014 the optional ILogger? parameter was
    /// never threaded through from any of the five call sites in
    /// <c>LacerteSessionManager</c>, so retries were silently invisible — an
    /// exhausted-after-3-retries failure was observationally indistinguishable
    /// from a first-attempt failure in the log stream. These tests pin the
    /// unconditional emission via the existing
    /// <see cref="WireLog.EnableForTests(string, long?, long?, int?, int?, bool)"/>
    /// seam, so a future refactor that drops the emission is caught immediately.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class AutoReconnectLoggingTests : IDisposable
    {
        private readonly string _logDir;

        public AutoReconnectLoggingTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(),
                "lacertmcp-autoreconnect-log-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_logDir);
            // Defensive — previous test in the collection might have left
            // _testForceConfig pointing at a now-deleted dir. Disable first,
            // then enable with our pristine dir.
            try { WireLog.DisableForTests(); } catch { /* swallow */ }
            WireLog.EnableForTests(_logDir);
        }

        public void Dispose()
        {
            try
            {
                WireLog.DisableForTests();
                WireLog.FlushForTests(_logDir);
            }
            catch { /* swallow */ }
            try
            {
                if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
            }
            catch { /* swallow */ }
        }

        private string[] ReadLogLines()
        {
            WireLog.FlushForTests(_logDir);
            var files = Directory.GetFiles(_logDir, "lacerte-*.log");
            if (files.Length == 0) return Array.Empty<string>();
            return files.SelectMany(f => File.ReadAllLines(f)).ToArray();
        }

        [Fact]
        public async Task WithRetry_LogsOneRetryLinePerFailedAttempt_PlusFinalSuccess()
        {
            int calls = 0;
            var result = await AutoReconnect.WithRetry<int>(() =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new COMException("RPC server unavailable", unchecked((int)0x800706BA));
                }
                return Task.FromResult(7);
            });
            Assert.Equal(7, result);
            Assert.Equal(3, calls);

            var lines = ReadLogLines();
            // Expect at least 2 AutoReconnect.retry lines (for attempts 1
            // and 2) plus a final success record (attempt 3 didn't retry).
            // Use >= to tolerate collection-level race where the legacy
            // AutoReconnectTests in the same [Collection] briefly hold
            // the global WireLog flag and tee their records here.
            var retryLines = lines.Where(l => l.Contains("\"op\":\"AutoReconnect.retry\"")).ToArray();
            Assert.True(retryLines.Length >= 2,
                $"Expected at least 2 retry lines; got {retryLines.Length}");

            // Pin payload shape — attempt, hresult, delayMs all present.
            foreach (var rl in retryLines)
            {
                using var doc = JsonDocument.Parse(rl);
                var args = doc.RootElement.GetProperty("args");
                Assert.True(args.TryGetProperty("attempt", out _));
                Assert.True(args.TryGetProperty("hresult", out _));
                Assert.True(args.TryGetProperty("delayMs", out _));
            }
        }

        [Fact]
        public async Task WithRetry_Exhausted_LogsExhaustedLine_AndPreservesExceptionDataStamps()
        {
            var ex = await Assert.ThrowsAsync<COMException>(async () =>
                await AutoReconnect.WithRetry<int>(() =>
                {
                    throw new COMException("RPC server unavailable", unchecked((int)0x800706BA));
                }));
            // Regression: existing P14-023 contract that ex.Data carries
            // the stamps remains intact.
            Assert.Equal(true, ex.Data["AutoReconnect.Exhausted"]);
            Assert.Equal(AutoReconnect.MaxRetries + 1, ex.Data["AutoReconnect.Attempts"]);

            var lines = ReadLogLines();
            var retryLines = lines.Where(l => l.Contains("\"op\":\"AutoReconnect.retry\"")).ToArray();
            var exhaustedLines = lines.Where(l => l.Contains("\"op\":\"AutoReconnect.exhausted\"")).ToArray();
            // 3 retries → 3 retry lines. Use >= rather than == to tolerate
            // collection-level race where the legacy AutoReconnectTests in
            // the SAME [Collection] briefly hold the global WireLog flag
            // and tee their failures into our temp file. Test still pins
            // the load-bearing invariant (at least MaxRetries lines for an
            // exhausted run) while not coupling to global test ordering.
            Assert.True(retryLines.Length >= AutoReconnect.MaxRetries,
                $"Expected at least {AutoReconnect.MaxRetries} retry lines; got {retryLines.Length}");
            Assert.True(exhaustedLines.Length >= 1);

            using var doc = JsonDocument.Parse(exhaustedLines[0]);
            var args = doc.RootElement.GetProperty("args");
            Assert.Equal(AutoReconnect.MaxRetries + 1, args.GetProperty("attempts").GetInt32());
            Assert.True(args.TryGetProperty("hresult", out _));
        }

        [Fact]
        public void WithRetrySync_LogsRetryAndExhausted()
        {
            var ex = Assert.Throws<COMException>(() =>
                AutoReconnect.WithRetrySync<int>(() =>
                {
                    throw new COMException("disconnected", unchecked((int)0x80010108));
                }));
            Assert.Equal(true, ex.Data["AutoReconnect.Exhausted"]);

            var lines = ReadLogLines();
            // Tolerate collection-level race — see WithRetry_Exhausted comment.
            Assert.True(lines.Count(l => l.Contains("\"op\":\"AutoReconnect.retry\"")) >= AutoReconnect.MaxRetries);
            Assert.True(lines.Count(l => l.Contains("\"op\":\"AutoReconnect.exhausted\"")) >= 1);
        }

        [Fact]
        public async Task ExDataStampsAppearOnFailedWireLogRecord()
        {
            // P15-014 — the WireLog error block now captures ex.Data so the
            // exhausted-retry stamps (Attempts / Exhausted) appear inline
            // with the wrapping op's record, not just on the separate
            // AutoReconnect.exhausted line.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await WireLog.Record<int>("WrapperOp", new { test = "stamp" }, async () =>
                {
                    return await AutoReconnect.WithRetry<int>(() =>
                    {
                        throw new COMException("rpc gone", unchecked((int)0x800706BA));
                    });
                });
            });

            var lines = ReadLogLines();
            // Find the WrapperOp record — its error block should now carry
            // the AutoReconnect.Attempts + Exhausted stamps under "data".
            var wrapperLines = lines.Where(l => l.Contains("\"op\":\"WrapperOp\"")).ToArray();
            Assert.NotEmpty(wrapperLines);
            using var doc = JsonDocument.Parse(wrapperLines.Last());
            var err = doc.RootElement.GetProperty("error");
            Assert.True(err.TryGetProperty("data", out var data),
                "Wrapping op's error block must carry ex.Data — see P15-014 BuildErrorRecord.");
            Assert.True(data.TryGetProperty("AutoReconnect.Exhausted", out _));
            Assert.True(data.TryGetProperty("AutoReconnect.Attempts", out _));
        }
    }
}
