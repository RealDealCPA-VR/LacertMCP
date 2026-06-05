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
    /// will fail e-file" before the operator hits transmit. v1 ships ~6
    /// rules that depend ONLY on currently-confirmed schema knowledge
    /// (DATA1 demographics + DATA5/7 row presence + Series=14 dependent
    /// descriptions); rules requiring the not-yet-landed F5 status-field
    /// code map are deferred to v1.1 with an explicit `skippedRules`
    /// surface so the operator sees what didn't run.
    ///
    /// <para>Severity:</para>
    /// <list type="bullet">
    ///   <item><description><c>strict</c> — every <c>warn</c> is promoted to <c>blocker</c>.</description></item>
    ///   <item><description><c>normal</c> (default) — warnings stay warnings.</description></item>
    /// </list>
    ///
    /// <para>Module gate: only the Individual ('I') module exposes the
    /// v1 catalog. C/P/S/F/R/T/N/B return <c>readiness='unsupported-module'</c>
    /// with a hint pointing at the rule-registry extension point.</para>
    /// </summary>
    [McpServerToolType]
    public sealed class PreFilingCheckTools
    {
        private readonly LacerteSessionManager _session;

        public PreFilingCheckTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Pre-filing readiness check — runs a small catalog of rules against ONE client in the active (datapath, taxYear, module) and returns whether the return is ready to e-file. v1 covers Individual ('I') only; other modules return readiness='unsupported-module'. Rule catalog (v1): required-name (C1_1 non-empty), required-last-name (C1_2 non-empty), required-email (C1_3 non-empty), forms-has-1040 (DATA7 carries a row whose C7_1 contains '1040'), forms-at-least-one (DATA7 not empty), dependents-have-description (every Series=14 detail row has non-empty Description), status-rows-present (DATA5 returns at least one row — surfaces 'never run F4 status' as a warning, not a blocker). Output: { clientNumber, readiness: 'ready'|'blocked'|'warn'|'unsupported-module', passed, blockers[], warnings[], checkedRules[], skippedRules[] }. severity='strict' promotes every warning to a blocker.")]
        public Task<string> LacertePreFilingCheck(
            [Description("ClientNumber (SDK-minted monotonic ID, e.g. '1001') to check. Must exist in the active context.")] string clientNumber,
            [Description("Severity policy — 'normal' (default) keeps warnings warnings; 'strict' promotes every warning to a blocker so readiness collapses to 'blocked' if any rule warns.")] string severity = "normal",
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
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

                // Module gate — v1 ships Individual only.
                if (ctx.Module != 'I')
                {
                    return (object)new
                    {
                        clientNumber,
                        readiness = "unsupported-module",
                        passed = false,
                        blockers = Array.Empty<object>(),
                        warnings = Array.Empty<object>(),
                        checkedRules = Array.Empty<object>(),
                        skippedRules = new[]
                        {
                            new
                            {
                                ruleId = "v1-catalog",
                                reason = "Module '" + ctx.Module + "' has no rule catalog in v1. The Individual ('I') catalog covers required-name / required-last-name / required-email / forms-has-1040 / forms-at-least-one / dependents-have-description / status-rows-present. Entity-module rule sets will be added in a future release; until then, surface readiness manually via lacerte_client_read / lacerte_table_read / lacerte_detail_read.",
                            },
                        },
                        hint = "lacerte_pre_filing_check v1 covers Individual ('I') only. Set context to an IData module to exercise the catalog.",
                    };
                }

                // Read the inputs the rules consume.
                var clientRow = await _session.ReadClient(clientNumber, cancellationToken)
                    .ConfigureAwait(false);
                if (clientRow == null)
                {
                    throw new LacerteSDKError(StatusCodes.OutOfIndex,
                        new Dictionary<string, object?>
                        {
                            ["clientNumber"] = clientNumber,
                            ["reason"] = "No client with this ClientNumber exists in the active (dataPath, taxYear, module). Call lacerte_client_list to enumerate available ClientNumbers.",
                        });
                }

                var statusRows = await _session.ReadStatuses(clientNumber, cancellationToken)
                    .ConfigureAwait(false);
                var formRows = await _session.ReadForms(clientNumber, cancellationToken)
                    .ConfigureAwait(false);
                var detailRows = await _session.ReadDetail(clientNumber, cancellationToken)
                    .ConfigureAwait(false);

                // Run the v1 rule catalog. Each rule produces a Finding;
                // Finding.Severity gates whether it lands in blockers or
                // warnings on the final envelope.
                var findings = new List<Finding>();
                var checkedRules = new List<string>();
                var skippedRules = new List<object>();

                CheckRequiredName(clientRow, findings, checkedRules);
                CheckRequiredLastName(clientRow, findings, checkedRules);
                CheckRequiredEmail(clientRow, findings, checkedRules);
                CheckFormsAtLeastOne(formRows, findings, checkedRules);
                CheckFormsHas1040(formRows, findings, checkedRules);
                CheckDependentsHaveDescription(detailRows, findings, checkedRules);
                CheckStatusRowsPresent(statusRows, findings, checkedRules);

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
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "required-name";
            checkedRules.Add(ruleId);
            if (!TryGetNonEmpty(client, "C1_1", out _))
            {
                findings.Add(new Finding(ruleId, "blocker",
                    "DATA1 field C1_1 (first name / entity name) is empty. The Lacerte SDK will reject the e-file with a 'name required' error.",
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

        private static void CheckFormsHas1040(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> forms,
            List<Finding> findings,
            List<string> checkedRules)
        {
            const string ruleId = "forms-has-1040";
            checkedRules.Add(ruleId);
            bool has1040 = false;
            if (forms != null)
            {
                foreach (var row in forms)
                {
                    if (row.TryGetValue("C7_1", out var v) && v is string s
                        && s.IndexOf("1040", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        has1040 = true;
                        break;
                    }
                }
            }
            if (!has1040)
            {
                findings.Add(new Finding(ruleId, "blocker",
                    "Individual e-file requires a 1040 row in DATA7. No DATA7 row in C7_1 contained '1040'.",
                    "DATA7.C7_1"));
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
