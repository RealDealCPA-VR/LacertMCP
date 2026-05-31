using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>Phase 5 task #49 — `lacerte_ef_status` (TY19+ EFilings JOIN EFStatuses).</summary>
    public class EFileToolsTests
    {
        [Fact]
        public async Task EfStatus_NoContext_Returns9009()
        {
            var tools = new EFileTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task EfStatus_ContextSet_ReturnsSuccessEnvelope()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("count").GetInt32() >= 0);
            Assert.True(result.TryGetProperty("efStatuses", out _));
        }

        // ----------------------------------------------------------------
        // P15-010 — sinceUtc push-down filter
        // ----------------------------------------------------------------

        [Fact]
        public async Task EfStatus_SinceUtc_FutureDate_ReturnsEmpty()
        {
            // Seed StatusDate is "2024-04-15" — filter ahead of that should
            // exclude every populated row. (Empty rows from the LEFT OUTER
            // join also drop because their StatusDate is null.)
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteEfStatus(sinceUtc: "2099-01-01"));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(0, root.GetProperty("result").GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task EfStatus_SinceUtc_PastDate_IncludesSeededRows()
        {
            // Filter on 2020-01-01 includes the seeded "2024-04-15" rows.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var unfiltered = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            var filtered = ToolTestHelpers.Parse(
                await tools.LacerteEfStatus(sinceUtc: "2020-01-01"));

            Assert.True(filtered.GetProperty("success").GetBoolean());
            // At least one populated EF status row survives the filter.
            Assert.True(filtered.GetProperty("result").GetProperty("count").GetInt32() >= 1);
            // Unfiltered (LEFT OUTER) returns >= filtered count by definition.
            Assert.True(
                unfiltered.GetProperty("result").GetProperty("count").GetInt32()
                >= filtered.GetProperty("result").GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task EfStatus_SinceUtc_Invalid_ReturnsStructuredError()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteEfStatus(sinceUtc: "not-a-date"));
            Assert.False(root.GetProperty("success").GetBoolean());
            // Reused InvalidDataPath (9013) — operator-actionable, no need
            // for a fresh code; the context dict carries the rejected value.
            Assert.Equal(9013, root.GetProperty("statusCode").GetInt32());
        }
    }
}
