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

        [Theory]
        [InlineData('R', "RData")]
        [InlineData('T', "TData")]
        [InlineData('N', "NData")]
        [InlineData('B', "BData")]
        public async Task PreFilingCheck_UnsupportedModule_ReturnsUnsupportedWithSupportedSetHint(
            char module, string folder)
        {
            // R6 module expansion — C/P/S run the catalog, and FP-4 added
            // Fiduciary ('F'), so the unsupported-module envelope is now
            // exercised via R/T/N/B (previously via F). The module gate
            // fires BEFORE the existence check (R6-13 gate order), so an
            // empty, never-seeded store still returns the envelope.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(@"C:\Lacerte\24Tax\" + folder, 2024, module);
            var tools = new PreFilingCheckTools(mgr);
            var root = ToolTestHelpers.Parse(
                await tools.LacertePreFilingCheck("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("unsupported-module", result.GetProperty("readiness").GetString());
            Assert.False(result.GetProperty("passed").GetBoolean());
            // R6-18 — single-char module code on the wire.
            Assert.Equal(module.ToString(), result.GetProperty("module").GetString());
            // The hint + skip reason must advertise the full supported set.
            var hint = result.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("I, P, C, S, and F", hint);
            var reason = result.GetProperty("skippedRules")[0].GetProperty("reason").GetString();
            Assert.NotNull(reason);
            Assert.Contains("Partnership", reason);
            Assert.Contains("S-Corporation", reason);
            Assert.Contains("Fiduciary", reason);
        }

        // ----------------------------------------------------------------
        // FP-4 — S-Corporation (seeded ORCH / HOLW) and Fiduciary (seeded
        // MAPL / BIRC) catalogs.
        // ----------------------------------------------------------------

        [Fact]
        public async Task PreFilingCheck_SCorp_OrchWithShareholders_IsReady()
        {
            var result = await RunCheck(SeedData.DataPathSData2024, 'S', SeedData.ORCH.ClientNumber);
            Assert.Equal("S", result.GetProperty("module").GetString());
            Assert.Equal("ready", result.GetProperty("readiness").GetString());
            Assert.True(result.GetProperty("passed").GetBoolean());
            Assert.Equal(0, result.GetProperty("blockers").GetArrayLength());
            Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());

            Assert.Equal(
                new[] { "required-name", "required-email", "forms-at-least-one", "forms-has-1120s", "status-rows-present", "shareholders-present" },
                ToStringList(result.GetProperty("checkedRules")));
        }

        [Fact]
        public async Task PreFilingCheck_SCorp_HolwWithoutShareholders_IsBlocked()
        {
            var result = await RunCheck(SeedData.DataPathSData2024, 'S', SeedData.HOLW.ClientNumber);
            Assert.Equal("blocked", result.GetProperty("readiness").GetString());
            Assert.False(result.GetProperty("passed").GetBoolean());
            // The only finding is the missing shareholder roster.
            Assert.Equal(new[] { "shareholders-present" }, RuleIds(result.GetProperty("blockers")));
            Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());
            var blocker = result.GetProperty("blockers")[0];
            Assert.Equal("DATA2", blocker.GetProperty("field").GetString());
            Assert.Contains("1120-S", blocker.GetProperty("message").GetString());
        }

        [Fact]
        public async Task PreFilingCheck_Fiduciary_MaplWithBeneficiaries_IsReady()
        {
            var result = await RunCheck(SeedData.DataPathFData2024, 'F', SeedData.MAPL.ClientNumber);
            Assert.Equal("F", result.GetProperty("module").GetString());
            Assert.Equal("ready", result.GetProperty("readiness").GetString());
            Assert.True(result.GetProperty("passed").GetBoolean());

            Assert.Equal(
                new[] { "required-name", "required-email", "forms-at-least-one", "forms-has-1041", "status-rows-present", "beneficiaries-present" },
                ToStringList(result.GetProperty("checkedRules")));
        }

        [Fact]
        public async Task PreFilingCheck_Fiduciary_BircWithoutBeneficiaries_WarnsNotBlocks()
        {
            // 0 beneficiaries is a WARNING (grantor trusts / estates with no
            // distributions can legitimately have none), never a blocker.
            var result = await RunCheck(SeedData.DataPathFData2024, 'F', SeedData.BIRC.ClientNumber);
            Assert.Equal("warn", result.GetProperty("readiness").GetString());
            Assert.False(result.GetProperty("passed").GetBoolean());
            Assert.Equal(0, result.GetProperty("blockers").GetArrayLength());
            Assert.Equal(new[] { "beneficiaries-present" }, RuleIds(result.GetProperty("warnings")));
            var message = result.GetProperty("warnings")[0].GetProperty("message").GetString();
            Assert.Contains("grantor trust", message);
            Assert.Contains("Schedule K-1", message);
        }

        [Fact]
        public async Task PreFilingCheck_Fiduciary_BircStrict_PromotesBeneficiaryWarningToBlocker()
        {
            var result = await RunCheck(SeedData.DataPathFData2024, 'F', SeedData.BIRC.ClientNumber, "strict");
            Assert.Equal("blocked", result.GetProperty("readiness").GetString());
            Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());
            var blocker = result.GetProperty("blockers")[0];
            Assert.Equal("beneficiaries-present", blocker.GetProperty("ruleId").GetString());
            Assert.True(blocker.GetProperty("promoted").GetBoolean());
        }

        [Fact]
        public async Task PreFilingCheck_Fiduciary_FreshClient_ReportsMissing1041AndWarns()
        {
            // A client minted through CreateClient has DATA1 only — the 1041
            // main-form rule must block, the missing status row and missing
            // beneficiaries warn.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathFData2024, 2024, 'F');
            var created = await mgr.CreateClient(new CreateClientArgs(
                "ASPN", "1041",
                FirstName: "Aspen Grove Trust",
                EmailId: "trustee@aspengrove.example.com"));
            Assert.NotNull(created.ClientNumber);

            var root = ToolTestHelpers.Parse(
                await new PreFilingCheckTools(mgr).LacertePreFilingCheck(created.ClientNumber!));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal("blocked", result.GetProperty("readiness").GetString());
            var blockerIds = RuleIds(result.GetProperty("blockers"));
            Assert.Contains("forms-at-least-one", blockerIds);
            Assert.Contains("forms-has-1041", blockerIds);
            Assert.DoesNotContain("beneficiaries-present", blockerIds);
            var warningIds = RuleIds(result.GetProperty("warnings"));
            Assert.Contains("status-rows-present", warningIds);
            Assert.Contains("beneficiaries-present", warningIds);
        }

        [Theory]
        [InlineData('I', "partners-present", "shareholders-present", "beneficiaries-present")]
        [InlineData('C', "partners-present", "shareholders-present", "beneficiaries-present")]
        [InlineData('P', "shareholders-present", "beneficiaries-present", null)]
        [InlineData('S', "partners-present", "beneficiaries-present", null)]
        [InlineData('F', "partners-present", "shareholders-present", null)]
        public async Task PreFilingCheck_OwnerRosterRules_SkippedOnEveryOtherModule(
            char module, string skipA, string skipB, string? skipC)
        {
            var (path, client) = SeededTarget(module);
            var result = await RunCheck(path, module, client);
            var skippedIds = SkippedRuleIds(result);
            var checkedRules = ToStringList(result.GetProperty("checkedRules"));

            var expectedSkipped = new[] { skipA, skipB, skipC }.Where(s => s != null).Cast<string>().ToList();
            var allOwnerRules = new[] { "partners-present", "shareholders-present", "beneficiaries-present" };
            foreach (var rule in allOwnerRules)
            {
                if (expectedSkipped.Contains(rule))
                {
                    Assert.Contains(rule, skippedIds);
                    Assert.DoesNotContain(rule, checkedRules);
                }
                else
                {
                    // The module's own roster rule runs and is never skipped.
                    Assert.Contains(rule, checkedRules);
                    Assert.DoesNotContain(rule, skippedIds);
                }
            }

            // Every not-applicable skip carries a reason naming the module.
            foreach (var skip in result.GetProperty("skippedRules").EnumerateArray())
            {
                var id = skip.GetProperty("ruleId").GetString();
                if (!allOwnerRules.Contains(id)) continue;
                Assert.Contains("Not applicable to module '" + module + "'", skip.GetProperty("reason").GetString());
            }

            // Individual-only rules stay skipped on every entity module.
            if (module != 'I')
            {
                Assert.Contains("required-last-name", skippedIds);
                Assert.Contains("dependents-have-description", skippedIds);
            }
            // F5 deferrals are present everywhere.
            Assert.Contains("status-locked", skippedIds);
            Assert.Contains("ef-ready-flag", skippedIds);
        }

        [Fact]
        public async Task PreFilingCheck_SCorp_OwnershipTotalRule_IsDeferredNotRun()
        {
            // Ownership % summing to 100 needs the verified FP-3 field
            // dictionary — it must surface as a skipped deferral on S only.
            var sResult = await RunCheck(SeedData.DataPathSData2024, 'S', SeedData.ORCH.ClientNumber);
            Assert.Contains("shareholder-ownership-totals-100", SkippedRuleIds(sResult));
            Assert.DoesNotContain("shareholder-ownership-totals-100",
                ToStringList(sResult.GetProperty("checkedRules")));

            var fResult = await RunCheck(SeedData.DataPathFData2024, 'F', SeedData.MAPL.ClientNumber);
            Assert.DoesNotContain("shareholder-ownership-totals-100", SkippedRuleIds(fResult));
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
            // Mint a fresh S-Corp through the session's CreateClient path
            // (DATA1 row + empty detail file only — no DATA7 forms, no DATA5
            // status, no DATA2 shareholders) alongside the FP-5 seeded
            // ORCH/HOLW. The catalog must report the missing pieces
            // honestly: forms + shareholder blockers + status warning.
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
            Assert.Contains("shareholders-present", blockerIds);
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

        /// <summary>Set context, run the check on one client, return `result`.</summary>
        private static async Task<JsonElement> RunCheck(
            string dataPath, char module, string clientNumber, string severity = "normal")
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(dataPath, 2024, module);
            var root = ToolTestHelpers.Parse(
                await new PreFilingCheckTools(mgr).LacertePreFilingCheck(clientNumber, severity));
            Assert.True(root.GetProperty("success").GetBoolean());
            return root.GetProperty("result");
        }

        /// <summary>A fully-populated seeded client for each supported module.</summary>
        private static (string Path, string ClientNumber) SeededTarget(char module) => module switch
        {
            'I' => (SeedData.DataPathIData2024, SeedData.BROW.ClientNumber),
            'C' => (SeedData.DataPathCData2024, SeedData.ACME.ClientNumber),
            'P' => (SeedData.DataPathPData2024, SeedData.PART.ClientNumber),
            'S' => (SeedData.DataPathSData2024, SeedData.ORCH.ClientNumber),
            'F' => (SeedData.DataPathFData2024, SeedData.MAPL.ClientNumber),
            _ => throw new System.ArgumentOutOfRangeException(nameof(module)),
        };

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
