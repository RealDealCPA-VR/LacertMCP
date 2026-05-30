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
                taxType: "Individual");

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ClientIDTooLong,
                root.GetProperty("statusCode").GetInt32());
            // Per task brief: message mentions LengthExceeded semantics.
            Assert.Contains("LengthExceeded",
                root.GetProperty("context").GetProperty("reason").GetString()!);
        }

        [Fact]
        public async Task ClientCreate_InvalidChars_ReturnsInValidClientName()
        {
            var (mgr, tool) = await NewRegisteredToolWithContextAsync();

            var json = await tool.LacerteClientCreate(
                clientID: "BAD!", // ! is not in [A-Z0-9]
                taxType: "Individual");

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
                taxType: "Individual");

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
                taxType: "Corporate"); // wrong for module I

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
                taxType: "");

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
                taxType: "Individual");

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
                idempotencyKey: "cc-1");
            Assert.True(Parse(json1).GetProperty("success").GetBoolean(), json1);

            // Same key, different ClientID → conflict.
            var json2 = await tool.LacerteClientCreate(
                clientID: "IDP2",
                taxType: "Individual",
                idempotencyKey: "cc-1");

            var root2 = Parse(json2);
            Assert.False(root2.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                root2.GetProperty("statusCode").GetInt32());
        }
    }
}
