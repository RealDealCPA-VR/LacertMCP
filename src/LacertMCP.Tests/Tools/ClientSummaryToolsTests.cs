using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-028 — <c>lacerte_client_summary</c>. Tests the one-call snapshot
    /// envelope (identity + counts + states + flags + efStatus + cursors).
    /// </summary>
    public class ClientSummaryToolsTests
    {
        [Fact]
        public async Task ClientSummary_NoContext_Returns9009()
        {
            var tools = new ClientSummaryTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientSummary_EmptyClientNumber_Returns9017()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientSummaryTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("  "));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientSummary_UnknownClient_ReturnsFoundFalseWithHint()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientSummaryTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("9999"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("found").GetBoolean());
            Assert.Equal("9999", result.GetProperty("clientNumber").GetString());

            // Hint surfaces the recovery path AND the ClientNumber-vs-ClientID
            // distinction so an LLM can self-recover.
            var hint = result.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("9999", hint);
            Assert.Contains("lacerte_client_list", hint);
        }

        [Fact]
        public async Task ClientSummary_IndividualClient_ReturnsCompactEnvelope()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientSummaryTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");

            // Top-level shape: summaryVersion + clientNumber + found + context
            // + identity + counts + states + flags + efStatus + cursors.
            Assert.Equal(ClientSummaryTools.SummaryVersion, result.GetProperty("summaryVersion").GetInt32());
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            Assert.True(result.GetProperty("found").GetBoolean());

            // Context echo — taxYear / module / dataPath (self-documenting
            // attached artifact per the verdict). R6-18 — `module` carries
            // the canonical single-char DBF code; the readable name moved
            // to `moduleName`.
            var ctx = result.GetProperty("context");
            Assert.Equal(2024, ctx.GetProperty("taxYear").GetInt32());
            Assert.Equal("I", ctx.GetProperty("module").GetString());
            Assert.Equal("Individual", ctx.GetProperty("moduleName").GetString());
            Assert.Equal(SeedData.DataPathIData2024, ctx.GetProperty("dataPath").GetString());

            // Identity: BROW seed — FirstName="Bill", LastName="Brown".
            var identity = result.GetProperty("identity");
            Assert.Equal("BROW", identity.GetProperty("clientId").GetString());
            Assert.Equal("Bill", identity.GetProperty("firstName").GetString());
            Assert.Equal("Brown", identity.GetProperty("lastName").GetString());
            Assert.Equal("Bill Brown", identity.GetProperty("name").GetString());
            // Email is masked by the Wire-scope WireLog redaction pass at the
            // WrapToolHandler envelope seam — assert the key exists but the
            // value is the canonical "***" mask, not the raw seed value.
            // Demonstrates the PII redaction story still holds for summary.
            Assert.Equal("***", identity.GetProperty("email").GetString());

            // Counts: BROW has 0 subclients, 0 states (Individual), 1 form, 1 status row.
            var counts = result.GetProperty("counts");
            Assert.Equal(0, counts.GetProperty("subclients").GetInt32());
            Assert.Equal(0, counts.GetProperty("statesFiled").GetInt32());
            Assert.Equal(1, counts.GetProperty("formsCount").GetInt32());
            Assert.Equal(1, counts.GetProperty("statusRowsSet").GetInt32());
            // detailRows omitted unless includeDetail=true.
            Assert.Equal(JsonValueKind.Null, counts.GetProperty("detailRows").ValueKind);
        }

        [Fact]
        public async Task ClientSummary_CorporateClient_StatesProjected()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientSummaryTools(mgr);

            // ACME has States = { CA, NY }.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1001"));
            var result = root.GetProperty("result");

            var states = result.GetProperty("states").EnumerateArray()
                .Select(s => s.GetString())
                .ToArray();
            Assert.Equal(2, states.Length);
            Assert.Contains("CA", states);
            Assert.Contains("NY", states);

            // statesFiled count reflects the unique state list.
            Assert.Equal(2, result.GetProperty("counts").GetProperty("statesFiled").GetInt32());
            Assert.False(result.GetProperty("statesTruncated").GetBoolean());

            // returnType matches the module canonical mapping.
            Assert.Equal("Corporate", result.GetProperty("context").GetProperty("returnType").GetString());
        }

        [Fact]
        public async Task ClientSummary_PartnershipClient_CountsSubclients()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tools = new ClientSummaryTools(mgr);

            // PART has 3 partners (DATA2 subclient rows).
            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1001"));
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("counts").GetProperty("subclients").GetInt32());
        }

        [Fact]
        public async Task ClientSummary_IncludeDetailTrue_SurfacesDetailRowCount()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientSummaryTools(mgr);

            // BROW has 3 detail rows (Wages, Wages, Dep).
            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1001", includeDetail: true));
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("counts").GetProperty("detailRows").GetInt32());
        }

        [Fact]
        public async Task ClientSummary_EFStatusTransmittedClient_FlagsEFileReady()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientSummaryTools(mgr);

            // PEND (1002) has EFStatus="Transmitted" — Status row's C5_1 set
            // to "InProgress" for non-rejected clients by the seed; the
            // EF rows surface in efStatus.latestStatus.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1002"));
            var result = root.GetProperty("result");

            var ef = result.GetProperty("efStatus");
            Assert.Equal("Transmitted", ef.GetProperty("latestStatus").GetString());
            Assert.NotEqual(JsonValueKind.Null, ef.GetProperty("latestStatusDate").ValueKind);
        }

        [Fact]
        public async Task ClientSummary_RejectedClient_FlagsHasErrors()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            var tools = new ClientSummaryTools(mgr);

            // REJE has EFStatus="Rejected" — seed flips Status row's C5_1
            // to "Rejected" so DeriveFlags surfaces hasErrors=true.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1003"));
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("flags").GetProperty("hasErrors").GetBoolean());
        }

        [Fact]
        public async Task ClientSummary_NonRejectedClient_FlagsNoErrors()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientSummaryTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientSummary("1001"));
            var flags = root.GetProperty("result").GetProperty("flags");
            Assert.False(flags.GetProperty("hasErrors").GetBoolean());
        }

        // ---- Helper-level tests for the pure projection internals ----

        [Fact]
        public void DeriveFlags_EmptyStatusRows_AllFalse()
        {
            var rows = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyDictionary<string, object?>>();
            var flags = ClientSummaryTools.DeriveFlags(rows);
            var json = JsonSerializer.Serialize(flags);
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("completed").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("eFileReady").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("locked").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("hasErrors").GetBoolean());
        }

        [Fact]
        public void DeriveFlags_AcceptedRow_CompletedAndEFileReady()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["C5_1"] = "Accepted" },
            };
            var json = JsonSerializer.Serialize(ClientSummaryTools.DeriveFlags(rows));
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("completed").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("eFileReady").GetBoolean());
        }

        [Fact]
        public void ProjectEfStatus_LatestByDate()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["Status"] = "Transmitted",
                    ["StatusDate"] = "2024-03-01",
                    ["EFId"] = "EF-A",
                    ["InstanceId"] = "INST-1",
                },
                new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["Status"] = "Accepted",
                    ["StatusDate"] = "2024-04-15",
                    ["EFId"] = "EF-A",
                    ["InstanceId"] = "INST-2",
                },
                new Dictionary<string, object?>
                {
                    // Different client — must be filtered out.
                    ["ClientId"] = "1002",
                    ["Status"] = "Rejected",
                    ["StatusDate"] = "2024-05-01",
                },
            };
            var json = JsonSerializer.Serialize(ClientSummaryTools.ProjectEfStatus(rows, "1001"));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Accepted", doc.RootElement.GetProperty("latestStatus").GetString());
            Assert.Equal("2024-04-15", doc.RootElement.GetProperty("latestStatusDate").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("perStateBreakdown").GetArrayLength());
        }

        // R6-32b — when NO row carries a parseable StatusDate, the fallback
        // is the LAST undateable row (append order ~ "latest appended"),
        // not the first.
        [Fact]
        public void ProjectEfStatus_NoDateableRows_FallsBackToLastUndateable_R6_32()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["Status"] = "Transmitted",
                    ["StatusDate"] = "not-a-date-1",
                },
                new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["Status"] = "Accepted",
                    ["StatusDate"] = "not-a-date-2",
                },
            };
            var json = JsonSerializer.Serialize(ClientSummaryTools.ProjectEfStatus(rows, "1001"));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Accepted", doc.RootElement.GetProperty("latestStatus").GetString());
            Assert.Equal("not-a-date-2", doc.RootElement.GetProperty("latestStatusDate").GetString());
        }

        // R6-32a — perStateBreakdownTruncated compares against the FILTERED
        // count (rows with a string Status), not the raw forClient count.
        // 12 rows for the client, but only 5 carry a Status — nothing is
        // dropped from the cap-10 breakdown, so truncated must be false.
        [Fact]
        public void ProjectEfStatus_TruncationUsesFilteredCount_R6_32()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            for (int i = 0; i < 7; i++)
            {
                // Null-status join rows (LEFT OUTER) — excluded from the
                // breakdown, must not count toward truncation.
                rows.Add(new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["StatusDate"] = "2024-03-01",
                });
            }
            for (int i = 0; i < 5; i++)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["Status"] = "Accepted",
                    ["StatusDate"] = "2024-03-0" + (i + 1),
                });
            }

            var json = JsonSerializer.Serialize(ClientSummaryTools.ProjectEfStatus(rows, "1001"));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(5, doc.RootElement.GetProperty("perStateBreakdown").GetArrayLength());
            Assert.False(doc.RootElement.GetProperty("perStateBreakdownTruncated").GetBoolean());

            // And with 11 status-carrying rows the cap (10) genuinely
            // truncates — flag must be true.
            rows.Add(new Dictionary<string, object?>
            {
                ["ClientId"] = "1001",
                ["Status"] = "Pending",
                ["StatusDate"] = "2024-03-09",
            });
            for (int i = 0; i < 5; i++)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["ClientId"] = "1001",
                    ["Status"] = "Pending",
                    ["StatusDate"] = "2024-03-1" + i,
                });
            }
            var json2 = JsonSerializer.Serialize(ClientSummaryTools.ProjectEfStatus(rows, "1001"));
            using var doc2 = JsonDocument.Parse(json2);
            Assert.Equal(10, doc2.RootElement.GetProperty("perStateBreakdown").GetArrayLength());
            Assert.True(doc2.RootElement.GetProperty("perStateBreakdownTruncated").GetBoolean());
        }
    }
}
