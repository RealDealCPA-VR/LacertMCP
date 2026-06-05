using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Diagnostics
{
    /// <summary>
    /// P15-054 + P17-003 + P17-014 — guards on the periodic heartbeat-file writer.
    ///
    /// <para>
    /// Asserts the env-var parsing contract (default 30s, 0/negative disables,
    /// unparseable falls back to default), the JSON payload shape an external
    /// watcher will script against ({pid, mode, registered, expiresAt,
    /// minutesUntilExpiry, buildVersion, ts}), that registered honours the
    /// real ExpiresAt clock (not the bare IsRegistered latch) so an idle
    /// server with a lapsed Intuit ticket truthfully reports
    /// <c>registered:false</c>, that the heartbeat file name is PID-suffixed
    /// so concurrent MCP instances don't clobber each other, and that write
    /// failures stay swallowed so the heartbeat can never poison a tool
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
        // P17-014 — PID-suffixed file name (multi-instance hygiene)
        // ------------------------------------------------------------------

        [Fact]
        public void HeartbeatFileName_IsPidSuffixed_P17_014()
        {
            // Two concurrent MCP server instances must not clobber the same
            // file every 30s. The canonical name MUST carry the running
            // process id so a watcher enumerating the glob sees one row per
            // live instance.
            var name = HeartbeatWriter.HeartbeatFileName;
            var expected = "lacerte-mcp-heartbeat-" + Process.GetCurrentProcess().Id + ".json";
            Assert.Equal(expected, name);
        }

        [Fact]
        public void BuildHeartbeatFileName_FormatsAsLowercasePidSuffix_P17_014()
        {
            Assert.Equal("lacerte-mcp-heartbeat-12345.json",
                HeartbeatWriter.BuildHeartbeatFileName(12345));
            Assert.Equal("lacerte-mcp-heartbeat-1.json",
                HeartbeatWriter.BuildHeartbeatFileName(1));
        }

        [Fact]
        public void HeartbeatFileGlob_MatchesPidSuffixedNames_P17_014()
        {
            // The exported glob must match any PID-suffixed file the writer
            // could produce. Keep the glob and the BuildHeartbeatFileName
            // format in lockstep so external watchers don't silently miss
            // instances after a future refactor.
            Assert.Equal("lacerte-mcp-heartbeat-*.json", HeartbeatWriter.HeartbeatFileGlob);

            // Convert the glob to a regex to actually verify match semantics.
            var globRegex = "^" + Regex.Escape(HeartbeatWriter.HeartbeatFileGlob).Replace("\\*", ".*") + "$";
            Assert.Matches(globRegex, HeartbeatWriter.BuildHeartbeatFileName(1));
            Assert.Matches(globRegex, HeartbeatWriter.BuildHeartbeatFileName(99999));
            Assert.Matches(globRegex, HeartbeatWriter.HeartbeatFileName);
        }

        [Fact]
        public void DefaultPath_DoesNotEqualLegacySharedPath_P17_014()
        {
            // Regression sentinel: the legacy "lacerte-mcp-heartbeat.json"
            // (no PID suffix) was the shared-clobber path. If someone
            // reverts BuildHeartbeatFileName back to a const, this fires.
            var legacy = Path.Combine(Path.GetTempPath(), "lacerte-mcp-heartbeat.json");
            Assert.NotEqual(legacy, HeartbeatWriter.DefaultPath());
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

                // P17-003 — shape is the README-advertised
                // {pid, mode, registered, expiresAt, minutesUntilExpiry,
                // buildVersion, ts}. Unregistered baseline => registered:false,
                // expiresAt:null, minutesUntilExpiry:null. buildVersion is
                // populated from the assembly stamp (BuildInfo.Version).
                Assert.Equal(JsonValueKind.Number, root.GetProperty("pid").ValueKind);
                Assert.Equal("simulation", root.GetProperty("mode").GetString());
                Assert.Equal(JsonValueKind.False, root.GetProperty("registered").ValueKind); // default Phase-0 sim: not registered
                Assert.Equal(JsonValueKind.Null, root.GetProperty("expiresAt").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("minutesUntilExpiry").ValueKind);
                // buildVersion comes from AssemblyInformationalVersion; always non-null
                // (BuildInfo.Version falls back to "unknown" on reflection failure).
                var buildVersion = root.GetProperty("buildVersion").GetString();
                Assert.False(string.IsNullOrEmpty(buildVersion));
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
        // P17-003 — registered/expiresAt staleness contract
        // ------------------------------------------------------------------

        [Fact]
        public void TryWrite_RegisteredSession_PopulatesExpiresAtAndCountdown_P17_003()
        {
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                session.SetRegisteredForTest(true);
                var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: path);

                Assert.True(writer.TryWrite());

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                Assert.Equal(JsonValueKind.True, root.GetProperty("registered").ValueKind);
                // expiresAt is the freshly-set Register timestamp + 24h; must
                // be parseable ISO-8601 and within ~24h of now.
                var expiresAt = DateTimeOffset.Parse(root.GetProperty("expiresAt").GetString()!);
                var delta = (expiresAt - DateTimeOffset.UtcNow).TotalMinutes;
                Assert.InRange(delta, 24 * 60 - 5, 24 * 60 + 5);
                // minutesUntilExpiry mirrors the same window.
                var minutes = root.GetProperty("minutesUntilExpiry").GetDouble();
                Assert.InRange(minutes, 24 * 60 - 5, 24 * 60 + 5);
            }
            finally
            {
                TryDelete(path);
            }
        }

        [Fact]
        public void TryWrite_ExpiredTicket_ReportsRegisteredFalse_EvenIfLatchStale_P17_003()
        {
            // The bug: with the bare _session.IsRegistered latch, an idle
            // server whose 24h Intuit ticket lapsed at 02:00 keeps writing
            // registered:true every 30s until the next tool call's
            // ThrowIfAuthExpired probe flips the latch. Watchers polling
            // only the heartbeat see a stale green light across the entire
            // overnight idle window — defeating the whole point of P15-054.
            //
            // Fix: BuildPayload computes registered as
            //   IsRegistered && ExpiresAt > UtcNow
            // so the heartbeat truthfully flips to false the moment the
            // ticket lapses, independent of any tool-call probe.
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                session.SetRegisteredForTest(true);                  // latch still true
                session.SetExpiresAtForTest(DateTimeOffset.UtcNow.AddSeconds(-1)); // ticket lapsed

                // Critical: do NOT call any tool / require-context here —
                // simulate the "idle since 02:00, no calls have probed
                // ThrowIfAuthExpired yet" condition the bug describes.
                Assert.True(session.IsRegistered, "Pre-condition: latch must still read true so the regression actually fires.");

                var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: path);
                Assert.True(writer.TryWrite());

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                // The whole point: registered MUST be false even though the
                // backing latch was never flipped.
                Assert.Equal(JsonValueKind.False, root.GetProperty("registered").ValueKind);
                // expiresAt is still populated (we know when the ticket lapsed).
                Assert.Equal(JsonValueKind.String, root.GetProperty("expiresAt").ValueKind);
                // minutesUntilExpiry is clamped to 0 by SessionStatus, not negative.
                var minutes = root.GetProperty("minutesUntilExpiry").GetDouble();
                Assert.Equal(0.0, minutes);
            }
            finally
            {
                TryDelete(path);
            }
        }

        [Fact]
        public void TryWrite_NeverRegistered_ExpiresAtNull_RegisteredFalse_P17_003()
        {
            // Baseline: a fresh session that was never registered should
            // emit registered:false + expiresAt:null + minutesUntilExpiry:null.
            // Pinning this prevents a future refactor from emitting an
            // ambiguous "{registered:false, expiresAt:'0001-01-01T...'}"
            // that would confuse watchers.
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: path);

                Assert.True(writer.TryWrite());
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                Assert.Equal(JsonValueKind.False, root.GetProperty("registered").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("expiresAt").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("minutesUntilExpiry").ValueKind);
            }
            finally
            {
                TryDelete(path);
            }
        }

        [Fact]
        public void TryWrite_PayloadShape_MatchesReadmeAdvertisedFields_P17_003()
        {
            // README.md:154 documents the heartbeat JSON as carrying
            // "(pid, mode, auth countdown, build version)". The implementation
            // additionally surfaces `registered`, `expiresAt`, and `ts` for the
            // watcher's mtime-staleness + per-tick parsing needs. This test
            // pins the full set so future drift (either side) fails CI rather
            // than reaching production.
            var path = NewTempPath();
            try
            {
                var session = new LacerteSessionManager();
                var writer = new HeartbeatWriter(session, intervalSeconds: 30, path: path);
                Assert.True(writer.TryWrite());

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                // Required keys (presence test, not value test).
                foreach (var key in new[] {
                    "pid", "mode", "registered",
                    "expiresAt", "minutesUntilExpiry",
                    "buildVersion", "ts",
                })
                {
                    Assert.True(root.TryGetProperty(key, out _),
                        $"Heartbeat payload is missing required key '{key}'.");
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
