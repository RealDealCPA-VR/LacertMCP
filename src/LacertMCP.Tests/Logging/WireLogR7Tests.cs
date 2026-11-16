using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// R7 — WireLog regressions: PII value-slot masking for diff/audit
    /// shapes, culture-invariant timestamps + file names, and the
    /// in-process ring's memory bound under
    /// <c>LACERTE_DEBUG_LOG_FULL_RESULTS</c>.
    ///
    /// <para>Shares the AccessMapMutation collection with the other WireLog
    /// suites because EnableForTests / the ring are process-wide.</para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class WireLogR7Tests : IDisposable
    {
        private readonly string _tempDir;

        public WireLogR7Tests()
        {
            try { WireLog.DisableForTests(); } catch { /* swallow */ }
            _tempDir = Path.Combine(Path.GetTempPath(),
                "lacerte-wirelog-r7-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { WireLog.DisableForTests(); } catch { /* swallow */ }
            try { WireLog.FlushForTests(_tempDir); } catch { /* swallow */ }
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
        }

        // -----------------------------------------------------------------
        // Item 1 — PII value slots beyond `value`
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(RedactionScope.Wire)]
        [InlineData(RedactionScope.Disk)]
        public void Redact_PiiFieldCode_MasksEveryValueSlot(RedactionScope scope)
        {
            var input = @"{""rows"":[
                {""field"":""C1_15"",""a"":""111-22-3333"",""b"":""444-55-6666""},
                {""field"":""C1_19"",""fromValue"":""1980-01-01"",""toValue"":""1981-02-02"",""changed"":true},
                {""field"":""C1_71"",""before"":""12345678"",""after"":""87654321"",""delta"":75308643},
                {""field"":""C1_3"",""oldValue"":""x@example.com"",""newValue"":""y@example.com"",""priorValue"":""z@example.com""}
            ]}";
            var output = WireLog.Redact(input, scope);
            foreach (var secret in new[]
            {
                "111-22-3333", "444-55-6666", "1980-01-01", "1981-02-02",
                "12345678", "87654321", "75308643", "x@example.com", "y@example.com", "z@example.com",
            })
            {
                Assert.DoesNotContain(secret, output);
            }

            var rows = JsonNode.Parse(output)!["rows"]!.AsArray();
            Assert.Equal("C1_15", rows[0]!["field"]!.GetValue<string>());
            Assert.Equal("***", rows[0]!["a"]!.GetValue<string>());
            Assert.Equal("***", rows[0]!["b"]!.GetValue<string>());
            Assert.Equal("***", rows[1]!["fromValue"]!.GetValue<string>());
            Assert.Equal("***", rows[1]!["toValue"]!.GetValue<string>());
            // Non-value metadata on the row survives.
            Assert.True(rows[1]!["changed"]!.GetValue<bool>());
            Assert.Equal("***", rows[2]!["delta"]!.GetValue<string>());
        }

        [Fact]
        public void Redact_NonPiiFieldCode_DiffSlotsVisibleOnWire()
        {
            var input = @"{""field"":""C1_2"",""a"":""Smith"",""b"":""Jones"",""fromValue"":""Old"",""toValue"":""New""}";
            var output = JsonNode.Parse(WireLog.Redact(input, RedactionScope.Wire))!;
            Assert.Equal("Smith", output["a"]!.GetValue<string>());
            Assert.Equal("Jones", output["b"]!.GetValue<string>());
            Assert.Equal("Old", output["fromValue"]!.GetValue<string>());
            Assert.Equal("New", output["toValue"]!.GetValue<string>());
        }

        [Fact]
        public void Redact_NonPiiFieldCode_ValueStillScrubbed_DenylistPreserved()
        {
            // P15-058 denylist semantics are unchanged: `value` is scrubbed
            // for EVERY field code.
            var output = JsonNode.Parse(WireLog.Redact(
                @"{""field"":""C1_2"",""value"":""Smith""}", RedactionScope.Wire))!;
            Assert.Equal("***", output["value"]!.GetValue<string>());
        }

        [Fact]
        public void Redact_PiiFieldCode_AddressingMetadataSurvives()
        {
            // WriteField args / error-context shapes: only the value slot
            // goes; the addressing keys FieldHistoryTools matches on and the
            // actionable reason stay intact.
            var input = @"{""table"":1,""clientNumber"":""1001"",""field"":""C1_15"",""value"":""111-22-3333"",""dryRun"":false,""reason"":""SSN must be 9 digits.""}";
            var output = JsonNode.Parse(WireLog.Redact(input, RedactionScope.Wire))!;
            Assert.Equal("***", output["value"]!.GetValue<string>());
            Assert.Equal(1, output["table"]!.GetValue<int>());
            Assert.Equal("1001", output["clientNumber"]!.GetValue<string>());
            Assert.Equal("C1_15", output["field"]!.GetValue<string>());
            Assert.False(output["dryRun"]!.GetValue<bool>());
            Assert.Equal("SSN must be 9 digits.", output["reason"]!.GetValue<string>());
        }

        // -----------------------------------------------------------------
        // Item 3 — culture-invariant timestamps and file names
        // -----------------------------------------------------------------

        private static readonly Regex TsShape = new Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", RegexOptions.CultureInvariant);

        private static readonly Regex LogNameShape = new Regex(
            @"^lacerte-\d{8}(-\d{6}(-\d+)?)?\.log$", RegexOptions.CultureInvariant);

        private static void AssertGregorianLogName(string fileName)
        {
            Assert.Matches(LogNameShape, fileName);
            var datePart = fileName.Substring("lacerte-".Length, 8);
            var parsed = DateTime.ParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture);
            // A Buddhist-calendar year would be +543, a Hijri one ~-579.
            Assert.InRange(parsed.Year, DateTime.UtcNow.Year - 1, DateTime.UtcNow.Year + 1);
        }

        [Theory]
        [InlineData("th-TH")]   // Thai Buddhist calendar
        [InlineData("fi-FI")]   // '.' time separator
        [InlineData("ar-SA")]   // Um Al Qura (Hijri) calendar
        public void TimestampsAndFileNames_AreGregorianInvariant_UnderAnyCulture(string cultureName)
        {
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

                // Record timestamp.
                var line = WireLog.BuildRedactedLine(
                    "r7-culture-op", null, 1, error: null, durationMs: 1,
                    resultSampleLimit: 5, fullResults: false);
                var ts = JsonNode.Parse(line)!["ts"]!.GetValue<string>();
                Assert.Matches(TsShape, ts);
                var parsedTs = DateTime.ParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                Assert.True(Math.Abs((DateTime.UtcNow - parsedTs).TotalMinutes) < 5,
                    $"ts '{ts}' does not round-trip to now under {cultureName}");

                // Per-day file name + size-cap rotated names.
                WireLog.EnableForTests(_tempDir, maxBytes: 300);
                AssertGregorianLogName(Path.GetFileName(WireLog.CurrentLogPath!));
                var bulky = new string('x', 200);
                for (int i = 0; i < 4; i++)
                {
                    WireLog.WriteForTest(_tempDir, "r7-culture-rotate", new { pad = bulky }, i, durationMs: 1);
                }
                var names = Directory.GetFiles(_tempDir, "lacerte-*.log")
                    .Select(Path.GetFileName).ToArray();
                Assert.Contains(names, n => n!.Length > "lacerte-yyyyMMdd.log".Length); // a rotated file exists
                foreach (var n in names) AssertGregorianLogName(n!);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                WireLog.DisableForTests();
            }
        }

        // -----------------------------------------------------------------
        // Item 5 — ring stays bounded with LACERTE_DEBUG_LOG_FULL_RESULTS
        // -----------------------------------------------------------------

        private static List<Dictionary<string, object?>> BigResult(int rows)
            => Enumerable.Range(0, rows)
                .Select(i => new Dictionary<string, object?> { ["row"] = i, ["pad"] = new string('p', 40) })
                .ToList();

        [Fact]
        public void FullResults_DiskGetsFullPayload_RingGetsSummary()
        {
            var op = "r7-full-results-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WireLog.EnableForTests(_tempDir, resultSampleLimit: 5, fullResults: true);
            try
            {
                WireLog.RecordSync(op, null, () => BigResult(200));
            }
            finally
            {
                WireLog.FlushForTests(_tempDir);
            }

            // Disk: the opted-in full payload.
            var diskLine = Directory.GetFiles(_tempDir, "lacerte-*.log")
                .SelectMany(f =>
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var r = new StreamReader(fs);
                    return r.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                })
                .Single(l => l.Contains(op));
            Assert.Equal(200, JsonNode.Parse(diskLine)!["result"]!.AsArray().Count);

            // Ring: summarized, bounded.
            var ringLine = WireLogRing.Snapshot().Lines.Last(l => l.Contains(op));
            Assert.True(ringLine.Length <= WireLogRing.MaxEntryChars);
            var result = JsonNode.Parse(ringLine)!["result"]!;
            Assert.Equal("collection", result["kind"]!.GetValue<string>());
            Assert.Equal(200, result["count"]!.GetValue<int>());
            Assert.Equal(5, result["sample"]!.AsArray().Count);
        }

        [Fact]
        public void RingLineFor_DefaultPath_IsIdentity()
        {
            var line = @"{""op"":""x"",""result"":[1,2,3,4,5,6,7]}";
            Assert.Same(line, WireLog.RingLineFor(line, 2, fullResults: false, isError: false));
            Assert.Same(line, WireLog.RingLineFor(line, 2, fullResults: true, isError: true));
            var summarized = JsonNode.Parse(WireLog.RingLineFor(line, 2, fullResults: true, isError: false))!;
            Assert.Equal(7, summarized["result"]!["count"]!.GetValue<int>());
        }

        [Fact]
        public void RingPush_OversizedNonArrayEntry_IsCompactedWithDiagnostics()
        {
            var op = "r7-oversized-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var big = new JsonObject
            {
                ["ts"] = "2026-11-15T00:00:00.000Z",
                ["op"] = op,
                ["cid"] = "abc12345",
                ["durationMs"] = 7,
                ["result"] = new JsonObject { ["blob"] = new string('b', WireLogRing.MaxEntryChars * 4) },
            }.ToJsonString();

            WireLogRing.Push(big);

            var stored = WireLogRing.Snapshot().Lines.Last(l => l.Contains(op));
            Assert.True(stored.Length <= WireLogRing.MaxEntryChars);
            var node = JsonNode.Parse(stored)!;
            Assert.Equal(op, node["op"]!.GetValue<string>());
            Assert.Equal("abc12345", node["cid"]!.GetValue<string>());
            Assert.Equal(7, node["durationMs"]!.GetValue<int>());
            Assert.True(node["truncated"]!.GetValue<bool>());
            Assert.Equal(big.Length, node["originalLength"]!.GetValue<int>());
            Assert.Null(node["result"]);
        }

        [Fact]
        public void RingPush_SmallEntry_StoredVerbatim()
        {
            var line = @"{""op"":""r7-small-" + Guid.NewGuid().ToString("N") + @""",""result"":1}";
            WireLogRing.Push(line);
            Assert.Contains(line, WireLogRing.Snapshot().Lines);
        }
    }
}
