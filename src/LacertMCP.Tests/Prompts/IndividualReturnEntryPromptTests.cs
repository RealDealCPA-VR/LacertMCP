using System;
using LacertMCP.Server.Prompts;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LacertMCP.Tests.Prompts
{
    /// <summary>The 1040 return-entry workflow prompt.</summary>
    public class IndividualReturnEntryPromptTests
    {
        private static string Body(int? taxYear = null, string? sourceLabel = null)
            => ((TextContentBlock)WorkflowPrompts.IndividualReturnEntry(taxYear, sourceLabel).Messages[0].Content).Text;

        private static string EntityBody(string returnType)
            => ((TextContentBlock)WorkflowPrompts.EntityReturnEntry(returnType).Messages[0].Content).Text;

        private static int At(string text, string needle)
        {
            var i = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(i >= 0, "missing: " + needle);
            return i;
        }

        [Fact]
        public void Body_TargetsTheIndividualModule()
        {
            var text = Body();
            Assert.Contains("Form 1040", text);
            Assert.Contains("module 'I'", text);
            Assert.Contains("lacerte_session_status", text);
            Assert.Contains("lacerte_set_context", text);
        }

        [Fact]
        public void Arguments_AppearInTheBody()
        {
            var text = Body(2024, "Smith 2024 documents");
            Assert.Contains("tax year 2024", text);
            Assert.Contains("\"Smith 2024 documents\"", text);
            Assert.Contains("1040 2024 from", text);
        }

        // The safety contract shared with the entity workflow: dictionary gate,
        // no guessing, a cited proposal, a PERSON approves, then commit.
        [Fact]
        public void Body_EnforcesDictionaryGateProposalAndApproval()
        {
            var text = Body();
            Assert.Contains("lacerte_field_dictionary", text);
            Assert.Contains("Never guess", text);
            Assert.Contains("lacerte_proposal_create", text);
            Assert.Contains("lacerte_proposal_add_lines", text);
            Assert.Contains("lacerte_proposal_add_manual_items", text);
            Assert.Contains("lacerte_proposal_add_questions", text);
            Assert.Contains("lacerte_proposal_submit", text);
            Assert.Contains("You cannot approve a proposal", text);
            Assert.Contains("approval PIN", text);
            Assert.Contains("source` {document", text);
            Assert.Contains("{kind: \"detail\", detail: {series", text);
            Assert.Contains("9023", text);
            Assert.Contains("9022", text);
            Assert.Contains("NEVER retry", text);
            Assert.Contains("lacerte_pre_filing_check", text);
            // The shared rules are plain text here: no doubled (escaped) braces.
            Assert.DoesNotContain("{{", text);
        }

        // Gate order: mappings, then the document inventory, then the
        // proposal, the coverage check, submit, and only then commit.
        [Fact]
        public void Steps_AreInOrder()
        {
            var text = Body();
            var dictionary = At(text, "Step 3 — Load the verified mappings (HARD GATE)");
            var inventory = At(text, "Step 4 — Document inventory (HARD GATE");
            var people = At(text, "Step 5 — People and filing status");
            var build = At(text, "Step 6 — Build a proposal");
            var coverage = At(text, "Step 7 — Coverage check");
            var submit = At(text, "Step 8 — Submit for review (HARD GATE: a person approves)");
            var commit = At(text, "Step 9 — Commit after approval");
            var review = At(text, "Step 10 — Review");
            Assert.True(dictionary < inventory && inventory < people && people < build
                        && build < coverage && coverage < submit && submit < commit && commit < review);
            Assert.True(At(text, "Call `lacerte_proposal_submit`") > coverage);
            Assert.True(At(text, "call `lacerte_proposal_commit`") > submit);
        }

        // What is specific to a 1040: the inventory, one line per box per
        // document, taxpayer / spouse ownership, and nothing left unentered.
        [Fact]
        public void Body_Has1040Rules()
        {
            var text = Body();
            Assert.Contains("W-2", text);
            Assert.Contains("1099-INT", text);
            Assert.Contains("Schedule K-1", text);
            Assert.Contains("different tax year or a different person", text);
            Assert.Contains("ask whether it is complete BEFORE entering anything", text);
            Assert.Contains("One line per box per document", text);
            Assert.Contains("never one total", text);
            Assert.Contains("`tsj` (Taxpayer / Spouse / Joint)", text);
            Assert.Contains("stateAbbrev", text);
            Assert.Contains("lacerte_year_over_year", text);
            Assert.Contains("foundInFromYear", text);
            Assert.Contains("A document on the inventory with nothing recorded against it is a mistake", text);
        }

        // Judgement stays with the preparer, identifiers stay out of free
        // text, and Claude never states a refund it cannot see.
        [Fact]
        public void Body_KeepsJudgementPrivacyAndResultsWithThePreparer()
        {
            var text = Body();
            Assert.Contains("Never choose or change the filing status", text);
            Assert.Contains("Always a question, never your decision", text);
            Assert.Contains("Always a manual item, never a guess", text);
            Assert.Contains("show the last 4 digits only", text);
            Assert.Contains("Never state or estimate the refund or the tax due", text);
            Assert.Contains("e-filing stay in Lacerte", text);
        }

        // The submit and commit steps are one shared copy: apart from the
        // step number they read the same in both workflows.
        [Theory]
        [InlineData("Submit for review (HARD GATE: a person approves)", "Commit after approval")]
        [InlineData("Commit after approval", "Review")]
        public void SharedSteps_MatchTheEntityWorkflow(string heading, string nextHeading)
        {
            Assert.Equal(Section(EntityBody("1120"), heading, nextHeading), Section(Body(), heading, nextHeading));
        }

        private static string Section(string text, string heading, string nextHeading)
        {
            var start = At(text, "— " + heading);
            var end = text.IndexOf("— " + nextHeading, start + 1, StringComparison.Ordinal);
            Assert.True(end > start);
            // Drop the trailing "Step N " of the next heading.
            var section = text.Substring(start, end - start);
            return section.Substring(0, section.LastIndexOf("Step ", StringComparison.Ordinal));
        }

        [Fact]
        public async System.Threading.Tasks.Task LacertePrompts_ListsAndRendersIt()
        {
            var list = await PromptsCatalogTool.LacertePrompts();
            Assert.Contains("lacerte_individual_return_entry", list);
            Assert.Contains("\"count\":6", list);

            var json = await PromptsCatalogTool.LacertePrompts("lacerte_individual_return_entry");
            Assert.Contains("\"found\":true", json);
            Assert.Contains("Form 1040", json);
            Assert.Contains("Document inventory", json);
        }

        // The entity workflow sends a 1040 request to this one.
        [Fact]
        public void EntityWorkflow_PointsAt1040Workflow()
        {
            Assert.Contains("lacerte_individual_return_entry", EntityBody("1040"));
        }
    }
}
