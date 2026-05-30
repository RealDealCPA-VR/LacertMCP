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
    }
}
