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

        // ---- R6-06 — detail diff full-fidelity + multiplicity tests ----

        private static LacertMCP.Server.Session.DetailRow MakeDetailRow(
            string series = "11", string prefix = "", string code = "1",
            string suffix = "", string stateAbbr = "", string cityAbbr = "",
            string source = "", string tsj = "T", string importSrc = "",
            double? amount = 100.0, string description = "Acme W-2")
            => new LacertMCP.Server.Session.DetailRow(
                series, prefix, code, suffix, stateAbbr, cityAbbr,
                source, tsj, importSrc, amount, description);

        private static System.Text.Json.JsonElement SerializeDiff(object diff)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(diff);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        [Fact]
        public void DiffDetailRows_TsjOnlyDifference_SurfacesAsChanged_R6_06a()
        {
            // Rows identical except TSJ (taxpayer vs spouse) — the exact
            // false-negative class R6-06 calls out. Must surface as changed.
            var a = new List<LacertMCP.Server.Session.DetailRow> { MakeDetailRow(tsj: "T") };
            var b = new List<LacertMCP.Server.Session.DetailRow> { MakeDetailRow(tsj: "S") };

            var root = SerializeDiff(ClientDiffTools.DiffDetailRows(a, b));
            Assert.Equal(0, root.GetProperty("added").GetArrayLength());
            Assert.Equal(0, root.GetProperty("removed").GetArrayLength());
            Assert.Equal(1, root.GetProperty("changed").GetArrayLength());

            var changed = root.GetProperty("changed")[0];
            Assert.Equal("T", changed.GetProperty("a").GetProperty("tsj").GetString());
            Assert.Equal("S", changed.GetProperty("b").GetProperty("tsj").GetString());
        }

        [Theory]
        [InlineData("source")]
        [InlineData("prefix")]
        [InlineData("suffix")]
        [InlineData("cityAbbr")]
        [InlineData("importSrc")]
        public void DiffDetailRows_SingleFieldDifference_SurfacesAsChanged_R6_06a(string field)
        {
            var a = new List<LacertMCP.Server.Session.DetailRow> { MakeDetailRow() };
            var bRow = field switch
            {
                "source" => MakeDetailRow(source: "W2"),
                "prefix" => MakeDetailRow(prefix: "PX"),
                "suffix" => MakeDetailRow(suffix: "SX"),
                "cityAbbr" => MakeDetailRow(cityAbbr: "NYC"),
                _ => MakeDetailRow(importSrc: "CSV"),
            };
            var b = new List<LacertMCP.Server.Session.DetailRow> { bRow };

            var root = SerializeDiff(ClientDiffTools.DiffDetailRows(a, b));
            Assert.Equal(1, root.GetProperty("changed").GetArrayLength());
        }

        [Fact]
        public void DiffDetailRows_ShapedRow_EmitsAllElevenFields_R6_06b()
        {
            // A removed row must surface every DetailRow field on the wire
            // (camelCase keys) so any-field diffs are visible, not just
            // detectable.
            var a = new List<LacertMCP.Server.Session.DetailRow>
            {
                MakeDetailRow(series: "12", code: "7", description: "Interest row"),
            };
            var b = new List<LacertMCP.Server.Session.DetailRow>();

            var root = SerializeDiff(ClientDiffTools.DiffDetailRows(a, b));
            var removed = root.GetProperty("removed");
            Assert.Equal(1, removed.GetArrayLength());
            var row = removed[0];
            foreach (var key in new[]
            {
                "series", "prefix", "code", "suffix", "stateAbbrev",
                "cityAbbr", "source", "tsj", "importSrc", "amount", "description",
            })
            {
                Assert.True(row.TryGetProperty(key, out _),
                    $"Shaped detail row must emit `{key}` (R6-06b). Got: {row}");
            }
        }

        [Fact]
        public void DiffDetailRows_TwoVsOneDuplicateRows_SurfacesSurplus_R6_06c()
        {
            // Two identical W-2 rows in A vs one in B — pre-fix this was an
            // invisible no-op (ToLookup + First()). The surplus A row must
            // surface as removed.
            var a = new List<LacertMCP.Server.Session.DetailRow>
            {
                MakeDetailRow(), MakeDetailRow(),
            };
            var b = new List<LacertMCP.Server.Session.DetailRow> { MakeDetailRow() };

            var root = SerializeDiff(ClientDiffTools.DiffDetailRows(a, b));
            Assert.Equal(0, root.GetProperty("added").GetArrayLength());
            Assert.Equal(1, root.GetProperty("removed").GetArrayLength());
            Assert.Equal(0, root.GetProperty("changed").GetArrayLength());

            // Symmetric: one in A, two in B — surplus surfaces as added.
            var root2 = SerializeDiff(ClientDiffTools.DiffDetailRows(b, a));
            Assert.Equal(1, root2.GetProperty("added").GetArrayLength());
            Assert.Equal(0, root2.GetProperty("removed").GetArrayLength());
        }

        [Fact]
        public void DiffDetailRows_DuplicateKeyGroup_PairwiseChanged_R6_06c()
        {
            // Same composite key on both sides, two rows each; second pair
            // differs in Amount only. Position-by-position comparison must
            // surface exactly one changed entry (position 1).
            var a = new List<LacertMCP.Server.Session.DetailRow>
            {
                MakeDetailRow(amount: 100.0), MakeDetailRow(amount: 200.0),
            };
            var b = new List<LacertMCP.Server.Session.DetailRow>
            {
                MakeDetailRow(amount: 100.0), MakeDetailRow(amount: 250.0),
            };

            var root = SerializeDiff(ClientDiffTools.DiffDetailRows(a, b));
            Assert.Equal(0, root.GetProperty("added").GetArrayLength());
            Assert.Equal(0, root.GetProperty("removed").GetArrayLength());
            Assert.Equal(1, root.GetProperty("changed").GetArrayLength());
            Assert.Equal(1, root.GetProperty("changed")[0].GetProperty("position").GetInt32());
        }

        // ---- R6-06d — rows without a key are surfaced, not dropped ----

        [Fact]
        public void DiffRowsByKey_RowsMissingKey_SurfaceInRowsWithoutKey_R6_06d()
        {
            var a = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["stateAbbrev"] = "CA", ["x"] = 1 },
                new Dictionary<string, object?> { ["x"] = 2 }, // no key
            };
            var b = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["stateAbbrev"] = "CA", ["x"] = 1 },
            };

            var root = SerializeDiff(ClientDiffTools.DiffRowsByKey(
                a, b, "stateAbbrev", new HashSet<string>()));
            var without = root.GetProperty("rowsWithoutKey");
            Assert.Equal(1, without.GetProperty("a").GetInt32());
            Assert.Equal(0, without.GetProperty("b").GetInt32());
            // The keyed row still matches cleanly.
            Assert.Equal(0, root.GetProperty("added").GetArrayLength());
            Assert.Equal(0, root.GetProperty("removed").GetArrayLength());
            Assert.Equal(0, root.GetProperty("changed").GetArrayLength());
        }
    }
}
