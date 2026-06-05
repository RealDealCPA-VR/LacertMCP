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
    }
}
