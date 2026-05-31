using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using LacertMCP.Cli.Commands;
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
            // branches must use the stderr writer. Use a known-good module so we hit
            // the access-parse failure, not the module-validation failure.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = FieldAccessCommand.RunSet(
                module: 'I', table: 1, field: "TEST_FIELD",
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
    }
}
