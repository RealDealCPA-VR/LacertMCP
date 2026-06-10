using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-034 — `lacerte_pre_filing_check` (X_feat_accel cluster).
    /// </summary>
    [Xunit.Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class PreFilingCheckToolsTests
    {
        [Fact]
        public async Task PreFilingCheck_NoContext_Returns9009()
        {
            var tools = new PreFilingCheckTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacertePreFilingCheck("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task PreFilingCheck_MissingClient_ReturnsFoundFalseWithHint_R6_13()
        {
            // R6-13 — unknown clientNumber is a SUCCESS envelope with
            // {found:false, hint}, matching lacerte_client_read /
            // lacerte_client_summary. The old behavior threw SDK OutOfIndex
            // (4), whose catalog remediation is about pre-TY21 row writes.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck("9999"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("found").GetBoolean());
            Assert.Equal("9999", result.GetProperty("clientNumber").GetString());
            var hint = result.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("9999", hint);
            Assert.Contains("lacerte_client_list", hint);
        }

        [Fact]
        public async Task PreFilingCheck_HappyPath_ReturnsReadyOrWarn()
        {
            // BROW is a fully-populated TY24 individual: name, email,
            // forms with 1040 row, DATA5 status row present. Should be
            // "ready" on the v1 catalog.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            var readiness = result.GetProperty("readiness").GetString();
            Assert.True(readiness == "ready" || readiness == "warn",
                "Expected ready or warn for fully-populated BROW; got " + readiness);
            Assert.True(result.GetProperty("checkedRules").GetArrayLength() >= 6);
        }

        [Fact]
        public async Task PreFilingCheck_UnsupportedModule_ReturnsUnsupportedWithSupportedSetHint()
        {
            // R6 module expansion — C/P/S now run the catalog, so the
            // unsupported-module envelope is exercised via Fiduciary ('F').
            // The module gate fires BEFORE the existence check (R6-13 gate
            // order), so an empty FData store still returns the envelope.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(@"C:\Lacerte\24Tax\FData", 2024, 'F');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("unsupported-module", result.GetProperty("readiness").GetString());
            Assert.False(result.GetProperty("passed").GetBoolean());
            // R6-18 — single-char module code on the wire.
            Assert.Equal("F", result.GetProperty("module").GetString());
            // The hint + skip reason must advertise the full supported set.
            var hint = result.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("I", hint);
            Assert.Contains("P", hint);
            Assert.Contains("C", hint);
            Assert.Contains("S", hint);
            var reason = result.GetProperty("skippedRules")[0].GetProperty("reason").GetString();
            Assert.NotNull(reason);
            Assert.Contains("Partnership", reason);
            Assert.Contains("S-Corporation", reason);
        }

        [Fact]
        public async Task PreFilingCheck_Corporate_RunsEntityCatalog()
        {
            // ACME is a fully-populated TY24 corp: entity name, email,
            // DATA7 1120 row, DATA5 status row → "ready" on the entity
            // catalog. Individual-only rules must surface as skipped,
            // never silently passed.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.ACME.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("C", result.GetProperty("module").GetString());
            Assert.Equal("ready", result.GetProperty("readiness").GetString());
            Assert.True(result.GetProperty("passed").GetBoolean());

            var checkedRules = ToStringList(result.GetProperty("checkedRules"));
            Assert.Contains("required-name", checkedRules);
            Assert.Contains("required-email", checkedRules);
            Assert.Contains("forms-at-least-one", checkedRules);
            Assert.Contains("forms-has-1120", checkedRules);
            Assert.Contains("status-rows-present", checkedRules);
            // Individual-only rules must NOT run on C…
            Assert.DoesNotContain("required-last-name", checkedRules);
            Assert.DoesNotContain("dependents-have-description", checkedRules);
            // …and must be reported as skipped with a reason.
            var skippedIds = SkippedRuleIds(result);
            Assert.Contains("required-last-name", skippedIds);
            Assert.Contains("dependents-have-description", skippedIds);
        }

        [Fact]
        public async Task PreFilingCheck_Partnership_ChecksPartnersAnd1065()
        {
            // PART has entity name, email, DATA7 1065 row, DATA5 status
            // row, and 3 DATA2 partner rows → "ready".
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.PART.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("P", result.GetProperty("module").GetString());
            Assert.Equal("ready", result.GetProperty("readiness").GetString());

            var checkedRules = ToStringList(result.GetProperty("checkedRules"));
            Assert.Contains("forms-has-1065", checkedRules);
            Assert.Contains("partners-present", checkedRules);
            Assert.DoesNotContain("forms-has-1040", checkedRules);
        }

        [Fact]
        public async Task PreFilingCheck_SCorp_FreshClient_ReportsHonestBlockers()
        {
            // No S-Corp seed exists, so mint one through the session's
            // CreateClient path (DATA1 row + empty detail file only — no
            // DATA7 forms, no DATA5 status). The catalog must report the
            // missing pieces honestly: forms blockers + status warning.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(@"C:\Lacerte\24Tax\SData", 2024, 'S');
            var created = await mgr.CreateClient(new CreateClientArgs(
                "SCRP", "1120S",
                FirstName: "Sunrise Consulting Inc",
                EmailId: "ops@sunrise.example.com"));
            Assert.NotNull(created.ClientNumber);

            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(created.ClientNumber!));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("S", result.GetProperty("module").GetString());
            Assert.Equal("blocked", result.GetProperty("readiness").GetString());

            var checkedRules = ToStringList(result.GetProperty("checkedRules"));
            Assert.Contains("forms-has-1120s", checkedRules);

            var blockerIds = RuleIds(result.GetProperty("blockers"));
            Assert.Contains("forms-at-least-one", blockerIds);
            Assert.Contains("forms-has-1120s", blockerIds);
            // Name + email were supplied at create time — not blockers.
            Assert.DoesNotContain("required-name", blockerIds);
            Assert.DoesNotContain("required-email", blockerIds);
            // Missing DATA5 status row stays a warning under severity=normal.
            var warningIds = RuleIds(result.GetProperty("warnings"));
            Assert.Contains("status-rows-present", warningIds);
        }

        [Fact]
        public async Task PreFilingCheck_Individual_StillEmitsModuleAnd1040Rule()
        {
            // Guard the expanded envelope against I-module regression:
            // module key present (single char) and the legacy forms-has-1040
            // ruleId unchanged.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("I", result.GetProperty("module").GetString());
            var checkedRules = ToStringList(result.GetProperty("checkedRules"));
            Assert.Contains("forms-has-1040", checkedRules);
            Assert.Contains("required-last-name", checkedRules);
            Assert.Contains("dependents-have-description", checkedRules);
            Assert.DoesNotContain("partners-present", checkedRules);
        }

        [Fact]
        public async Task PreFilingCheck_StrictPromotesWarningsToBlockers()
        {
            // Normal vs strict on the same client — strict path may have
            // a different readiness if any warnings exist.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);

            var normal = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber, severity: "normal"));
            var strict = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber, severity: "strict"));
            Assert.True(normal.GetProperty("success").GetBoolean());
            Assert.True(strict.GetProperty("success").GetBoolean());

            // If normal produced warnings, strict must produce blockers
            // of at least equal count.
            int normalWarnings = normal.GetProperty("result").GetProperty("warnings").GetArrayLength();
            int normalBlockers = normal.GetProperty("result").GetProperty("blockers").GetArrayLength();
            int strictBlockers = strict.GetProperty("result").GetProperty("blockers").GetArrayLength();
            int strictWarnings = strict.GetProperty("result").GetProperty("warnings").GetArrayLength();

            Assert.Equal(0, strictWarnings); // strict has no warnings (all promoted)
            Assert.Equal(normalWarnings + normalBlockers, strictBlockers);
        }

        [Fact]
        public async Task PreFilingCheck_SkippedRules_CarryF5Deferral()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber));
            Assert.True(root.GetProperty("success").GetBoolean());
            var skipped = root.GetProperty("result").GetProperty("skippedRules");
            Assert.True(skipped.GetArrayLength() >= 2);
        }

        [Fact]
        public async Task PreFilingCheck_InvalidSeverity_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck(SeedData.BROW.ClientNumber, severity: "MEDIUM"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("severity",
                root.GetProperty("context").GetProperty("argument").GetString());
        }

        [Fact]
        public async Task PreFilingCheck_EmptyClientNumber_ReturnsInvalidArgument()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck("  "));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        /// <summary>Project a JSON string array into a List&lt;string&gt;.</summary>
        private static List<string> ToStringList(JsonElement array)
            => array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();

        /// <summary>ruleId values from a blockers[]/warnings[] finding array.</summary>
        private static List<string> RuleIds(JsonElement findings)
            => findings.EnumerateArray()
                .Select(f => f.GetProperty("ruleId").GetString() ?? string.Empty)
                .ToList();

        /// <summary>ruleId values from the envelope's skippedRules[] array.</summary>
        private static List<string> SkippedRuleIds(JsonElement result)
            => RuleIds(result.GetProperty("skippedRules"));
    }
}
