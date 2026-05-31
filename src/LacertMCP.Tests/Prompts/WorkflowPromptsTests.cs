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
            // Confirmed fields (firstName=C1_1 / lastName=C1_2 / emailID=C1_3)
            // are spec-sourced; TBD-F5 markers must be present so the LLM
            // doesn't invent codes for fields the schema doesn't yet expose.
            var text = ((TextContentBlock)WorkflowPrompts.ClientIntake().Messages[0].Content).Text;
            Assert.Contains("CSV column", text);
            Assert.Contains("firstName", text);
            Assert.Contains("C1_1", text);
            Assert.Contains("emailID", text);
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
