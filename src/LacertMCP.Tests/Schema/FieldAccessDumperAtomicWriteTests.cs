using System;
using System.IO;
using System.Text;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    /// <summary>
    /// P18-007 — pins the contract that the FieldAccessDumper's atomic
    /// write-then-rename pattern, paired with the loader's defensive
    /// degrade-to-empty on malformed JSON, makes a crashed-mid-write
    /// dumper invisible to the loader at runtime.
    ///
    /// <para>
    /// The dumper itself is an OutputType=Exe project not referenced by
    /// the test assembly, so these tests reproduce the dumper's
    /// atomic-write recipe locally on a temp shard, then verify the
    /// loader's behavior against three crash points:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>kill BEFORE the tmp is written — canonical shard
    /// untouched, no orphan tmp;</description></item>
    /// <item><description>kill DURING the tmp write — tmp is a truncated
    /// blob on disk but the canonical shard remains intact (the loader
    /// only globs <c>*.json</c>, not <c>*.tmp</c>);</description></item>
    /// <item><description>kill AFTER tmp written + BEFORE Move — same as
    /// above: canonical shard intact, orphan tmp on disk. The dumper's
    /// next-run orphan-tmp sweep cleans it.</description></item>
    /// </list>
    ///
    /// <para>
    /// Lives in <see cref="AccessMapMutationCollection"/> because it
    /// mutates the loader caches + shard files on disk.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class FieldAccessDumperAtomicWriteTests : IDisposable
    {
        private readonly string _shardPath;
        private readonly string? _originalShard;
        private readonly bool _shardExisted;

        public FieldAccessDumperAtomicWriteTests()
        {
            var asmDir = Path.GetDirectoryName(typeof(FieldAccessMapLoader).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            _shardPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "base", "individual.json");

            if (File.Exists(_shardPath))
            {
                _shardExisted = true;
                _originalShard = File.ReadAllText(_shardPath);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_shardPath)!);

            FieldAccessMapLoader.ReloadCaches();
        }

        public void Dispose()
        {
            if (_shardExisted && _originalShard != null)
            {
                File.WriteAllText(_shardPath, _originalShard);
            }
            else
            {
                File.WriteAllText(_shardPath, "{}\n");
            }

            // Clean up any orphan tmp left behind by a test.
            var tmp = _shardPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);

            FieldAccessMapLoader.ReloadCaches();
        }

        // ============================================================
        // P18-007 — atomic write contract
        // ============================================================

        [Fact]
        public void AtomicWrite_KillBeforeMove_LeavesCanonicalShardIntact()
        {
            // Setup: a known-good canonical shard with one entry.
            File.WriteAllText(_shardPath,
                "{ \"I|1|C1_PRIOR\": { \"access\": \"ReadWrite\", \"source\": \"prior-run\" } }");
            FieldAccessMapLoader.ReloadCaches();
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_PRIOR", 2024));

            // Mid-write: the dumper has written the tmp blob but the
            // OS killed the process before it could Move it over the
            // canonical. Simulate by writing a tmp directly.
            var tmpPath = _shardPath + ".tmp";
            File.WriteAllText(tmpPath,
                "{ \"I|1|C1_NEW\": { \"access\": \"ReadWrite\", \"source\": \"new-run\" } }");

            // The loader's next call should see the OLD canonical, not
            // the staged tmp — proves the atomicity contract: a kill in
            // the staging window is observationally a no-op.
            FieldAccessMapLoader.ReloadCaches();
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_PRIOR", 2024));
            Assert.Equal(FieldAccess.Unknown,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NEW", 2024));
        }

        [Fact]
        public void AtomicWrite_KillDuringTmpWrite_LeavesCanonicalShardIntact_AndLoaderUnpoisoned()
        {
            // Setup: known-good canonical shard.
            File.WriteAllText(_shardPath,
                "{ \"I|1|C1_PRIOR\": { \"access\": \"ReadWrite\", \"source\": \"prior-run\" } }");
            FieldAccessMapLoader.ReloadCaches();

            // Kill the dumper partway through writing the tmp blob —
            // tmp is a truncated/garbled JSON fragment on disk.
            var tmpPath = _shardPath + ".tmp";
            File.WriteAllBytes(tmpPath, Encoding.UTF8.GetBytes("{ \"I|1|C1_NEW\": { \"access\": \"Read"));

            // The loader still sees the prior canonical and lookups
            // continue to work normally.
            FieldAccessMapLoader.ReloadCaches();
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_PRIOR", 2024));
        }

        [Fact]
        public void AtomicWrite_SuccessPath_ReplacesCanonical_AndCleansUpTmp()
        {
            // Setup: prior canonical.
            File.WriteAllText(_shardPath,
                "{ \"I|1|C1_PRIOR\": { \"access\": \"ReadWrite\", \"source\": \"prior-run\" } }");
            FieldAccessMapLoader.ReloadCaches();

            // Exercise the atomic-write recipe (mirrors the dumper).
            var tmpPath = _shardPath + ".tmp";
            File.WriteAllText(tmpPath,
                "{ \"I|1|C1_NEW\": { \"access\": \"ReadWrite\", \"source\": \"new-run\" } }");
            File.Delete(_shardPath);
            File.Move(tmpPath, _shardPath);

            // No orphan tmp left behind.
            Assert.False(File.Exists(tmpPath));

            // Loader picks up the new shard after reload.
            FieldAccessMapLoader.ReloadCaches();
            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_NEW", 2024));
            Assert.Equal(FieldAccess.Unknown,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_PRIOR", 2024));
        }

        [Fact]
        public void LoaderGlobOnly_Reads_Json_Not_TmpFiles()
        {
            // The loader's LoadModule reads exactly `{module}.json`. An
            // orphan `{module}.json.tmp` next to it must be invisible —
            // even if the tmp is well-formed JSON with entries that
            // contradict the canonical.
            File.WriteAllText(_shardPath,
                "{ \"I|1|C1_CANONICAL\": { \"access\": \"ReadWrite\", \"source\": \"canonical\" } }");
            File.WriteAllText(_shardPath + ".tmp",
                "{ \"I|1|C1_GHOST\": { \"access\": \"ReadWrite\", \"source\": \"ghost\" } }");
            FieldAccessMapLoader.ReloadCaches();

            Assert.Equal(FieldAccess.ReadWrite,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_CANONICAL", 2024));
            // Ghost from the tmp must NOT be visible.
            Assert.Equal(FieldAccess.Unknown,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_GHOST", 2024));
        }
    }
}
