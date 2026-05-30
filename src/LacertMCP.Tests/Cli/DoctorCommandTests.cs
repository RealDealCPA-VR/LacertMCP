using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LacertMCP.Cli.Commands;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Doctor exit-code contract (mirrors QB MCP <c>cli/doctor.ts</c>): 0 green / 1 red / 2 skipped.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class DoctorCommandTests
    {
        // P14-071 helpers: build synthetic ProbeResult lists for pinning ComputeExitCode.
        private static DoctorCommand.ProbeResult Pass(string n = "p") =>
            new DoctorCommand.ProbeResult(n, DoctorCommand.ProbeStatus.Pass, "ok");
        private static DoctorCommand.ProbeResult Fail(string n = "p") =>
            new DoctorCommand.ProbeResult(n, DoctorCommand.ProbeStatus.Fail, "bad", "fix it");
        private static DoctorCommand.ProbeResult Skip(string n = "p") =>
            new DoctorCommand.ProbeResult(n, DoctorCommand.ProbeStatus.Skipped, "skipped: x");
        private static DoctorCommand.ProbeResult Warn(string n = "p") =>
            new DoctorCommand.ProbeResult(n, DoctorCommand.ProbeStatus.Warn, "warn");

        /// <summary>
        /// P14-071: all-Pass results → exit 0. Pins the green-path contract.
        /// </summary>
        [Fact]
        public void ComputeExitCode_AllPass_Returns0()
        {
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Pass("b"), Pass("c") };
            Assert.Equal(0, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// P14-071: a Warn alongside Passes still counts as green (Warn is advisory).
        /// </summary>
        [Fact]
        public void ComputeExitCode_PassesPlusWarn_Returns0()
        {
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Warn("b"), Pass("c") };
            Assert.Equal(0, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// P14-071: at least one Fail → exit 1. Pins the red-path contract.
        /// </summary>
        [Fact]
        public void ComputeExitCode_OneFail_Returns1()
        {
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Fail("b"), Pass("c") };
            Assert.Equal(1, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// P14-071: at least one Skipped, no Fails → exit 2. Pins the partial-path contract.
        /// </summary>
        [Fact]
        public void ComputeExitCode_OneSkipNoFail_Returns2()
        {
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Skip("b"), Pass("c") };
            Assert.Equal(2, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// P14-071: Fail trumps Skip — a red probe always wins over a skipped one,
        /// even when both are present. Confirms the precedence ordering.
        /// </summary>
        [Fact]
        public void ComputeExitCode_FailTrumpsSkip_Returns1()
        {
            var results = new List<DoctorCommand.ProbeResult> { Skip("a"), Fail("b"), Pass("c") };
            Assert.Equal(1, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// P14-071: defensive — empty results = no failures, no skips = green path.
        /// </summary>
        [Fact]
        public void ComputeExitCode_Empty_Returns0()
        {
            var results = new List<DoctorCommand.ProbeResult>();
            Assert.Equal(0, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// P14-071: null guard — argument validation on the pure function.
        /// </summary>
        [Fact]
        public void ComputeExitCode_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => DoctorCommand.ComputeExitCode(null!));
        }

        [Fact]
        public void Doctor_Run_Emits_Output_And_Returns_Valid_Exit_Code()
        {
            var sw = new StringWriter();
            var exit = DoctorCommand.Run(sw);

            // 0, 1, or 2 — every other code is a contract violation.
            Assert.InRange(exit, 0, 2);

            var output = sw.ToString();
            Assert.Contains("lacerte-mcp doctor", output);
            Assert.Contains("Result:", output);
        }

        [Fact]
        public void Doctor_NonWindows_Marks_Windows_Probes_As_Skipped()
        {
            // This test is meaningful only on the dev box (which today is non-Windows for the
            // build pipeline, Windows for the operator). On the operator's Windows box every
            // probe runs for real — so we soften the assertion to "either skipped (off-Windows)
            // OR pass/fail (on-Windows), but never throws".
            var results = DoctorCommand.RunAllProbes();

            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.Name)));
            Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.Detail)));

            // The "Windows version" probe is the sentinel for the OS branch.
            var win = results.FirstOrDefault(r => r.Name == "Windows version");
            Assert.NotNull(win);

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Assert.Equal(DoctorCommand.ProbeStatus.Pass, win!.Status);
            }
            else
            {
                Assert.Equal(DoctorCommand.ProbeStatus.Skipped, win!.Status);
                Assert.Contains("not on Windows", win.Detail);
            }
        }

        [Fact]
        public void Doctor_NonWindows_Returns_Exit_Code_1_Or_2()
        {
            // Off-Windows we should see at least one Skipped, possibly also a Fail (no AppID stored).
            // Either way exit code must be 1 (fail) or 2 (skip) — never 0.
            // On Windows with a clean install all probes might pass → exit 0. Allow that.
            var sw = new StringWriter();
            var exit = DoctorCommand.Run(sw);

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.InRange(exit, 1, 2);
                var output = sw.ToString();
                // Mention "skipped" somewhere so the operator knows what happened.
                Assert.True(
                    output.Contains("skipped:") || output.Contains("Skipped") || output.Contains("PARTIAL"),
                    "Off-Windows doctor output should clearly mention skipped probes. Got:\n" + output);
            }
            else
            {
                Assert.InRange(exit, 0, 2);
            }
        }
    }
}
