using System.Collections.Generic;
using System.IO;
using LacertMCP.Server.Session;
using LacertMCP.Server.Startup;
using Xunit;

namespace LacertMCP.Tests.Startup
{
    /// <summary>
    /// P15-033 — EnvValidator drives an env dictionary and writes warnings +
    /// a one-line banner to a supplied TextWriter. Tests are pure off-Windows:
    /// no env mutation, no host build, no Lacerte SDK.
    /// </summary>
    public class EnvValidatorTests
    {
        [Fact]
        public void BooleanFlag_Empty_NoWarning()
        {
            var sw = new StringWriter();
            var env = Env(("LACERTE_LIVE", ""));
            EnvValidator.Validate(sw, env);
            Assert.DoesNotContain("LACERTE_LIVE=", sw.ToString());
        }

        [Fact]
        public void BooleanFlag_Unset_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env());
            Assert.DoesNotContain("LACERTE_LIVE", sw.ToString());
            Assert.DoesNotContain("LACERTE_SIMULATION", sw.ToString());
        }

        [Fact]
        public void BooleanFlag_True_EmitsTypoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_LIVE", "true")));
            var output = sw.ToString();
            Assert.Contains("LACERTE_LIVE='true'", output);
            Assert.Contains("treated as disabled", output);
        }

        [Fact]
        public void BooleanFlag_PaddedOne_EmitsWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_DEBUG_WIRE", " 1")));
            var output = sw.ToString();
            Assert.Contains("LACERTE_DEBUG_WIRE=' 1'", output);
            Assert.Contains("treated as disabled", output);
        }

        [Fact]
        public void BooleanFlag_Zero_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_LIVE", "0")));
            // The banner mentions mode=, but there must be NO LACERTE_LIVE='0' warning line.
            Assert.DoesNotContain("LACERTE_LIVE='0'", sw.ToString());
        }

        [Fact]
        public void BooleanFlag_One_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_LIVE", "1")));
            Assert.DoesNotContain("LACERTE_LIVE='1'", sw.ToString());
        }

        [Fact]
        public void DefaultTaxYear_NonInt_EmitsWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_DEFAULT_TAX_YEAR", "twenty-twenty-four")));
            Assert.Contains("LACERTE_DEFAULT_TAX_YEAR='twenty-twenty-four'", sw.ToString());
            Assert.Contains("not an integer", sw.ToString());
        }

        [Fact]
        public void DefaultTaxYear_TooLow_EmitsWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_DEFAULT_TAX_YEAR", "1999")));
            Assert.Contains("LACERTE_DEFAULT_TAX_YEAR=1999", sw.ToString());
            Assert.Contains("outside", sw.ToString());
        }

        [Fact]
        public void DefaultTaxYear_Valid_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_DEFAULT_TAX_YEAR", "2023")));
            Assert.DoesNotContain("LACERTE_DEFAULT_TAX_YEAR=", warningLines(sw));
        }

        [Fact]
        public void DataRoot_NonexistentDir_EmitsWarning()
        {
            var sw = new StringWriter();
            var bogus = Path.Combine(Path.GetTempPath(), "lacertemcp-nonexistent-" + System.Guid.NewGuid().ToString("N"));
            EnvValidator.Validate(sw, Env(("LACERTE_DATA_ROOT", bogus)));
            Assert.Contains("LACERTE_DATA_ROOT='" + bogus + "'", sw.ToString());
            Assert.Contains("does not exist", sw.ToString());
        }

        [Fact]
        public void DataRoot_ExistingDir_NoWarning()
        {
            var sw = new StringWriter();
            var dir = Path.GetTempPath(); // always exists
            EnvValidator.Validate(sw, Env(("LACERTE_DATA_ROOT", dir)));
            Assert.DoesNotContain("does not exist", sw.ToString());
        }

        [Fact]
        public void DebugLogPath_WireDisabled_NoWarningEvenIfBogus()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(
                ("LACERTE_DEBUG_WIRE", "0"),
                ("LACERTE_DEBUG_LOG_PATH", "Z:\\not\\real\\path")));
            // Without wire enabled the path is moot — no warning.
            Assert.DoesNotContain("LACERTE_DEBUG_LOG_PATH", sw.ToString());
        }

        [Fact]
        public void Banner_AlwaysWrittenOnSingleLine()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env());
            var lines = sw.ToString().TrimEnd().Split('\n');
            // Exactly one [startup] line ending in the banner (no warnings on a clean env).
            Assert.Single(lines);
            var line = lines[0];
            Assert.Contains("[startup] LacertMCP", line);
            Assert.Contains("mode=", line);
            Assert.Contains("dataRoot=", line);
            Assert.Contains("defaultTaxYear=", line);
            Assert.Contains("debugWire=", line);
        }

        [Fact]
        public void Banner_ReportsSimulationWhenSimWins()
        {
            var sw = new StringWriter();
            // Both flags set — sim wins per the documented precedence.
            var mode = EnvValidator.Validate(sw, Env(
                ("LACERTE_LIVE", "1"),
                ("LACERTE_SIMULATION", "1")));
            Assert.Equal(SessionMode.Simulation, mode);
            Assert.Contains("mode=simulation", sw.ToString());
        }

        [Fact]
        public void Banner_ReportsUnsetAsAngleBrackets()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env());
            // Cleanest signal to the operator: <unset> not "" so empty strings
            // can be visually distinguished from missing values.
            Assert.Contains("dataRoot=<unset>", sw.ToString());
            Assert.Contains("defaultTaxYear=<unset>", sw.ToString());
        }

        private static IDictionary<string, string?> Env(params (string, string?)[] pairs)
        {
            var d = new Dictionary<string, string?>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }

        private static string warningLines(StringWriter sw)
        {
            // Strip the banner so a substring search for a var name only
            // matches warnings.
            var text = sw.ToString();
            var lines = text.Split('\n');
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                if (l.Contains("[startup] LacertMCP")) continue;
                sb.AppendLine(l);
            }
            return sb.ToString();
        }
    }
}
