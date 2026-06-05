using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Prompts
{
    /// <summary>
    /// Pre-canned MCP workflow prompts per Todo.md task #79. Each prompt is a
    /// multi-step instruction template the agent invokes via the standard MCP
    /// <c>prompts/get</c> handshake; the prompt body references real
    /// <c>lacerte_*</c> tool names so the agent's tool-use loop is deterministic.
    /// Mirrors QB MCP <c>prompts/workflows.ts</c> (agent 07 §13).
    ///
    /// <para>
    /// Registration: <c>WithPromptsFromAssembly()</c> in <c>Program.cs</c>
    /// picks up this class via the <see cref="McpServerPromptTypeAttribute"/>
    /// and exposes each <see cref="McpServerPromptAttribute"/>-decorated method
    /// as a discoverable prompt.
    /// </para>
    ///
    /// <para>
    /// Each method returns a single-message <see cref="GetPromptResult"/> with
    /// <see cref="Role.User"/> content — the agent treats it as a "follow these
    /// steps" instruction. Keep the text concrete + reference real tool names
    /// so the agent doesn't fabricate.
    /// </para>
    /// </summary>
    [McpServerPromptType]
    public static class WorkflowPrompts
    {
        // -----------------------------------------------------------------
        // /lacerte_client_intake
        // -----------------------------------------------------------------

        [McpServerPrompt(Name = "lacerte_client_intake")]
        [Description(
            "Walk an operator through creating a brand-new Lacerte client: pick datapath + module + year, " +
            "validate identifiers, create the client, then seed Schedule C / W-2 detail rows. " +
            "Steps reference lacerte_session_status, lacerte_set_context, lacerte_list_datapaths, " +
            "lacerte_client_create, lacerte_field_write, and lacerte_detail_write.")]
        public static GetPromptResult ClientIntake(
            [Description("Optional friendly client name to seed the intake script. Default: ask the user.")]
            string? clientName = null)
        {
            var name = string.IsNullOrEmpty(clientName) ? "the new client" : clientName!;
            var text =
$@"You are about to onboard {name} into Lacerte. Run the steps below in order; STOP and report any tool failure verbatim — do not improvise around an error.

Step 1 — Verify the session is alive
  • Call `lacerte_session_status`.
  • If `isRegistered` is false, STOP and tell the operator to run `lacerte-mcp register` from the shell.

Step 2 — Confirm or set the active context
  • If `context` is null, call `lacerte_list_datapaths` to discover available installs.
  • Pick the right `(dataPath, taxYear, module)` for the new client. Modules: I (individual), C (corporate), P (partnership), S (s-corp), F (fiduciary), R (exempt), T (estate), N (gift), B (benefit).
  • Call `lacerte_set_context` with the picked triple. Leave `readOnly` false (we will write).

Step 3 — Create the client record
  • Validate the operator's chosen `clientId`: <= 8 alphanumeric characters, no special chars.
  • Pick a `taxType` that matches the module (e.g. ""1040"" for I, ""1120"" for C, ""1065"" for P).
  • Call `lacerte_client_create` with `{{ clientId, taxType, firstName, lastName, emailId }}`.
  • If status is `DuplicateClient`, ask the operator for a different clientId and retry.
  • Note the returned `clientNumber` — every subsequent write needs it.

Step 4 — Seed core address / phone fields (if collected during intake)
  • For each field, call `lacerte_field_write` with `{{ table: 1, clientNumber, field, value }}`.
  • Pass `idempotencyKey` (e.g. ""intake-{name}-name"") so a retry does not double-write.

  Canonical Tax-Organizer column → Lacerte DATA1 field mapping (module I).
  Only the three name/email fields are confirmed off-spec today; every other
  code below is marked TBD pending the live FieldAccessDumper introspection
  pass (Todo.md item F5). Do NOT invent codes — if the operator hands you a
  column not in this table, call `lacerte_field_access_map` (it returns the
  per-field access enum) and STOP if the field is missing from the map.

    | CSV column           | Lacerte field | Confirmed?           |
    | -------------------- | ------------- | -------------------- |
    | firstName            | C1_1          | yes (spec §4)        |
    | lastName             | C1_2          | yes (spec §4)        |
    | emailId / email      | C1_3          | yes (spec §4)        |
    | addressLine1         | TBD-F5        | no — gated on F5     |
    | addressLine2         | TBD-F5        | no — gated on F5     |
    | city                 | TBD-F5        | no — gated on F5     |
    | state                | TBD-F5        | no — gated on F5     |
    | zip                  | TBD-F5        | no — gated on F5     |
    | homePhone            | TBD-F5        | no — gated on F5     |
    | workPhone            | TBD-F5        | no — gated on F5     |
    | dateOfBirth (DOB)    | TBD-F5        | no — gated on F5     |
    | ssn / taxpayerID     | TBD-F5        | no — gated on F5     |

Step 5 — Seed starter detail rows (W-2 / 1099 / Schedule C totals collected at intake)
  • `lacerte_detail_write` takes 5 REQUIRED flat params (clientNumber, series, code, amount, description) plus 7 optional row-key segments that default to empty string (prefix, suffix, stateAbbrev, cityAbbr, source, tsj, importSrc) and 3 optional control params (replaceIndex, dryRun, idempotencyKey). There is NO nested `row:` wrapper — every field is a top-level kwarg.
  • A W-2 row looks like:
      `lacerte_detail_write({{ clientNumber, series: ""11"", code: ""1"", amount: 50000.00, description: ""Employer A"", source: ""W2"", tsj: ""T"" }})`
    Series cheat-sheet: 11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners. `tsj` is one of T/S/J/"""" (taxpayer/spouse/joint/unspecified). `stateAbbrev` is """" or a 2-letter postal code (validated ^[A-Z]{{2}}$).
  • For idempotent retries pass `idempotencyKey: ""intake-<clientId>-detail-<seriesCode>""`. For a dry-run preview pass `dryRun: true`.
  • The SDK does NOT validate detail-row shape; this tool's client-side validation is the only safeguard. Read the error message verbatim — series/code/stateAbbrev/tsj each surface a per-field `reason` in the error context if rejected.

Step 6 — Verify the new client appears in the roster
  • Call `lacerte_client_list` (no filter) and confirm the new `clientNumber` is present.
  • Optionally `lacerte_client_read` it back and surface the address fields you just wrote so the operator can sanity-check the values.

If any step fails, surface the structured `statusCode` + `userMessage` + `remediation` from the tool response — those are the operator's recovery hints.";

            return new GetPromptResult
            {
                Description = "Onboard a new Lacerte client end-to-end (context → create → field seed → detail seed → verify).",
                Messages = new List<PromptMessage>
                {
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = text },
                    },
                },
            };
        }

        // -----------------------------------------------------------------
        // /lacerte_review_ef_status
        // -----------------------------------------------------------------

        [McpServerPrompt(Name = "lacerte_review_ef_status")]
        [Description(
            "Triage e-file status across the active module's client roster: pull EFiling + EFStatus rows, " +
            "group by status, surface rejected returns first, list the next operator action per bucket. " +
            "Steps reference lacerte_session_status, lacerte_set_context, and lacerte_ef_status.")]
        public static GetPromptResult ReviewEfStatus(
            [Description("Optional tax year filter — only review this year's e-files. Default: use the active context's year.")]
            int? taxYear = null)
        {
            var yearNote = taxYear.HasValue
                ? $"Filter results to tax year {taxYear.Value}."
                : "Use whatever year the active context already has.";

            var text =
$@"You are doing an e-file status sweep. {yearNote} Run the steps below.

Step 1 — Confirm the session + context
  • Call `lacerte_session_status`. If `isRegistered` is false, STOP.
  • If `context` is null, ask the operator for `(dataPath, taxYear, module)` then call `lacerte_set_context`.

Step 2 — Pull the join
  • Call `lacerte_ef_status` (no arguments — it uses the active context).
  • EF status is available TY19+. If the active year is older, surface that to the operator and STOP.

Step 3 — Group the rows by `Status`
  • Buckets are typically: ""Transmitted"", ""Accepted"", ""Rejected"", ""Pending"" (plus any other Status strings the SDK returns).
  • Count the rows in each bucket. Report the counts first so the operator gets the shape at a glance.

Step 4 — Triage in this order
  • ""Rejected"" first — call `lacerte_detail_read` for each `ClientId` and surface the rejection reason fields if present. The operator needs to fix + retransmit these.
  • ""Pending"" / ""Transmitted"" next — note `StatusDate`; flag anything older than 72 hours as ""stale, escalate"".
  • ""Accepted"" last — confirm count matches the operator's expectation; no action needed.

Step 5 — Report
  • Render a short table: `ClientId | EFId | Status | StatusDate | NextAction`.
  • For each Rejected row, the NextAction should name the specific field to correct (from the detail-row dump if available).

Surface every tool error verbatim — do not summarize away a 9xxx status code.";

            return new GetPromptResult
            {
                Description = "Triage e-file status across the active module's client roster, surface rejected returns first.",
                Messages = new List<PromptMessage>
                {
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = text },
                    },
                },
            };
        }

        // -----------------------------------------------------------------
        // /lacerte_bulk_field_update
        // -----------------------------------------------------------------

        // P16-052 — prompt name aligned with the actual tool name
        // (lacerte_field_write_bulk). The noun-first family (lacerte_field_write
        // + lacerte_field_write_bulk) is the canonical pattern; verb-first
        // names are legacy.
        [McpServerPrompt(Name = "lacerte_field_write_bulk")]
        [Description(
            "Apply the same field write across a list of clients in a single MCP round-trip with dry-run preview, " +
            "per-row idempotency keys, and a stop-on-first-error rule. Steps reference lacerte_session_status, " +
            "lacerte_set_context, lacerte_field_access_map, lacerte_field_write_bulk, and lacerte_client_read.")]
        public static GetPromptResult BulkFieldUpdate(
            [Description("DATA table number to update (1 = client, 2 = subclient, 3 = state, 4 = state-subclient, 5 = status). Default 1.")]
            int table = 1,
            [Description("Field key (e.g. C1_1 for first name on DATA1). Required.")]
            string? field = null,
            [Description("Number of clients to preview as dry-run BEFORE the real write. Default 3.")]
            int previewCount = 3)
        {
            var fieldLabel = string.IsNullOrEmpty(field) ? "<ask the operator for the field>" : field!;

            var text =
$@"You are about to bulk-update field `{fieldLabel}` on DATA{table} across multiple clients. Bulk writes are HIGH RISK — the steps below ENFORCE a dry-run preview AND per-row idempotency keys to keep retries safe. Use `lacerte_field_write_bulk` (single MCP call that fans out to N rows) — do NOT loop `lacerte_field_write` per client; the bulk tool runs the same chokepoint with one round-trip and aggregates `{{totalCount, successCount, errorCount, perItem[]}}`.

Step 1 — Confirm session + context
  • `lacerte_session_status` — must be `isRegistered: true`.
  • Confirm `context` matches the module + year the operator intended. If wrong, `lacerte_set_context`.

Step 2 — Sanity-check the field is writable
  • Call `lacerte_field_access_map` with the active module + table.
  • Confirm `{fieldLabel}` is `ReadWrite`. If `ReadOnly` / `Hidden` / `NotExposed` / `NotSupported`, STOP and tell the operator the field can't be bulk-updated.

Step 3 — Build the target client list
  • Either: the operator gave you an explicit `[clientNumber...]` list — use it.
  • Or: call `lacerte_client_list` with the relevant filter and extract the `ClientNumber` field.
  • Tell the operator the count. If > 50, ask for explicit confirmation before proceeding.

Step 4 — Dry-run preview (first {previewCount})
  • Build an `entries` array with the first {previewCount} clients: `[{{ clientNumber, field: ""{fieldLabel}"", value }}, ...]`. For DATA2/4 add `subClientId`; for DATA3/4 add `stateAbbrev`.
  • Call `lacerte_field_write_bulk` ONCE: `{{ table: {table}, entries, dryRun: true, continueOnError: false, idempotencyKey: ""bulk-<batchId>"" }}`. The tool synthesizes per-row keys as `""<prefix>-<clientNumber>-<field>""` automatically — never aliases across fields.
  • Inspect `perItem[]`. If ANY row's `success` is false (or `statusCode` is non-Success), STOP and report the failed clientNumber + the structured error. Do not proceed to the real write.

Step 5 — Real write (only if preview was 100% clean)
  • Build the full `entries` array for every target client (same per-row shape as Step 4).
  • Call `lacerte_field_write_bulk` ONCE: `{{ table: {table}, entries, dryRun: false, continueOnError: false, idempotencyKey: ""bulk-<batchId>"" }}`. SAME prefix as Step 4 — the session-side idempotency lock replays cached results for the preview rows instead of re-writing them, so the previewed rows are skipped automatically.
  • If `errorCount > 0` in the response envelope, STOP. The first failing row's `index` + `statusCode` + `userMessage` tell the operator exactly where the batch stopped. Remaining rows after the failure are marked `skipped: true` (stop-on-first-error posture).

Step 6 — Verify a sample
  • Pick 3 random clients from the batch's successful `perItem[]` rows. Call `lacerte_client_read` for each and confirm `{fieldLabel}` now holds the new value.
  • Report `totalCount` / `successCount` / `errorCount` from the bulk response plus the sample-verified count.

Idempotency note: per-row keys are `""<prefix>-<clientNumber>-<field>""`. If a retry replays the same prefix with the same per-row payload, the session manager returns the cached result without re-writing. If the per-row payload differs, that row throws `IdempotencyConflict (9102)` and the batch stops (or continues, depending on `continueOnError`). Pick a fresh `batchId` for a genuinely new run.";

            return new GetPromptResult
            {
                Description = "Apply a field write across many clients via lacerte_field_write_bulk (single MCP call) with dry-run preview, per-row idempotency keys, stop-on-first-error.",
                Messages = new List<PromptMessage>
                {
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = text },
                    },
                },
            };
        }

        // -----------------------------------------------------------------
        // /lacerte_csv_intake
        // -----------------------------------------------------------------

        [McpServerPrompt(Name = "lacerte_csv_intake")]
        [Description(
            "Bulk-onboard clients from a CSV / Tax-Organizer export. Operator declares a column→field mapping ONCE up " +
            "front; the prompt walks per-row create + seed + per-row outcome report. Mirrors REQUIREMENTS §1.3 " +
            "(\"Bootstrapping new clients from a CSV or Tax Organizer responses\"). " +
            "Steps reference lacerte_session_status, lacerte_set_context, lacerte_field_access_map, " +
            "lacerte_client_create, and lacerte_field_write.")]
        public static GetPromptResult CsvIntake(
            [Description("Optional human label for the source spreadsheet (e.g. \"2024-organizer-batch-A.csv\"). Used only in operator-facing report rows. Default: \"intake.csv\".")]
            string? sourceLabel = null,
            [Description("Optional row-count hint so the LLM can warn the operator when the batch crosses the >50 confirmation gate. Default: 0 (ask the operator).")]
            int rowCount = 0)
        {
            var label = string.IsNullOrEmpty(sourceLabel) ? "intake.csv" : sourceLabel!;
            var countNote = rowCount > 0
                ? $"You will process {rowCount} row(s) from `{label}`."
                : $"Ask the operator how many rows are in `{label}` before you begin.";

            var text =
$@"You are bulk-onboarding clients from `{label}`. {countNote} Bulk creates are HIGH RISK — the steps below ENFORCE a one-shot column→field mapping declaration AND per-row idempotency keys so a retry never double-creates.

Step 1 — Confirm session + context
  • `lacerte_session_status` — must be `isRegistered: true`.
  • If `context` is null, ask the operator for `(dataPath, taxYear, module)` and call `lacerte_set_context` with `readOnly: false`.

Step 2 — Declare the column → field mapping ONCE
  • Ask the operator (or read the CSV header row) for the column names present in `{label}`.
  • Build a mapping dictionary `{{ csvColumn: lacerteField }}` per the canonical table in the `lacerte_client_intake` prompt's Step 4 (firstName=C1_1, lastName=C1_2, emailId=C1_3 are confirmed; address/phone/DOB/SSN are TBD pending Todo.md F5).
  • For each TBD field the operator wants to seed, call `lacerte_field_access_map` to confirm the field exists and is `ReadWrite` on the active module. If `Unknown` / `ReadOnly` / `Hidden`, STOP and tell the operator that field cannot be bulk-seeded yet.

Step 3 — Validate the row payloads BEFORE any write
  • For each CSV row, build `{{ clientId, taxType, firstName, lastName, emailId }}` plus a `seedFields` dict from the column mapping.
  • Reject any row missing the 5 required fields up front; report which row + which field is missing in your operator summary BEFORE any side-effect.
  • If row count > 50, ask the operator for explicit confirmation before proceeding.

Step 4 — Per-row create (stop-on-first-error by default)
  • For each row, generate `idempotencyKey = \""csv-intake-<batchId>-<clientId>\""` so a retry of the same row replays the cached result instead of double-creating.
  • Call `lacerte_client_create` with the row's 5 required fields. If `lacerte_client_create` supports a `seedFields` param (P15-038 part 1), pass the seed dict in the SAME call so create + seed share one idempotency unit. Otherwise:
      - call `lacerte_client_create` first;
      - capture the returned `clientNumber`;
      - then loop the seedFields dict, calling `lacerte_field_write` for each `{{ table: 1, clientNumber, field, value, idempotencyKey: \""csv-intake-<batchId>-<clientId>-<field>\"" }}`.
  • On `DuplicateClient` (status 18) STOP unless the operator explicitly said \""skip duplicates\""; on any other non-Success statusCode, STOP and surface the structured `userMessage` + `remediation`.

Step 5 — Verify a 3-row sample
  • Pick 3 random successful `clientNumber`s from the batch.
  • Call `lacerte_client_read` on each and confirm at least one seed field matches the CSV value the operator gave you.

Step 6 — Report
  • Render a short table: `csvRow | clientId | clientNumber | status | seedFieldsWritten | error?`
  • Totals at the bottom: requested / created / duplicateSkipped / failed / verifiedSample.

Surface every tool error verbatim — do not summarize away a 9xxx status code. If you stopped mid-batch, name the exact row index so the operator can fix + resume.";

            return new GetPromptResult
            {
                Description = "Bulk-onboard clients from a CSV / Tax-Organizer export with a one-shot column→field mapping, per-row idempotency, stop-on-first-error.",
                Messages = new List<PromptMessage>
                {
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = text },
                    },
                },
            };
        }
    }
}
