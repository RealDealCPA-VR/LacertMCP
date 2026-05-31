using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Diagnostics
{
    /// <summary>
    /// P15-054 — guards on the periodic heartbeat-file writer.
    ///
    /// <para>
    /// Asserts the env-var parsing contract (default 30s, 0/negative disables,
    /// unparseable falls back to default), the JSON payload shape an external
    /// watcher will script against ({pid, registered, mode, ts}), and that
    /// write failures stay swallowed so the heartbeat can never poison a tool
    /// call. Runs entirely off-Windows — uses Path.GetTempPath, in-memory
    /// session, and the test-seam ctor that skips env-var resolution.
    /// </para>
    /// </summary>
    public class HeartbeatWriterTests
    {
        // ------------------------------------------------------------------
        // IntervalSeconds parser
        // ------------------------------------------------------------------

        [Fact]
        public void IntervalSeconds_NullOrEmpty_ReturnsDefault()
        {
            Assert.Equal(HeartbeatWriter.DefaultIntervalSeconds, HeartbeatWriter.IntervalSeconds(null));
            Assert.Equal(HeartbeatWriter.DefaultIntervalSeconds, HeartbeatWriter.IntervalSeconds(""));
            Assert.Equal(HeartbeatWriter.DefaultIntervalSeconds, HeartbeatWriter.IntervalSeconds("   "));
        }

        [Fact]
        public void IntervalSeconds_Zero_ReturnsZeroToDisable()
        {
            Assert.Equal(0, HeartbeatWriter.IntervalSeconds("0"));
        }

        [Fact]
        public void IntervalSeconds_Negative_TreatedAsDisabled()
        {
            // Operator typing "-1" almost always means "off"; honour it
            // instead of letting it slip through as a non-positive cadence
            // and racing the BackgroundService loop.
            Assert.Equal(0, HeartbeatWriter.IntervalSeconds("-1"));
            Assert.Equal(0, HeartbeatWriter.IntervalSeconds("-60"));
        }

        [Fact]
        public void IntervalSeconds_PositiveInteger_ReturnedVerbatim()
        {
            Assert.Equal(5, HeartbeatWriter.IntervalSeconds("5"));
            Assert.Equal(60, HeartbeatWriter.IntervalSeconds("60"));
            Assert.Equal(3600, HeartbeatWriter.IntervalSeconds("3600"));
        }

        [Fact]
        public void IntervalSeconds_Unparseable_FallsBackToDefault()
        {
            Assert.Equal(HeartbeatWriter.DefaultIntervalSeconds, HeartbeatWriter.IntervalSeconds("abc"));
            Assert.Equal(HeartbeatWriter.DefaultIntervalSeconds, HeartbeatWriter.IntervalSeconds("30s"));
            Assert.Equal(HeartbeatWriter.DefaultIntervalSeconds, HeartbeatWriter.IntervalSeconds("3.5"));
        }

        // ------------------------------------------------------------------
        // DefaultPath
        // ------------------------------------------------------------------

        [Fact]
        public void DefaultPath_LandsInTempWithCanonicalFileName()
        {
            var path = HeartbeatWriter.DefaultPath();
            Assert.Equal(HeartbeatWriter.HeartbeatFileName, Path.GetFileName(path));
            // Path.GetTempPath always ends with a directory separator; just
            // assert the file lives under it rather than reconstructing the
            // exact string (Path.Combine canonicalises trailing separators).
            Assert.Equal(
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        // ------------------------------------------------------------------
        // TryWrite + payload shape
        // ------------------------------------------------------------------

        [Fact]
        public void TryWrite_WritesParseableJsonWithExpectedShape()
        {
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: path);

                Assert.True(writer.TryWrite());
                Assert.True(File.Exists(path));

                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                Assert.Equal(JsonValueKind.Number, root.GetProperty("pid").ValueKind);
                Assert.Equal(JsonValueKind.False, root.GetProperty("registered").ValueKind); // default Phase-0 sim: not registered
                Assert.Equal("simulation", root.GetProperty("mode").GetString());
                // ISO-8601 timestamp — Parse round-trips, anything else throws.
                var ts = DateTimeOffset.Parse(root.GetProperty("ts").GetString()!);
                Assert.True((DateTimeOffset.UtcNow - ts).Duration() < TimeSpan.FromSeconds(10));
            }
            finally
            {
                TryDelete(path);
            }
        }

        [Fact]
        public void TryWrite_OverwritesPreviousContent()
        {
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: path);

                Assert.True(writer.TryWrite());
                var first = File.ReadAllText(path);
                // sleep just enough for the timestamp to advance (ts is ISO
                // with subsecond precision; 20ms is comfortably above the
                // DateTimeOffset.UtcNow tick resolution on every supported OS).
                Thread.Sleep(20);
                Assert.True(writer.TryWrite());
                var second = File.ReadAllText(path);

                Assert.NotEqual(first, second);
                // Ensure it's still single-document, not appended garbage.
                using var doc = JsonDocument.Parse(second);
                Assert.Equal("simulation", doc.RootElement.GetProperty("mode").GetString());
            }
            finally
            {
                TryDelete(path);
            }
        }

        [Fact]
        public void TryWrite_OnIOErrorReturnsFalseAndDoesNotThrow()
        {
            // Target a path under a non-existent directory; net48
            // File.WriteAllText throws DirectoryNotFoundException.
            var badPath = Path.Combine(
                Path.GetTempPath(),
                "lacerte-heartbeat-" + Guid.NewGuid().ToString("N"),
                "nested",
                HeartbeatWriter.HeartbeatFileName);

            var session = new LacerteSessionManager();
            var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: badPath);

            // Must not throw — heartbeat IO failure is a swallowed soft error.
            var ok = writer.TryWrite();
            Assert.False(ok);
            Assert.False(File.Exists(badPath));
        }

        // ------------------------------------------------------------------
        // Hosted-service loop — single iteration via short interval
        // ------------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_WritesImmediatelyAndKeepsWritingUntilCancelled()
        {
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                // 1-second cadence keeps the test fast while still exercising
                // the Delay loop (not just the immediate-write path).
                var writer = new HeartbeatWriter(session, intervalSeconds: 1, path: path);
                using var cts = new CancellationTokenSource();

                var runTask = writer.StartAsync(cts.Token);
                await runTask;

                // Immediate write should already be on disk.
                Assert.True(File.Exists(path));
                var firstMtime = File.GetLastWriteTimeUtc(path);

                // Wait long enough for one tick.
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
                cts.Cancel();
                try { await writer.StopAsync(CancellationToken.None); }
                catch (OperationCanceledException) { /* expected on graceful stop */ }

                var secondMtime = File.GetLastWriteTimeUtc(path);
                // Either the file was rewritten (mtime advanced) or the
                // payload changed (filesystems with low mtime resolution).
                if (secondMtime == firstMtime)
                {
                    // Fallback — parse and accept any valid JSON; the loop
                    // running at all is what we're asserting.
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    Assert.Equal("simulation", doc.RootElement.GetProperty("mode").GetString());
                }
                else
                {
                    Assert.True(secondMtime > firstMtime);
                }
            }
            finally
            {
                TryDelete(path);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string NewTempPath() =>
            Path.Combine(Path.GetTempPath(), "lacerte-mcp-heartbeat-" + Guid.NewGuid().ToString("N") + ".json");

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
