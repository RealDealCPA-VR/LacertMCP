using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #43 — generic `lacerte_table_read` (DATA1..DATA7).</summary>
    public class TablesToolsTests
    {
        [Fact]
        public async Task TableRead_NoContext_Returns9009()
        {
            var tools = new TablesTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteTableRead(1));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task TableRead_Data1_ReturnsClientRows()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new TablesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteTableRead(1));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("table").GetInt32());
            Assert.True(result.GetProperty("count").GetInt32() >= 2);
        }

        [Fact]
        public async Task TableRead_Data2_PartnershipReturnsSubclientRows()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tools = new TablesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteTableRead(2));
            var result = root.GetProperty("result");
            // Partnership PART has 3 partners → 3 DATA2 rows seeded.
            Assert.True(result.GetProperty("count").GetInt32() >= 3);
        }

        [Fact]
        public async Task TableRead_InvalidTable_ReturnsDBNotSupported()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new TablesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteTableRead(9));
            Assert.False(root.GetProperty("success").GetBoolean());
            // StatusCodes.DBNotSupported = 5
            Assert.Equal(5, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task TableRead_ClientFilter_NarrowsResult()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new TablesTools(mgr);

            var rootAll = ToolTestHelpers.Parse(await tools.LacerteTableRead(1));
            var rootOne = ToolTestHelpers.Parse(await tools.LacerteTableRead(1, clientNumber: "1001"));

            var allCount = rootAll.GetProperty("result").GetProperty("count").GetInt32();
            var oneCount = rootOne.GetProperty("result").GetProperty("count").GetInt32();
            Assert.True(oneCount <= allCount);
            Assert.True(oneCount >= 1);
        }

        // --- Follow-up F13: Pagination retrofit (limit / offset / hasMore / nextOffset) ---

        [Fact]
        public async Task TableRead_Pagination_LimitOne_HasMoreAndNextOffset()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new TablesTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteTableRead(1, limit: 1, offset: 0));
            var result = root.GetProperty("result");

            Assert.Equal(1, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(1, result.GetProperty("limit").GetInt32());
            Assert.Equal(0, result.GetProperty("offset").GetInt32());
            var total = result.GetProperty("total").GetInt32();
            Assert.True(total >= 2, "seed must have at least 2 DATA1 rows for hasMore");
            Assert.True(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(1, result.GetProperty("nextOffset").GetInt32());

            // legacy field still present
            Assert.Equal(1, result.GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task TableRead_Pagination_OffsetSkipsFirstRow()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new TablesTools(mgr);

            var first = ToolTestHelpers.Parse(await tools.LacerteTableRead(1, limit: 1, offset: 0))
                .GetProperty("result").GetProperty("rows")[0];
            var second = ToolTestHelpers.Parse(await tools.LacerteTableRead(1, limit: 1, offset: 1))
                .GetProperty("result").GetProperty("rows")[0];

            // Different rows at offsets 0 and 1.
            Assert.NotEqual(first.GetRawText(), second.GetRawText());
        }

        [Fact]
        public async Task TableRead_Pagination_LastPage_HasMoreFalse_NextOffsetNull()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new TablesTools(mgr);

            // Get total first
            var totalRoot = ToolTestHelpers.Parse(await tools.LacerteTableRead(1, limit: 1, offset: 0))
                .GetProperty("result");
            var total = totalRoot.GetProperty("total").GetInt32();

            var pLast = ToolTestHelpers.Parse(await tools.LacerteTableRead(1, limit: 1, offset: total - 1))
                .GetProperty("result");
            Assert.Equal(1, pLast.GetProperty("rows").GetArrayLength());
            Assert.False(pLast.GetProperty("hasMore").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, pLast.GetProperty("nextOffset").ValueKind);
        }
    }
}
