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
            // P16-044 / P16-048 — InvalidArgument (9017): the 9013
            // InvalidDataPath userMessage is path-specific and misleads the
            // LLM toward rerunning lacerte_set_context when it should fix
            // the date string. The new code carries a {argument, value,
            // reason} context dict and a remediation that explicitly tells
            // the LLM NOT to change context / registration.
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("InvalidArgument", root.GetProperty("statusName").GetString());
            var context = root.GetProperty("context");
            Assert.Equal("sinceUtc", context.GetProperty("argument").GetString());
            Assert.Equal("not-a-date", context.GetProperty("value").GetString());
        }

        // ----------------------------------------------------------------
        // P16-077 — sinceUtc format coverage. The parser accepts three
        // ISO-8601 forms; the tool description, error reason, and
        // [Description] attribute now ALL enumerate the same set so an LLM
        // reading the schema knows what's valid before it constructs a value.
        // ----------------------------------------------------------------

        [Theory]
        [InlineData("2020-01-01")]
        [InlineData("2020-01-01T00:00:00")]
        [InlineData("2020-01-01T00:00:00Z")]
        public async Task LacerteEfStatus_AcceptsAllDocumentedFormats(string sinceUtc)
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus(sinceUtc: sinceUtc));
            Assert.True(root.GetProperty("success").GetBoolean());
            // Sentinel: the slice contains at least one populated row.
            Assert.True(root.GetProperty("result").GetProperty("count").GetInt32() >= 1);
        }

        [Fact]
        public async Task LacerteEfStatus_RejectsFractionalSecondsWithActionableError()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            const string bad = "2025-05-30T15:30:00.123Z";
            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus(sinceUtc: bad));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            // Regression sentinel: if a future edit drops one accepted form
            // from the parser but not the docs (or vice versa), one of
            // these literals goes missing from the reason string and the
            // test flips. Echoed value lets the operator see the diff at a
            // glance.
            var reason = root.GetProperty("context").GetProperty("reason").GetString();
            Assert.NotNull(reason);
            Assert.Contains(bad, reason!);
            Assert.Contains("yyyy-MM-ddTHH:mm:ssZ", reason!);
            Assert.Contains("yyyy-MM-dd", reason!);
        }

        // ----------------------------------------------------------------
        // P16-047 — pagination on lacerte_ef_status. Module-wide joins
        // produce the largest result sets in the API (a busy practice's
        // 200+ EF filings against one module); pagination is the canonical
        // defense against blowing the MCP message budget.
        // ----------------------------------------------------------------

        [Fact]
        public async Task LacerteEfStatus_PaginationEnvelope_HasCanonicalShape()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");

            // New canonical fields.
            Assert.True(result.TryGetProperty("rows", out _));
            Assert.True(result.TryGetProperty("total", out _));
            Assert.True(result.TryGetProperty("limit", out _));
            Assert.True(result.TryGetProperty("offset", out _));
            Assert.True(result.TryGetProperty("hasMore", out _));

            // Default limit clamped to Pagination.DefaultLimit.
            Assert.Equal(100, result.GetProperty("limit").GetInt32());
            Assert.Equal(0, result.GetProperty("offset").GetInt32());

            // Legacy v0.1 keys preserved.
            Assert.True(result.TryGetProperty("count", out _));
            Assert.True(result.TryGetProperty("efStatuses", out _));
        }

        [Fact]
        public async Task LacerteEfStatus_LimitOne_ReturnsHasMore_WhenTotalGreater()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new EFileTools(mgr);

            var unfiltered = ToolTestHelpers.Parse(await tools.LacerteEfStatus());
            var total = unfiltered.GetProperty("result").GetProperty("total").GetInt32();

            // Only run the hasMore assertion if seed has > 1 row.
            if (total <= 1) return;

            var paged = ToolTestHelpers.Parse(await tools.LacerteEfStatus(limit: 1));
            var result = paged.GetProperty("result");
            Assert.Equal(1, result.GetProperty("rows").GetArrayLength());
            Assert.Equal(total, result.GetProperty("total").GetInt32());
            Assert.True(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(1, result.GetProperty("nextOffset").GetInt32());
        }
    }
}
