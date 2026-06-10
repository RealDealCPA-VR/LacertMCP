using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-034 — `lacerte_pre_filing_check`. Pre-flight gate that runs a
    /// catalog of small rules against one client to surface "this return
    /// will fail e-file" before the operator hits transmit. Rules depend
    /// ONLY on currently-confirmed schema knowledge (DATA1 demographics +
    /// DATA2/5/7 row presence + Series=14 dependent descriptions); rules
    /// requiring the not-yet-landed F5 status-field code map are deferred
    /// to v1.1 with an explicit `skippedRules` surface so the operator
    /// sees what didn't run.
    ///
    /// <para>Severity:</para>
    /// <list type="bullet">
    ///   <item><description><c>strict</c> — every <c>warn</c> is promoted to <c>blocker</c>.</description></item>
    ///   <item><description><c>normal</c> (default) — warnings stay warnings.</description></item>
    /// </list>
    ///
    /// <para>Module gate (R6 gap "expand beyond module I"): the catalog
    /// covers the four modules a solo practitioner files — Individual
    /// ('I', 1040), Partnership ('P', 1065), Corporate ('C', 1120), and
    /// S-Corporation ('S', 1120S). Module-agnostic rules (required-name,
    /// required-email, forms-at-least-one, main-form presence,
    /// status-rows-present) run everywhere; Individual-only rules
    /// (required-last-name, dependents-have-description) are reported as
    /// <c>skippedRules</c> entries on entity modules rather than silently
    /// passed; Partnership adds <c>partners-present</c> over DATA2.
    /// F/R/T/N/B return <c>readiness='unsupported-module'</c> with a hint
    /// pointing at the rule-registry extension point.</para>
    /// </summary>
    [McpServerToolType]
    public sealed class PreFilingCheckTools
    {
        private readonly LacerteSessionManager _session;

        public PreFilingCheckTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Pre-filing readiness check — runs a small catalog of rules against ONE client in the active (datapath, taxYear, module) and returns whether the return is ready to e-file. Covers Individual ('I', 1040), Partnership ('P', 1065), Corporate ('C', 1120), and S-Corporation ('S', 1120S); other modules (F/R/T/N/B) return readiness='unsupported-module'. Module-agnostic rules: required-name (C1_1 non-empty — first name for I, entity name for P/C/S), required-email (C1_3 non-empty), forms-at-least-one (DATA7 not empty), main-form presence (DATA7 has a C7_1 row containing the module's main form: forms-has-1040 / forms-has-1065 / forms-has-1120 / forms-has-1120s), status-rows-present (DATA5 returns at least one row — surfaces 'never run F4 status' as a warning, not a blocker). Individual-only rules: required-last-name (C1_2 non-empty), dependents-have-description (every Series=14 detail row has non-empty Description) — on P/C/S these are listed in skippedRules with a not-applicable reason, never silently passed. Partnership-only rule: partners-present (DATA2 partner rows — 0 rows is a blocker, 1 row warns because a 1065 needs at least two partners). Output: { clientNumber, module, readiness: 'ready'|'blocked'|'warn'|'unsupported-module', passed, blockers[], warnings[], checkedRules[], skippedRules[] } — module is the single-character DBF code. Unknown clientNumber returns { clientNumber, found:false, hint } (success envelope, same shape as lacerte_client_read). Gate order is fixed: input validation, then context, then module support, then existence. severity='strict' promotes every warning to a blocker.")]
        public Task<string> LacertePreFilingCheck(
            [Description("ClientNumber (SDK-minted monotonic ID, e.g. '1001') to check. Must exist in the active context.")] string clientNumber,
            [Description("Severity policy — 'normal' (default) keeps warnings warnings; 'strict' promotes every warning to a blocker so readiness collapses to 'blocked' if any rule warns.")] string severity = "normal",
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // R6-13 — documented gate order, identical for every module
                // so response shape never depends on which gate fired first:
                //   1. input validation (clientNumber, severity)
                //   2. session context present (9009)
                //   3. module support (unsupported-module envelope)
                //   4. client existence ({found:false, hint} envelope)
                if (string.IsNullOrWhiteSpace(clientNumber))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "clientNumber",
                            ["value"] = clientNumber,
                            ["reason"] = "clientNumber is required.",
                        });
                }

                var sev = (severity ?? "normal").Trim().ToLowerInvariant();
                if (sev != "normal" && sev != "strict")
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["argument"] = "severity",
                            ["value"] = severity,
                            ["reason"] = "severity must be 'normal' or 'strict' (lowercase).",
                        });
                }

                // Pull active context — fail with 9009 if not set.
                var snapshot = _session.SessionStatus();
                var ctx = snapshot.Context;
                if (ctx == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
                }

                // Module gate — the catalog covers the four modules a solo
                // practitioner files: I (1040), P (1065), C (1120), S (1120S).
                char mod = ctx.Module;
                if (mod != 'I' && mod != 'P' && mod != 'C' && mod != 'S')
                {
                    return (object)new
                    {
                        clientNumber,
                        // R6-18 — `module` wire key carries the single-char
                        // DBF code, never a friendly name.
                        module = mod.ToString(),
                        readiness = "unsupported-module",
                        passed = false,
                        blockers = Array.Empty<object>(),
                        warnings = Array.Empty<object>(),
                        checkedRules = Array.Empty<object>(),
                        skippedRules = new[]
                        {
                            new
                            {
                                ruleId = "module-catalog",
                                reason = "Module '" + mod + "' has no rule catalog. Supported modules: Individual ('I'), Partnership ('P'), Corporate ('C'), S-Corporation ('S'). Rule sets for F/R/T/N/B will be added in a future release; until then, surface readiness manually via lacerte_client_read / lacerte_table_read / lacerte_detail_read.",
                            },
                        },
                        hint = "lacerte_pre_filing_check covers modules I, P, C, and S. Set context to one of those modules to exercise the catalog.",
                    };
                }

                // Read the inputs the rules consume.
                var clientRow = await _session.ReadClient(clientNumber, cancellationToken)
                    .ConfigureAwait(false);
                if (clientRow == null)
                {
                    // R6-13 — unknown clientNumber is a success envelope with
                    // {found:false, hint}, matching every sibling read tool
                    // (see ClientsTools.LacerteClientRead). The old behavior
                    // threw SDK OutOfIndex (4), whose catalog remediation
                    // talks about pre-TY21 row writes — nonsense here.
                    return (object)new
                    {
                        clientNumber,
                        found = false,
                        hint = $"ClientNumber '{clientNumber}' not present in the active (datapath, year, module). Call lacerte_client_list to enumerate available ClientNumbers, or lacerte_create_client to create one. Note: ClientNumber is the SDK-minted identifier (sim's monotonic counter, e.g. \"1001\"), distinct from the operator-chosen ClientID (e.g. \"BROW\") — these are not interchangeable.",
                    };
                }

                var statusRows = await _session.ReadStatuses(clientNumber, cancellationToken)
                    .ConfigureAwait(false);
                var formRows = await _session.ReadForms(clientNumber, cancellationToken)
                    .ConfigureAwait(false);

                // Run the per-module rule catalog. Each rule produces a
                // Finding; Finding.Severity gates whether it lands in
                // blockers or warnings on the final envelope. Rules that
                // CANNOT be evaluated for the active module are reported in
                // skippedRules with a reason — never silently passed.
                var findings = new List<Finding>();
                var checkedRules = new List<string>();
                var skippedRules = new List<object>();

                // ---- module-agnostic rules (I/P/C/S) ----
                CheckRequiredName(clientRow, mod, findings, checkedRules);
                CheckRequiredEmail(clientRow, findings, checkedRules);
                CheckFormsAtLeastOne(formRows, findings, checkedRules);
                CheckFormsHasMainForm(formRows, mod, findings, checkedRules);
                CheckStatusRowsPresent(statusRows, findings, checkedRules);

                // ---- Individual-only rules ----
                if (mod == 'I')
                {
                    var detailRows = await _session.ReadDetail(clientNumber, cancellationToken)
                        .ConfigureAwait(false);
                    CheckRequiredLastName(clientRow, findings, checkedRules);
                    CheckDependentsHaveDescription(detailRows, findings, checkedRules);
                }
                else
                {
                    skippedRules.Add(new
                    {
                        ruleId = "required-last-name",
                        reason = "Not applicable to module '" + mod + "' — DATA1 C1_2 (last name) is an Individual-only field; entity modules carry the entity name in C1_1 (covered by required-name).",
                    });
                    skippedRules.Add(new
                    {
                        ruleId = "dependents-have-description",
                        reason = "Not applicable to module '" + mod + "' — Series=14 dependent detail rows are Individual-only.",
                    });
                }

                // ---- Partnership-only rule ----
                if (mod == 'P')
                {
                    var partnerRows = await _session.ReadSubclients(clientNumber, cancellationToken)
                        .ConfigureAwait(false);
                    CheckPartnersPresent(partnerRows, findings, checkedRules);
                }

                // F5-gated rules (v1.1) — surface the deferral so the LLM
                // knows what's missing.
                skippedRules.Add(new
                {
                    ruleId = "status-locked",
                    reason = "Requires F5 FieldAccessDumper landing — DATA5 status-field code (locked vs unlocked) is not yet mapped. Tracked for v1.1.",
                });
                skippedRules.Add(new
                {
                    ruleId = "ef-ready-flag",
                    reason = "Requires F5 FieldAccessDumper landing — DATA5 efReady-field code is not yet mapped. Tracked for v1.1.",
                });

                // Promote warnings if severity='strict'.
                bool strict = sev == "strict";
                var blockers = new List<object>();
                var warnings = new List<object>();
                foreach (var f in findings)
                {
                    var renderedFinding = new
                    {
                        ruleId = f.RuleId,
                        message = f.Message,
                        field = f.Field,
                        promoted = strict && f.Severity == "warn",
                    };
                    if (f.Severity == "blocker" || (strict && f.Severity == "warn"))
                    {
                        blockers.Add(renderedFinding);
                    }
                    else if (f.Severity == "warn")
                    {
                        warnings.Add(renderedFinding);
                    }
                }

                string readiness =
                    blockers.Count > 0 ? "blocked" :
                    warnings.Count > 0 ? "warn" :
                    "ready";

                return new
                {
                    clientNumber,
                    // R6-18 — `module` wire key carries the single-char
                    // DBF code, never a friendly name.
                    module = mod.ToString(),
                    readiness,
                    passed = readiness == "ready",
                    blockers,
                    warnings,
                    checkedRules,
                    skippedRules,
                };
            });

        // ----------------------------------------------------------------
        // Rule implementations
        // ----------------------------------------------------------------

        private static void CheckRequiredName(
            IReadOnlyDictionary<string, object?> client,
            char module,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "required-name";
            checkedRules.Add(ruleId);
            if (!TryGetNonEmpty(client, "C1_1", out _))
            {
                string what = module == 'I' ? "first name" : "entity name";
                findings.Add(new Finding(ruleId, "blocker",
                    $"DATA1 field C1_1 ({what}) is empty. The Lacerte SDK will reject the e-file with a 'name required' error.",
                    "C1_1"));
            }
        }

        private static void CheckRequiredLastName(
            IReadOnlyDictionary<string, object?> client,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "required-last-name";
            checkedRules.Add(ruleId);
            if (!TryGetNonEmpty(client, "C1_2", out _))
            {
                findings.Add(new Finding(ruleId, "blocker",
                    "DATA1 field C1_2 (last name) is empty. Required for Individual e-file.",
                    "C1_2"));
            }
        }

        private static void CheckRequiredEmail(
            IReadOnlyDictionary<string, object?> client,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "required-email";
            checkedRules.Add(ruleId);
            if (!TryGetNonEmpty(client, "C1_3", out _))
            {
                findings.Add(new Finding(ruleId, "blocker",
                    "DATA1 field C1_3 (email) is empty. Required for e-file confirmation and signature roundtrip.",
                    "C1_3"));
            }
        }

        private static void CheckFormsAtLeastOne(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> forms,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "forms-at-least-one";
            checkedRules.Add(ruleId);
            if (forms == null || forms.Count == 0)
            {
                findings.Add(new Finding(ruleId, "blocker",
                    "DATA7 forms table is empty for this client — there is nothing to e-file. Add at least one form row before attempting transmission.",
                    "DATA7"));
            }
        }

        /// <summary>
        /// Module-aware main-form presence rule. Generalizes the v1
        /// <c>forms-has-1040</c> rule: each supported module has one main
        /// form whose token must appear in some DATA7 <c>C7_1</c> value —
        /// I→1040, P→1065, C→1120, S→1120S. The ruleId stays per-module
        /// (<c>forms-has-1040</c> / <c>forms-has-1065</c> /
        /// <c>forms-has-1120</c> / <c>forms-has-1120s</c>) so envelopes
        /// remain self-describing and the I-module wire shape is unchanged.
        /// Note the C-module 'contains 1120' check also matches an 1120S
        /// row — acceptable looseness: a C-module datapath holding an 1120S
        /// form row is an operator filing error this rule doesn't police.
        /// </summary>
        private static void CheckFormsHasMainForm(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> forms,
            char module,
            List<Finding> findings,
            List<string> checkedRules)
        {
            string token;
            string ruleId;
            string filing;
            switch (module)
            {
                case 'P': token = "1065"; ruleId = "forms-has-1065"; filing = "Partnership"; break;
                case 'C': token = "1120"; ruleId = "forms-has-1120"; filing = "Corporate"; break;
                case 'S': token = "1120S"; ruleId = "forms-has-1120s"; filing = "S-Corporation"; break;
                default: token = "1040"; ruleId = "forms-has-1040"; filing = "Individual"; break;
            }

            checkedRules.Add(ruleId);
            bool hasMainForm = false;
            if (forms != null)
            {
                foreach (var row in forms)
                {
                    if (row.TryGetValue("C7_1", out var v) && v is string s
                        && s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hasMainForm = true;
                        break;
                    }
                }
            }
            if (!hasMainForm)
            {
                findings.Add(new Finding(ruleId, "blocker",
                    $"{filing} e-file requires a {token} row in DATA7. No DATA7 row in C7_1 contained '{token}'.",
                    "DATA7.C7_1"));
            }
        }

        /// <summary>
        /// Partnership-only rule — a 1065 is structurally a pass-through of
        /// its DATA2 partner (SubClient) rows. Zero rows means there is no
        /// K-1 to generate (blocker); exactly one row warns because the IRS
        /// requires at least two partners for a partnership return.
        /// </summary>
        private static void CheckPartnersPresent(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> partners,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "partners-present";
            checkedRules.Add(ruleId);
            int count = partners?.Count ?? 0;
            if (count == 0)
            {
                findings.Add(new Finding(ruleId, "blocker",
                    "DATA2 has no partner (SubClient) rows for this client — a 1065 cannot be filed without partners; there is nothing to issue K-1s against.",
                    "DATA2"));
            }
            else if (count == 1)
            {
                findings.Add(new Finding(ruleId, "warn",
                    "DATA2 has only one partner (SubClient) row — the IRS requires at least two partners for a 1065. Verify the partner roster before transmitting.",
                    "DATA2"));
            }
        }

        private static void CheckDependentsHaveDescription(
            IReadOnlyList<DetailRow> detailRows,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "dependents-have-description";
            checkedRules.Add(ruleId);
            if (detailRows == null) return;
            int missing = 0;
            foreach (var r in detailRows)
            {
                // Series "14" = Dependents per SeriesIds.Dep.
                if (r.Series == "14" && string.IsNullOrWhiteSpace(r.Description))
                {
                    missing++;
                }
            }
            if (missing > 0)
            {
                findings.Add(new Finding(ruleId, "blocker",
                    $"{missing} dependent detail row(s) (Series=14) have an empty Description — the IRS rejects 1040 e-files when a dependent has no name/relationship in the Description field.",
                    "Series=14.Description"));
            }
        }

        private static void CheckStatusRowsPresent(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> statuses,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "status-rows-present";
            checkedRules.Add(ruleId);
            if (statuses == null || statuses.Count == 0)
            {
                findings.Add(new Finding(ruleId, "warn",
                    "DATA5 (F4 Status) has no row for this client — the F4 status workflow was never run. Open the return in Lacerte and run F4 to populate the status row before transmitting.",
                    "DATA5"));
            }
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static bool TryGetNonEmpty(
            IReadOnlyDictionary<string, object?> row, string key, out string value)
        {
            if (row.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
            value = string.Empty;
            return false;
        }

        private sealed class Finding
        {
            public string RuleId { get; }
            public string Severity { get; }
            public string Message { get; }
            public string Field { get; }

            public Finding(string ruleId, string severity, string message, string field)
            {
                RuleId = ruleId;
                Severity = severity;
                Message = message;
                Field = field;
            }
        }
    }
}
