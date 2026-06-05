using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-029 — <c>lacerte_diff_clients</c>. Tests the structured
    /// comparison envelope ({onlyIn1, onlyIn2, valuesDiffer} via per-section
    /// added/removed/changed buckets).
    /// </summary>
    public class ClientDiffToolsTests
    {
        [Fact]
        public async Task DiffClients_NoContext_Returns9009()
        {
            var tools = new ClientDiffTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients("1001", "1002"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DiffClients_SameClientNumber_Returns9017()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients("1001", "1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DiffClients_EmptyClientNumber_Returns9017()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients("", "1002"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task DiffClients_UnknownClient1_ReturnsNotFound()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients("9999", "1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("found").GetBoolean());
            Assert.Equal("9999", result.GetProperty("clientNumber").GetString());
        }

        [Fact]
        public async Task DiffClients_TwoIndividuals_SurfacesNameAndEmailDiffs()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            // BROW (1001) vs PEND (1002) — different FirstName / LastName /
            // Email / FilingStatus / Dependents on every Individual row.
            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients("1001", "1002"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");

            // Top-level shape: client1Number, client2Number, summary, dataOne,
            // subclients, states, detail, ignoredFields.
            Assert.Equal("1001", result.GetProperty("client1Number").GetString());
            Assert.Equal("1002", result.GetProperty("client2Number").GetString());

            var summary = result.GetProperty("summary");
            Assert.True(summary.GetProperty("differingFieldsCount").GetInt32() > 0);

            // The DATA1 diff must include both FirstName (C1_1) and Email (C1_3).
            var diffs = result.GetProperty("dataOne").GetProperty("fields")
                .EnumerateArray().ToArray();
            var diffedFields = diffs.Select(d => d.GetProperty("field").GetString()).ToArray();
            Assert.Contains("C1_1", diffedFields);  // FirstName Bill vs Patty
            Assert.Contains("C1_3", diffedFields);  // Email differs
            // clientId differs too (BROW vs PEND).
            Assert.Contains("clientId", diffedFields);

            // ignoredFields must include the built-in clientNumber suppression.
            var ignored = result.GetProperty("ignoredFields").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Contains("clientNumber", ignored);
        }

        [Fact]
        public async Task DiffClients_AdditionalIgnoreFields_HonoredInDiff()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            // Pass an extra ignore for C1_1 (FirstName) — the diff must no
            // longer surface it even though it would normally differ.
            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients(
                "1001", "1002", ignoreFields: new[] { "C1_1" }));
            var result = root.GetProperty("result");

            var diffs = result.GetProperty("dataOne").GetProperty("fields")
                .EnumerateArray().Select(d => d.GetProperty("field").GetString()).ToArray();
            Assert.DoesNotContain("C1_1", diffs);

            var ignored = result.GetProperty("ignoredFields").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Contains("C1_1", ignored);
            Assert.Contains("clientNumber", ignored);
        }

        [Fact]
        public async Task DiffClients_IncludeSubclientsFalse_OmitsSubclientsSection()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients(
                "1001", "1002", includeSubclients: false, includeStates: false));
            var result = root.GetProperty("result");

            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("subclients").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("states").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("detail").ValueKind);
        }

        [Fact]
        public async Task DiffClients_IncludeDetailTrue_DiffsDetailRows()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientDiffTools(mgr);

            // BROW (3 rows: Wages, Wages, Dep) vs PEND (2 rows: Wages, Int).
            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients(
                "1001", "1002", includeDetail: true));
            var result = root.GetProperty("result");
            var detail = result.GetProperty("detail");

            // Detail buckets present.
            Assert.True(detail.TryGetProperty("added", out _));
            Assert.True(detail.TryGetProperty("removed", out _));
            Assert.True(detail.TryGetProperty("changed", out _));

            // BROW's "Dependent: Tim Brown, age 12" row is absent from PEND
            // — surfaces as removed (A has it, B does not).
            var removedDescs = detail.GetProperty("removed").EnumerateArray()
                .Select(d => d.GetProperty("description").GetString())
                .ToArray();
            Assert.Contains(removedDescs, d => d != null && d.Contains("Tim Brown"));
        }

        [Fact]
        public async Task DiffClients_CorporateStatesDiff_AddedAndRemoved()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientDiffTools(mgr);

            // ACME (1001): states CA + NY. WIDG (1002): state TX.
            // Diffing 1001 vs 1002:
            //   - states on 1002 only: TX  -> added
            //   - states on 1001 only: CA, NY -> removed
            var root = ToolTestHelpers.Parse(await tools.LacerteDiffClients("1001", "1002"));
            var states = root.GetProperty("result").GetProperty("states");

            var added = states.GetProperty("added").EnumerateArray()
                .Select(a => a.GetProperty("key").GetString())
                .ToArray();
            var removed = states.GetProperty("removed").EnumerateArray()
                .Select(r => r.GetProperty("key").GetString())
                .ToArray();

            Assert.Contains("TX", added);
            Assert.Contains("CA", removed);
            Assert.Contains("NY", removed);
        }

        // ---- Pure helper tests ----

        [Fact]
        public void ScalarEquals_BothNull_True()
        {
            Assert.True(ClientDiffTools.ScalarEquals(null, null));
        }

        [Fact]
        public void ScalarEquals_OneNull_False()
        {
            Assert.False(ClientDiffTools.ScalarEquals("x", null));
            Assert.False(ClientDiffTools.ScalarEquals(null, "x"));
        }

        [Fact]
        public void ScalarEquals_SameStrings_True()
        {
            Assert.True(ClientDiffTools.ScalarEquals("ABC", "ABC"));
            Assert.True(ClientDiffTools.ScalarEquals(42, 42));
        }

        [Fact]
        public void ScalarEquals_DifferentStrings_False()
        {
            Assert.False(ClientDiffTools.ScalarEquals("ABC", "abc"));  // case matters in v1
            Assert.False(ClientDiffTools.ScalarEquals("foo", "bar"));
        }

        [Fact]
        public void ScalarEquals_StringVsInt_TrueWhenToStringMatches()
        {
            // v1 string-equality after .ToString() per the verdict scope-note.
            // Numeric coercion is deferred — "12345" and 12345 share the same
            // ToString() output so they compare equal under v1 semantics.
            Assert.True(ClientDiffTools.ScalarEquals("12345", 12345));
        }

        [Fact]
        public void DiffDataOne_IdenticalRows_NoDiffs()
        {
            var a = new Dictionary<string, object?> { ["x"] = 1, ["y"] = "a" };
            var b = new Dictionary<string, object?> { ["x"] = 1, ["y"] = "a" };
            var diff = ClientDiffTools.DiffDataOne(a, b, new HashSet<string>());
            Assert.Empty(diff.Diffs);
            Assert.Equal(2, diff.MatchedFieldsCount);
        }

        [Fact]
        public void DiffDataOne_OnlyInA_SurfacesAsDiff()
        {
            var a = new Dictionary<string, object?> { ["x"] = 1, ["y"] = "a" };
            var b = new Dictionary<string, object?> { ["x"] = 1 };
            var diff = ClientDiffTools.DiffDataOne(a, b, new HashSet<string>());
            Assert.Single(diff.Diffs);
        }

        [Fact]
        public void DiffDataOne_IgnoredField_NotEmittedEvenIfDifferent()
        {
            var a = new Dictionary<string, object?> { ["x"] = 1, ["clientNumber"] = "1001" };
            var b = new Dictionary<string, object?> { ["x"] = 1, ["clientNumber"] = "1002" };
            var diff = ClientDiffTools.DiffDataOne(a, b,
                new HashSet<string>(new[] { "clientNumber" }));
            Assert.Empty(diff.Diffs);
        }
    }
}
