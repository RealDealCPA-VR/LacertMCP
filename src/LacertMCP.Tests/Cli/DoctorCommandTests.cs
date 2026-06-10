using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private static DoctorCommand.ProbeResult Warn(string n = "p", string? remediation = null) =>
            new DoctorCommand.ProbeResult(n, DoctorCommand.ProbeStatus.Warn, "warn", remediation);

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
        /// R6-29: a Warn alongside Passes lands in the PARTIAL bucket (exit 2).
        /// Previously a warn-only run exited 0 "all probes green" with visible
        /// ⚠ rows, so scripts gating on the exit code never saw advisory
        /// problems. This also makes the in-code comment in ProbeAppId
        /// ("Warn keeps exit code in the PARTIAL bucket (2)") true.
        /// </summary>
        [Fact]
        public void ComputeExitCode_PassesPlusWarn_Returns2()
        {
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Warn("b"), Pass("c") };
            Assert.Equal(2, DoctorCommand.ComputeExitCode(results));
        }

        /// <summary>
        /// R6-29: Fail still trumps Warn — a red probe wins over an advisory one.
        /// </summary>
        [Fact]
        public void ComputeExitCode_FailTrumpsWarn_Returns1()
        {
            var results = new List<DoctorCommand.ProbeResult> { Warn("a"), Fail("b"), Pass("c") };
            Assert.Equal(1, DoctorCommand.ComputeExitCode(results));
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

        // ----- P15-027: --json contract -----

        [Fact]
        public void Doctor_Json_EmitsParseableObject()
        {
            var sw = new StringWriter();
            var exit = DoctorCommand.Run(sw, asJson: true);

            using var doc = JsonDocument.Parse(sw.ToString());
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.True(root.TryGetProperty("exitCode", out var exitProp));
            Assert.True(root.TryGetProperty("probes", out var probes));
            Assert.Equal(JsonValueKind.Array, probes.ValueKind);
            Assert.True(probes.GetArrayLength() > 0, "Expected at least one probe in the JSON payload.");
            Assert.Equal(exit, exitProp.GetInt32());
        }

        [Fact]
        public void Doctor_Json_StatusSerializesAsString()
        {
            // P15-027: PropertyNamingPolicy.CamelCase does NOT cover enum values —
            // the JsonStringEnumConverter is what keeps Status as "pass"/"warn"/"fail"
            // instead of the int 0/1/2/3. This test guards the enum converter.
            var sw = new StringWriter();
            DoctorCommand.Run(sw, asJson: true);

            using var doc = JsonDocument.Parse(sw.ToString());
            var probes = doc.RootElement.GetProperty("probes");
            Assert.True(probes.GetArrayLength() > 0);

            var first = probes[0];
            var status = first.GetProperty("status");
            Assert.Equal(JsonValueKind.String, status.ValueKind);
            // Should be lowercase per JsonNamingPolicy.CamelCase passed to the converter.
            var s = status.GetString();
            Assert.Contains(s, new[] { "pass", "warn", "fail", "skipped" });
        }

        [Fact]
        public void Doctor_Json_ExitCodeMatchesReturnedInt()
        {
            var sw = new StringWriter();
            var returned = DoctorCommand.Run(sw, asJson: true);

            using var doc = JsonDocument.Parse(sw.ToString());
            var inJson = doc.RootElement.GetProperty("exitCode").GetInt32();
            Assert.Equal(returned, inJson);
        }

        // ----- P15-031: expanded probe surface -----

        [Fact]
        public void Doctor_Probes_Includes_OperatingMode_Probe()
        {
            var results = DoctorCommand.RunAllProbes();
            var mode = results.FirstOrDefault(r => r.Name == "Operating mode");
            Assert.NotNull(mode);
            Assert.Contains("resolved:", mode!.Detail);
        }

        [Fact]
        public void Doctor_Probes_Includes_FieldAccessMap_Shards()
        {
            var results = DoctorCommand.RunAllProbes();
            var shards = results.FirstOrDefault(r => r.Name == "FieldAccessMap shards");
            Assert.NotNull(shards);
        }

        /// <summary>
        /// P15-034: the FieldAccessMap shards probe is wired through the schema-side
        /// <c>FieldAccessMapLoader.GetShardCounts</c> + <c>GetSchemaRoot</c> helpers
        /// so doctor and the loader can never drift. The probe must surface (a) the
        /// resolved schema-root path (so the operator can confirm where the loader is
        /// looking) and (b) either Pass (modules &gt; 0) or Warn (modules == 0) — never
        /// Fail under normal conditions (Fail is reserved for probe exceptions).
        /// </summary>
        [Fact]
        public void Doctor_FieldAccessMapShards_UsesLoaderHelpers_And_NeverHardFails()
        {
            var results = DoctorCommand.RunAllProbes();
            var shards = results.FirstOrDefault(r => r.Name == "FieldAccessMap shards");
            Assert.NotNull(shards);

            // Never a hard Fail under normal conditions (v1 ships empty shards by
            // design; the probe Warns instead so green-doctor stays achievable).
            Assert.NotEqual(DoctorCommand.ProbeStatus.Fail, shards!.Status);
            Assert.True(
                shards.Status == DoctorCommand.ProbeStatus.Pass ||
                shards.Status == DoctorCommand.ProbeStatus.Warn,
                $"Expected Pass or Warn for FieldAccessMap shards; got {shards.Status}: {shards.Detail}");

            // Detail must include the resolved schema-root path (the whole point of
            // wiring through GetSchemaRoot — the operator should be able to paste the
            // doctor output into a bug report and see where the loader was looking).
            var expectedRoot = LacertMCP.Server.Schema.FieldAccessMapLoader.GetSchemaRoot();
            Assert.Contains(expectedRoot, shards.Detail);
        }

        [Fact]
        public void Doctor_Probes_Includes_DataPath_And_DataRoot_Env()
        {
            var results = DoctorCommand.RunAllProbes();
            Assert.Contains(results, r => r.Name == "LACERTE_DATA_PATH env");
            Assert.Contains(results, r => r.Name == "LACERTE_DATA_ROOT env");
        }

        [Fact]
        public void Doctor_Probes_Includes_LacerteComProgId()
        {
            var results = DoctorCommand.RunAllProbes();
            var progId = results.FirstOrDefault(r => r.Name == "Lacerte COM ProgID");
            Assert.NotNull(progId);
            // Off-Windows: must Skip. On Windows: Pass or Fail (depending on SDK install).
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.Equal(DoctorCommand.ProbeStatus.Skipped, progId!.Status);
            }
        }

        [Fact]
        public void Doctor_DataPath_DirectoryProbe_Fails_When_Path_Missing()
        {
            // Set LACERTE_DATA_PATH to a guaranteed-missing path and assert the
            // directory-existence probe flips to Fail. Snapshot + restore.
            var saved = Environment.GetEnvironmentVariable("LACERTE_DATA_PATH");
            try
            {
                var ghost = Path.Combine(Path.GetTempPath(), "lacerte-mcp-ghost-" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("LACERTE_DATA_PATH", ghost);

                var results = DoctorCommand.RunAllProbes();
                var dirProbe = results.FirstOrDefault(r => r.Name == "LACERTE_DATA_PATH directory");
                Assert.NotNull(dirProbe);
                Assert.Equal(DoctorCommand.ProbeStatus.Fail, dirProbe!.Status);
                Assert.Contains("does not exist", dirProbe.Detail);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LACERTE_DATA_PATH", saved);
            }
        }

        // ----- P15-065: ASCII glyph fallback -----

        [Fact]
        public void Doctor_ProbeResult_Glyph_Is_NonEmpty_String()
        {
            var p = new DoctorCommand.ProbeResult("test", DoctorCommand.ProbeStatus.Pass, "ok");
            var glyph = p.Glyph;
            Assert.False(string.IsNullOrEmpty(glyph));
        }

        [Fact]
        public void Doctor_ProbeResult_Glyph_FallsBackToAscii_OnLegacyEncoding()
        {
            // Snapshot the current encoding, force CP437 (legacy OEM US), assert the
            // glyph returns ASCII tags; then restore. This pins the auto-detect logic.
            var saved = Console.OutputEncoding;
            try
            {
                try { Console.OutputEncoding = System.Text.Encoding.GetEncoding(437); }
                catch { return; /* code page unavailable on this host — skip */ }

                var pass = new DoctorCommand.ProbeResult("p", DoctorCommand.ProbeStatus.Pass, "ok").Glyph;
                var warn = new DoctorCommand.ProbeResult("p", DoctorCommand.ProbeStatus.Warn, "ok").Glyph;
                var fail = new DoctorCommand.ProbeResult("p", DoctorCommand.ProbeStatus.Fail, "ok").Glyph;
                var skipped = new DoctorCommand.ProbeResult("p", DoctorCommand.ProbeStatus.Skipped, "ok").Glyph;

                foreach (var g in new[] { pass, warn, fail, skipped })
                {
                    // No char above ASCII tilde — guarantees no mojibake on CP437.
                    foreach (var c in g)
                    {
                        Assert.True(c <= 0x7E, $"Glyph '{g}' contains non-ASCII char 0x{(int)c:X4} on CP437.");
                    }
                }
                Assert.Equal("[OK]", pass);
                Assert.Equal("[WARN]", warn);
                Assert.Equal("[FAIL]", fail);
                Assert.Equal("[--]", skipped);
            }
            finally
            {
                try { Console.OutputEncoding = saved; } catch { }
            }
        }

        // ----- P15-032: App.config shim probe -----

        /// <summary>
        /// P15-032: the App.config shim probe must appear in the standard probe
        /// list so the doctor command exposes it under the canonical name.
        /// </summary>
        [Fact]
        public void Doctor_Probes_Includes_AppConfigShim()
        {
            var results = DoctorCommand.RunAllProbes();
            var shim = results.FirstOrDefault(r => r.Name == "App.config shim");
            Assert.NotNull(shim);
        }

        /// <summary>
        /// P15-032: missing file → Fail with a remediation that names the canonical
        /// fix path so the operator knows exactly where the file should live.
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_MissingFile_Fails_WithRemediation()
        {
            var ghost = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-ghost-" + Guid.NewGuid().ToString("N") + ".exe.config");

            var result = DoctorCommand.ProbeAppConfigShimAt(ghost);

            Assert.Equal(DoctorCommand.ProbeStatus.Fail, result.Status);
            Assert.Equal("App.config shim", result.Name);
            Assert.Contains("not found", result.Detail);
            Assert.False(string.IsNullOrEmpty(result.Remediation));
            Assert.Contains("ARCHITECTURE.md", result.Remediation);
        }

        /// <summary>
        /// P15-032: malformed XML → Fail. Tests the XmlException catch branch
        /// distinctly from the missing-file branch.
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_MalformedXml_Fails()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-malformed-" + Guid.NewGuid().ToString("N") + ".exe.config");
            File.WriteAllText(path, "<?xml version=\"1.0\"?><configuration><startup");
            try
            {
                var result = DoctorCommand.ProbeAppConfigShimAt(path);

                Assert.Equal(DoctorCommand.ProbeStatus.Fail, result.Status);
                Assert.Contains("malformed", result.Detail);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>
        /// P15-032: well-formed XML but missing the &lt;startup&gt; element → Fail
        /// with a detail that names the missing element so the operator can fix
        /// it by hand.
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_MissingStartupElement_Fails()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-nostartup-" + Guid.NewGuid().ToString("N") + ".exe.config");
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration></configuration>");
            try
            {
                var result = DoctorCommand.ProbeAppConfigShimAt(path);

                Assert.Equal(DoctorCommand.ProbeStatus.Fail, result.Status);
                Assert.Contains("<startup>", result.Detail);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>
        /// P15-032: startup element present but useLegacyV2RuntimeActivationPolicy
        /// attribute missing/wrong → Fail. This is the most likely real-world
        /// corruption (someone trimmed App.config to "modernize" it).
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_MissingLegacyV2Attr_Fails()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-nolegacy-" + Guid.NewGuid().ToString("N") + ".exe.config");
            // startup is present but missing useLegacyV2RuntimeActivationPolicy
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration>" +
                "<startup><supportedRuntime version=\"v4.0\" /></startup></configuration>");
            try
            {
                var result = DoctorCommand.ProbeAppConfigShimAt(path);

                Assert.Equal(DoctorCommand.ProbeStatus.Fail, result.Status);
                Assert.Contains("useLegacyV2RuntimeActivationPolicy", result.Detail);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>
        /// P15-032: startup element + legacy attr present but supportedRuntime
        /// child missing → Fail. Catches the other half of "someone trimmed it."
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_MissingSupportedRuntime_Fails()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-nosupruntime-" + Guid.NewGuid().ToString("N") + ".exe.config");
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration>" +
                "<startup useLegacyV2RuntimeActivationPolicy=\"true\"></startup></configuration>");
            try
            {
                var result = DoctorCommand.ProbeAppConfigShimAt(path);

                Assert.Equal(DoctorCommand.ProbeStatus.Fail, result.Status);
                Assert.Contains("supportedRuntime", result.Detail);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>
        /// P15-032: canonical 14-line file shape → Pass. Mirrors the actual
        /// src/LacertMCP.Server/App.config so any regression that breaks parse
        /// of the real file is caught here too.
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_CanonicalFile_Passes()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-canonical-" + Guid.NewGuid().ToString("N") + ".exe.config");
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<configuration>\n" +
                "  <startup useLegacyV2RuntimeActivationPolicy=\"true\">\n" +
                "    <supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.6\" />\n" +
                "  </startup>\n" +
                "</configuration>\n");
            try
            {
                var result = DoctorCommand.ProbeAppConfigShimAt(path);

                Assert.Equal(DoctorCommand.ProbeStatus.Pass, result.Status);
                Assert.Contains("runtime shim present", result.Detail);
                Assert.Null(result.Remediation);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>
        /// P15-032: empty/null path → Fail (defensive — should never happen in
        /// practice but the probe's resolver could theoretically return empty
        /// in pathological host setups).
        /// </summary>
        [Fact]
        public void Doctor_AppConfigShim_EmptyPath_Fails()
        {
            var result = DoctorCommand.ProbeAppConfigShimAt(string.Empty);
            Assert.Equal(DoctorCommand.ProbeStatus.Fail, result.Status);
        }

        /// <summary>
        /// P15-032: ResolveServerConfigPath returns a non-empty path under
        /// AppContext.BaseDirectory that ends in LacertMCP.Server.exe.config.
        /// Pins the resolution contract so the probe stays pointed at the
        /// canonical file location.
        /// </summary>
        [Fact]
        public void Doctor_ResolveServerConfigPath_Returns_BaseDir_Combined_Path()
        {
            var resolved = DoctorCommand.ResolveServerConfigPath();
            Assert.False(string.IsNullOrEmpty(resolved));
            Assert.EndsWith("LacertMCP.Server.exe.config", resolved);
            // Must be under the test bin's AppContext.BaseDirectory.
            Assert.StartsWith(AppContext.BaseDirectory, resolved);
        }

        // ----- P16-023: DATA_PATH vs DATA_ROOT remediation differentiation -----

        /// <summary>
        /// P16-023: when LACERTE_DATA_ROOT is unset, the unset-state probe must
        /// carry a remediation that names DATA_ROOT specifically and explains
        /// it is NOT settable via `lacerte_set_context` (preempting the prior
        /// copy-pasted message that sent operators on a wild-goose chase).
        /// </summary>
        [Fact]
        public void Doctor_DataRoot_Unset_Remediation_Names_DataRoot_Not_SetContext()
        {
            // R6 flake fix: pin the env via the RunAllProbes override seam
            // instead of mutating the process-wide var — SetContext in
            // concurrently-running test collections reads LACERTE_DATA_ROOT
            // at call time and a transient mutation here fails THEIR tests.
            var results = DoctorCommand.RunAllProbes(
                new Dictionary<string, string?> { ["LACERTE_DATA_ROOT"] = null });
            var dataRoot = results.FirstOrDefault(r => r.Name == "LACERTE_DATA_ROOT env");
            Assert.NotNull(dataRoot);
            Assert.Equal(DoctorCommand.ProbeStatus.Warn, dataRoot!.Status);
            Assert.False(string.IsNullOrEmpty(dataRoot.Remediation),
                "DATA_ROOT unset warn must carry a remediation.");
            Assert.Contains("LACERTE_DATA_ROOT", dataRoot.Remediation!);
            Assert.Contains("NOT settable via lacerte_set_context", dataRoot.Remediation);
        }

        /// <summary>
        /// P16-023: bonus tier-down — when DATA_PATH is unset BUT DATA_ROOT is
        /// set to an existing directory, DATA_PATH should report Pass (not Warn)
        /// because discovery via DATA_ROOT already covers the use case. Without
        /// this an operator with correctly-configured DATA_ROOT sees a false-
        /// positive warn telling them to call set_context.
        /// </summary>
        [Fact]
        public void Doctor_DataPath_Unset_With_DataRoot_Set_Downgrades_To_Pass()
        {
            // R6 flake fix: env pinned via the override seam (see the test
            // above). Temp dir = guaranteed-existing path for DATA_ROOT.
            var results = DoctorCommand.RunAllProbes(
                new Dictionary<string, string?>
                {
                    ["LACERTE_DATA_ROOT"] = Path.GetTempPath(),
                    ["LACERTE_DATA_PATH"] = null,
                });
            var dataPath = results.FirstOrDefault(r => r.Name == "LACERTE_DATA_PATH env");
            Assert.NotNull(dataPath);
            Assert.Equal(DoctorCommand.ProbeStatus.Pass, dataPath!.Status);
        }

        // ----- R6-05: AppID probe no longer carries Phase-9-stub caveats -----

        /// <summary>
        /// R6-05 (supersedes P16-058): now that `lacerte-mcp register` actually runs
        /// the ITPRegistration.Register ceremony, the AppID probe's detail/remediation
        /// must NOT mention the retired Phase-9 stub on ANY platform/mode — the
        /// P16-058 Win32NT+Live caveat branches were deleted per their own
        /// PHASE-9-CLEANUP note.
        /// </summary>
        [Fact]
        public void Doctor_ProbeAppId_DetailAndRemediation_DoNotMentionPhase9Stub()
        {
            var results = DoctorCommand.RunAllProbes();
            var appId = results.FirstOrDefault(r => r.Name == "AppID GUID");
            Assert.NotNull(appId);
            Assert.DoesNotContain("Phase 9", appId!.Detail);
            Assert.DoesNotContain("[partial]", appId.Detail);
            if (!string.IsNullOrEmpty(appId.Remediation))
            {
                Assert.DoesNotContain("Phase 9", appId.Remediation!);
                Assert.DoesNotContain("[partial]", appId.Remediation!);
            }
        }

        // ----- P16-073: doctor Next: epilogue -----

        /// <summary>
        /// P16-073: human output must include a "Next:" block keyed to exit code
        /// so the operator isn't left at a probe table needing to context-switch
        /// to README. JSON output must NOT include it (schema is contract-pinned).
        /// </summary>
        [Fact]
        public void Doctor_HumanOutput_Includes_Next_Step_Block()
        {
            var sw = new StringWriter();
            DoctorCommand.Run(sw, asJson: false);
            var output = sw.ToString();
            Assert.Contains("Next:", output);
        }

        [Fact]
        public void Doctor_JsonOutput_Does_Not_Include_Next_Step_Block()
        {
            var sw = new StringWriter();
            DoctorCommand.Run(sw, asJson: true);
            var output = sw.ToString();
            Assert.DoesNotContain("Next:", output);
            // Sanity: still parseable JSON.
            using var doc = JsonDocument.Parse(output);
            Assert.True(doc.RootElement.TryGetProperty("probes", out _));
        }

        // ----- R6-29: Warn rendering (remediation line + PARTIAL summary) -----

        /// <summary>
        /// R6-29(a): Warn rows must print their remediation in the human table
        /// (previously only Fail rows surfaced the `→` hint, leaving ⚠ rows
        /// with no guidance). Uses the results-injectable Run seam.
        /// </summary>
        [Fact]
        public void Doctor_HumanOutput_PrintsWarnRemediation()
        {
            var sw = new StringWriter();
            var results = new List<DoctorCommand.ProbeResult>
            {
                Pass("a"),
                Warn("b", remediation: "tighten the flux capacitor"),
            };
            var exit = DoctorCommand.Run(sw, asJson: false, results);

            Assert.Equal(2, exit);
            Assert.Contains("tighten the flux capacitor", sw.ToString());
        }

        /// <summary>
        /// R6-29(b): a warn-only run must NOT claim "all probes green" — the
        /// summary line is "Result: PARTIAL (N warning(s)). Exit 2."
        /// </summary>
        [Fact]
        public void Doctor_WarnOnly_SummaryIsPartialNotGreen()
        {
            var sw = new StringWriter();
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Warn("b"), Warn("c") };
            var exit = DoctorCommand.Run(sw, asJson: false, results);

            Assert.Equal(2, exit);
            var output = sw.ToString();
            Assert.Contains("Result: PARTIAL (2 warning(s)). Exit 2.", output);
            Assert.DoesNotContain("all probes green", output);
        }

        /// <summary>
        /// R6-29(b): warn + skip mix reports both counts in the PARTIAL summary.
        /// Skip-only keeps the original off-Windows wording.
        /// </summary>
        [Fact]
        public void Doctor_WarnPlusSkip_SummaryReportsBothCounts()
        {
            var sw = new StringWriter();
            var results = new List<DoctorCommand.ProbeResult> { Warn("a"), Skip("b"), Pass("c") };
            var exit = DoctorCommand.Run(sw, asJson: false, results);

            Assert.Equal(2, exit);
            Assert.Contains("Result: PARTIAL (1 warning(s), 1 probe(s) skipped). Exit 2.", sw.ToString());
        }

        [Fact]
        public void Doctor_SkipOnly_SummaryKeepsOffWindowsWording()
        {
            var sw = new StringWriter();
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Skip("b") };
            var exit = DoctorCommand.Run(sw, asJson: false, results);

            Assert.Equal(2, exit);
            Assert.Contains("Result: PARTIAL (one or more probes skipped — not on Windows). Exit 2.", sw.ToString());
        }

        /// <summary>
        /// R6-29: all-green path still says OK / exits 0 through the injectable seam.
        /// </summary>
        [Fact]
        public void Doctor_AllPass_SummaryIsGreenExit0()
        {
            var sw = new StringWriter();
            var results = new List<DoctorCommand.ProbeResult> { Pass("a"), Pass("b") };
            var exit = DoctorCommand.Run(sw, asJson: false, results);

            Assert.Equal(0, exit);
            Assert.Contains("Result: OK (all probes green). Exit 0.", sw.ToString());
        }

        // ----- P16-068: UnsafeRelaxedJsonEscaping -----

        /// <summary>
        /// P16-068: doctor JSON must not escape `<` / `>` / `&` / `+` — the
        /// App.config remediation literally contains <startup> XML and the
        /// default HTML-safe encoder turns it into unreadable `<` noise
        /// when an operator pastes the JSON into a bug report.
        /// </summary>
        [Fact]
        public void Doctor_Json_Does_Not_Escape_Ascii_Punctuation()
        {
            var sw = new StringWriter();
            DoctorCommand.Run(sw, asJson: true);
            var output = sw.ToString();
            Assert.DoesNotContain("\\u003C", output);
            Assert.DoesNotContain("\\u003E", output);
            Assert.DoesNotContain("\\u002B", output);
            Assert.DoesNotContain("\\u0026", output);
        }
    }
}
