using LacertMCP.Server.Prompts;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LacertMCP.Tests.Prompts
{
    /// <summary>FP-4 — the entity return-entry workflow prompt.</summary>
    public class EntityReturnEntryPromptTests
    {
        private static string Body(string returnType)
            => ((TextContentBlock)WorkflowPrompts.EntityReturnEntry(returnType).Messages[0].Content).Text;

        [Theory]
        [InlineData("1120", "Form 1120,", "'C'")]
        [InlineData("1120-S", "Form 1120-S", "'S'")]
        [InlineData("s", "Form 1120-S", "'S'")]
        [InlineData("1065", "Form 1065", "'P'")]
        [InlineData("trust", "Form 1041", "'F'")]
        public void MapsReturnTypeToModule(string returnType, string form, string module)
        {
            var text = Body(returnType);
            Assert.Contains(form, text);
            Assert.Contains("module " + module, text);
        }

        // The safety contract: dictionary gate, no guessing, everything goes
        // into a proposal with citations, a PERSON approves, then commit with
        // read-back and a pre-filing check.
        [Fact]
        public void Body_EnforcesDictionaryGateProposalAndApproval()
        {
            var text = Body("1065");
            Assert.Contains("lacerte_field_dictionary", text);
            Assert.Contains("HARD GATE", text);
            Assert.Contains("Never guess", text);
            Assert.Contains("lacerte_proposal_create", text);
            Assert.Contains("lacerte_proposal_add_lines", text);
            Assert.Contains("lacerte_proposal_add_manual_items", text);
            Assert.Contains("lacerte_proposal_add_questions", text);
            Assert.Contains("lacerte_proposal_submit", text);
            Assert.Contains("lacerte_proposal_commit", text);
            Assert.Contains("You cannot approve a proposal", text);
            Assert.Contains("source` {document", text);
            Assert.Contains("confidence", text);
            Assert.Contains("9022", text);
            Assert.Contains("lacerte_pre_filing_check", text);
            Assert.Contains("at least two", text); // 1065 partners note
            // Commit comes only after approval, and after the submit step.
            Assert.True(text.IndexOf("lacerte_proposal_submit", System.StringComparison.Ordinal)
                        < text.IndexOf("call `lacerte_proposal_commit`", System.StringComparison.Ordinal));
        }

        // Detail rows that may have landed are never retried; one line per source line.
        [Fact]
        public void Body_DetailRetryAndSourceLineAdvice()
        {
            var text = Body("1041");
            Assert.Contains("NEVER retry", text);
            Assert.Contains("rowWritten", text);
            Assert.Contains("one line per SOURCE line", text);
            Assert.Contains("lacerte-mcp proposal resolve", text);
            Assert.Contains("{kind: \"detail\", detail: {series", text);
        }

        [Fact]
        public async System.Threading.Tasks.Task LacertePrompts_RendersTheRequestedReturnType()
        {
            var json = await PromptsCatalogTool.LacertePrompts("lacerte_entity_return_entry", returnType: "1065");
            Assert.Contains("Form 1065", json);
            Assert.DoesNotContain("Form 1120,", json);
        }

        [Fact]
        public void UnknownReturnType_AsksInsteadOfGuessing()
        {
            var text = Body("1040");
            Assert.Contains("not one this workflow covers", text);
            Assert.DoesNotContain("lacerte_field_write", text);
        }
    }
}
