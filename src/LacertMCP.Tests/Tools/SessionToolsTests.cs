using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Phase 5 task #41 — `lacerte_session_status`, `lacerte_set_context`,
    /// `lacerte_can_use_new_apis`, `lacerte_list_modules`,
    /// `lacerte_list_datapaths`.
    ///
    /// <para>
    /// FINAL-VERIFY trivial fix — placed in the AccessMapMutationCollection so
    /// the SessionStatus_SurfacesWireLogState_WhenDisabled assertion (which
    /// reads the process-global WireLog._testForceConfig state via
    /// WireLog.IsEnabled) doesn't race the WireLogRotationTests /
    /// WireLogSessionIntegrationTests / WireLogResultSummarizationTests that
    /// flip that state under EnableForTests/DisableForTests brackets. All
    /// three of those test classes already live in AccessMapMutationCollection;
    /// adding SessionToolsTests to the same collection serializes them per
    /// the existing project convention.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionToolsTests
    {
        private static SessionTools NewTools() => new SessionTools(ToolTestHelpers.NewSimManager());

        // -----------------------------------------------------------------
        // lacerte_session_status
        // -----------------------------------------------------------------

        [Fact]
        public async Task SessionStatus_FreshManager_ReturnsSimMode_NotRegistered_NoContext()
        {
            // P15-050 — use the explicit unregistered helper so this test
            // continues to assert the fresh-manager-pre-Register snapshot
            // shape (the default NewSimManager auto-registers now to keep
            // legacy chokepoint tests green).
            var tools = new SessionTools(ToolTestHelpers.NewUnregisteredSimManager());
            var json = await tools.LacerteSessionStatus();
            var root = ToolTestHelpers.Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            // Mode casing aligned with lacerte_health + HeartbeatWriter — every
            // surface emits lowercase ("simulation"/"live") for the conceptual
            // mode field.
            Assert.Equal("simulation", result.GetProperty("mode").GetString());
            Assert.False(result.GetProperty("isRegistered").GetBoolean());
            // No context set yet — should be JSON null.
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("context").ValueKind);
        }

        [Fact]
        public async Task SessionStatus_AfterRegister_ReflectsRegistered()
        {
            // P15-050 — NewSimManager already auto-Registers; no need to call
            // Register again. The original double-Register was harmless but
            // confusing post-P15-050.
            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new SessionTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteSessionStatus());
            var result = root.GetProperty("result");

            Assert.True(result.GetProperty("isRegistered").GetBoolean());
            Assert.True(result.GetProperty("minutesUntilExpiry").GetDouble() > 0);
        }

        // -----------------------------------------------------------------
        // lacerte_set_context
        // -----------------------------------------------------------------

        [Fact]
        public async Task SetContext_HappyPath_ReturnsContextEcho()
        {
            var tools = NewTools();
            var json = await tools.LacerteSetContext(SeedData.DataPathIData2024, 2024, "I");
            var root = ToolTestHelpers.Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean());
            var ctx = root.GetProperty("result");
            Assert.Equal(SeedData.DataPathIData2024, ctx.GetProperty("dataPath").GetString());
            // P16-069/P16-079 — response field is `taxYear`.
            Assert.Equal(2024, ctx.GetProperty("taxYear").GetInt32());
            Assert.Equal("I", ctx.GetProperty("module").GetString());
            Assert.False(ctx.GetProperty("readOnly").GetBoolean());
        }

        [Fact]
        public async Task SetContext_YearBelowFloor_Returns9003()
        {
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(SeedData.DataPathIData2024, 2017, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9003, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_EmptyModule_RejectsCleanly()
        {
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(SeedData.DataPathIData2024, 2024, ""));
            Assert.False(root.GetProperty("success").GetBoolean());
            // Tool-level validation maps to InValidClientName (24) per the convention used by SetContext.
            Assert.Equal(24, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_ReadOnlyFlag_PreservedThroughSessionStatus()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new SessionTools(mgr);
            await tools.LacerteSetContext(SeedData.DataPathIData2024, 2024, "I", readOnly: true);

            var statusRoot = ToolTestHelpers.Parse(await tools.LacerteSessionStatus());
            var ctx = statusRoot.GetProperty("result").GetProperty("context");
            Assert.True(ctx.GetProperty("readOnly").GetBoolean());
        }

        // -----------------------------------------------------------------
        // lacerte_can_use_new_apis
        // -----------------------------------------------------------------

        [Fact]
        public async Task CanUseNewApis_TY24_ReturnsTrue_WithUpdateTaxReturnPath()
        {
            var root = ToolTestHelpers.Parse(await NewTools().LacerteCanUseNewApis(2024, "I"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("canUseNewApis").GetBoolean());
            Assert.Equal("UpdateTaxReturn", result.GetProperty("apiPath").GetString());
        }

        [Fact]
        public async Task CanUseNewApis_TY20_ReturnsFalse_WithLegacyPath()
        {
            var root = ToolTestHelpers.Parse(await NewTools().LacerteCanUseNewApis(2020, "I"));
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("canUseNewApis").GetBoolean());
            Assert.Equal("WriteToDBFDatabase", result.GetProperty("apiPath").GetString());
        }

        // -----------------------------------------------------------------
        // lacerte_list_modules
        // -----------------------------------------------------------------

        [Fact]
        public async Task ListModules_Returns9Modules_AllChars()
        {
            var root = ToolTestHelpers.Parse(await NewTools().LacerteListModules());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(9, result.GetProperty("count").GetInt32());

            var chars = result.GetProperty("modules").EnumerateArray()
                .Select(m => m.GetProperty("module").GetString())
                .ToArray();
            foreach (var expected in new[] { "I", "C", "P", "S", "F", "R", "T", "N", "B" })
            {
                Assert.Contains(expected, chars);
            }
        }

        // -----------------------------------------------------------------
        // lacerte_list_datapaths
        // -----------------------------------------------------------------

        [Fact]
        public async Task ListDatapaths_DefaultRoot_ReturnsEnvelope_NoCrash()
        {
            // Off-Windows / no Lacerte install: rootExists may be false, but the
            // tool must still return a structured success envelope (empty array).
            var root = ToolTestHelpers.Parse(await NewTools().LacerteListDatapaths());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.TryGetProperty("root", out _));
            Assert.True(result.TryGetProperty("datapaths", out var arr));
            Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
        }

        [Fact]
        public void DiscoverDatapaths_NonExistentRoot_ReturnsEmpty()
        {
            var entries = SessionTools.DiscoverDatapaths(@"Q:\definitely\not\a\real\path");
            Assert.Empty(entries);
        }

        [Fact]
        public void DiscoverDatapaths_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Empty(SessionTools.DiscoverDatapaths(""));
            Assert.Empty(SessionTools.DiscoverDatapaths(null!));
        }

        // -----------------------------------------------------------------
        // P14-049 — dataPath PathValidation gate (9013 InvalidDataPath)
        // -----------------------------------------------------------------

        [Fact]
        public async Task SetContext_RelativePath_Returns9013()
        {
            // "foo\\bar" is not rooted — must be rejected before any sim
            // backend write happens.
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(@"foo\bar", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_DotDotTraversal_Returns9013()
        {
            // '..' as a separator-bounded segment is the textbook traversal
            // primitive — Path.GetFullPath would silently collapse it; the
            // PathValidation gate catches it BEFORE canonicalization.
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(@"C:\Lacerte\24Tax\..\Windows", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_UncPath_RejectedWithout_AllowUnc()
        {
            // UNC paths are rejected by default; the escape hatch is
            // LACERTE_ALLOW_UNC=1 (tested separately at the helper level so
            // we don't have to mutate process env in xUnit).
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext(@"\\server\share\IData", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_NullByte_Returns9013()
        {
            // Control characters (< 0x20 OR 0x7F) are highest-risk parser
            // confusion strings — null in particular has been used to slice
            // strings in C-FFI marshalling boundaries.
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext("C:\\Lacerte\\24Tax\\IData\0evil", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task SetContext_CrLf_Returns9013()
        {
            var tools = NewTools();
            var root = ToolTestHelpers.Parse(
                await tools.LacerteSetContext("C:\\Lacerte\\24Tax\\IData\r\n", 2024, "I"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }

        // -----------------------------------------------------------------
        // P14-049 — PathValidation.ValidateDataPath helper (direct)
        // -----------------------------------------------------------------
        // These exercise the helper with explicit env dictionaries so we can
        // cover the allowlist + escape-hatch branches without mutating the
        // process-wide environment (xUnit runs tests in parallel — env
        // mutation would race).

        [Fact]
        public void ValidateDataPath_AllowsUnc_WhenEscapeHatchSet()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_ALLOW_UNC"] = "1",
            };
            // Must not throw; UNC paths pass when the escape hatch is set.
            // Don't assert on the canonical return — GetFullPath behavior on
            // UNC is host-dependent.
            LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"\\server\share\IData", env);
        }

        [Fact]
        public void ValidateDataPath_DataRoot_RejectsOutsideRoot()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_DATA_ROOT"] = @"C:\Lacerte",
            };
            var ex = Assert.Throws<LacertMCP.Server.Errors.LacerteSDKError>(
                () => LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"C:\Windows\System32", env));
            Assert.Equal(9013, ex.StatusCode);
        }

        [Fact]
        public void ValidateDataPath_DataRoot_AcceptsUnderRoot()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_DATA_ROOT"] = @"C:\Lacerte",
            };
            // Must not throw; happy path under the allowlist root.
            LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"C:\Lacerte\24Tax\IData", env);
        }

        [Fact]
        public void ValidateDataPath_DataRoot_NoFalseMatch_OnSiblingPrefix()
        {
            // Trailing-separator normalization: "C:\Lacerte2\foo" must NOT
            // match root "C:\Lacerte" via naive StartsWith.
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LACERTE_DATA_ROOT"] = @"C:\Lacerte",
            };
            var ex = Assert.Throws<LacertMCP.Server.Errors.LacerteSDKError>(
                () => LacertMCP.Server.Session.PathValidation.ValidateDataPath(@"C:\Lacerte2\foo", env));
            Assert.Equal(9013, ex.StatusCode);
        }

        [Fact]
        public void ValidateLogDir_RejectsRelative()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>();
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"relative\logs", env);
            Assert.Null(result);
        }

        [Fact]
        public void ValidateLogDir_RejectsUnc_WithoutEscapeHatch()
        {
            var env = new System.Collections.Generic.Dictionary<string, string?>();
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"\\evil\share\logs", env);
            Assert.Null(result);
        }

        [Fact]
        public void ValidateLogDir_AcceptsUnderUserprofile()
        {
            // Allowlist test — when USERPROFILE is set and the log dir is
            // under it, the canonical path is returned.
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["USERPROFILE"] = @"C:\Users\tester",
            };
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"C:\Users\tester\AppData\logs", env);
            Assert.NotNull(result);
        }

        [Fact]
        public void ValidateLogDir_NoEnvAllowlistRoots_RejectsToForceFallback()
        {
            // P17-010 — when the env dictionary has no allowlist roots (the
            // typical POSIX dev box where USERPROFILE/TEMP/TMP are not set),
            // the helper MUST reject so WireLog falls back to ./logs rather
            // than provisioning arbitrary filesystem locations. The prior
            // behavior of returning the canonical path silently disabled the
            // gate off-Windows, breaking the sim/live boundary invariant.
            var env = new System.Collections.Generic.Dictionary<string, string?>();
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(@"C:\anywhere\logs", env);
            Assert.Null(result);
        }

        [Fact]
        public void ValidateLogDir_PosixShapedEnv_RejectsArbitraryPath()
        {
            // P17-010 — POSIX-conventional env vars (HOME, TMPDIR) are NOT in
            // the Windows-shaped allowlist set (USERPROFILE, TEMP, TMP,
            // LACERTE_DATA_ROOT). With only HOME/TMPDIR populated, the
            // allowlist is empty and the helper rejects arbitrary paths.
            // Operators on POSIX who need a custom log path must set
            // LACERTE_DATA_ROOT explicitly so the allowlist has something to
            // enforce against.
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["HOME"] = "/home/dev",
                ["TMPDIR"] = "/tmp",
            };
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir("/etc/cron.d", env);
            Assert.Null(result);
        }

        [Fact]
        public void ValidateLogDir_NonExistentTailUnderRoot_StillAccepted()
        {
            // P17-016 — the reparse-point walk treats non-existent tail
            // segments as clean (lazy-create-on-first-write is a legitimate
            // pattern WireLog relies on). A path under USERPROFILE pointing
            // to a directory that doesn't exist yet still validates.
            var env = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["USERPROFILE"] = @"C:\Users\tester",
            };
            var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(
                @"C:\Users\tester\AppData\Local\NotYetCreated\logs", env);
            Assert.NotNull(result);
        }

        [Fact]
        public void ValidateLogDir_RejectsJunctionUnderTemp_Windows()
        {
            // P17-016 — on Windows, create a real junction inside a temp
            // root and assert ValidateLogDir rejects it. The junction has the
            // FileAttributes.ReparsePoint bit which the segment walker
            // detects.
            //
            // Skipped off-Windows: mklink is a Windows-only mechanism and
            // the FileAttributes.ReparsePoint surface is not meaningfully
            // tested on POSIX hosts. The fail-closed posture for the rest
            // of the gate is exercised by ValidateLogDir_PosixShapedEnv_*.
            if (System.Environment.OSVersion.Platform != System.PlatformID.Win32NT)
            {
                return;
            }

            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lac-mcp-junction-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            var target = System.IO.Path.Combine(tempRoot, "real-target");
            var junction = System.IO.Path.Combine(tempRoot, "via-junction");
            System.IO.Directory.CreateDirectory(target);
            try
            {
                // Create the junction via cmd /c mklink /J (no admin needed).
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + junction + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p!.WaitForExit(5000);
                    if (p.ExitCode != 0)
                    {
                        // Junction creation can fail on locked-down boxes —
                        // skip rather than fail the suite (no privileges to
                        // exercise the test).
                        return;
                    }
                }

                // Point the allowlist at the temp root via TEMP env, then
                // try to validate a path that traverses the junction. Expect
                // null (rejected by the reparse-point walker).
                var env = new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["TEMP"] = tempRoot,
                };
                var result = LacertMCP.Server.Session.PathValidation.ValidateLogDir(
                    System.IO.Path.Combine(junction, "logs"), env);
                Assert.Null(result);

                // Sanity: a NON-junction sibling under the same allowlist
                // root validates fine, confirming the rejection is
                // specifically because of the reparse point, not a broader
                // gate.
                var cleanResult = LacertMCP.Server.Session.PathValidation.ValidateLogDir(
                    System.IO.Path.Combine(target, "logs"), env);
                Assert.NotNull(cleanResult);
            }
            finally
            {
                try { System.IO.Directory.Delete(junction, recursive: false); } catch { }
                try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        // -----------------------------------------------------------------
        // P16-030 — hint on empty lacerte_list_datapaths result
        //
        // Tests drive BuildListDatapathsPayload directly to avoid mutating
        // the process-wide LACERTE_DATA_ROOT env var, which would race
        // against parallel test classes.
        // -----------------------------------------------------------------

        [Fact]
        public void ListDatapaths_NonExistentRoot_PayloadCarriesHint_NamingEnvVarAndLayout()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                SessionTools.BuildListDatapathsPayload(@"Q:\definitely\not\a\real\path"));
            var result = ToolTestHelpers.Parse(json);
            Assert.False(result.GetProperty("rootExists").GetBoolean());
            Assert.Equal(0, result.GetProperty("count").GetInt32());
            var hint = result.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("LACERTE_DATA_ROOT", hint);
            Assert.Contains("{YY}Tax", hint);
        }

        [Fact]
        public void ListDatapaths_RootExistsButEmpty_PayloadCarriesHint_MentioningSharedLayout()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lacert-mcp-emptyroot-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    SessionTools.BuildListDatapathsPayload(root));
                var result = ToolTestHelpers.Parse(json);
                Assert.True(result.GetProperty("rootExists").GetBoolean());
                Assert.Equal(0, result.GetProperty("count").GetInt32());
                var hint = result.GetProperty("hint").GetString();
                Assert.NotNull(hint);
                Assert.Contains("{YY}data", hint);
                Assert.Contains("lacerte_set_context", hint);
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* swallow */ }
            }
        }

        [Fact]
        public void ListDatapaths_RootWithMatchingFolders_OmitsHintOnHappyPath()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lacert-mcp-happy-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "24Tax", "IData"));
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    SessionTools.BuildListDatapathsPayload(root));
                var result = ToolTestHelpers.Parse(json);
                Assert.Equal(1, result.GetProperty("count").GetInt32());
                Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("hint").ValueKind);
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* swallow */ }
            }
        }

        // -----------------------------------------------------------------
        // P16-066 — wire-log diagnostic state on lacerte_session_status
        // -----------------------------------------------------------------

        [Fact]
        public async Task SessionStatus_SurfacesWireLogState_WhenDisabled_PathIsWouldBe()
        {
            // Default test process: LACERTE_DEBUG_WIRE not set, logging off.
            // Snapshot must report debugWireEnabled=false AND a non-null
            // wireLogPath (the would-be path so the operator can enable +
            // restart + grep without guessing).
            var json = await NewTools().LacerteSessionStatus();
            var result = ToolTestHelpers.Parse(json).GetProperty("result");
            Assert.False(result.GetProperty("debugWireEnabled").GetBoolean());
            var path = result.GetProperty("wireLogPath").GetString();
            Assert.NotNull(path);
            // Filename portion is the canonical lacerte-YYYYMMDD.log shape.
            Assert.Contains("lacerte-", path);
            Assert.EndsWith(".log", path);
        }

        [Fact]
        public void WireLog_ResolvedOrWouldBeLogPath_ReturnsCanonicalFilename()
        {
            // The would-be-path resolver is process-state-only (env var read
            // happens elsewhere) — verify it always produces a non-empty path
            // ending in the canonical lacerte-YYYYMMDD.log filename. This is
            // race-safe to run unconditionally and pins the helper contract
            // so a future refactor can't silently start returning null when
            // logging is disabled.
            var path = LacertMCP.Server.Logging.WireLog.ResolvedOrWouldBeLogPath();
            Assert.False(string.IsNullOrEmpty(path));
            var fname = System.IO.Path.GetFileName(path);
            Assert.StartsWith("lacerte-", fname);
            Assert.EndsWith(".log", fname);
        }

        // -----------------------------------------------------------------
        // P16-083 — minutesUntilExpiry rounded to 1 decimal at the boundary
        // -----------------------------------------------------------------

        [Fact]
        public async Task SessionStatus_MinutesUntilExpiry_RoundedToOneDecimal()
        {
            var json = await NewTools().LacerteSessionStatus();
            var result = ToolTestHelpers.Parse(json).GetProperty("result");
            var mins = result.GetProperty("minutesUntilExpiry").GetDouble();
            // Round-trip: |mins - round(mins,1)| < epsilon
            Assert.True(System.Math.Abs(mins - System.Math.Round(mins, 1, System.MidpointRounding.AwayFromZero)) < 1e-9,
                $"minutesUntilExpiry {mins} not rounded to 1 decimal");
        }

        // -----------------------------------------------------------------
        // P16-051/P16-078 — dataPath description uses single backslashes
        // (no C# escape leak doubling them on the wire).
        // -----------------------------------------------------------------

        [Fact]
        public void SetContextDescription_DataPathExample_UsesSingleBackslashes()
        {
            var method = typeof(SessionTools).GetMethod(nameof(SessionTools.LacerteSetContext))!;
            var dataPathParam = System.Linq.Enumerable.First(
                method.GetParameters(),
                p => p.Name == "dataPath");
            var descAttr = (System.ComponentModel.DescriptionAttribute?)System.Attribute.GetCustomAttribute(
                dataPathParam, typeof(System.ComponentModel.DescriptionAttribute));
            Assert.NotNull(descAttr);
            // Must contain the canonical single-backslash example...
            Assert.Contains(@"C:\Lacerte\24Tax\IData", descAttr!.Description);
            // ...and must NOT contain the 4-char doubled-backslash sequence the
            // LLM would copy verbatim into a call payload.
            Assert.DoesNotContain(@"C:\\Lacerte", descAttr.Description);
        }

        [Fact]
        public void DiscoverDatapaths_RealLayout_FindsYYTaxModuleData()
        {
            // Build a temp tree: <tmp>/20Tax/IData + <tmp>/24Tax/CData + a couple of decoys.
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lacert-mcp-discover-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "20Tax", "IData"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "24Tax", "CData"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "NotTax", "IData")); // decoy
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "20Tax", "XData"));  // unknown module
            try
            {
                var entries = SessionTools.DiscoverDatapaths(root);
                Assert.Equal(2, entries.Count);
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* swallow */ }
            }
        }
    }
}
