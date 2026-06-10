using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-024 — discoverable in-MCP help. Single entrypoint the LLM can call
    /// when the operator says "what can you do?" or when the LLM needs context
    /// after a /clear and the host (Cursor, Windsurf, generic stdio) does not
    /// expose easy README-paste affordances.
    ///
    /// <para>
    /// The killer payload is <c>whatThisDoesNotDo</c> — the per-tool descriptions
    /// surfaced by <c>tools/list</c> already give the LLM positive scope, but
    /// they carry no negative-space bounds, so the LLM ends up asking the
    /// operator "can you e-file?" / "can you print?" on every fresh session.
    /// The README's "What this MCP does NOT do" bullet list is distilled into
    /// the response so the LLM has authoritative scope without README access.
    /// </para>
    ///
    /// <para>
    /// Pure static content — no session, no SDK, no filesystem. The handler is
    /// observationally identical between sim and live, has no failure modes
    /// beyond the topic-name guard, and is safe to call before
    /// <c>lacerte_set_context</c>. Total payload &lt;= ~5KB JSON per the size
    /// budget test in <c>HelpToolsTests</c>.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class HelpTools
    {
        // Topic dictionary keyed by lowercase string — matches the wire
        // convention from P16/P17 (lowercase mode strings, lowercase enum
        // values on the wire). Each topic returns one of the public
        // sub-payloads below; `null` returns the full overview.
        //
        // Canonical topic vocabulary per the P18-024 verdict.refinedDesign:
        //   null         -> full overview (all sections)
        //   "modes"      -> simulation + live mode descriptions
        //   "tools"      -> toolsByCategory dictionary
        //   "env"        -> envVarsRequired list
        //   "limits"     -> whatThisDoesNotDo bullets (the killer payload)
        //   "first-call" -> firstCallExample + recommended boot sequence
        //   "prompts"    -> relatedPrompts list
        //
        // Aliases ("envvars", "errors", "quickstart", "troubleshooting",
        // "overview") map to the closest canonical topic so an LLM that
        // guesses from the hint vocabulary still gets a useful answer.
        private static readonly Dictionary<string, string> TopicAliases
            = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["envvars"] = "env",
                ["env-vars"] = "env",
                ["environment"] = "env",
                ["quickstart"] = "first-call",
                ["firstcall"] = "first-call",
                ["overview"] = "",
                ["all"] = "",
                ["limit"] = "limits",
                ["scope"] = "limits",
                ["prompt"] = "prompts",
                ["mode"] = "modes",
                ["tool"] = "tools",
            };

        [McpServerTool]
        [Description("Return a distilled README — what this MCP is, what it does NOT do, modes, tool categories, env vars, first-call example, and related prompts. Topic filter: null=overview (all sections), \"modes\", \"tools\", \"env\", \"limits\", \"first-call\", \"prompts\". Pure static content; safe to call at any time, including before lacerte_set_context.")]
        public Task<string> LacerteHelp(
            [Description("Optional topic filter. One of: \"modes\", \"tools\", \"env\", \"limits\", \"first-call\", \"prompts\". Omit (null) for the full overview. Unknown topics return a structured error listing the valid vocabulary.")]
            string? topic = null)
        {
            return WrapToolHandler.Run(() =>
            {
                var resolved = ResolveTopic(topic);
                if (resolved == null)
                {
                    // Unknown topic — fail with the same synthetic InvalidArgument
                    // posture used by other validating tools. The context dict
                    // surfaces the canonical vocabulary so the LLM can retry
                    // without a second tool round-trip.
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidArgument,
                        new Dictionary<string, object?>
                        {
                            ["topic"] = topic,
                            ["reason"] = "topic must be one of: modes, tools, env, limits, first-call, prompts (or null for the full overview).",
                            ["validTopics"] = new[] { "modes", "tools", "env", "limits", "first-call", "prompts" },
                        });
                }

                // Each arm produces a different anonymous shape, so the
                // switch-expression's natural type doesn't unify — cast
                // every arm to object explicitly.
                object payload = resolved switch
                {
                    "" => (object)BuildOverview(),
                    "modes" => (object)new { topic = "modes", modes = Modes() },
                    "tools" => (object)new { topic = "tools", toolCount = ToolCountStatic, toolsByCategory = ToolsByCategory() },
                    "env" => (object)new { topic = "env", envVarsRequired = EnvVarsRequired() },
                    "limits" => (object)new { topic = "limits", whatThisDoesNotDo = WhatThisDoesNotDo() },
                    "first-call" => (object)new { topic = "first-call", firstCallExample = FirstCallExample(), relatedCli = RelatedCli() },
                    "prompts" => (object)new { topic = "prompts", relatedPrompts = RelatedPrompts() },
                    _ => (object)BuildOverview(), // unreachable — ResolveTopic guards
                };

                return Task.FromResult<object>(payload);
            });
        }

        // -----------------------------------------------------------------
        // Resolution helpers
        // -----------------------------------------------------------------

        private static string? ResolveTopic(string? topic)
        {
            if (string.IsNullOrWhiteSpace(topic)) return "";
            // After the IsNullOrWhiteSpace check the compiler doesn't always
            // flow-narrow `topic` to non-null on net48 — `!` is safe here.
            var t = topic!.Trim();
            if (TopicAliases.TryGetValue(t, out var aliased) && aliased != null)
            {
                t = aliased;
            }
            if (t.Length == 0) return "";
            switch (t.ToLowerInvariant())
            {
                case "modes":
                case "tools":
                case "env":
                case "limits":
                case "first-call":
                case "prompts":
                    return t.ToLowerInvariant();
                default:
                    return null;
            }
        }

        // -----------------------------------------------------------------
        // Static content payloads
        // -----------------------------------------------------------------

        private const string WhatThisIs =
            "Local Windows MCP server that exposes Intuit Lacerte tax software (TY18+) as tools an LLM can call. " +
            "Wraps the Lacerte SDK (LacerteSDK_Access COM/.NET + LacerteDSII ODBC) over JSON-RPC/stdio. " +
            "Read/Write/Create against DBF tables 1-7 + detail files; read-only EF status (TY19+). " +
            "Runs in `live` (real SDK on Windows) or `simulation` (in-memory fixtures, default off-Windows).";

        // The README's "What this MCP does NOT do" bullets — the highest-value
        // payload per the P18-024 verdict. Verbatim shape from README.md so
        // operators reading the wire and operators reading the README see the
        // same scope language.
        private static string[] WhatThisDoesNotDo() => new[]
        {
            "No e-filing actions. Read-only EF status via lacerte_ef_status (TY19+). Submitting, retransmitting, or correcting an e-file still happens in Lacerte's own UI.",
            "No printing, PDF rendering, or third-party export. DBF read/write + COM field-write only. Use Lacerte's File -> Print or Print to PDF for any rendered output.",
            "No organizer, client portal, letter template, or diagnostics-detail access. Those live in non-DBF Lacerte surfaces this MCP doesn't wire up.",
            "Single-operator only. No concurrency control across multiple Lacerte users sharing one datapath. Don't point two MCP servers at the same LACERTE_DATA_PATH simultaneously.",
            "No archived / closed-copy / prior-firm-transfer file access. The MCP sees the live working set under {YY}Tax\\{Module}Data, not Lacerte's archive folders.",
            "No live UI refresh. Writes land on disk, but the Lacerte editor only rereads on client close/reopen. Verify a write actually landed via lacerte_client_read / lacerte_table_read.",
        };

        private static object Modes() => new
        {
            // Lowercase mode strings per P16/P17 wire convention.
            simulation = new
            {
                name = "simulation",
                description = "In-memory DBF fixtures. Default off-Windows. Enable explicitly with LACERTE_SIMULATION=1. Observationally identical to live for the happy path (CLAUDE.md two-mode discipline). Use for development, CI, and demo flows without a Lacerte install.",
            },
            live = new
            {
                name = "live",
                description = "Real Lacerte SDK on Windows. Enable with LACERTE_LIVE=1. Requires the 64-bit Lacerte SDK MSI (32-bit driver has a known INSERT crash) + interactive Intuit consent (run `lacerte-mcp register` from PowerShell) + 24h auth refresh (`lacerte-mcp login`). Reads run via ODBC; writes via COM with TY21+ UpdateTaxReturn branching.",
            },
        };

        // Each tool name listed here MUST be a real registered tool, AND
        // every [McpServerTool] registered in the assembly MUST be listed
        // here — the parity test in HelpToolsTests is bidirectional (R6-08)
        // so the categorization can't silently drift away from reality in
        // either direction (a future tool added without a catalog row fails
        // the suite).
        private static Dictionary<string, string[]> ToolsByCategory() => new Dictionary<string, string[]>
        {
            ["session"] = new[]
            {
                "lacerte_session_status",
                "lacerte_set_context",
                "lacerte_list_modules",
                "lacerte_list_datapaths",
                "lacerte_can_use_new_apis",
                "lacerte_health",
            },
            ["clients"] = new[]
            {
                "lacerte_client_list",
                "lacerte_client_read",
                "lacerte_client_find",
                "lacerte_client_query",
                "lacerte_client_create",
                "lacerte_client_create_bulk",
                "lacerte_client_summary",
                "lacerte_export_client",
            },
            ["tables"] = new[]
            {
                "lacerte_table_read",
                "lacerte_subclient_read",
                "lacerte_state_read",
                "lacerte_state_subclient_read",
                "lacerte_status_read",
                "lacerte_forms_read",
                "lacerte_detail_read",
            },
            ["writes"] = new[]
            {
                "lacerte_field_write",
                "lacerte_field_write_bulk",
                "lacerte_update_return",
                "lacerte_detail_write",
                "lacerte_detail_write_bulk",
            },
            ["efile"] = new[]
            {
                "lacerte_ef_status",
            },
            ["dashboards"] = new[]
            {
                "lacerte_firm_dashboard",
            },
            // R6-08 — round-5 analysis/accelerator surface. These were
            // registered tools missing from the catalog, so lacerte_help
            // under-reported the server's capability set by 9 tools.
            ["accelerators"] = new[]
            {
                "lacerte_diff_clients",
                "lacerte_pre_filing_check",
                "lacerte_year_over_year",
                "lacerte_field_history",
            },
            // R6-08 — round-5 observability surface (in-process ring log,
            // request stats, cache/lock introspection).
            ["observability"] = new[]
            {
                "lacerte_recent_log",
                "lacerte_request_stats",
                "lacerte_cache_state",
                "lacerte_resource_usage",
                "lacerte_active_locks",
            },
            ["meta"] = new[]
            {
                "lacerte_field_access_map",
                "lacerte_return_codes",
                "lacerte_reload_schema",
                "lacerte_help",
                "lacerte_prompts",
            },
        };

        // Count is derived from ToolsByCategory at runtime so the
        // overview/tools payloads stay in sync without manual bookkeeping.
        private static int ToolCountStatic
        {
            get
            {
                int n = 0;
                foreach (var kv in ToolsByCategory()) n += kv.Value.Length;
                return n;
            }
        }

        // Env vars: distilled from README "Environment variables (full
        // reference)" with the lowest-noise required + recommended set.
        // Marked `required` only for the LIVE/SIMULATION discriminator;
        // everything else is recommended or optional.
        private static object[] EnvVarsRequired() => new object[]
        {
            new
            {
                name = "LACERTE_LIVE",
                required = "one of LIVE / SIMULATION must be set",
                defaultValue = "unset",
                purpose = "Set to 1 to enable the real SDK on Windows. Mutually exclusive with LACERTE_SIMULATION.",
            },
            new
            {
                name = "LACERTE_SIMULATION",
                required = "one of LIVE / SIMULATION must be set",
                defaultValue = "1 off-Windows",
                purpose = "Set to 1 to force simulation regardless of platform. Default off-Windows.",
            },
            new
            {
                name = "LACERTE_DATA_PATH",
                required = "advisory",
                defaultValue = "unset",
                purpose = "Quoted in ODBC-error remediations. NOT auto-applied to session context — every session must still call lacerte_set_context.",
            },
            new
            {
                name = "LACERTE_DEFAULT_TAX_YEAR",
                required = "recommended",
                defaultValue = "unset",
                purpose = "Default tax year used when lacerte_set_context omits one.",
            },
            new
            {
                name = "LACERTE_APP_NAME",
                required = "recommended",
                defaultValue = "LacerteMCP",
                purpose = "Friendly name shown in the Intuit SDK consent dialog.",
            },
            new
            {
                name = "LACERTE_DEBUG_WIRE",
                required = "optional",
                defaultValue = "0",
                purpose = "Set to 1 to log COM + SQL to logs/lacerte-YYYYMMDD.log. Secrets redacted.",
            },
            new
            {
                name = "LACERTE_HEARTBEAT_SECONDS",
                required = "optional",
                defaultValue = "30",
                purpose = "Heartbeat JSON write cadence to %TEMP%/lacerte-mcp-heartbeat-{pid}.json. 0, negative, or unparseable disables; clamped to 86400 max.",
            },
            new
            {
                name = "LACERTE_DATA_ROOT",
                required = "optional",
                defaultValue = "C:\\Lacerte",
                purpose = "Root scanned by lacerte_list_datapaths when discovering {YY}Tax\\{Module}Data folders.",
            },
        };

        // First-call example: lacerte_session_status per the P18-024 verdict
        // ("matches every WorkflowPrompts Step 1 — not a write tool").
        private static object FirstCallExample() => new
        {
            tool = "lacerte_session_status",
            why = "Zero wire I/O, returns mode + registration + 24h-expiry + active-context snapshot. Every workflow prompt starts here.",
            args = new { },
            then = new[]
            {
                "lacerte_list_datapaths to discover {YY}Tax\\{Module}Data folders on the box.",
                "lacerte_set_context with the chosen (dataPath, taxYear, module) triple.",
                "lacerte_client_list to verify the context resolves to clients.",
            },
        };

        // The four real prompts registered via WithPromptsFromAssembly. Names
        // verbatim from WorkflowPrompts.cs / README.
        private static object[] RelatedPrompts() => new object[]
        {
            new
            {
                name = "lacerte_client_intake",
                purpose = "Onboard a new client end-to-end: verify session, pick datapath/module/year, create client, seed DATA1 fields, seed detail rows, verify roster.",
                args = new[] { "clientName?: string" },
            },
            new
            {
                name = "lacerte_review_ef_status",
                purpose = "Triage e-file status: pull EFiling+EFStatus join, group by Status, surface Rejected first, flag stale Pending/Transmitted (>72h). TY19+ only.",
                args = new[] { "taxYear?: int" },
            },
            new
            {
                name = "lacerte_field_write_bulk",
                purpose = "Bulk field write across many clients with mandatory dry-run preview, idempotency keys, stop-on-first-error.",
                args = new[] { "table: int = 1", "field: string (required)", "previewCount: int = 3" },
            },
            new
            {
                name = "lacerte_csv_intake",
                purpose = "Bulk-onboard clients from a CSV / Tax-Organizer export with one-shot column->field mapping, per-row idempotency keys, stop-on-first-error.",
                args = new[] { "sourceLabel?: string", "rowCount?: int" },
            },
        };

        // CLI commands that an operator (not the LLM) can run for deeper
        // introspection. Listed in the overview so an LLM that hits a wall
        // inside the MCP can tell the operator the right next move.
        private static object[] RelatedCli() => new object[]
        {
            new { name = "lacerte-mcp tools --names-only", purpose = "List every MCP tool the server registers (reflection only, no stdio boot)." },
            new { name = "lacerte-mcp tools --name <tool>", purpose = "Deep-print one tool's full parameter surface incl. per-parameter descriptions." },
            new { name = "lacerte-mcp status --json", purpose = "Snapshot of registration state, mode, storage backend, platform. Zero wire I/O." },
            new { name = "lacerte-mcp doctor", purpose = "Setup verification across OS, install, config, identity probes. Exit 0/1/2 contract." },
            new { name = "lacerte-mcp register", purpose = "One-time AppID mint + Intuit consent + sign-in ceremony (interactive PowerShell only)." },
            new { name = "lacerte-mcp login", purpose = "Refresh the 24h Intuit auth ticket using the existing AppID." },
        };

        // -----------------------------------------------------------------
        // Overview composition
        // -----------------------------------------------------------------

        private static object BuildOverview() => new
        {
            // No `topic` echo on the overview — the absence of the filter is
            // itself the signal. Every sub-payload echoes its topic for
            // identification by an LLM that fans out parallel calls.
            whatThisIs = WhatThisIs,
            whatThisDoesNotDo = WhatThisDoesNotDo(),
            modes = Modes(),
            toolCount = ToolCountStatic,
            toolsByCategory = ToolsByCategory(),
            envVarsRequired = EnvVarsRequired(),
            firstCallExample = FirstCallExample(),
            relatedPrompts = RelatedPrompts(),
            relatedCli = RelatedCli(),
        };
    }
}
