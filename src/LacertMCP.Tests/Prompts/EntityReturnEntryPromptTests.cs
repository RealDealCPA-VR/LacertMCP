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

        // The safety contract: dictionary gate, no guessing, dry run before
        // commit, operator approval, verification handling.
        [Fact]
        public void Body_EnforcesDictionaryGateDryRunAndVerification()
        {
            var text = Body("1065");
            Assert.Contains("lacerte_field_dictionary", text);
            Assert.Contains("HARD GATE", text);
            Assert.Contains("Never guess", text);
            Assert.Contains("dryRun: true", text);
            Assert.Contains("approve", text);
            Assert.Contains("9022", text);
            Assert.Contains("lacerte_pre_filing_check", text);
            Assert.Contains("at least two", text); // 1065 partners note
        }

        // R8-review — retry advice differs for append-only detail rows, keys
        // are unique per source line, dry runs carry no key.
        [Fact]
        public void Body_DetailRetryAndKeyAdvice()
        {
            var text = Body("1041");
            Assert.Contains("NEVER retry", text);
            Assert.Contains("rowWritten", text);
            Assert.Contains("<source line number>", text);
            Assert.Contains("NO idempotency key", text);
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
