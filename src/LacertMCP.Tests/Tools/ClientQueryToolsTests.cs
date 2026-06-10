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
    /// R6 gap item #3 — `lacerte_client_query` (server-side filtered
    /// cross-client search). Covers the op set (eq/ne/contains/startsWith/
    /// empty/notEmpty), pagination over the FILTERED set per the standard
    /// rows/total/limit/returnedCount/hasMore/nextOffset contract (R6-19),
    /// the filter echo, and the validation gates (9017 for fieldName/op,
    /// DBNotSupported for bad table, 9009 for missing context).
    /// </summary>
    [Xunit.Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class ClientQueryToolsTests
    {
        [Fact]
        public async Task ClientQuery_NoContext_Returns9009()
        {
            var tools = new ClientQueryTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_3", "notEmpty"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientQuery_Eq_MatchesFilingStatus()
        {
            // TY24 IData: BROW + PEND are both FilingStatus=Joint.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_4", "eq", "Joint"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(2, result.GetProperty("total").GetInt32());
            Assert.Equal(2, result.GetProperty("returnedCount").GetInt32());
            Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
            Assert.False(result.GetProperty("hasMore").GetBoolean());

            // Filter echo carries the applied predicate verbatim.
            var filter = result.GetProperty("filter");
            Assert.Equal(1, filter.GetProperty("table").GetInt32());
            Assert.Equal("C1_4", filter.GetProperty("fieldName").GetString());
            Assert.Equal("eq", filter.GetProperty("op").GetString());
            Assert.Equal("Joint", filter.GetProperty("value").GetString());

            // Rows carry the same shape lacerte_table_read returns.
            var clientNumbers = ClientNumbers(result.GetProperty("rows"));
            Assert.Contains(SeedData.BROW.ClientNumber, clientNumbers);
            Assert.Contains(SeedData.PEND.ClientNumber, clientNumbers);
        }

        [Fact]
        public async Task ClientQuery_Eq_IsCaseInsensitive_AndOpNormalizes()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            // Value comparison is OrdinalIgnoreCase; op casing is forgiven
            // and echoed back canonical.
            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_4", "EQ", "joint"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(2, result.GetProperty("total").GetInt32());
            Assert.Equal("eq", result.GetProperty("filter").GetProperty("op").GetString());
        }

        [Fact]
        public async Task ClientQuery_Contains_MatchesEmailSubstring()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_3", "contains", "BROWN"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("total").GetInt32());
            Assert.Equal(
                SeedData.BROW.ClientNumber,
                result.GetProperty("rows")[0].GetProperty("clientNumber").GetString());
        }

        [Fact]
        public async Task ClientQuery_Empty_MatchesMissingField()
        {
            // Corp DATA1 rows carry no C1_2 (entity modules have no last
            // name) — `empty` must treat the MISSING key as a match.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_2", "empty"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            // ACME + WIDG both lack C1_2 entirely.
            Assert.Equal(2, result.GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task ClientQuery_NotEmpty_ExcludesMissingField()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientQueryTools(mgr);

            var missing = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_2", "notEmpty"));
            Assert.Equal(0, missing.GetProperty("result").GetProperty("total").GetInt32());

            // Every corp row has an email.
            var present = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_3", "notEmpty"));
            Assert.Equal(2, present.GetProperty("result").GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task ClientQuery_StartsWith_MatchesPrefix()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_2", "startsWith", "jo"));
            Assert.True(root.GetProperty("success").GetBoolean());
            // Only Jones starts with "jo" (Smith, Rejected don't).
            Assert.Equal(1, root.GetProperty("result").GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task ClientQuery_NonStringField_ComparesInvariantStringForm()
        {
            // C1_5 (dependents) is seeded as an int — eq compares the
            // invariant string form so "2" matches the int 2 (BROW).
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_5", "eq", "2"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("total").GetInt32());
            Assert.Equal(
                SeedData.BROW.ClientNumber,
                result.GetProperty("rows")[0].GetProperty("clientNumber").GetString());
        }

        [Fact]
        public async Task ClientQuery_SubclientTable_FiltersData2Rows()
        {
            // table=2 in the Partnership module — 3 seeded partner rows,
            // all with a non-empty C2_2 (partner name).
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C2_2", "contains", "partner", table: 2));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("total").GetInt32());
            Assert.Equal(2, result.GetProperty("filter").GetProperty("table").GetInt32());
        }

        [Fact]
        public async Task ClientQuery_Pagination_PagesTheFilteredSet()
        {
            // TY20 IData has 3 clients, all with non-empty C1_1. Page the
            // FILTERED set with limit=2: page 1 → 2 rows, hasMore,
            // nextOffset=2; page 2 → 1 row, end of set.
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            var tools = new ClientQueryTools(mgr);

            var page1 = ToolTestHelpers.Parse(
                    await tools.LacerteClientQuery("C1_1", "notEmpty", limit: 2, offset: 0))
                .GetProperty("result");
            Assert.Equal(3, page1.GetProperty("total").GetInt32());
            Assert.Equal(2, page1.GetProperty("limit").GetInt32());
            Assert.Equal(0, page1.GetProperty("offset").GetInt32());
            Assert.Equal(2, page1.GetProperty("returnedCount").GetInt32());
            Assert.Equal(2, page1.GetProperty("rows").GetArrayLength());
            Assert.True(page1.GetProperty("hasMore").GetBoolean());
            Assert.Equal(2, page1.GetProperty("nextOffset").GetInt32());

            var page2 = ToolTestHelpers.Parse(
                    await tools.LacerteClientQuery("C1_1", "notEmpty", limit: 2, offset: 2))
                .GetProperty("result");
            Assert.Equal(3, page2.GetProperty("total").GetInt32());
            Assert.Equal(1, page2.GetProperty("returnedCount").GetInt32());
            Assert.False(page2.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextOffset").ValueKind);

            // No row overlap between the two pages.
            var first = ClientNumbers(page1.GetProperty("rows"));
            var second = ClientNumbers(page2.GetProperty("rows"));
            Assert.Empty(first.Intersect(second));
        }

        [Fact]
        public async Task ClientQuery_NoMatches_ReturnsEmptyRowsTotalZero()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_3", "eq", "nobody@nowhere.example"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("total").GetInt32());
            Assert.Equal(0, result.GetProperty("returnedCount").GetInt32());
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
            Assert.False(result.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("nextOffset").ValueKind);
        }

        [Fact]
        public async Task ClientQuery_InvalidOp_Returns9017WithAllowedList()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_3", "like", "%brown%"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            var context = root.GetProperty("context");
            Assert.Equal("op", context.GetProperty("argument").GetString());
            var reason = context.GetProperty("reason").GetString();
            Assert.NotNull(reason);
            // The allowed set must be enumerated so the LLM self-corrects.
            Assert.Contains("eq", reason);
            Assert.Contains("contains", reason);
            Assert.Contains("startsWith", reason);
            Assert.Contains("notEmpty", reason);
        }

        [Fact]
        public async Task ClientQuery_EmptyFieldName_Returns9017()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("  ", "eq", "x"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("fieldName",
                root.GetProperty("context").GetProperty("argument").GetString());
        }

        [Fact]
        public async Task ClientQuery_InvalidTable_ReturnsDBNotSupported()
        {
            // Same gate lacerte_table_read uses — the session rejects
            // tables outside 1..7 with StatusCodes.DBNotSupported (5).
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientQueryTools(mgr);

            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C8_1", "notEmpty", table: 8));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(5, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientQuery_ValidationPrecedesContextGate()
        {
            // R6-13 gate-order convention: input validation fires before
            // the 9009 context gate, so a bad op on a context-less manager
            // reports 9017, not 9009.
            var tools = new ClientQueryTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(
                await tools.LacerteClientQuery("C1_3", "matches", "x"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static List<string> ClientNumbers(JsonElement rows)
            => rows.EnumerateArray()
                .Select(r => r.GetProperty("clientNumber").GetString() ?? string.Empty)
                .ToList();
    }
}
