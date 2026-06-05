using System.Threading.Tasks;
using LacertMCP.Server.Prompts;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LacertMCP.Tests.Prompts
{
    /// <summary>
    /// Phase 11 task #79 — prompt content / shape tests. The MCP SDK's
    /// runtime registration happens in Program.cs; here we exercise the
    /// methods directly so the prompt bodies, parameter defaults, and tool
    /// references are covered without standing up an MCP server.
    /// </summary>
    public class WorkflowPromptsTests
    {
        [Fact]
        public void ClientIntake_DefaultArg_ReturnsUserMessageWithSteps()
        {
            var result = WorkflowPrompts.ClientIntake();
            Assert.NotNull(result);
            Assert.NotEmpty(result.Messages);
            var msg = result.Messages[0];
            Assert.Equal(Role.User, msg.Role);
            var text = Assert.IsType<TextContentBlock>(msg.Content);
            // Bodies should reference the actual tool names so the agent's
            // tool-use loop is deterministic.
            Assert.Contains("lacerte_session_status", text.Text);
            Assert.Contains("lacerte_set_context", text.Text);
            Assert.Contains("lacerte_client_create", text.Text);
            Assert.Contains("lacerte_detail_write", text.Text);
        }

        [Fact]
        public void ClientIntake_WithName_NamePresentInBody()
        {
            var result = WorkflowPrompts.ClientIntake(clientName: "Acme LLC");
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("Acme LLC", text);
        }

        [Fact]
        public void ReviewEfStatus_DefaultArg_ReturnsTriagePlan()
        {
            var result = WorkflowPrompts.ReviewEfStatus();
            Assert.NotEmpty(result.Messages);
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("lacerte_ef_status", text);
            Assert.Contains("Rejected", text);
            Assert.Contains("Transmitted", text);
        }

        [Fact]
        public void ReviewEfStatus_WithYear_YearAppearsInBody()
        {
            var result = WorkflowPrompts.ReviewEfStatus(taxYear: 2023);
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("2023", text);
        }

        [Fact]
        public void BulkFieldUpdate_Defaults_ReferenceDryRunAndIdempotency()
        {
            var result = WorkflowPrompts.BulkFieldUpdate();
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("lacerte_field_write", text);
            Assert.Contains("dryRun", text);
            Assert.Contains("idempotencyKey", text);
        }

        [Fact]
        public void BulkFieldUpdate_UsesBulkToolNotPerClientLoop()
        {
            // P16-035 — the bulk-update prompt MUST steer the LLM to the
            // lacerte_field_write_bulk single-call tool (P15-008) rather than
            // looping lacerte_field_write per client. The prompt body must
            // mention the bulk tool by name, mention `continueOnError`, and
            // mention the `perItem[]` aggregation shape so the LLM knows
            // exactly how to read the response. Locks the post-P16-035
            // contract so a future refactor can't silently regress to the
            // per-client loop pattern.
            var result = WorkflowPrompts.BulkFieldUpdate();
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("lacerte_field_write_bulk", text);
            Assert.Contains("continueOnError", text);
            Assert.Contains("perItem", text);
            // Belt-and-suspenders: the prompt must NOT instruct the LLM to
            // "loop ... per client" / "for each client, call lacerte_field_write".
            // The pre-P16-035 wording used "Loop over the full list" with
            // per-client lacerte_field_write — confirm that pattern is gone.
            Assert.DoesNotContain("Loop over the full list", text);
        }

        [Fact]
        public void ClientIntake_Step5_UsesFlatDetailWriteSignatureNotNestedRow()
        {
            // P16-017 — Step 5's lacerte_detail_write example MUST use the
            // actual flat 12-param signature (series, code, amount, description,
            // plus the 7 optional row-key segments) and MUST NOT use the old
            // invented `{ row: { Series, Code, ... } }` nested-object shape
            // that does not exist on the tool. An LLM following the old shape
            // hit a parameter-binding error on the first detail write of
            // every intake. Locks the post-P16-017 contract.
            var text = ((TextContentBlock)WorkflowPrompts.ClientIntake().Messages[0].Content).Text;
            // Real param names must be present as kwargs.
            Assert.Contains("series:", text);
            Assert.Contains("code:", text);
            Assert.Contains("amount:", text);
            Assert.Contains("description:", text);
            // The Series cheat-sheet must spell out at least the common buckets
            // so the LLM doesn't invent codes.
            Assert.Contains("11=W-2", text);
            // Pre-P16-017 shape must be gone. The literal `row: {` was the
            // tell-tale; check for it (case-sensitive — match the old text
            // verbatim so this test only false-fires if someone reintroduces
            // the exact bad pattern).
            Assert.DoesNotContain("row: {{", text);
            Assert.DoesNotContain("Series, Code, ...", text);
        }

        [Fact]
        public void BulkFieldUpdate_WithField_FieldNameInBody()
        {
            var result = WorkflowPrompts.BulkFieldUpdate(table: 1, field: "C1_5", previewCount: 7);
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("C1_5", text);
            Assert.Contains("7", text);
        }

        [Fact]
        public void ClientIntake_Step4_DocumentsCanonicalCsvColumnMapping()
        {
            // P15-038 part 3 — Step 4 must spell out the canonical CSV column →
            // Lacerte field mapping so the LLM doesn't reinvent it per session.
            // Confirmed fields (firstName=C1_1 / lastName=C1_2 / emailId=C1_3)
            // are spec-sourced; TBD-F5 markers must be present so the LLM
            // doesn't invent codes for fields the schema doesn't yet expose.
            // P17-005 — assertion was previously `emailID` (ALL-CAPS suffix)
            // which pinned the broken spelling left over from P16-080's
            // half-done rename; rebound to canonical camelCase `emailId`
            // (matches the wire parameter on lacerte_client_create).
            var text = ((TextContentBlock)WorkflowPrompts.ClientIntake().Messages[0].Content).Text;
            Assert.Contains("CSV column", text);
            Assert.Contains("firstName", text);
            Assert.Contains("C1_1", text);
            Assert.Contains("emailId", text);
            Assert.Contains("C1_3", text);
            Assert.Contains("TBD-F5", text);
            Assert.Contains("addressLine1", text);
            Assert.Contains("dateOfBirth", text);
        }

        [Fact]
        public void CsvIntake_DefaultArgs_ReturnsBulkOnboardingPlan()
        {
            var result = WorkflowPrompts.CsvIntake();
            Assert.NotNull(result);
            Assert.NotEmpty(result.Messages);
            var msg = result.Messages[0];
            Assert.Equal(Role.User, msg.Role);
            var text = ((TextContentBlock)msg.Content).Text;
            // Touchstones: declared mapping, idempotency key shape, stop-on-first-error,
            // and reference to the real tool names the LLM will call.
            Assert.Contains("lacerte_session_status", text);
            Assert.Contains("lacerte_set_context", text);
            Assert.Contains("lacerte_field_access_map", text);
            Assert.Contains("lacerte_client_create", text);
            Assert.Contains("lacerte_field_write", text);
            Assert.Contains("idempotencyKey", text);
            Assert.Contains("intake.csv", text);
        }

        [Fact]
        public void CsvIntake_WithSourceLabelAndRowCount_AppearsInBody()
        {
            var result = WorkflowPrompts.CsvIntake(sourceLabel: "2024-organizer.csv", rowCount: 87);
            var text = ((TextContentBlock)result.Messages[0].Content).Text;
            Assert.Contains("2024-organizer.csv", text);
            Assert.Contains("87", text);
            // Row count > 50 should trigger the explicit-confirmation gate;
            // the prompt body wires the literal "> 50" check, which we assert
            // tolerantly (collapsing whitespace) so future copy-edits don't
            // false-fail.
            Assert.Contains(">50", text.Replace(" ", string.Empty));
        }

        // ----------------------------------------------------------------
        // P18-003 — CsvIntake Step 4 must reference DuplicateClient as a
        // NAME match (statusCode 12), not the stale "(status 18)" integer.
        // status 18 in StatusCodes.cs is DB3Error (a state-DB I/O failure),
        // so the prior wording would have either silently misclassified
        // transient DB3Errors as duplicates OR made a careful LLM doubt the
        // mapping entirely. Pins the post-P18-003 contract — fails fast on
        // a regression that flips the integer back.
        // ----------------------------------------------------------------
        [Fact]
        public void CsvIntake_Step4_DuplicateClientUsesCorrectStatusCode12NotStaleEighteen()
        {
            var text = ((TextContentBlock)WorkflowPrompts.CsvIntake().Messages[0].Content).Text;
            // The corrected wording must reference DuplicateClient by name
            // (StatusCodes.cs:43 DuplicateClient = 12), and must NOT carry
            // the stale "status 18" integer (StatusCodes.cs:49 DB3Error = 18).
            Assert.Contains("DuplicateClient", text);
            Assert.DoesNotContain("(status 18)", text);
            // Confirmatory integer (12) is allowed but the name must lead;
            // a careful LLM should be able to find DuplicateClient as a
            // string match regardless of the integer.
            Assert.Contains("DuplicateClient", text);
        }

        // ----------------------------------------------------------------
        // P18-012 — Step 4 of ClientIntake must give an idempotencyKey
        // shape that includes a per-field discriminator. The prior literal
        // "intake-{name}-name" would have been reused across every field
        // write, and on the second write the cached payload-fingerprint
        // mismatch would throw IdempotencyConflict (9102). Pins the
        // per-field key shape so a regression that drops the <field>
        // discriminator fails fast.
        // ----------------------------------------------------------------
        [Fact]
        public void ClientIntake_Step4_IdempotencyKeyHasPerFieldDiscriminator()
        {
            var text = ((TextContentBlock)WorkflowPrompts.ClientIntake().Messages[0].Content).Text;
            // The shape must include <field> (or <clientId>-<field>) so each
            // distinct write gets a distinct cache key. The stale literal
            // "intake-{name}-name" must be gone.
            Assert.Contains("<clientId>-<field>", text);
            // A warning about reuse + IdempotencyConflict must be present so
            // the LLM understands the contract, not just the example.
            Assert.Contains("IdempotencyConflict", text);
            Assert.Contains("9102", text);
            // The stale literal that pinned the broken shape must NOT be in
            // the prompt body any longer.
            Assert.DoesNotContain("intake-{name}-name", text);
        }

        // ----------------------------------------------------------------
        // P18-013 — Step 4 must call out the degenerate-shard case
        // explicitly so the LLM does not (a) call lacerte_field_access_map
        // 8 times per row only to learn each TBD field is unknown, and (b)
        // does not silently fabricate empty writes. Pins the IMPORTANT
        // block introduced for v1 (pre-Phase-F5) shards.
        // ----------------------------------------------------------------
        [Fact]
        public void ClientIntake_Step4_DegenerateShardHandlingIsDocumented()
        {
            var text = ((TextContentBlock)WorkflowPrompts.ClientIntake().Messages[0].Content).Text;
            Assert.Contains("degenerate-shard handling", text);
            Assert.Contains("Phase-F5", text);
            Assert.Contains("Do NOT call `lacerte_field_access_map` per TBD column", text);
            // The 5-element STOP list (including Unknown + absent) must be
            // referenced for the UNKNOWN-column escape hatch.
            Assert.Contains("Unknown", text);
            Assert.Contains("NotExposed", text);
            Assert.Contains("NotSupported", text);
        }

        // ----------------------------------------------------------------
        // P18-014 — ReviewEfStatus Step 4 must explicitly bind the EF
        // row's ClientId to the detail_read tool's clientNumber parameter,
        // because the parameter NAME mismatch otherwise misleads a careful
        // LLM (per ClientsTools.cs:112 ClientNumber-vs-ClientID hint).
        // Step 5's column header must surface clientNumber (canonical
        // wire name) — the join column ef.ClientId IS the SDK ClientNumber.
        // ----------------------------------------------------------------
        [Fact]
        public void ReviewEfStatus_Step4_ExplicitlyBindsClientIdToClientNumber()
        {
            var text = ((TextContentBlock)WorkflowPrompts.ReviewEfStatus().Messages[0].Content).Text;
            // The Step 4 instruction must teach the LLM that the EF row's
            // ClientId IS the clientNumber the detail_read tool expects, so
            // the call does not get blocked on a parameter-name discrepancy.
            Assert.Contains("clientNumber", text);
            // Cross-walk text linking the two names must be present (not
            // just one mention) so the LLM has the binding hint near the
            // call site.
            Assert.Contains("ef.ClientId = ci.C1_0", text);
            // Step 5's column header surfaces clientNumber (canonical) — the
            // operator's report row must NOT carry the ambiguous ClientId
            // label as the LEADING column. We assert clientNumber leads.
            Assert.Contains("clientNumber | EFId", text);
        }

        // ----------------------------------------------------------------
        // P18-015 — BulkFieldUpdate Step 2's STOP list must include
        // Unknown / absent (in addition to ReadOnly / Hidden / NotExposed
        // / NotSupported). MetadataTools.cs:109 actively filters Unknown
        // entries from the access-map response — so an LLM that only
        // sees a SHORT STOP list might conclude "field not in
        // {ReadOnly,Hidden,NotExposed,NotSupported}, therefore safe to
        // bulk-write" and fire up to 500 chokepoint-rejected writes.
        // ----------------------------------------------------------------
        [Fact]
        public void BulkFieldUpdate_Step2_StopListIncludesUnknownAndAbsentAndNotSupported()
        {
            var text = ((TextContentBlock)WorkflowPrompts.BulkFieldUpdate().Messages[0].Content).Text;
            // All five access states must appear in the STOP list. NotSupported
            // covers read-only families, Unknown covers the v1-shard default,
            // ABSENT covers the loader-filtered case.
            Assert.Contains("Unknown", text);
            Assert.Contains("ABSENT", text);
            Assert.Contains("ReadOnly", text);
            Assert.Contains("Hidden", text);
            Assert.Contains("NotExposed", text);
            Assert.Contains("NotSupported", text);
            // The v1 shard reality (only C{table}_0 cataloged) must be
            // explicitly explained so the LLM understands why the STOP gate
            // matters in practice.
            Assert.Contains("Phase-F5", text.Replace("Todo.md F5", "Phase-F5"));
            Assert.Contains("UnknownFieldAccess", text);
        }

        // ----------------------------------------------------------------
        // P18-015 follow-up — CsvIntake Step 2's STOP list must mirror the
        // BulkFieldUpdate STOP list so the two prompts agree on the access
        // gate. The original wording omitted NotExposed / NotSupported /
        // ABSENT.
        // ----------------------------------------------------------------
        [Fact]
        public void CsvIntake_Step2_StopListMirrorsBulkFieldUpdateStopList()
        {
            var text = ((TextContentBlock)WorkflowPrompts.CsvIntake().Messages[0].Content).Text;
            Assert.Contains("Unknown", text);
            Assert.Contains("ReadOnly", text);
            Assert.Contains("Hidden", text);
            Assert.Contains("NotExposed", text);
            Assert.Contains("NotSupported", text);
        }

        // ----------------------------------------------------------------
        // P18-023 — Step 6 must NOT unconditionally instruct "surface the
        // address fields you just wrote" because Step 4 makes those address
        // writes impossible until Phase-F5 lands. The corrected wording must
        // be conditional (only the fields actually written) and must set the
        // expectation that the v1 baseline read-back is firstName/lastName/
        // emailId only.
        // ----------------------------------------------------------------
        [Fact]
        public void ClientIntake_Step6_AlignsWithStep4DegenerateShardReality()
        {
            var text = ((TextContentBlock)WorkflowPrompts.ClientIntake().Messages[0].Content).Text;
            // Must reference the Phase-F5 gate explicitly so the operator's
            // read-back does not look like a bug.
            Assert.Contains("gated on Todo.md F5", text);
            // Must warn against fabricating values to fill the empty address
            // section (the original "surface the address fields you just
            // wrote" wording could otherwise prompt hallucination).
            Assert.Contains("do not fabricate", text);
            // The stale literal that pinned the broken Step 4/Step 6 mismatch
            // must NOT remain — "surface the address fields you just wrote"
            // is the smoking-gun phrase that asserts data that does not exist.
            Assert.DoesNotContain("surface the address fields you just wrote", text);
        }

        // ----------------------------------------------------------------
        // P18-017 / P18-023 — `lacerte_prompts` tool: when called with no
        // arguments must list every workflow prompt available on this MCP,
        // and when called with a name must return the full body so hosts
        // without a Prompts UI (Cursor, Windsurf, custom CLI) can still
        // surface the recipes through the tools/list channel.
        // ----------------------------------------------------------------
        [Fact]
        public async Task LacertePrompts_NoArg_ListsAllRegisteredWorkflowRecipes()
        {
            var json = await PromptsCatalogTool.LacertePrompts();
            Assert.False(string.IsNullOrWhiteSpace(json));
            // Canonical WrapToolHandler success envelope.
            Assert.Contains("\"success\":true", json);
            // Every prompt registered by WorkflowPrompts must appear in the
            // listing — locks the catalog in lock-step with the prompts
            // surface so a future added prompt is forced to also be added
            // to the catalog table.
            Assert.Contains("lacerte_client_intake", json);
            Assert.Contains("lacerte_review_ef_status", json);
            Assert.Contains("lacerte_field_write_bulk", json);
            Assert.Contains("lacerte_csv_intake", json);
            // Discovery hint pointing at the body-fetch invocation.
            Assert.Contains("whenToUse", json);
        }

        [Fact]
        public async Task LacertePrompts_WithName_ReturnsFullPromptBody()
        {
            var json = await PromptsCatalogTool.LacertePrompts("lacerte_client_intake");
            Assert.Contains("\"success\":true", json);
            Assert.Contains("\"found\":true", json);
            Assert.Contains("\"name\":\"lacerte_client_intake\"", json);
            // The body must be the rendered prompt text — assert it carries
            // a recognizable Step 1 anchor and at least one canonical tool
            // reference, so a body-substitution regression is caught.
            Assert.Contains("Step 1", json);
            Assert.Contains("lacerte_session_status", json);
        }

        [Fact]
        public async Task LacertePrompts_WithName_FetchesEveryRegisteredRecipe()
        {
            foreach (var promptName in new[]
            {
                "lacerte_client_intake",
                "lacerte_review_ef_status",
                "lacerte_field_write_bulk",
                "lacerte_csv_intake",
            })
            {
                var json = await PromptsCatalogTool.LacertePrompts(promptName);
                Assert.Contains("\"found\":true", json);
                Assert.Contains($"\"name\":\"{promptName}\"", json);
                // Every body must be non-trivial (>200 chars after envelope
                // overhead) so an empty-body regression cannot slip through.
                Assert.True(json.Length > 200, $"prompt body for {promptName} too short");
            }
        }

        [Fact]
        public async Task LacertePrompts_UnknownName_ReturnsNotFoundWithAvailableNames()
        {
            var json = await PromptsCatalogTool.LacertePrompts("lacerte_does_not_exist");
            Assert.Contains("\"success\":true", json);
            Assert.Contains("\"found\":false", json);
            // Must echo the requested name so the LLM can fix its call.
            Assert.Contains("lacerte_does_not_exist", json);
            // And must list available names as the recovery hint.
            Assert.Contains("availableNames", json);
            Assert.Contains("lacerte_client_intake", json);
        }
    }
}
