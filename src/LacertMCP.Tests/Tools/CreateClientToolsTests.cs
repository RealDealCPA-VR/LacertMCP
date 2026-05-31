using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Phase 7 #57-59: <c>lacerte_client_create</c> happy path + every
    /// documented error path (DuplicateClient, LengthExceeded, InValidClientName,
    /// InValidTaxYear, TaxModuleMismatch) + dry-run + idempotency.
    /// </summary>
    public class CreateClientToolsTests
    {
        private static async Task<(LacerteSessionManager mgr, CreateClientTools tool)> NewRegisteredToolWithContextAsync(
            string? dataPath = null, int year = 2024, char module = 'I', bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            // Register first — SimulateCreateClient checks for the registered
            // latch before any other gate.
            await mgr.Register();
            mgr.SetContext(dataPath ?? SeedData.DataPathIData2024, year, module, readOnly);
            return (mgr, new CreateClientTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public async Task ClientCreate_HappyPath_ReturnsNewClientNumber()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "NEWC1",
                taxType: "Individual",
                firstName: "New",
                lastName: "Client",
                emailID: "new@example.com");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(StatusCodes.Success, result.GetProperty("statusCode").GetInt32());
            var cn = result.GetProperty("clientNumber").GetString();
            Assert.False(string.IsNullOrWhiteSpace(cn));
        }

        [Fact]
        public async Task ClientCreate_ClientIDTooLong_Returns9002()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "TOOLONGNAME", // 11 chars — exceeds 8
                taxType: "Individual",
                firstName: "Too",
                lastName: "Long",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ClientIDTooLong,
                root.GetProperty("statusCode").GetInt32());
            // P15-001: wire-scope redaction preserves the actionable
            // reason key (only true secrets get scrubbed) so the LLM
            // consumer sees the validation phrase that tells it WHY the
            // request was rejected, not just the status code.
            Assert.Contains("LengthExceeded",
                root.GetProperty("context").GetProperty("reason").GetString()!);
        }

        [Fact]
        public async Task ClientCreate_InvalidChars_ReturnsInValidClientName()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "BAD!", // ! is not in [A-Z0-9]
                taxType: "Individual",
                firstName: "Bad",
                lastName: "Chars",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.InValidClientName,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_DuplicateClientID_ReturnsDuplicateClient()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            // BROW is seeded in TY24 IData — duplicate should fail.
            var json = await tool.LacerteClientCreate(
                clientID: "BROW",
                taxType: "Individual",
                firstName: "Dup",
                lastName: "Brown",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.DuplicateClient,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_TaxYearTooOld_Returns9003()
        {
            // Year-too-old can't go through SetContext (which enforces >= 2018).
            // We trigger the session-level check by constructing the manager
            // and synthesizing a year-2017 context, but SetContext refuses that
            // too. So the only practical surface for 9003 from the create tool
            // is via the tool's own defensive check — we exercise the session
            // path by passing a fresh manager with no context, which surfaces
            // ContextNotSet (9009) instead. To test the 9003 path, manually
            // construct a session-level call through SetContext-then-mutate;
            // but SetContext is the only context surface. So we cover the
            // "TaxYearTooOld from SetContext" path here.
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();

            // SetContext with year 2017 should throw 9003 directly.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                mgr.SetContext(SeedData.DataPathIData2024, 2017, 'I'));
            Assert.Equal(SyntheticStatusCodes.TaxYearTooOld, ex.StatusCode);
        }

        [Fact]
        public async Task ClientCreate_TaxModuleMismatch_ReturnsTaxModuleMismatch()
        {
            // Active module is I (Individual) but pass a Corporate TaxType.
            var (mgr, tool) = await NewRegisteredToolWithContextAsync(module: 'I');

            var json = await tool.LacerteClientCreate(
                clientID: "MISMTCH",
                taxType: "Corporate", // wrong for module I
                firstName: "Mis",
                lastName: "Match",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.TaxModuleMismatch,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_EmptyTaxType_ReturnsTaxModuleMismatch()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "EMTYTYP",
                taxType: "",
                firstName: "Empty",
                lastName: "Type",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.TaxModuleMismatch,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_ReadOnlySession_Returns9001()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync(readOnly: true);

            var json = await tool.LacerteClientCreate(
                clientID: "ROC1",
                taxType: "Individual",
                firstName: "Read",
                lastName: "Only",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_DryRun_DoesNotPersist()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var beforeList = await mgr.ListClients(limit: 1000);
            var beforeCount = beforeList.Count;

            var json = await tool.LacerteClientCreate(
                clientID: "DRY1",
                taxType: "Individual",
                firstName: "Dry",
                lastName: "Run",
                emailID: "dry@example.com",
                dryRun: true);

            Assert.True(Parse(json).GetProperty("success").GetBoolean(), json);

            var afterList = await mgr.ListClients(limit: 1000);
            Assert.Equal(beforeCount, afterList.Count);
        }

        [Fact]
        public async Task ClientCreate_IdempotencyConflict_Returns9102()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json1 = await tool.LacerteClientCreate(
                clientID: "IDP1",
                taxType: "Individual",
                firstName: "Idem",
                lastName: "Potent",
                emailID: "idp@example.com",
                idempotencyKey: "cc-1");
            Assert.True(Parse(json1).GetProperty("success").GetBoolean(), json1);

            // Same key, different ClientID → conflict.
            var json2 = await tool.LacerteClientCreate(
                clientID: "IDP2",
                taxType: "Individual",
                firstName: "Idem",
                lastName: "Potent",
                emailID: "idp@example.com",
                idempotencyKey: "cc-1");

            var root2 = Parse(json2);
            Assert.False(root2.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                root2.GetProperty("statusCode").GetInt32());
        }

        // -----------------------------------------------------------------
        // P14-053: Required-field gates for firstName / lastName / emailID
        // -----------------------------------------------------------------

        [Fact]
        public async Task ClientCreate_MissingFirstName_ReturnsInValidClientName()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "NOFNAM",
                taxType: "Individual",
                firstName: "   ",
                lastName: "Doe",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.InValidClientName,
                root.GetProperty("statusCode").GetInt32());
            Assert.Equal("firstName",
                root.GetProperty("context").GetProperty("field").GetString());
        }

        [Fact]
        public async Task ClientCreate_MissingLastName_ReturnsInValidClientName()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "NOLNAM",
                taxType: "Individual",
                firstName: "John",
                lastName: "",
                emailID: "x@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.InValidClientName,
                root.GetProperty("statusCode").GetInt32());
            Assert.Equal("lastName",
                root.GetProperty("context").GetProperty("field").GetString());
        }

        [Fact]
        public async Task ClientCreate_MissingEmailID_ReturnsInValidClientName()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "NOEMAIL",
                taxType: "Individual",
                firstName: "John",
                lastName: "Doe",
                emailID: "");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.InValidClientName,
                root.GetProperty("statusCode").GetInt32());
            Assert.Equal("emailID",
                root.GetProperty("context").GetProperty("field").GetString());
        }

        // -----------------------------------------------------------------
        // P15-059 — colloquial form-number aliases canonicalize before
        // they reach the live SDK / sim store. The accepted alphabet for
        // IClient(TaxType, ...) is the 9 spec ReturnType strings; aliases
        // like "1040" / "1120S" / "S-Corporation" must succeed observation-
        // ally identically to the canonical "Individual" / "SCorporate".
        // -----------------------------------------------------------------

        [Theory]
        [InlineData('I', "1040")]
        [InlineData('I', "Individual")]
        [InlineData('I', "INDIVIDUAL")]
        public async Task ClientCreate_AcceptsAlias_OnModuleI(char module, string taxType)
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync(module: module);

            var json = await tool.LacerteClientCreate(
                clientID: "ALIAS1",
                taxType: taxType,
                firstName: "Al",
                lastName: "Ias",
                emailID: "a@example.com");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_FormNumberAlias_AndCanonical_YieldSameSuccessShape()
        {
            // Two independent managers — same context, same payload modulo
            // the TaxType alias. Both must end at statusCode=Success with a
            // freshly minted ClientNumber. This is the divergence the
            // recommendation called out: today live would reject "1040"
            // and accept "Individual", so the shapes would differ on the
            // happy path.
            var (mgrA, toolA) = await NewRegisteredToolWithContextAsync();
            var (mgrB, toolB) = await NewRegisteredToolWithContextAsync();

            var jsonAlias = await toolA.LacerteClientCreate(
                clientID: "C1040", taxType: "1040",
                firstName: "Form", lastName: "Number", emailID: "f@example.com");
            var jsonCanon = await toolB.LacerteClientCreate(
                clientID: "C1040", taxType: "Individual",
                firstName: "Form", lastName: "Number", emailID: "f@example.com");

            var rootAlias = Parse(jsonAlias);
            var rootCanon = Parse(jsonCanon);
            Assert.True(rootAlias.GetProperty("success").GetBoolean(), jsonAlias);
            Assert.True(rootCanon.GetProperty("success").GetBoolean(), jsonCanon);
            Assert.Equal(
                rootAlias.GetProperty("result").GetProperty("statusCode").GetInt32(),
                rootCanon.GetProperty("result").GetProperty("statusCode").GetInt32());
            Assert.Equal(
                rootAlias.GetProperty("result").GetProperty("statusName").GetString(),
                rootCanon.GetProperty("result").GetProperty("statusName").GetString());
        }

        [Theory]
        [InlineData('S', "1120S")]
        [InlineData('S', "S-Corporation")]
        [InlineData('S', "SCorp")]
        [InlineData('S', "SCorporate")]
        public async Task ClientCreate_AcceptsAlias_OnModuleS(char module, string taxType)
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync(module: module);

            var json = await tool.LacerteClientCreate(
                clientID: "ALIASS",
                taxType: taxType,
                firstName: "S",
                lastName: "Corp",
                emailID: "s@example.com");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(StatusCodes.Success,
                root.GetProperty("result").GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientCreate_UnknownAlias_StillReturnsTaxModuleMismatch()
        {
            // Canonicalization MUST NOT short-circuit the existing
            // TaxModuleMismatch flow for unknown aliases. The accept-gate
            // fires first, the operator sees the exact same 14 they did
            // before P15-059 with the acceptedTaxTypes list in context.
            var (mgr, tool) = await NewRegisteredToolWithContextAsync(module: 'I');

            var json = await tool.LacerteClientCreate(
                clientID: "BOGUS",
                taxType: "Bogus", // not an alias for module I
                firstName: "Bo",
                lastName: "Gus",
                emailID: "b@example.com");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(StatusCodes.TaxModuleMismatch,
                root.GetProperty("statusCode").GetInt32());
            // Accept-gate also includes the acceptedTaxTypes list — confirm
            // canonicalization didn't strip that context.
            var accepted = root.GetProperty("context").GetProperty("acceptedTaxTypes");
            Assert.Equal(System.Text.Json.JsonValueKind.Array, accepted.ValueKind);
            Assert.True(accepted.GetArrayLength() >= 2); // at least "Individual" + "1040"
        }
    }
}
