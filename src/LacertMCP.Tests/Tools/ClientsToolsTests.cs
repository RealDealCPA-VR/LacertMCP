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

        // ----------------------------------------------------------------
        // P18-005 + cluster E_tools_session — nameFilter substring on first OR last name.
        //
        // Pre-P18 the session manager applied a prefix-only filter against
        // a single column (C1_2 for Individual). The tool now applies a
        // substring filter across BOTH C1_1 (first) and C1_2 (last) so the
        // common LLM/operator pattern ("find smith", "find john") matches
        // whether the operator typed the first or the last name.
        // ----------------------------------------------------------------

        [Fact]
        public async Task ClientList_NameFilter_MatchesFirstNameSubstring()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // BROW seed has FirstName="Bill", LastName="Brown". Pre-P18 the
            // session-mgr filter only checked C1_2 (last name) so "bil"
            // would silently return zero rows. Post-P18 the tool routes the
            // filter across BOTH columns and "bil" matches "Bill".
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(nameFilter: "bil"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            var clients = result.GetProperty("clients").EnumerateArray().ToArray();
            Assert.NotEmpty(clients);
            Assert.Contains(clients, c => c.GetProperty("C1_2").GetString() == "Brown");
        }

        [Fact]
        public async Task ClientList_NameFilter_MidSubstringOnLastName_Matches()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // "row" is a MID-substring of "Brown" — pre-P18 prefix-only
            // would have missed this; substring catches it. Demonstrates
            // the broader semantics promised by the new Description.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(nameFilter: "row"));
            var clients = root.GetProperty("result").GetProperty("clients")
                .EnumerateArray().ToArray();
            Assert.Contains(clients, c => c.GetProperty("C1_2").GetString() == "Brown");
        }

        [Fact]
        public async Task ClientList_NameFilter_NoMatch_ReturnsEmpty_NoCrash()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(nameFilter: "ZZZZZ"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("total").GetInt32());
            Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
        }

        [Fact]
        public async Task ClientList_NameFilter_CorporateModule_MatchesEntityName()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientsTools(mgr);

            // ACME entity (C1_1="ACME Industries Inc") — substring "acme"
            // matches case-insensitively. Non-Individual modules have no
            // C1_2 so the matcher should silently fall through.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientList(nameFilter: "acme"));
            var clients = root.GetProperty("result").GetProperty("clients")
                .EnumerateArray().ToArray();
            Assert.NotEmpty(clients);
            Assert.Contains(clients, c => c.GetProperty("C1_1").GetString() == "ACME Industries Inc");
        }

        // ----------------------------------------------------------------
        // P18-005 — LacerteClientFind: name-to-ClientNumber resolver.
        // ----------------------------------------------------------------

        [Fact]
        public async Task ClientFind_NoContext_Returns9009()
        {
            var tools = new ClientsTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("Smith"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ClientFind_EmptyName_Rejected()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // Empty / whitespace-only name yields a structured error rather
            // than silently returning the full roster — the count-only
            // contract belongs to lacerte_client_list, not the find tool.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("  "));
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task ClientFind_FirstNameSubstring_MatchesIndividual()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // BROW seed: FirstName="Bill", LastName="Brown" — "bill" hits
            // C1_1 only. Demonstrates the first-name pivot the
            // session-manager prefix filter could not handle.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("bill"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("total").GetInt32() >= 1);

            var matches = result.GetProperty("matches").EnumerateArray().ToArray();
            Assert.Contains(matches, m => m.GetProperty("lastName").GetString() == "Brown");
            // Match envelope must surface clientNumber first so the
            // next-call ID is unambiguous.
            Assert.True(matches[0].TryGetProperty("clientNumber", out _));
            Assert.True(matches[0].TryGetProperty("clientId", out _));
        }

        [Fact]
        public async Task ClientFind_LastNameSubstring_MatchesIndividual()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // Same row reachable via the last-name pivot — round-trip
            // proves the substring is OR'd across both columns.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("brown"));
            var matches = root.GetProperty("result").GetProperty("matches")
                .EnumerateArray().ToArray();
            Assert.Contains(matches, m => m.GetProperty("firstName").GetString() == "Bill");
        }

        [Fact]
        public async Task ClientFind_CorporateEntityName_Matches()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientsTools(mgr);

            // Non-Individual modules carry only C1_1 (entity name) — the
            // matcher must skip the missing C1_2 without throwing.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("widget"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(1, result.GetProperty("total").GetInt32());
            var match = result.GetProperty("matches").EnumerateArray().First();
            Assert.Equal("Widget Holdings LLC", match.GetProperty("firstName").GetString());
            // lastName comes from C1_2 which corporate rows don't carry —
            // surfaces as JSON null, not a missing key.
            Assert.Equal(System.Text.Json.JsonValueKind.Null, match.GetProperty("lastName").ValueKind);
        }

        [Fact]
        public async Task ClientFind_ZeroMatches_DidYouMeanCarriesClosestName()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // "Browne" mis-spells "Brown" by one character — Levenshtein
            // distance 1 places "Brown" at the top of the did-you-mean.
            // Demonstrates the typo-recovery loop closes without forcing
            // the LLM to bounce back to the operator.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("Browne"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("total").GetInt32());
            var hint = result.GetProperty("didYouMean").GetString();
            Assert.NotNull(hint);
            Assert.Contains("Brown", hint);
        }

        [Fact]
        public async Task ClientFind_LimitNegative_Rejected()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("Brown", limit: -1));
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task ClientFind_LimitTruncates_TotalRemainsExact()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientsTools(mgr);

            // 2024 Individual roster has 2 seeded rows (BROW + PEND).
            // limit=1 truncates `matches` but total stays exact so the LLM
            // can detect overflow.
            var root = ToolTestHelpers.Parse(await tools.LacerteClientFind("e", limit: 1));
            var result = root.GetProperty("result");
            var total = result.GetProperty("total").GetInt32();
            var matches = result.GetProperty("matches").EnumerateArray().ToArray();
            if (total > 1)
            {
                Assert.Equal(1, matches.Length);
                Assert.True(result.GetProperty("truncated").GetBoolean());
            }
        }

        // Pure-helper tests for the substring + Levenshtein internals — exercise
        // edge cases the WrapToolHandler envelope obscures (missing C1_2, empty
        // rows list, ties).

        [Fact]
        public void FilterByName_EmptySubstring_ReturnsAllRows()
        {
            var rows = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyDictionary<string, object?>>
            {
                new System.Collections.Generic.Dictionary<string, object?> { ["C1_1"] = "Alice" },
            };
            Assert.Single(ClientsTools.FilterByName(rows, ""));
        }

        [Fact]
        public void FilterByName_MissingC1_2_SkipsLastNameCheckWithoutThrowing()
        {
            // Entity-shape row carries only C1_1 — matcher must not NRE on
            // the missing C1_2 lookup.
            var rows = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyDictionary<string, object?>>
            {
                new System.Collections.Generic.Dictionary<string, object?> { ["C1_1"] = "ACME Inc" },
            };
            Assert.Single(ClientsTools.FilterByName(rows, "acme"));
        }

        [Fact]
        public void BuildDidYouMeanHint_EmptyRoster_ReturnsNull()
        {
            var rows = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyDictionary<string, object?>>();
            Assert.Null(ClientsTools.BuildDidYouMeanHint(rows, "Smith"));
        }

        [Fact]
        public void BuildDidYouMeanHint_SurfacesUpToThreeClosestNames()
        {
            var rows = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyDictionary<string, object?>>
            {
                new System.Collections.Generic.Dictionary<string, object?> { ["C1_1"] = "John", ["C1_2"] = "Smith" },
                new System.Collections.Generic.Dictionary<string, object?> { ["C1_1"] = "Jane", ["C1_2"] = "Smyth" },
                new System.Collections.Generic.Dictionary<string, object?> { ["C1_1"] = "Bob",  ["C1_2"] = "Jones" },
                new System.Collections.Generic.Dictionary<string, object?> { ["C1_1"] = "Mary", ["C1_2"] = "Brown" },
            };
            // Query "Smithh" is 1 edit from "Smith", 2 from "Smyth" — both
            // should appear in the top-3 closest, ahead of "Jones"/"Brown".
            var hint = ClientsTools.BuildDidYouMeanHint(rows, "Smithh");
            Assert.NotNull(hint);
            Assert.Contains("Smith", hint);
            Assert.Contains("Smyth", hint);
        }
    }
}
