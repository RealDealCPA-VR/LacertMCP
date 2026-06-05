using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #42 — `lacerte_client_list`, `lacerte_client_read`.</summary>
    public class ClientsToolsTests
    {
        [Fact]
        public async Task ClientList_NoContext_Returns9009()
        {
            var tools = new ClientsTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList());
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientList_TY24Individual_ReturnsBrownAndPend()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientList());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("count").GetInt32() >= 2);

            // P17-011 — wire key is camelCase `clientId` (P16-054/P16-080
            // canonical casing applied to read-tool response keys).
            var clients = result.GetProperty("clients").EnumerateArray()
                .Select(c => c.GetProperty("clientId").GetString())
                .ToArray();
            Assert.Contains("BROW", clients);
            Assert.Contains("PEND", clients);
        }

        [Fact]
        public async Task ClientList_NameFilter_FiltersCaseInsensitive()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(nameFilter: "bro"));
            var result = root.GetProperty("result");
            var clients = result.GetProperty("clients").EnumerateArray().ToArray();
            Assert.NotEmpty(clients);
            // Brown last name starts with "Bro" — should match.
            Assert.Contains(clients, c => c.GetProperty("C1_2").GetString() == "Brown");
        }

        [Fact]
        public async Task ClientRead_NoContext_Returns9009()
        {
            var tools = new ClientsTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteClientRead("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientRead_KnownClient_ReturnsRow_FoundTrue()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientRead("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("found").GetBoolean());
            // P17-011 — wire key is camelCase clientId.
            Assert.Equal("BROW", result.GetProperty("client").GetProperty("clientId").GetString());
        }

        [Fact]
        public async Task ClientRead_UnknownClient_ReturnsFoundFalse()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientRead("DOES_NOT_EXIST"));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.False(root.GetProperty("result").GetProperty("found").GetBoolean());
        }

        // --- P16-081: not-found envelope carries an actionable hint ---

        [Fact]
        public async Task ClientRead_UnknownClient_EnvelopeCarriesHint()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientRead("9999"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("found").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("client").ValueKind);

            // Hint must be present, mention the missing ClientNumber, and call
            // out the ClientNumber-vs-ClientID distinction so an LLM cannot
            // mis-summarize the empty record as "exists but has no data".
            Assert.True(result.TryGetProperty("hint", out var hint), "not-found envelope must carry a `hint` field");
            var hintText = hint.GetString();
            Assert.NotNull(hintText);
            Assert.Contains("9999", hintText);
            Assert.Contains("lacerte_client_list", hintText);
            Assert.Contains("ClientID", hintText);
        }

        [Fact]
        public async Task ClientRead_KnownClient_EnvelopeOmitsHint()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientRead("1001"));
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("found").GetBoolean());
            // Hint is reserved for the not-found path — the found-true envelope
            // must NOT carry one, otherwise an LLM may treat success as a soft
            // warning state.
            Assert.False(result.TryGetProperty("hint", out _), "found-true envelope must not carry a `hint` field");
        }

        // --- P16-075: nullable limit/offset four-state contract ---

        [Fact]
        public async Task ClientList_LimitOmitted_UsesDefaultPageSize()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // Call with no limit/offset — should land on the helper's default
            // page size (Pagination.DefaultLimit = 100), not unbounded.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList());
            var result = root.GetProperty("result");
            Assert.Equal(Server.Pagination.Pagination.DefaultLimit, result.GetProperty("limit").GetInt32());
        }

        [Fact]
        public async Task ClientList_LimitZero_ReturnsCountOnly()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // limit=0 must surface { rows: [], total: N } — the count-only
            // contract. The LLM hallucination footgun "limit: 0 means
            // unbounded" is now safe: empty rows + correct total, no
            // MaxLimit bypass.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(limit: 0));
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(0, result.GetProperty("limit").GetInt32());
            Assert.True(result.GetProperty("total").GetInt32() >= 2);
            // Legacy fields stay in lockstep.
            Assert.Equal(0, result.GetProperty("count").GetInt32());
            Assert.Equal(0, result.GetProperty("clients").GetArrayLength());
        }

        [Fact]
        public async Task ClientList_LimitNegative_ReturnsError()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // Negative limit must be rejected — the helper's old
            // "non-positive = unbounded" branch is unreachable from this
            // tool now, so -1 surfaces as a structured error instead of
            // silently returning the full firm-history.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(limit: -1));
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        // --- Follow-up F13: Pagination retrofit (limit / offset / hasMore / nextOffset) ---

        [Fact]
        public async Task ClientList_Pagination_LimitOne_HasMoreAndNextOffset()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(limit: 1, offset: 0));
            var result = root.GetProperty("result");

            // Paginated shape
            Assert.Equal(1, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(1, result.GetProperty("limit").GetInt32());
            Assert.Equal(0, result.GetProperty("offset").GetInt32());
            var total = result.GetProperty("total").GetInt32();
            Assert.True(total >= 2, "seed must have at least 2 clients for hasMore to be true");
            Assert.True(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(1, result.GetProperty("nextOffset").GetInt32());

            // Legacy fields preserved (v0.1 additive)
            Assert.Equal(1, result.GetProperty("count").GetInt32());
            Assert.Equal(1, result.GetProperty("clients").GetArrayLength());
        }

        [Fact]
        public async Task ClientList_Pagination_OffsetWalksThroughTotal()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // Page 1
            var p1 = ToolTestHelpers.Parse(await tools.LacerteClientList(limit: 1, offset: 0))
                .GetProperty("result");
            var total = p1.GetProperty("total").GetInt32();
            Assert.True(p1.GetProperty("hasMore").GetBoolean());

            // Last page (offset = total - 1) should yield hasMore=false, nextOffset=null
            var pLast = ToolTestHelpers.Parse(await tools.LacerteClientList(limit: 1, offset: total - 1))
                .GetProperty("result");
            Assert.Equal(1, pLast.GetProperty("rows").GetArrayLength());
            Assert.False(pLast.GetProperty("hasMore").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, pLast.GetProperty("nextOffset").ValueKind);
        }

        [Fact]
        public async Task ClientList_Pagination_LimitCoversTotal_HasMoreFalse()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // Big limit (clamped to MaxLimit) — should fetch everything in one page.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(limit: 999, offset: 0));
            var result = root.GetProperty("result");

            var total = result.GetProperty("total").GetInt32();
            Assert.Equal(total, result.GetProperty("rows").GetArrayLength());
            Assert.False(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("nextOffset").ValueKind);
        }
    }
}
