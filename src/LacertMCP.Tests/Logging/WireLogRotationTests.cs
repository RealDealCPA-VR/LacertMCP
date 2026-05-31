using System;
using System.IO;
using System.Linq;
using System.Text;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// P15-015 + P15-071 — log rotation (size cap) and retention (day window).
    ///
    /// <para>
    /// Tests use small caps (4 KB file / 16 KB dir / 1 day retention) so the
    /// suite doesn't materialize megabytes during dotnet test. Each test
    /// exercises a single branch:
    /// </para>
    /// <list type="bullet">
    ///   <item>Size cap rotates the active file into a timestamped sibling.</item>
    ///   <item>Directory-size cap prunes oldest files after rotation.</item>
    ///   <item>Retention prunes files older than RetentionDays on the first
    ///         write of each UTC day.</item>
    ///   <item>OnRotation observability callback fires with a non-empty
    ///         message on each rotation event.</item>
    ///   <item>Malformed filenames in the log dir are skipped by the prune
    ///         routine (never throw out of WriteRecord).</item>
    /// </list>
    ///
    /// <para>
    /// Uses the same xUnit Collection as <see cref="WireLogSessionIntegrationTests"/>
    /// because WireLog's static config + cached-writer state + OnRotation
    /// callback are mutated by every test in this file. Without the collection,
    /// xUnit's default parallelism races EnableForTests / DisableForTests
    /// across tests and causes the session integration test to see a temp
    /// dir that another rotation test has just deleted.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class WireLogRotationTests
    {
        private static string NewTempDir(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Write_ExceedingSizeCap_RotatesFile_P15_015()
        {
            var dir = NewTempDir("wirelog-rot");
            try
            {
                // Small cap: 1 KB. Each record is well over 100 bytes so a
                // small loop trips the cap quickly.
                WireLog.EnableForTests(dir, maxBytes: 1024L);

                var big = new string('x', 200);
                for (int i = 0; i < 20; i++)
                {
                    WireLog.WriteForTest(dir, "rot-test-" + i, new { payload = big }, new { ok = true }, durationMs: 1);
                }
                WireLog.FlushForTests(dir);

                var files = Directory.GetFiles(dir, "lacerte-*.log");
                // Today's file plus at least one rotated file (size cap exceeded).
                Assert.True(files.Length >= 2, $"Expected at least 2 files (rotation occurred); got {files.Length}: " + string.Join(", ", files));

                // Confirm at least one file has the rotated yyyyMMdd-HHmmss
                // shape (the date-only file is the active one).
                var rotated = files.Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    return name.StartsWith("lacerte-")
                        && name.Length > "lacerte-yyyyMMdd".Length
                        && name.Contains("-");
                }).ToArray();
                // The active file is lacerte-YYYYMMDD.log (8 digits + ".log");
                // the rotated form is lacerte-YYYYMMDD-HHmmss.log (15 chars
                // before .log). So we expect at least one rotated.
                var rotatedExpanded = files.Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f); // lacerte-YYYYMMDD[-HHmmss[-salt]]
                    var tail = name.Substring("lacerte-".Length);
                    return tail.Length > 8;
                }).ToArray();
                Assert.NotEmpty(rotatedExpanded);
            }
            finally
            {
                WireLog.DisableForTests();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Rotation_FiresOnRotationCallback_P15_015()
        {
            var dir = NewTempDir("wirelog-cb");
            int callbacks = 0;
            string? lastMsg = null;
            try
            {
                WireLog.OnRotation = msg => { callbacks++; lastMsg = msg; };
                WireLog.EnableForTests(dir, maxBytes: 512L);

                var big = new string('y', 200);
                for (int i = 0; i < 10; i++)
                {
                    WireLog.WriteForTest(dir, "cb-test", new { payload = big }, new { ok = true }, durationMs: 1);
                }
                WireLog.FlushForTests(dir);

                Assert.True(callbacks >= 1, $"OnRotation should have fired at least once; got {callbacks}");
                Assert.False(string.IsNullOrEmpty(lastMsg));
                Assert.Contains("rotated", lastMsg);
            }
            finally
            {
                WireLog.OnRotation = null;
                WireLog.DisableForTests();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Prune_DropsFilesOlderThanRetentionDays_P15_071()
        {
            var dir = NewTempDir("wirelog-prune");
            try
            {
                // Seed an old file (90 days ago) and a recent file (today-2).
                // RetentionDays=14 — old file should be deleted, recent kept.
                var oldDate = DateTime.UtcNow.Date.AddDays(-90).ToString("yyyyMMdd");
                var recentDate = DateTime.UtcNow.Date.AddDays(-2).ToString("yyyyMMdd");
                var oldFile = Path.Combine(dir, $"lacerte-{oldDate}.log");
                var recentFile = Path.Combine(dir, $"lacerte-{recentDate}.log");
                File.WriteAllText(oldFile, "{\"op\":\"old\"}\n");
                File.WriteAllText(recentFile, "{\"op\":\"recent\"}\n");

                WireLog.EnableForTests(dir, retentionDays: 14);
                // Trigger a write so PruneOldLogs runs (gated by
                // _lastPrunedDayOfYear which EnableForTests reset to -1).
                WireLog.WriteForTest(dir, "probe", new { }, new { }, durationMs: 1);
                WireLog.FlushForTests(dir);

                Assert.False(File.Exists(oldFile), "90-day-old file should have been pruned.");
                Assert.True(File.Exists(recentFile), "2-day-old file should have been kept.");
            }
            finally
            {
                WireLog.DisableForTests();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Prune_IgnoresMalformedFilenames_P15_071()
        {
            var dir = NewTempDir("wirelog-bad");
            try
            {
                // Files that don't match lacerte-YYYYMMDD or are otherwise
                // unparseable. PruneOldLogs must skip them silently — never
                // delete + never throw out of WriteRecord.
                var bad1 = Path.Combine(dir, "lacerte-notadate.log");
                var bad2 = Path.Combine(dir, "lacerte-2025xx01.log");
                var bad3 = Path.Combine(dir, "random.log");
                File.WriteAllText(bad1, "{}");
                File.WriteAllText(bad2, "{}");
                File.WriteAllText(bad3, "{}");

                WireLog.EnableForTests(dir, retentionDays: 1);
                WireLog.WriteForTest(dir, "probe", new { }, new { }, durationMs: 1);
                WireLog.FlushForTests(dir);

                Assert.True(File.Exists(bad1));
                Assert.True(File.Exists(bad2));
                Assert.True(File.Exists(bad3));
            }
            finally
            {
                WireLog.DisableForTests();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Prune_DirSizeCapDeletesOldestAfterRotation_P15_015()
        {
            var dir = NewTempDir("wirelog-dircap");
            try
            {
                // Seed several oversized files, then trigger rotation. The
                // MaxDirBytes prune should kick in immediately after the
                // rotation event and drop the oldest seeds.
                for (int i = 0; i < 5; i++)
                {
                    var name = $"lacerte-{DateTime.UtcNow.Date.AddDays(-i - 1):yyyyMMdd}.log";
                    var p = Path.Combine(dir, name);
                    File.WriteAllBytes(p, new byte[1024]); // 1 KB each
                    // Set LastWriteTimeUtc explicitly so the prune sort is deterministic.
                    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-i - 1));
                }

                // Tight caps: 256 B file cap and 1 KB dir cap. Every write
                // triggers a rotation (size > cap). The rotation handler
                // calls PruneToMaxDirBytes which should reduce the dir
                // below 1 KB.
                WireLog.EnableForTests(dir, maxBytes: 256L, maxDirBytes: 1024L, retentionDays: 365);
                for (int i = 0; i < 3; i++)
                {
                    WireLog.WriteForTest(dir, "trigger", new { payload = new string('z', 200) }, new { ok = true }, durationMs: 1);
                }
                WireLog.FlushForTests(dir);

                long totalSize = Directory.GetFiles(dir, "lacerte-*.log")
                    .Select(f => new FileInfo(f).Length).Sum();
                // Total must fit under MaxDirBytes (allow some slack for the
                // active file whose latest write may be partial).
                Assert.True(totalSize <= 4096L,
                    $"Dir size cap should have pruned old files; total bytes = {totalSize}");
            }
            finally
            {
                WireLog.DisableForTests();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void WriteRecord_NeverThrows_EvenIfPruneFails_P15_015()
        {
            // Hold a malformed file open exclusively so File.Delete inside
            // PruneOldLogsLocked fails. WriteRecord must swallow and continue
            // writing the new record.
            var dir = NewTempDir("wirelog-deletefail");
            try
            {
                var oldName = $"lacerte-{DateTime.UtcNow.Date.AddDays(-30):yyyyMMdd}.log";
                var oldPath = Path.Combine(dir, oldName);
                using var hold = new FileStream(oldPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                hold.Write(Encoding.UTF8.GetBytes("locked"), 0, 6);
                hold.Flush();

                WireLog.EnableForTests(dir, retentionDays: 14);
                // Should not throw despite File.Delete on the held file failing.
                WireLog.WriteForTest(dir, "stays-alive", new { }, new { }, durationMs: 1);
                WireLog.FlushForTests(dir);

                // The active write succeeded.
                var todayFile = Path.Combine(dir, $"lacerte-{DateTime.UtcNow:yyyyMMdd}.log");
                Assert.True(File.Exists(todayFile));
            }
            finally
            {
                WireLog.DisableForTests();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
