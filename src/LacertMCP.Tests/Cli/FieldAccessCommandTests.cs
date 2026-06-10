using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using LacertMCP.Cli.Commands;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Pins the P15-029 (discover redirect + exit 3) and P15-067 (stderr routing on
    /// list / set) contracts so a future refactor that re-introduces stdout-routed
    /// error messages or flips the discover exit code back to 2 is caught at the
    /// test boundary.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class FieldAccessCommandTests
    {
        [Fact]
        public async Task Discover_PrintsRedirectAndExits3()
        {
            // P15-029: discover is now an honest stub that redirects to
            // LacertMCP.FieldAccessDumper.exe and exits 3 (not-implemented),
            // distinct from doctor's exit 2 (environment-skip).
            // The discover handler writes via static Console.Out (not the parser's
            // IConsole) so we redirect Console.Out for the test rather than using
            // a TestConsole.
            var cmd = FieldAccessCommand.Build();
            var originalOut = Console.Out;
            var sw = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(sw);
                exit = await cmd.InvokeAsync(new[] { "discover" });
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Equal(3, exit);
            var stdout = sw.ToString();
            Assert.Contains("LacertMCP.FieldAccessDumper.exe", stdout);
            Assert.Contains("not exposed", stdout);
            // R6-39(a): the redirect must advertise the dumper's REAL flags
            // (--overwrite/--live/--year/--out per FieldAccessDumper/Program.cs
            // ParseArgs) — the old hint named --module/--table, which the
            // dumper rejects (and now exits 1 on).
            Assert.Contains("--overwrite", stdout);
            Assert.Contains("--live", stdout);
            Assert.Contains("--year", stdout);
            Assert.Contains("--out", stdout);
            Assert.DoesNotContain("--module <I|C|P|S|F|R|T|N|B>", stdout);
            Assert.DoesNotContain("--table <1..7>", stdout);
        }

        [Fact]
        public void RunList_UnknownModule_RoutesErrorToStderr()
        {
            // P15-067: error messages must flow through the stderr TextWriter, not
            // the static Console.Error — otherwise tests that capture only stdout
            // silently miss the error.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = FieldAccessCommand.RunList(module: 'Z', table: 1, year: 2024, stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout.ToString());
            Assert.Contains("Unknown module", stderr.ToString());
        }

        [Fact]
        public void RunSet_InvalidAccess_RoutesErrorToStderr()
        {
            // P15-067 (set path): both the "Unknown module" and "Invalid --access"
            // branches must use the stderr writer. Use a known-good module + a
            // field name matching the P16-021 shape regex (<letter><table>_<n>) so
            // we hit the access-parse failure, not the field-shape or module guards.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = FieldAccessCommand.RunSet(
                module: 'I', table: 1, field: "I1_42",
                accessRaw: "NotAnAccessLevel",
                dataType: null, maxLength: null, source: "manual", note: null,
                stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout.ToString());
            Assert.Contains("Invalid --access", stderr.ToString());
        }

        [Fact]
        public void RunSet_UnknownModule_RoutesErrorToStderr()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = FieldAccessCommand.RunSet(
                module: 'Z', table: 1, field: "X",
                accessRaw: "ReadWrite",
                dataType: null, maxLength: null, source: "manual", note: null,
                stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout.ToString());
            Assert.Contains("Unknown module", stderr.ToString());
        }

        // ----- P16-021: input validation -----

        /// <summary>
        /// P16-021: <c>list --table 99</c> previously cheerfully printed
        /// <c>Universal rule: C99_0 → ReadOnly</c> for a table that doesn't exist.
        /// Now exits 1 with a stderr message naming the valid range so a typo can't
        /// silently mislead the operator.
        /// </summary>
        [Fact]
        public void RunList_TableOutOfRange_PrintsErrorAndExitsOne()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = FieldAccessCommand.RunList(module: 'I', table: 99, year: 2024, stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout.ToString());
            Assert.Contains("--table must be in 1..7", stderr.ToString());
        }

        /// <summary>
        /// P16-021: <c>set --table 99</c> previously polluted the on-disk shard with
        /// a key the loader couldn't read. Now exits 1 before any mutation.
        /// </summary>
        [Fact]
        public void RunSet_TableOutOfRange_DoesNotMutateShard()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            // Capture shard mtime before/after to confirm the write did NOT happen.
            var shardPath = FieldAccessCommand.ShardPath(Modules.FileBasenameFor('I')!);
            var before = File.Exists(shardPath) ? File.GetLastWriteTimeUtc(shardPath) : (DateTime?)null;

            var exit = FieldAccessCommand.RunSet(
                module: 'I', table: 99, field: "I1_42",
                accessRaw: "ReadWrite",
                dataType: null, maxLength: null, source: "manual", note: null,
                stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Contains("--table must be in 1..7", stderr.ToString());
            var after = File.Exists(shardPath) ? File.GetLastWriteTimeUtc(shardPath) : (DateTime?)null;
            Assert.Equal(before, after);
        }

        /// <summary>
        /// P16-021: <c>set --field oops_typo</c> (no leading letter+digit) is
        /// rejected unless <c>--force</c> is passed. With --force the shape gate
        /// is bypassed (escape hatch for legacy imports).
        /// </summary>
        [Fact]
        public void RunSet_FieldShapeMismatch_RejectsWithoutForce()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = FieldAccessCommand.RunSet(
                module: 'I', table: 1, field: "oops_typo",
                accessRaw: "ReadWrite",
                dataType: null, maxLength: null, source: "manual", note: null,
                force: false, stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout.ToString());
            Assert.Contains("does not match the <letter><table>_<n>", stderr.ToString());
            Assert.Contains("--force", stderr.ToString());
        }

        /// <summary>
        /// R6-28(b): the universal rule is ONLY the exact <c>C{table}_0</c>
        /// ClientNumber field (the literal letter C on EVERY module — see
        /// FieldAccessMapLoader.IsClientNumberField). The prior message printed
        /// <c>{module}{table}_0</c> (e.g. N7_0, which the loader classifies
        /// Unknown, not ReadOnly) and claimed "any field ending in _0" — both
        /// wrong. Supersedes the P16-021 pin that asserted the broken form.
        /// </summary>
        [Fact]
        public void RunList_EmptyTable_UniversalRule_NamesClientNumberFieldOnly()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            // Pick a module/table pair that's likely to have zero entries in the
            // shipped base shard (P/T/R/N/B modules ship empty in v0.1.x); fall
            // through to the universal-rule message.
            var exit = FieldAccessCommand.RunList(module: 'N', table: 7, year: 2024, stdout, stderr);

            Assert.Equal(0, exit);
            var output = stdout.ToString();
            // Either the module had no entries (universal-rule branch) OR it had
            // entries (header branch). The universal-rule line, when emitted,
            // must name the literal C7_0 (ClientNumber) — NOT the queried module
            // letter form N7_0 (Unknown access, not ReadOnly) — and must NOT
            // claim that any field ending in _0 is covered.
            if (output.Contains("Universal rule"))
            {
                Assert.Contains("C7_0", output);
                Assert.DoesNotContain("N7_0", output);
                Assert.DoesNotContain("any field ending in _0", output);
            }
        }

        // ----- R6-28(a): uppercase normalization on set -----

        /// <summary>
        /// R6-28(a): <c>set --field c1_5</c> previously wrote a lowercase shard
        /// key the loader (which uppercases field names on lookup) could never
        /// read — a permanently dead row. RunSet must uppercase-normalize the
        /// module char and field name before writing. Lowercase module input is
        /// normalized too (Modules.GetByChar is case-sensitive).
        /// </summary>
        [Fact]
        public void RunSet_LowercaseModuleAndField_NormalizedToUppercaseShardKey()
        {
            var shardPath = FieldAccessCommand.ShardPath(Modules.FileBasenameFor('B')!);
            var hadShard = File.Exists(shardPath);
            var originalJson = hadShard ? File.ReadAllText(shardPath) : null;
            try
            {
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                var exit = FieldAccessCommand.RunSet(
                    module: 'b', table: 7, field: "b7_123",
                    accessRaw: "ReadWrite",
                    dataType: null, maxLength: null, source: "manual", note: null,
                    force: false, stdout, stderr);

                Assert.Equal(0, exit);
                var json = File.ReadAllText(shardPath);
                // Key must be fully uppercase — the loader's lookup shape.
                Assert.Contains("B|7|B7_123", json);
                Assert.DoesNotContain("b7_123", json);
                Assert.DoesNotContain("b|7|", json);
                // The operator-facing diff echo must show the normalized key too.
                Assert.Contains("B|7|B7_123", stdout.ToString());
            }
            finally
            {
                // Restore the shard so this test never pollutes sibling tests
                // (the CLI and loader share the test bin's Schema tree), and
                // drop the loader's in-memory cache so no other test sees the
                // transient B7_123 entry.
                if (hadShard) File.WriteAllText(shardPath, originalJson!);
                else if (File.Exists(shardPath)) File.Delete(shardPath);
                FieldAccessMapLoader.ResetCacheForTests();
            }
        }
    }
}
