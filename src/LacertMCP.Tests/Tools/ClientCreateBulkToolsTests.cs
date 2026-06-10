using System;
using System.Collections.Generic;
using System.Linq;
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
    /// R6 gap #2: <c>lacerte_client_create_bulk</c> — the CSV-intake
    /// bootstrap tool (REQUIREMENTS.md #3). Covers: happy path multi-row,
    /// cap rejection (9020 + LACERTE_BULK_CREATE_MAX override), dryRun
    /// (no mutation + null clientNumber + wouldCreate=true per R6-22),
    /// continueOnError both ways, duplicate clientId within a batch,
    /// per-row positional idempotency replay (no duplicate clients),
    /// validation-failure rows, cancellation (stoppedEarly), and the
    /// context-not-set preflight.
    /// </summary>
    public class ClientCreateBulkToolsTests
    {
        private static (LacerteSessionManager mgr, ClientCreateBulkTools tool) NewToolWithContext(bool readOnly = false)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            // SimulateCreateClient checks the registered latch first.
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I', readOnly);
            return (mgr, new ClientCreateBulkTools(mgr));
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        private static ClientCreateBulkTools.BulkCreateEntry Entry(string clientId) =>
            new ClientCreateBulkTools.BulkCreateEntry
            {
                ClientId = clientId,
                TaxType = "Individual",
                FirstName = "Bulk",
                LastName = "Created",
                EmailId = $"{clientId.ToLowerInvariant()}@example.com",
            };

        private static async Task<int> ClientCount(LacerteSessionManager mgr)
        {
            var rows = await mgr.ListClients(limit: 1000);
            return rows.Count;
        }

        // (a) Happy path — 3 entries all create; summary counts + per-row
        //     shape (succeeded, statusCode, statusName, clientNumber,
        //     wouldCreate=false) + store actually grew by 3.
        [Fact]
        public async Task CreateBulk_HappyPath_ThreeRows_AllSucceed()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = await ClientCount(mgr);

            var json = await tool.LacerteClientCreateBulk(
                new[] { Entry("BCR0"), Entry("BCR1"), Entry("BCR2") });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(3, result.GetProperty("processedCount").GetInt32());
            Assert.Equal(3, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("skippedCount").GetInt32());
            Assert.False(result.GetProperty("stoppedEarly").GetBoolean());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.Equal(i, rows[i].GetProperty("index").GetInt32());
                Assert.True(rows[i].GetProperty("succeeded").GetBoolean());
                Assert.Equal(StatusCodes.Success, rows[i].GetProperty("statusCode").GetInt32());
                Assert.Equal("Success", rows[i].GetProperty("statusName").GetString());
                Assert.False(string.IsNullOrWhiteSpace(
                    rows[i].GetProperty("clientNumber").GetString()));
                Assert.False(rows[i].GetProperty("wouldCreate").GetBoolean());
            }

            Assert.Equal(before + 3, await ClientCount(mgr));
        }

        // (b) Cap rejection — entries.Length > LACERTE_BULK_CREATE_MAX
        //     throws 9020 BulkBatchTooLarge BEFORE any row runs, with
        //     {entryCount, maxAllowed, envOverride} context naming the
        //     create-specific env var.
        [Fact]
        public async Task CreateBulk_ExceedsCap_Throws9020_NothingCreated()
        {
            var prior = Environment.GetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, "2");

                var (mgr, tool) = NewToolWithContext();
                var before = await ClientCount(mgr);

                var json = await tool.LacerteClientCreateBulk(
                    new[] { Entry("CAP0"), Entry("CAP1"), Entry("CAP2") });

                var root = Parse(json);
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(SyntheticStatusCodes.BulkBatchTooLarge,
                    root.GetProperty("statusCode").GetInt32());
                Assert.Equal("BulkBatchTooLarge",
                    root.GetProperty("statusName").GetString());

                var ctx = root.GetProperty("context");
                Assert.Equal(3, ctx.GetProperty("entryCount").GetInt32());
                Assert.Equal(2, ctx.GetProperty("maxAllowed").GetInt32());
                Assert.Equal(ClientCreateBulkTools.BulkCreateMaxEnvName,
                    ctx.GetProperty("envOverride").GetString());

                // Fail-closed: no row ran.
                Assert.Equal(before, await ClientCount(mgr));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, prior);
            }
        }

        // (c) entries.Length == cap passes (upper bound, not strict).
        [Fact]
        public async Task CreateBulk_AtCapBoundary_Succeeds()
        {
            var prior = Environment.GetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, "2");

                var (mgr, tool) = NewToolWithContext();
                var json = await tool.LacerteClientCreateBulk(
                    new[] { Entry("CAPB0"), Entry("CAPB1") });

                var root = Parse(json);
                Assert.True(root.GetProperty("success").GetBoolean(), json);
                Assert.Equal(2, root.GetProperty("result").GetProperty("succeededCount").GetInt32());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, prior);
            }
        }

        // (d) Env-var parsing — invalid values fall back to the default 100
        //     (never-throw-from-env-parse convention); valid values replace.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-number")]
        [InlineData("-1")]
        [InlineData("0")]
        public void ResolveMaxBulkCreateEntries_InvalidEnvVar_FallsBackToDefault(string? raw)
        {
            var prior = Environment.GetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, raw);
                Assert.Equal(ClientCreateBulkTools.DefaultMaxBulkCreateEntries,
                    ClientCreateBulkTools.ResolveMaxBulkCreateEntries());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, prior);
            }
        }

        [Fact]
        public void ResolveMaxBulkCreateEntries_ValidEnvVar_ReplacesDefault()
        {
            var prior = Environment.GetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName);
            try
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, "250");
                Assert.Equal(250, ClientCreateBulkTools.ResolveMaxBulkCreateEntries());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ClientCreateBulkTools.BulkCreateMaxEnvName, prior);
            }
        }

        // (e) Default cap is 100 — stricter than the 500 field-write cap
        //     (creates are heavier).
        [Fact]
        public void DefaultCap_Is100_StricterThanFieldWriteBulk()
        {
            Assert.Equal(100, ClientCreateBulkTools.DefaultMaxBulkCreateEntries);
            Assert.True(ClientCreateBulkTools.DefaultMaxBulkCreateEntries
                < FieldWriteBulkTools.DefaultMaxBulkEntries);
        }

        // (f) dryRun — every row validates + simulates then rolls back. Per
        //     R6-22 a dry-run create CANNOT mint, so rows carry
        //     clientNumber=null and wouldCreate=true; the store is unchanged.
        [Fact]
        public async Task CreateBulk_DryRun_NoMutation_NullClientNumber_WouldCreateTrue()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = await ClientCount(mgr);

            var json = await tool.LacerteClientCreateBulk(
                new[] { Entry("DRYB0"), Entry("DRYB1") },
                dryRun: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("dryRun").GetBoolean());
            Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());

            foreach (var row in result.GetProperty("rows").EnumerateArray())
            {
                Assert.True(row.GetProperty("succeeded").GetBoolean());
                Assert.True(row.GetProperty("wouldCreate").GetBoolean());
                // Dry-run rows must NEVER leak a ClientNumber (R6-22).
                Assert.Equal(JsonValueKind.Null, row.GetProperty("clientNumber").ValueKind);
            }

            Assert.Equal(before, await ClientCount(mgr));
        }

        // (g) Stop-on-error (default) — row 1 fails validation, row 2 is
        //     marked skipped and NOT created.
        [Fact]
        public async Task CreateBulk_StopOnError_HaltsAtFirstFailureAndSkipsRemainder()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(new[]
            {
                Entry("SOE0"),
                Entry("BAD!"), // ! fails the ^[A-Z0-9]+$ gate
                Entry("SOE2"),
            });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(1, result.GetProperty("skippedCount").GetInt32());
            Assert.Equal(1, result.GetProperty("firstFailureIndex").GetInt32());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.False(rows[1].GetProperty("succeeded").GetBoolean());
            Assert.Equal(StatusCodes.InValidClientName,
                rows[1].GetProperty("statusCode").GetInt32());
            Assert.False(rows[2].GetProperty("succeeded").GetBoolean());
            Assert.True(rows[2].GetProperty("skipped").GetBoolean());

            // The skipped row's client must NOT exist.
            var all = await mgr.ListClients(limit: 1000);
            Assert.DoesNotContain(all, r =>
                r.TryGetValue("clientId", out var v) && v is string s && s == "SOE2");
        }

        // (h) continueOnError=true — same payload keeps going past the bad
        //     row; row 2 IS created.
        [Fact]
        public async Task CreateBulk_ContinueOnError_KeepsGoingPastFailure()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(new[]
                {
                    Entry("COE0"),
                    Entry("BAD!"),
                    Entry("COE2"),
                },
                continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(2, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("skippedCount").GetInt32());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(rows[2].GetProperty("succeeded").GetBoolean());

            var all = await mgr.ListClients(limit: 1000);
            Assert.Contains(all, r =>
                r.TryGetValue("clientId", out var v) && v is string s && s == "COE2");
        }

        // (i) Duplicate clientId WITHIN one batch — the first row creates,
        //     the second hits the session's DuplicateClient gate (12).
        [Fact]
        public async Task CreateBulk_DuplicateClientIdInBatch_SecondRowFailsDuplicateClient()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(new[]
                {
                    Entry("DUPB"),
                    Entry("DUPB"),
                },
                continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.False(rows[1].GetProperty("succeeded").GetBoolean());
            Assert.Equal(StatusCodes.DuplicateClient,
                rows[1].GetProperty("statusCode").GetInt32());
        }

        // (j) Seeded duplicate — BROW exists in TY24 IData; a bulk row with
        //     clientId BROW fails DuplicateClient exactly like single-create.
        [Fact]
        public async Task CreateBulk_SeededDuplicate_FailsDuplicateClient()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(
                new[] { Entry("BROW") },
                continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var rows = root.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(StatusCodes.DuplicateClient,
                rows[0].GetProperty("statusCode").GetInt32());
        }

        // (k) Idempotency replay — resubmitting the SAME batch with the SAME
        //     prefix replays every row from cache: same clientNumbers, no
        //     duplicate clients minted, no DuplicateClient errors.
        [Fact]
        public async Task CreateBulk_IdempotencyReplay_SameBatch_NoDuplicates()
        {
            var (mgr, tool) = NewToolWithContext();
            var entries = new[] { Entry("RPL0"), Entry("RPL1") };

            var json1 = await tool.LacerteClientCreateBulk(
                entries, idempotencyKey: "ccb-replay");
            var root1 = Parse(json1);
            Assert.True(root1.GetProperty("success").GetBoolean(), json1);
            Assert.Equal(2, root1.GetProperty("result").GetProperty("succeededCount").GetInt32());
            var numbers1 = root1.GetProperty("result").GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("clientNumber").GetString()).ToArray();

            var countAfterFirst = await ClientCount(mgr);

            // Replay: same entries, same order, same prefix.
            var json2 = await tool.LacerteClientCreateBulk(
                entries, idempotencyKey: "ccb-replay");
            var root2 = Parse(json2);
            Assert.True(root2.GetProperty("success").GetBoolean(), json2);
            var result2 = root2.GetProperty("result");
            Assert.Equal(2, result2.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, result2.GetProperty("failedCount").GetInt32());
            var numbers2 = result2.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("clientNumber").GetString()).ToArray();

            // Cached replay returns the ORIGINAL clientNumbers...
            Assert.Equal(numbers1, numbers2);
            // ...and mints nothing new.
            Assert.Equal(countAfterFirst, await ClientCount(mgr));
        }

        // (l) Same prefix + DIFFERENT per-row payload → 9102
        //     IdempotencyConflict for the changed rows only.
        [Fact]
        public async Task CreateBulk_IdempotencyConflict_DifferentPayloadSameKey_Returns9102Rows()
        {
            var (mgr, tool) = NewToolWithContext();

            var json1 = await tool.LacerteClientCreateBulk(
                new[] { Entry("CFL0") }, idempotencyKey: "ccb-conflict");
            Assert.True(Parse(json1).GetProperty("success").GetBoolean(), json1);

            // Same key, same position, same clientId — but different name.
            var changed = Entry("CFL0");
            changed.FirstName = "Changed";
            var json2 = await tool.LacerteClientCreateBulk(
                new[] { changed },
                continueOnError: true,
                idempotencyKey: "ccb-conflict");

            var root2 = Parse(json2);
            Assert.True(root2.GetProperty("success").GetBoolean(), json2);
            var rows = root2.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.False(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.IdempotencyConflict,
                rows[0].GetProperty("statusCode").GetInt32());
        }

        // (m) Validation-failure row — missing emailId fails per-row with
        //     InValidClientName + context.field=emailId, identical surface
        //     to the single-create tool (P14-053 parity).
        [Fact]
        public async Task CreateBulk_MissingEmail_RowFailsInValidClientName()
        {
            var (mgr, tool) = NewToolWithContext();

            var bad = Entry("NOEML");
            bad.EmailId = "";

            var json = await tool.LacerteClientCreateBulk(
                new[] { bad, Entry("OKEML") },
                continueOnError: true);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(1, result.GetProperty("failedCount").GetInt32());

            var rows = result.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(StatusCodes.InValidClientName,
                rows[0].GetProperty("statusCode").GetInt32());
            Assert.Equal("emailId",
                rows[0].GetProperty("context").GetProperty("field").GetString());
            Assert.True(rows[1].GetProperty("succeeded").GetBoolean());
        }

        // (n) Pre-cancelled token — stops BEFORE any mutation; partial
        //     envelope carries stoppedEarly=true + processedCount=0 (R6-20b
        //     pattern inherited from lacerte_field_write_bulk).
        [Fact]
        public async Task CreateBulk_PreCancelledToken_StopsEarly_NothingCreated()
        {
            var (mgr, tool) = NewToolWithContext();
            var before = await ClientCount(mgr);

            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            var json = await tool.LacerteClientCreateBulk(
                new[] { Entry("CXL0"), Entry("CXL1") },
                cancellationToken: cts.Token);

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("stoppedEarly").GetBoolean());
            Assert.Equal(0, result.GetProperty("processedCount").GetInt32());
            Assert.Equal(2, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());

            Assert.Equal(before, await ClientCount(mgr));
        }

        // (o) Missing context preflight → 9009 ContextNotSet with the bulk
        //     envelope context, before any row runs.
        [Fact]
        public async Task CreateBulk_MissingContext_Returns9009()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var tool = new ClientCreateBulkTools(mgr);

            var json = await tool.LacerteClientCreateBulk(new[] { Entry("NOCTX") });

            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.ContextNotSet,
                root.GetProperty("statusCode").GetInt32());
        }

        // (p) Empty array → no-op envelope with zero counts and no
        //     recoveryHint / firstFailure noise.
        [Fact]
        public async Task CreateBulk_EmptyArray_ReturnsZeroCountEnvelope()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(
                Array.Empty<ClientCreateBulkTools.BulkCreateEntry>());

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("requestedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("processedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("succeededCount").GetInt32());
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("recoveryHint").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("firstFailureIndex").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("firstFailure").ValueKind);
        }

        // (q) Lowercase clientId normalizes to uppercase end-to-end (P16-038
        //     parity with single-create) — the row echoes the normalized form.
        [Fact]
        public async Task CreateBulk_LowercaseClientId_NormalizedToUpper()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(new[] { Entry("lowc1") });

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var rows = root.GetProperty("result").GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(rows[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal("LOWC1", rows[0].GetProperty("clientId").GetString());
        }

        // (r) recoveryHint — failures with an idempotencyKey emit
        //     canResumeViaSameBatch=true and the create-specific positional
        //     key shape.
        [Fact]
        public async Task CreateBulk_FailureWithKey_RecoveryHintNamesPositionalKeyShape()
        {
            var (mgr, tool) = NewToolWithContext();

            var json = await tool.LacerteClientCreateBulk(new[]
                {
                    Entry("RHA0"),
                    Entry("BAD!"),
                },
                idempotencyKey: "ccb-recovery");

            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var hint = root.GetProperty("result").GetProperty("recoveryHint");
            Assert.True(hint.GetProperty("canResumeViaSameBatch").GetBoolean());
            Assert.Equal("ccb-recovery", hint.GetProperty("idempotencyKeyPrefix").GetString());
            Assert.Equal("{prefix}-{index}-{clientId}",
                hint.GetProperty("perRowKeyShape").GetString());
        }
    }
}
