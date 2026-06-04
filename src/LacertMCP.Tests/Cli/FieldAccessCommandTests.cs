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
        /// P16-021: the universal-rule message must use the queried module letter
        /// (not the hard-coded `C` prefix that was wrong on non-C modules and read
        /// like a typo when the user queried module=I).
        /// </summary>
        [Fact]
        public void RunList_EmptyTable_UniversalRule_UsesQueriedModuleLetter()
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
            // must NOT contain the bare `C7_0` form — it should use the queried
            // module letter (N).
            if (output.Contains("Universal rule"))
            {
                Assert.Contains("N7_0", output);
                Assert.DoesNotContain("C7_0", output);
            }
        }
    }
}
