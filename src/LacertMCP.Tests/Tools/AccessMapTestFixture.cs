using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using LacertMCP.Server.Schema;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Test-only helper that injects a writable field entry into the
    /// individual.json shard at the location the loader reads from
    /// (next to the running LacertMCP.Server.dll in the test bin).
    /// Resets the loader's per-process caches via reflection on both
    /// sides of the operation so tests are deterministic.
    ///
    /// <para>
    /// v1 shipped shards are all <c>{}</c> — every cataloged field
    /// returns <see cref="FieldAccess.Unknown"/> → AssertWritable
    /// throws 9001. To exercise the write happy-path we need at least
    /// one entry that resolves to <see cref="FieldAccess.ReadWrite"/>.
    /// This fixture seeds <c>I|1|C1_15</c> as ReadWrite for the duration
    /// of the test and restores the original file content on dispose.
    /// </para>
    /// </summary>
    public sealed class AccessMapTestFixture : IDisposable
    {
        /// <summary>The field we inject. Tests assert against this.</summary>
        public const string WritableField = "C1_15";
        public const char Module = 'I';
        public const int Table = 1;

        private readonly string _shardPath;
        private readonly string? _originalContent;
        private readonly bool _shardExisted;

        public AccessMapTestFixture()
        {
            var asmDir = Path.GetDirectoryName(typeof(FieldAccessMapLoader).Assembly.Location)
                         ?? Directory.GetCurrentDirectory();
            _shardPath = Path.Combine(asmDir, "Schema", "FieldAccessMap", "base", "individual.json");

            // Save the original so we can restore on dispose.
            if (File.Exists(_shardPath))
            {
                _shardExisted = true;
                _originalContent = File.ReadAllText(_shardPath);
            }

            // Ensure directory exists; write a shard that catalogs C1_15 as
            // ReadWrite. Keep the JSON shape consistent with what the loader
            // expects (flat keyed map of FieldEntry records).
            Directory.CreateDirectory(Path.GetDirectoryName(_shardPath)!);
            File.WriteAllText(_shardPath, "{\n  \"I|1|" + WritableField + "\": { \"access\": \"ReadWrite\", \"source\": \"test-fixture\" }\n}\n");

            ResetLoaderCache();
        }

        public void Dispose()
        {
            // Restore original content; if the shard didn't exist, write {}
            // back (v1 ships {} so this matches the on-disk default).
            if (_shardExisted && _originalContent != null)
            {
                File.WriteAllText(_shardPath, _originalContent);
            }
            else
            {
                File.WriteAllText(_shardPath, "{}\n");
            }
            ResetLoaderCache();
        }

        private static void ResetLoaderCache()
        {
            // P18-020 — cache state moved into a single immutable
            // AccessMapSnapshot swapped atomically by ReloadCaches; call it to
            // install a fresh empty snapshot (semantically equivalent to
            // clearing the two dictionaries from the previous design).
            FieldAccessMapLoader.ReloadCaches();
        }
    }
}
