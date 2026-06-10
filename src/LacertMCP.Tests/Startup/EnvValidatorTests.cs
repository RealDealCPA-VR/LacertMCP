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

        // ----- R6-30(a): LACERTE_PARITY_RECORDING is a path, not a boolean -----

        [Fact]
        public void ParityRecording_PathValue_NoBooleanTypoWarning()
        {
            // Pre-R6-30 the boolean check warned "'<path>' is not '1'" on every
            // documented usage. A path value must never trigger the strict-"1"
            // boolean warning.
            var sw = new StringWriter();
            var bogus = Path.Combine(Path.GetTempPath(), "lacertemcp-recording-" + System.Guid.NewGuid().ToString("N") + ".json");
            EnvValidator.Validate(sw, Env(("LACERTE_PARITY_RECORDING", bogus)));
            Assert.DoesNotContain("treated as disabled", sw.ToString());
        }

        [Fact]
        public void ParityRecording_MissingFile_WarnsFileDoesNotExist()
        {
            var sw = new StringWriter();
            var bogus = Path.Combine(Path.GetTempPath(), "lacertemcp-recording-" + System.Guid.NewGuid().ToString("N") + ".json");
            EnvValidator.Validate(sw, Env(("LACERTE_PARITY_RECORDING", bogus)));
            Assert.Contains("LACERTE_PARITY_RECORDING='" + bogus + "'", sw.ToString());
            Assert.Contains("does not exist", sw.ToString());
        }

        [Fact]
        public void ParityRecording_ExistingFile_NoWarning()
        {
            var sw = new StringWriter();
            var path = Path.GetTempFileName();
            try
            {
                EnvValidator.Validate(sw, Env(("LACERTE_PARITY_RECORDING", path)));
                Assert.DoesNotContain("LACERTE_PARITY_RECORDING", sw.ToString());
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        // ----- R6-30(b): LACERTE_IDEMPOTENCY_PERSIST is default-ON / disable-with-0 -----

        [Fact]
        public void IdempotencyPersist_Zero_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_IDEMPOTENCY_PERSIST", "0")));
            Assert.DoesNotContain("LACERTE_IDEMPOTENCY_PERSIST", sw.ToString());
        }

        [Fact]
        public void IdempotencyPersist_One_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_IDEMPOTENCY_PERSIST", "1")));
            Assert.DoesNotContain("LACERTE_IDEMPOTENCY_PERSIST", sw.ToString());
        }

        [Fact]
        public void IdempotencyPersist_False_WarnsDefaultOnSemantics()
        {
            // "false" does NOT disable a default-ON flag — only the exact
            // string "0" does. The warning must say the value leaves the
            // feature ENABLED (the strict-boolean "treated as disabled" text
            // would be exactly backwards here).
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_IDEMPOTENCY_PERSIST", "false")));
            var output = sw.ToString();
            Assert.Contains("LACERTE_IDEMPOTENCY_PERSIST='false'", output);
            Assert.Contains("defaults to ON", output);
            Assert.Contains("ENABLED", output);
            Assert.DoesNotContain("treated as disabled", output);
        }

        // ----- R6-30(b): LACERTE_DEBUG_LOG_FULL_RESULTS is a strict-"1" flag -----

        [Fact]
        public void DebugLogFullResults_True_EmitsTypoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_DEBUG_LOG_FULL_RESULTS", "true")));
            var output = sw.ToString();
            Assert.Contains("LACERTE_DEBUG_LOG_FULL_RESULTS='true'", output);
            Assert.Contains("treated as disabled", output);
        }

        [Fact]
        public void DebugLogFullResults_One_NoWarning()
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env(("LACERTE_DEBUG_LOG_FULL_RESULTS", "1")));
            Assert.DoesNotContain("LACERTE_DEBUG_LOG_FULL_RESULTS", sw.ToString());
        }

        // ----- R6-30(c): numeric env vars warn when not a non-negative integer -----

        [Theory]
        [InlineData("LACERTE_HEARTBEAT_SECONDS")]
        [InlineData("LACERTE_BULK_WRITE_MAX")]
        [InlineData("LACERTE_RECENT_LOG_CAPACITY")]
        [InlineData("LACERTE_DEBUG_LOG_MAX_BYTES")]
        [InlineData("LACERTE_DEBUG_LOG_MAX_DIR_BYTES")]
        [InlineData("LACERTE_DEBUG_LOG_RETENTION_DAYS")]
        [InlineData("LACERTE_DEBUG_LOG_RESULT_SAMPLE")]
        public void NumericVar_NonNumeric_EmitsWarning(string name)
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env((name, "thirty")));
            var output = sw.ToString();
            Assert.Contains($"{name}='thirty'", output);
            Assert.Contains("not a non-negative integer", output);
        }

        [Theory]
        [InlineData("LACERTE_HEARTBEAT_SECONDS")]
        [InlineData("LACERTE_DEBUG_LOG_MAX_BYTES")]
        public void NumericVar_Negative_EmitsWarning(string name)
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env((name, "-5")));
            Assert.Contains("not a non-negative integer", sw.ToString());
        }

        [Theory]
        [InlineData("LACERTE_HEARTBEAT_SECONDS", "0")]
        [InlineData("LACERTE_HEARTBEAT_SECONDS", "30")]
        [InlineData("LACERTE_BULK_WRITE_MAX", "500")]
        [InlineData("LACERTE_DEBUG_LOG_MAX_BYTES", "10485760")]
        public void NumericVar_ValidNonNegative_NoWarning(string name, string value)
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, Env((name, value)));
            Assert.DoesNotContain(name, warningLines(sw));
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
