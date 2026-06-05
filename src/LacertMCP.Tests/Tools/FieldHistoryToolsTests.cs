using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-036 — `lacerte_field_history` (X_feat_accel cluster).
    ///
    /// <para>Uses the <see cref="LacertMCP.Tests.AccessMapMutationCollection"/>
    /// because <see cref="WireLog"/>'s static <c>_testForceConfig</c> +
    /// cached writer state is process-wide; without serialization the
    /// sibling WireLog rotation/summarization test classes — which
    /// already use this collection — would race our <c>EnableForTests</c>
    /// / <c>DisableForTests</c> calls.</para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldHistoryToolsTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _logFile;
        private readonly bool _wireWasEnabled;

        public FieldHistoryToolsTests()
        {
            // Defensive: make sure any prior test's WireLog state is fully
            // released BEFORE we set up our own. Otherwise a stale writer
            // (from a same-collection sibling that EnableForTests'd then
            // failed to fully disable) could hold a handle to our shared
            // YYYYMMDD path even though our temp dir is unique.
            try { WireLog.DisableForTests(); } catch { /* swallow */ }

            _tempDir = Path.Combine(Path.GetTempPath(),
                "lacerte-field-history-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempDir);
            _logFile = Path.Combine(_tempDir,
                "lacerte-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
            _wireWasEnabled = WireLog.IsEnabled;
        }

        public void Dispose()
        {
            try { WireLog.DisableForTests(); } catch { /* swallow */ }
            try { WireLog.FlushForTests(_tempDir); } catch { /* swallow */ }
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
        }

        [Fact]
        public async Task FieldHistory_WireLogDisabled_ReturnsHint()
        {
            // Confirm WireLog is disabled (default test state).
            WireLog.DisableForTests();
            var tools = new FieldHistoryTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteFieldHistory("1001", "C1_3"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(0, result.GetProperty("total").GetInt32());
            Assert.True(result.TryGetProperty("hint", out var hint));
            Assert.Contains("LACERTE_DEBUG_WIRE", hint.GetString());
        }

        [Fact]
        public async Task FieldHistory_EmptyClientNumber_ReturnsInvalidArgument()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("  ", "C1_3"));
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_EmptyField_ReturnsInvalidArgument()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", ""));
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_InvalidSinceUtc_ReturnsInvalidArgument()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", "C1_3", sinceUtc: "not-a-date"));
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_MatchingWrites_ReturnsRows()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                // Write three lines: two matching WriteField for client/field,
                // one mismatched field, one inner Bridge.WriteField mirror.
                var today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var lines = new[]
                {
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***", dryRun: false, result: 0, durationMs: 12),
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***", dryRun: true,  result: 0, durationMs: 8),
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_99", "***", dryRun: false, result: 0, durationMs: 6),
                    BuildWriteFieldLine(today, "Bridge.WriteField", "1001", "C1_3", "***", dryRun: false, result: 0, durationMs: 4),
                };
                // Flush any cached writer for our path before writing so
                // a sibling test's stale handle can't block File.WriteAllLines.
                WireLog.FlushForTests(_tempDir);
                File.WriteAllLines(_logFile, lines);

                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", "C1_3"));
                Assert.True(root.GetProperty("success").GetBoolean());
                var result = root.GetProperty("result");
                Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
                Assert.Equal(2, result.GetProperty("total").GetInt32());
                Assert.False(result.GetProperty("truncated").GetBoolean());
                // Every row's value is "***".
                foreach (var row in result.GetProperty("rows").EnumerateArray())
                {
                    Assert.Equal("***", row.GetProperty("value").GetString());
                }
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_LimitTruncates_AndSetsTruncatedTrue()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var lines = Enumerable.Range(0, 5).Select(i =>
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: i)).ToArray();
                // Flush any cached writer for our path before writing so
                // a sibling test's stale handle can't block File.WriteAllLines.
                WireLog.FlushForTests(_tempDir);
                File.WriteAllLines(_logFile, lines);

                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", "C1_3", limit: 2));
                var result = root.GetProperty("result");
                Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
                Assert.Equal(5, result.GetProperty("total").GetInt32());
                Assert.True(result.GetProperty("truncated").GetBoolean());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_LimitZero_ReturnsEmptyRowsButTotal()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var lines = Enumerable.Range(0, 3).Select(i =>
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: i)).ToArray();
                // Flush any cached writer for our path before writing so
                // a sibling test's stale handle can't block File.WriteAllLines.
                WireLog.FlushForTests(_tempDir);
                File.WriteAllLines(_logFile, lines);

                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", "C1_3", limit: 0));
                var result = root.GetProperty("result");
                Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
                Assert.Equal(3, result.GetProperty("total").GetInt32());
                Assert.True(result.GetProperty("truncated").GetBoolean());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_MalformedLine_SkippedSilently()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var lines = new[]
                {
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: 5),
                    "{ not valid json {{",
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: 10),
                };
                // Flush any cached writer for our path before writing so
                // a sibling test's stale handle can't block File.WriteAllLines.
                WireLog.FlushForTests(_tempDir);
                File.WriteAllLines(_logFile, lines);

                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", "C1_3"));
                var result = root.GetProperty("result");
                // 2 rows survive; the malformed line is skipped.
                Assert.Equal(2, result.GetProperty("total").GetInt32());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_BridgeMirrorOps_NotCounted()
        {
            WireLog.EnableForTests(_tempDir);
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var lines = new[]
                {
                    BuildWriteFieldLine(today, "WriteField", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: 5),
                    BuildWriteFieldLine(today, "Bridge.WriteField", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: 6),
                    BuildWriteFieldLine(today, "Bridge.UpdateTaxReturn", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: 7),
                    BuildWriteFieldLine(today, "Bridge.WriteToDBFDatabase", "1001", "C1_3", "***",
                        dryRun: false, result: 0, durationMs: 8),
                };
                // Flush any cached writer for our path before writing so
                // a sibling test's stale handle can't block File.WriteAllLines.
                WireLog.FlushForTests(_tempDir);
                File.WriteAllLines(_logFile, lines);

                var tools = new FieldHistoryTools();
                var root = ToolTestHelpers.Parse(
                    await tools.LacerteFieldHistory("1001", "C1_3"));
                var result = root.GetProperty("result");
                // Only the outer WriteField record counts.
                Assert.Equal(1, result.GetProperty("total").GetInt32());
            }
            finally { WireLog.DisableForTests(); }
        }

        [Fact]
        public async Task FieldHistory_RedactionNote_AlwaysPresent()
        {
            WireLog.DisableForTests();
            var tools = new FieldHistoryTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteFieldHistory("1001", "C1_3"));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("result").TryGetProperty("redactionNote", out _));
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static string BuildWriteFieldLine(
            string ts, string op, string clientNumber, string field,
            string value, bool dryRun, int result, long durationMs)
        {
            var record = new
            {
                ts,
                op,
                args = new
                {
                    table = 1,
                    clientNumber,
                    field,
                    value,
                    dryRun,
                },
                durationMs,
                result,
            };
            return JsonSerializer.Serialize(record);
        }
    }
}
