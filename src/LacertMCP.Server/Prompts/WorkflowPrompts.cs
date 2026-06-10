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
  • Pass `idempotencyKey` (e.g. ""intake-<clientId>-<field>"") so a retry of the SAME field replays the cached result — each different field MUST get a DISTINCT key.
  • Do NOT reuse one key across multiple field writes. Same key + different payload throws `IdempotencyConflict` (statusCode 9102) and the field is not written.

  Canonical Tax-Organizer column → Lacerte DATA1 field mapping (module I).
  Only the three name/email fields are confirmed off-spec today; every other
  code below is marked TBD pending the live FieldAccessDumper introspection
  pass (Todo.md item F5). Do NOT invent codes — if the operator hands you a
  column not in this table, call `lacerte_field_access_map` with the active
  module, table, and taxYear (the `taxYear` parameter is REQUIRED — it returns
  the per-field access enum) and STOP if the field is missing from the map.

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

  IMPORTANT — degenerate-shard handling: the 8 TBD-F5 rows above CANNOT be
  written on the v1 codebase (the individual.json shard ships empty until
  Phase-F5 dumper output lands). If the operator pasted address/phone/DOB/SSN
  values for the intake row:
    • Do NOT call `lacerte_field_access_map` per TBD column — it will return
      only the universal C1_0 entry regardless of which TBD field you ask
      about. One upfront call per session is enough to confirm.
    • Emit one summary line to the operator BEFORE the writes: ""Note:
      address/phone/DOB/SSN cannot be seeded into Lacerte today (v1 shard
      pending Phase-F5). I will record only firstName/lastName/emailId.
      Echo the pasted address values so the operator can paste them into
      the Lacerte UI manually after `lacerte_client_create` returns.""
    • Skip the per-TBD-field `lacerte_field_write` calls entirely.
    • Continue with the 3 confirmed writes (C1_1/C1_2/C1_3).
  For an UNKNOWN column the operator hands you that is NEITHER in the
  confirmed-3 list NOR in the TBD-8 list, call `lacerte_field_access_map`
  once for that column; if `ReadOnly` / `Hidden` / `NotExposed` / `NotSupported`
  / `Unknown` / absent from the response, STOP and report the column.

Step 5 — Seed starter detail rows (W-2 / 1099 / Schedule C totals collected at intake)
  • `lacerte_detail_write` takes 5 REQUIRED flat params (clientNumber, series, code, amount, description) plus 7 optional row-key segments that default to empty string (prefix, suffix, stateAbbrev, cityAbbr, source, tsj, importSrc) and 3 optional control params (replaceIndex, dryRun, idempotencyKey). There is NO nested `row:` wrapper — every field is a top-level kwarg.
  • A W-2 row looks like:
      `lacerte_detail_write({{ clientNumber, series: ""11"", code: ""1"", amount: 50000.00, description: ""Employer A"", source: ""W2"", tsj: ""T"" }})`
    Series cheat-sheet: 11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners. `tsj` is one of T/S/J/"""" (taxpayer/spouse/joint/unspecified). `stateAbbrev` is """" or a 2-letter postal code (validated ^[A-Z]{{2}}$).
  • For idempotent retries pass `idempotencyKey: ""intake-<clientId>-detail-<seriesCode>""`. For a dry-run preview pass `dryRun: true` — and if you dry-run first, use a DIFFERENT key for the real write (e.g. append `-preview` to the dry-run key): `dryRun` is part of the idempotency fingerprint and dry-run results are cached, so reusing one key across a dry-run and a real write throws `IdempotencyConflict (9102)`.
  • The SDK does NOT validate detail-row shape; this tool's client-side validation is the only safeguard. Read the error message verbatim — series/code/stateAbbrev/tsj each surface a per-field `reason` in the error context if rejected.

Step 6 — Verify the new client appears in the roster
  • Call `lacerte_client_list` (no filter) and confirm the new `clientNumber` is present.
  • Optionally `lacerte_client_read` it back and surface ONLY the fields you actually wrote in Step 4: firstName/lastName/emailId are always populated (C1_1/C1_2/C1_3); address/phone/DOB/SSN appear only if Phase-F5 has landed AND the operator gave you those values AND the per-field access-map check passed. For a v1 install the read-back will show only C1_0/C1_1/C1_2/C1_3 + ClientID — the address section being empty is EXPECTED, not a bug; do not fabricate values to fill it. Report the read-back as `{{firstName, lastName, emailId, address: ""(gated on Todo.md F5 — not yet writable)""}}` so the operator sees the gate explicitly.

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
                ? $"Target tax year {taxYear.Value}: EF rows carry NO year field, so you cannot filter rows after the fact — set the context to that year FIRST (call `lacerte_set_context` with taxYear {taxYear.Value}) before pulling any EF data."
                : "Use whatever year the active context already has.";

            var text =
$@"You are doing an e-file status sweep. {yearNote} Run the steps below.

Step 1 — Confirm the session + context
  • Call `lacerte_session_status`. If `isRegistered` is false, STOP.
  • If `context` is null, ask the operator for `(dataPath, taxYear, module)` then call `lacerte_set_context`.

Step 2 — Pull the join
  • Call `lacerte_ef_status` (no arguments — it uses the active context).
  • EF status is available TY19+. If the active year is older, surface that to the operator and STOP.
  • FOLLOW PAGINATION: if the response says `hasMore: true`, call `lacerte_ef_status` again with `offset: <nextOffset>` and repeat until `hasMore` is false. Collect ALL pages before moving on — bucket counts computed from a single page are wrong for any roster larger than one page.

Step 3 — Group the rows by `Status`
  • Only group once pagination is complete (every page collected in Step 2).
  • Buckets are typically: ""Transmitted"", ""Accepted"", ""Rejected"", ""Pending"" (plus any other Status strings the SDK returns).
  • Count the rows in each bucket. Report the counts first so the operator gets the shape at a glance.

Step 4 — Triage in this order
  • ""Rejected"" first — call `lacerte_detail_read` passing each row's `ClientId` AS the `clientNumber` parameter (the SQL join is `ef.ClientId = ci.C1_0`, so they are the same SDK-minted identifier; see P16 ClientNumber-vs-ClientID hint) and surface the rejection reason fields if present. The operator needs to fix + retransmit these.
  • ""Pending"" / ""Transmitted"" next — note `StatusDate`; flag anything older than 72 hours as ""stale, escalate"".
  • ""Accepted"" last — confirm count matches the operator's expectation; no action needed.

Step 5 — Report
  • Render a short table: `clientNumber | EFId | Status | StatusDate | NextAction` (the join column `ef.ClientId` IS the SDK ClientNumber — surface it under the canonical wire name `clientNumber` to match every other tool's terminology).
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
  • Call `lacerte_field_access_map` with the active module, table, AND taxYear (the `taxYear` parameter is REQUIRED — pass the active context's year). The response's `fields` dict only contains entries for fields whose access is NOT `Unknown` (the loader filters Unknown to reduce noise).
  • Confirm `{fieldLabel}` appears in `fields` AND its access is exactly `ReadWrite`. STOP if any of: field is ABSENT from `fields` / access is `Unknown` / `ReadOnly` / `Hidden` / `NotExposed` / `NotSupported`. (NOTE: until Todo.md F5 lands full shard introspection, only `C{table}_0` is cataloged ReadWrite for every module — every other field on a v1 module returns Unknown and will be absent from the response. If you don't see `{fieldLabel}` listed, that IS the STOP signal — do NOT proceed and bulk-write entries that the chokepoint will reject with `UnknownFieldAccess` 9012.)

Step 3 — Build the target client list
  • Either: the operator gave you an explicit `[clientNumber...]` list — use it.
  • Or: call `lacerte_client_list` with the relevant filter and extract the `ClientNumber` field.
  • Tell the operator the count. If > 50, ask for explicit confirmation before proceeding.

Step 4 — Dry-run preview (first {previewCount})
  • Build an `entries` array with the first {previewCount} clients: `[{{ clientNumber, field: ""{fieldLabel}"", value }}, ...]`. For DATA2/4 add `subClientId`; for DATA3/4 add `stateAbbrev`.
  • Call `lacerte_field_write_bulk` ONCE: `{{ table: {table}, entries, dryRun: true, continueOnError: false, idempotencyKey: ""bulk-<batchId>-preview"" }}`. The tool synthesizes per-row keys as `""<prefix>-<index>-<clientNumber>-<field>""` automatically (positional — keys never alias, even for repeated client+field pairs in one batch).
  • Inspect `perItem[]`. If ANY row's `success` is false (or `statusCode` is non-Success), STOP and report the failed clientNumber + the structured error. Do not proceed to the real write.

Step 5 — Real write (only if preview was 100% clean)
  • Build the full `entries` array for every target client (same per-row shape as Step 4). Previewed rows are NOT skipped — every row in this commit array is written for real.
  • Call `lacerte_field_write_bulk` ONCE: `{{ table: {table}, entries, dryRun: false, continueOnError: false, idempotencyKey: ""bulk-<batchId>"" }}`. This is a DIFFERENT prefix than Step 4 (`bulk-<batchId>`, not `bulk-<batchId>-preview`) and that is mandatory: `dryRun` is part of every row's idempotency fingerprint and dry-run results ARE cached, so reusing the preview prefix here would make the commit's first previewed row throw `IdempotencyConflict (9102)` — and with `continueOnError: false` the whole real write stops at row 0. Never reuse one idempotency key across a dry-run and a real write.
  • If `errorCount > 0` in the response envelope, STOP. The first failing row's `index` + `statusCode` + `userMessage` tell the operator exactly where the batch stopped. Remaining rows after the failure are marked `skipped: true` (stop-on-first-error posture).

Step 6 — Verify a sample
  • Pick 3 random clients from the batch's successful `perItem[]` rows. Call `lacerte_client_read` for each and confirm `{fieldLabel}` now holds the new value.
  • Report `totalCount` / `successCount` / `errorCount` from the bulk response plus the sample-verified count.

Idempotency note: per-row keys are `""<prefix>-<index>-<clientNumber>-<field>""` (the index is the row's position in the batch, so identical client+field pairs in one batch get distinct keys). If a retry replays the same prefix with the same per-row payload (INCLUDING the same `dryRun` value), the session manager returns the cached result without re-writing. If the per-row payload differs — including flipping `dryRun` — that row throws `IdempotencyConflict (9102)` and the batch stops (or continues, depending on `continueOnError`). That is why the preview pass (`bulk-<batchId>-preview`) and the commit pass (`bulk-<batchId>`) MUST use different prefixes: a dry-run and a real write of the same row have different fingerprints, so a shared key can only ever conflict. To RETRY a failed commit, reuse the SAME `bulk-<batchId>` prefix (succeeded rows replay from cache; failed/skipped rows re-execute). Pick a fresh `batchId` for a genuinely new run.";

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
  • For each TBD field the operator wants to seed, call `lacerte_field_access_map` (with the active module, table, and taxYear — the `taxYear` parameter is REQUIRED) to confirm the field exists and is `ReadWrite` on the active module. STOP if any of: field is ABSENT from `fields` / `Unknown` / `ReadOnly` / `Hidden` / `NotExposed` / `NotSupported`, and tell the operator that field cannot be bulk-seeded yet.

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
  • On `DuplicateClient` (statusName from the error envelope, statusCode 12) STOP unless the operator explicitly said \""skip duplicates\""; on any other non-Success statusCode, STOP and surface the structured `userMessage` + `remediation`.

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
