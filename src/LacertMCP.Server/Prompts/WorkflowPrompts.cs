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
  • Validate the operator's chosen `clientID`: <= 8 alphanumeric characters, no special chars.
  • Pick a `taxType` that matches the module (e.g. ""1040"" for I, ""1120"" for C, ""1065"" for P).
  • Call `lacerte_client_create` with `{{ clientID, taxType, firstName, lastName, emailID }}`.
  • If status is `DuplicateClient`, ask the operator for a different clientID and retry.
  • Note the returned `clientNumber` — every subsequent write needs it.

Step 4 — Seed core address / phone fields (if collected during intake)
  • For each field, call `lacerte_field_write` with `{{ table: 1, clientNumber, field, value }}`.
  • Common DATA1 fields for I: C1_1 (first name), C1_2 (last name), C1_3 (email). Use `lacerte_field_access_map` if unsure which field is writable.
  • Pass `idempotencyKey` (e.g. ""intake-{name}-name"") so a retry does not double-write.

Step 5 — Seed starter detail rows (W-2 / 1099 / Schedule C totals collected at intake)
  • For each row, call `lacerte_detail_write` with `{{ clientNumber, row: {{ Series, Code, ... }} }}`.
  • The SDK does NOT validate detail-row shape; this tool's client-side validation is the only safeguard. Read the error message verbatim and fix the failed field before retrying.

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

        [McpServerPrompt(Name = "lacerte_bulk_field_update")]
        [Description(
            "Apply the same field write across a list of clients with dry-run preview, idempotency keys, " +
            "and a stop-on-first-error rule. Steps reference lacerte_session_status, lacerte_set_context, " +
            "lacerte_field_access_map, lacerte_field_write, and lacerte_client_read.")]
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
$@"You are about to bulk-update field `{fieldLabel}` on DATA{table} across multiple clients. Bulk writes are HIGH RISK — the steps below ENFORCE a dry-run preview AND idempotency keys to keep retries safe.

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
  • For each of the first {previewCount} clients, call `lacerte_field_write` with `{{ table: {table}, clientNumber, field: ""{fieldLabel}"", value, dryRun: true, idempotencyKey: ""bulk-<batchId>-<clientNumber>"" }}`.
  • If ANY preview returns a non-Success statusCode, STOP and report the failed client + the error. Do not proceed to the real write.

Step 5 — Real write (only if preview was 100% clean)
  • Loop over the full list. Call `lacerte_field_write` with `dryRun: false` and the SAME idempotencyKey shape — `""bulk-<batchId>-<clientNumber>""`.
  • On the first failure, STOP. Surface the structured error. Do not skip + continue — the operator needs to know exactly where the batch stopped.

Step 6 — Verify a sample
  • Pick 3 random clients from the batch. Call `lacerte_client_read` for each and confirm `{fieldLabel}` now holds the new value.
  • Report total written + sample-verified counts.

Idempotency note: if a retry replays the same `idempotencyKey` with the same payload, the session manager returns the cached result without re-writing. If the payload differs, it throws `IdempotencyConflict (9102)` — pick a fresh key suffix.";

            return new GetPromptResult
            {
                Description = "Apply a field write across many clients with dry-run preview, idempotency keys, stop-on-first-error.",
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
