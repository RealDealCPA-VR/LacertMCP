using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    public class StatusCodesTests
    {
        public static IEnumerable<object[]> AllTAPIStatusCodes =>
            Enumerable.Range(0, 29).Select(i => new object[] { i });

        public static IEnumerable<object[]> AllRegistrationCodes =>
            Enumerable.Range(200, 5).Select(i => new object[] { i });

        public static IEnumerable<object[]> AllSyntheticCodes => new[]
        {
            new object[] { 9001 },
            new object[] { 9002 },
            new object[] { 9003 },
            new object[] { 9004 },
            new object[] { 9005 },
            new object[] { 9006 },
            new object[] { 9007 },
            new object[] { 9008 },
            new object[] { 9009 },
            // P15-022 — 9010/9011 (BridgeUnavailableLegacy/SimulationOnlyLegacy)
            // removed; canonical codes are 9201/9202 in the bridge range.
            new object[] { 9012 },
            // P15-007 — InvalidAppId. Distinct from AppNotRegistered (9006) so
            // bad-shape appIds don't send LLM clients into "rerun register"
            // loops that don't fix the actual problem.
            new object[] { 9103 },
            // P15-035 — InternalServerError. Distinct from StatusCodes.Error (0)
            // so the wrapper doesn't misattribute in-server faults as SDK traces.
            new object[] { 9203 },
        };

        [Theory]
        [MemberData(nameof(AllTAPIStatusCodes))]
        public void StatusTable_ContainsEveryTAPIStatusCode(int code)
        {
            Assert.True(
                StatusCodes.StatusTable.ContainsKey(code),
                $"StatusCodes.StatusTable missing TAPIStatus entry for code {code}.");
        }

        [Theory]
        [MemberData(nameof(AllRegistrationCodes))]
        public void StatusTable_ContainsEveryRegistrationCode(int code)
        {
            Assert.True(
                StatusCodes.StatusTable.ContainsKey(code),
                $"StatusCodes.StatusTable missing TPSDKStatus entry for code {code}.");
        }

        [Theory]
        [MemberData(nameof(AllSyntheticCodes))]
        public void SyntheticStatusTable_ContainsEverySyntheticCode(int code)
        {
            Assert.True(
                SyntheticStatusCodes.StatusTable.ContainsKey(code),
                $"SyntheticStatusCodes.StatusTable missing synthetic entry for code {code}.");
        }

        [Fact]
        public void GetStatusInfo_Success_IsSeveritySuccess()
        {
            var info = StatusCodes.GetStatusInfo(1);
            Assert.Equal("Success", info.Name);
            Assert.Equal(Severity.Success, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_NotExposed_IsNamedAndError()
        {
            var info = StatusCodes.GetStatusInfo(7);
            Assert.Equal("NotExposed", info.Name);
            Assert.Equal(Severity.Error, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_ExposedForRead_IsWarningPerAgent05ThirdSection()
        {
            // Per agent 05 §3 justification: access-map informational answers are warning at the
            // table level so the access-map tool doesn't surface isError on a normal answer.
            var info = StatusCodes.GetStatusInfo(8);
            Assert.Equal("ExposedForRead", info.Name);
            Assert.Equal(Severity.Warning, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_AccessGranted_IsSeveritySuccess()
        {
            var info = StatusCodes.GetStatusInfo(201);
            Assert.Equal("AccessGranted", info.Name);
            Assert.Equal(Severity.Success, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_UnknownCode_FallsBackToUnknownErrorEntry()
        {
            var info = StatusCodes.GetStatusInfo(99999);
            Assert.Equal("Unknown(99999)", info.Name);
            Assert.Equal(Severity.Error, info.Severity);
            Assert.Contains("99999", info.UserMessage);
            Assert.NotNull(info.Remediation);
        }

        [Fact]
        public void GetStatusInfo_RegVersionBlocked_UsesPrefixedNameToAvoidTAPICollision()
        {
            // Per agent 05 §1 VersionBlocked collision note + follow-up F2: TPSDKStatus 202 is
            // registered as RegVersionBlocked to disambiguate from TAPIStatus 11 VersionBlocked.
            var reg = StatusCodes.GetStatusInfo(202);
            var tapi = StatusCodes.GetStatusInfo(11);

            Assert.Equal("RegVersionBlocked", reg.Name);
            Assert.Equal("VersionBlocked", tapi.Name);
        }

        public static IEnumerable<object[]> AllDBxErrorCodes => new[]
        {
            new object[] { StatusCodes.DB1Error },
            new object[] { StatusCodes.DB2Error },
            new object[] { StatusCodes.DB3Error },
            new object[] { StatusCodes.DB4Error },
            new object[] { StatusCodes.DB5Error },
            new object[] { StatusCodes.DB6Error },
            new object[] { StatusCodes.DB7Error },
            new object[] { StatusCodes.DB8Error },
        };

        [Theory]
        [MemberData(nameof(AllDBxErrorCodes))]
        public void DBxError_Remediation_MentionsExpectedDbfContextKey_P15_036(int code)
        {
            // P15-036: DBxError remediations must name the offending file via the
            // context.expectedDbf + context.dataPath keys the live bridge populates,
            // not the old generic "check the file on disk" string that named no file.
            var info = StatusCodes.GetStatusInfo(code);
            Assert.NotNull(info.Remediation);
            Assert.Contains("expectedDbf", info.Remediation!);
            Assert.Contains("dataPath", info.Remediation);
        }

        [Theory]
        [MemberData(nameof(AllDBxErrorCodes))]
        public void DBxError_Remediation_DoesNotUseOldGenericStringWithNoFilename_P15_036(int code)
        {
            // Regression guard for P15-036: the pre-fix string was identical across all 8
            // entries and named no file. If anyone reverts to that wording, this fails.
            var info = StatusCodes.GetStatusInfo(code);
            Assert.NotEqual(
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year.",
                info.Remediation);
        }

        [Fact]
        public void Remediations_NeverNameNonexistentMcpTool_LacerteRegister_P16_011()
        {
            // P16-011 — guard rail: no remediation string may say
            // `lacerte_register` because there is no LacerteRegister MCP
            // tool — an LLM following that string will tools/call a non-
            // existent name and loop. The CLI ceremony is `lacerte-mcp
            // register` (hyphenated). This sentinel prevents regression:
            // future contributors adding a new remediation string can't
            // reintroduce the underscore form without a test failure.
            var allRemediations =
                SyntheticStatusCodes.StatusTable.Values.Select(v => v.Remediation)
                .Concat(StatusCodes.StatusTable.Values.Select(v => v.Remediation))
                .Where(r => r != null)
                .ToList();
            var offenders = allRemediations
                .Where(r => Regex.IsMatch(r!, @"\blacerte_register\b"))
                .ToList();
            Assert.Empty(offenders);
        }

        [Fact]
        public void UnknownFieldAccess_Remediation_P16_001_P16_016_PointsAtWorkingSurfaces()
        {
            // P16-001 / P16-016 — the prior remediation pointed at two dead-ends:
            // (a) the `lacerte-mcp field-access discover` CLI verb that now exits 3
            // with a redirect, and (b) a `force:true` parameter that no MCP tool
            // exposes. Both made the most-common write-path error a tool-not-found
            // loop. The new remediation must point at: (i) the in-session
            // `lacerte_field_access_map` MCP tool for discovery, (ii) the
            // `LacertMCP.FieldAccessDumper.exe` standalone for populate, and
            // (iii) the `lacerte_reload_schema` MCP tool for refresh.
            var info = SyntheticStatusCodes.StatusTable[SyntheticStatusCodes.UnknownFieldAccess];
            Assert.NotNull(info.Remediation);
            Assert.Contains("lacerte_field_access_map", info.Remediation!);
            Assert.Contains("LacertMCP.FieldAccessDumper.exe", info.Remediation);
            Assert.Contains("lacerte_reload_schema", info.Remediation);
            // The dead-end strings must NOT appear in this catalog entry.
            Assert.DoesNotContain("field-access discover", info.Remediation);
            Assert.DoesNotContain("force:true", info.Remediation);
        }

        public static IEnumerable<object[]> AuthLifecycleRemediations => new[]
        {
            new object[] { SyntheticStatusCodes.AppNotRegistered, true, false },   // register-needs-shell
            new object[] { SyntheticStatusCodes.LoginExpired, false, true },       // login-needs-shell
            new object[] { SyntheticStatusCodes.ReAuthRequired, false, true },     // login (with register fallback)
            new object[] { SyntheticStatusCodes.InvalidAppId, true, false },       // unregister/register
            new object[] { StatusCodes.ConsentDenied, true, false },               // re-register
            new object[] { StatusCodes.RegVersionBlocked, true, false },           // upgrade-then-register
            new object[] { StatusCodes.NotSignedIn, false, true },                 // login
        };

        [Theory]
        [MemberData(nameof(AuthLifecycleRemediations))]
        public void AuthLifecycle_Remediations_FrameCliAsShellCommand_P16_015_P16_018(
            int code, bool referencesRegister, bool referencesLogin)
        {
            // P16-015 / P16-018 — every auth-lifecycle remediation must verb-prefix
            // CLI references with "Ask the operator to run" (or similar shell-command
            // tag) so the LLM cannot confuse the CLI ceremony with a callable MCP
            // tool name. The old "Run lacerte-mcp register" wording read like a
            // bare command name an LLM might try to invoke; the new wording makes
            // the operator-side action explicit.
            var info = code >= LacerteSDKError.SyntheticCodeThreshold
                ? SyntheticStatusCodes.StatusTable[code]
                : StatusCodes.StatusTable[code];
            Assert.NotNull(info.Remediation);
            Assert.Contains("Ask the operator", info.Remediation!);

            // CLI commands must appear hyphenated (lacerte-mcp ...), never
            // underscored (lacerte_register).
            Assert.DoesNotContain("lacerte_register", info.Remediation);

            if (referencesRegister)
            {
                Assert.Contains("lacerte-mcp register", info.Remediation);
            }
            if (referencesLogin)
            {
                Assert.Contains("lacerte-mcp login", info.Remediation);
            }
        }

        [Fact]
        public void InValidClientName_Remediation_DoesNotContradict_ClientIDRegex_P16_019()
        {
            // P16-019 — code 24 is reused for three distinct gates (ClientID
            // whitelist, single-character module, required name fields). The
            // pre-fix remediation said "Use letters, numbers, spaces, hyphen/
            // apostrophe" which actively contradicted CreateClientTools'
            // ^[A-Z0-9]+$ regex — an LLM following the old text retried
            // `O-Reilly`/`O'Reilly` and looped. The new text must NOT advise
            // hyphens/apostrophes on the ClientID path; it MUST steer the
            // operator at context.reason for the gate-specific rule.
            var info = StatusCodes.StatusTable[StatusCodes.InValidClientName];
            Assert.NotNull(info.Remediation);
            // The old contradictory wording must be gone.
            Assert.NotEqual(
                "Use letters, numbers, spaces, hyphen/apostrophe only; avoid < > \" ' & / \\ | ?.",
                info.Remediation);
            // The new text must steer at context.reason and name the canonical
            // ClientID regex so the LLM doesn't invent allowed characters.
            Assert.Contains("context.reason", info.Remediation!);
            Assert.Contains("^[A-Z0-9]+$", info.Remediation);
            // Module-char cases need a hint too.
            Assert.Contains("I/C/P/S/F/R/T/N/B", info.Remediation);
        }

        [Fact]
        public void ContextNotSet_Remediation_NamesDiscoveryTools_P16_042()
        {
            // P16-042 — the first error every LLM consumer hits. The previous
            // "Call lacerte_set_context first" was correct but operationally
            // insufficient — the LLM had no signal that lacerte_list_datapaths
            // is the discovery primitive, no module catalog tool, and no hint
            // about sim-mode seeded paths. The new remediation must name all
            // three so an LLM can build a valid call without a second error.
            var info = SyntheticStatusCodes.StatusTable[SyntheticStatusCodes.ContextNotSet];
            Assert.NotNull(info.Remediation);
            Assert.Contains("lacerte_list_datapaths", info.Remediation!);
            Assert.Contains("lacerte_list_modules", info.Remediation);
            Assert.Contains("lacerte_set_context", info.Remediation);
            // Sim-mode seeded paths surface so an LLM hitting an empty live
            // datapath list off-Windows knows where the in-memory fixtures live.
            Assert.Contains("C:\\Lacerte\\24Tax\\IData", info.Remediation);
        }

        [Fact]
        public void RemediationStrings_NoAllCapsIdSuffix_P17_006()
        {
            // P17-006 — sister sentinel to NamingConventionsTests'
            // ToolParameters_NoAllCapsIdSuffix_P16_054_P16_080. The
            // parameter-side test pins the wire param spellings (clientId /
            // emailId / subClientId — camelCase `Id` per Microsoft Framework
            // Design Guidelines). This test pins the SAME convention on
            // every UserMessage / Remediation string in both catalogs so an
            // LLM reading an error envelope and copy-pasting the parameter
            // name back into a retry never sees the ALL-CAPS form. Prose
            // mentions of the SDK's C# `ClientID` constructor or the wire-
            // emitted StatusInfo.Name discriminators (e.g. "SubClientIDRequired")
            // are excluded by inspecting only UserMessage + Remediation.
            //
            // Two-pronged catch: extending the regex to the four variants
            // (clientID / ClientID / emailID / EmailID / subClientID /
            // SubClientID) future-proofs against drift on related fields the
            // next time the catalog grows.
            var banned = new Regex(@"\b(?:subClientID|SubClientID|clientID|ClientID|emailID|EmailID)\b");

            var allEntries =
                SyntheticStatusCodes.StatusTable.Select(kv => new { Code = kv.Key, Info = kv.Value, Catalog = "synthetic" })
                .Concat(StatusCodes.StatusTable.Select(kv => new { Code = kv.Key, Info = kv.Value, Catalog = "sdk" }));

            var offenders = allEntries
                .SelectMany(e => new[]
                {
                    new { e.Code, e.Catalog, Field = "UserMessage", Text = (string?)e.Info.UserMessage },
                    new { e.Code, e.Catalog, Field = "Remediation", Text = (string?)e.Info.Remediation },
                })
                .Where(x => x.Text != null && banned.IsMatch(x.Text!))
                .Select(x => $"[{x.Catalog} {x.Code}].{x.Field}: \"{x.Text}\"")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "Catalog UserMessage / Remediation strings must use camelCase `Id` (clientId / emailId / subClientId), not ALL-CAPS `ID`. Offenders: "
                + string.Join("; ", offenders));
        }

        [Theory]
        [InlineData(SyntheticStatusCodes.BridgeUnavailable)]
        [InlineData(SyntheticStatusCodes.InternalServerError)]
        public void BridgeAndInternalErrorRemediations_NameCidAndLogPath_P16_065(int code)
        {
            // P16-065 — when the bridge crashes or the server throws, the
            // envelope already carries `cid` (the per-request correlation ID
            // stamped by WrapToolHandler.cs) and the wire log already lands
            // under ./logs/lacerte-YYYYMMDD.log. The pre-fix remediation
            // never closed the loop by telling the operator the cid IS the
            // grep token, never named the resolved-log-path lookup
            // (`lacerte_session_status`), and never warned that
            // LACERTE_DEBUG_WIRE must be ON BEFORE the failure. The new
            // text must mention all three.
            var info = SyntheticStatusCodes.StatusTable[code];
            Assert.NotNull(info.Remediation);
            Assert.Contains("cid", info.Remediation!);
            Assert.Contains("LACERTE_DEBUG_WIRE", info.Remediation);
            Assert.Contains("lacerte_session_status", info.Remediation);
            Assert.Contains("BEFORE", info.Remediation);
        }
    }
}
