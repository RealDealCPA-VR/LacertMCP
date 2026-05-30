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

            var clients = result.GetProperty("clients").EnumerateArray()
                .Select(c => c.GetProperty("ClientID").GetString())
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
            Assert.Equal("BROW", result.GetProperty("client").GetProperty("ClientID").GetString());
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
