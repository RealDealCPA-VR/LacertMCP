using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Tools;
using LacertMCP.Tests.Schema;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>FP-3 — <c>lacerte_field_dictionary</c>.</summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldDictionaryToolsTests
    {
        private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

        private static JsonElement Result(string json)
        {
            var root = Root(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            return root.GetProperty("result");
        }

        [Theory]
        [InlineData("I")]
        [InlineData("c")]
        [InlineData("P")]
        [InlineData("S")]
        [InlineData("F")]
        [InlineData("R")]
        [InlineData("T")]
        [InlineData("N")]
        [InlineData("B")]
        public async Task ShippedDictionary_IsEmpty_AndSaysNotToGuess(string module)
        {
            FieldDictionaryLoader.ReloadCaches();
            var r = Result(await new FieldDictionaryTools().LacerteFieldDictionary(module));
            Assert.Equal(module.ToUpperInvariant(), r.GetProperty("module").GetString());
            Assert.Equal(0, r.GetProperty("count").GetInt32());
            Assert.Equal(0, r.GetProperty("totalEntries").GetInt32());
            Assert.Empty(r.GetProperty("entries").EnumerateArray());
            var note = r.GetProperty("note").GetString()!;
            Assert.Contains("No verified mappings", note);
            Assert.Contains("lacerte-mcp map-diff", note);
            Assert.Contains("Do not guess", note);
            Assert.False(r.TryGetProperty("loadError", out _));
        }

        [Theory]
        [InlineData("1120", "C")]
        [InlineData("1120S", "S")]
        [InlineData("1065", "P")]
        [InlineData("1041", "F")]
        [InlineData("Partnership", "P")]
        [InlineData("s-corporation", "S")]
        public async Task Module_AcceptsReturnTypeAliases(string alias, string expected)
        {
            var r = Result(await new FieldDictionaryTools().LacerteFieldDictionary(alias));
            Assert.Equal(expected, r.GetProperty("module").GetString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("X")]
        [InlineData("1099")]
        public async Task Module_Invalid_ReturnsInvalidArgument(string module)
        {
            var root = Root(await new FieldDictionaryTools().LacerteFieldDictionary(module));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task TaxYear_TooOld_IsRejected()
        {
            var root = Root(await new FieldDictionaryTools().LacerteFieldDictionary("C", null, 2017));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.TaxYearTooOld, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task Search_And_TaxYear_Filter_Entries()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('C',
                TempFieldDictionary.Field("1120.gross_receipts", 1, "C1_9001", 2024),
                TempFieldDictionary.Field("1120.state_apportionment", 3, "C3_9001"),
                TempFieldDictionary.Detail("1120.k1.box4", 40, 4, prefix: 1, suffix: 1000));

            var all = Result(await new FieldDictionaryTools().LacerteFieldDictionary("C"));
            Assert.Equal(3, all.GetProperty("count").GetInt32());
            Assert.False(all.TryGetProperty("note", out _));
            var ids = all.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
            Assert.Equal(new[] { "1120.gross_receipts", "1120.k1.box4", "1120.state_apportionment" }, ids);

            // Case-insensitive search over id / concept / form / line / notes.
            var gross = Result(await new FieldDictionaryTools().LacerteFieldDictionary("C", "GROSS"));
            var e0 = Assert.Single(gross.GetProperty("entries").EnumerateArray());
            Assert.Equal("field", e0.GetProperty("kind").GetString());
            Assert.Equal(1, e0.GetProperty("table").GetInt32());
            Assert.Equal("C1_9001", e0.GetProperty("field").GetString());
            Assert.False(e0.GetProperty("needsState").GetBoolean());
            Assert.Equal(2024, Assert.Single(e0.GetProperty("taxYears").EnumerateArray()).GetInt32());
            Assert.False(e0.TryGetProperty("series", out _));

            var k1 = Result(await new FieldDictionaryTools().LacerteFieldDictionary("C", "k-1 BOX"));
            var d = Assert.Single(k1.GetProperty("entries").EnumerateArray());
            Assert.Equal(40, d.GetProperty("series").GetInt32());
            Assert.Equal(4, d.GetProperty("code").GetInt32());
            Assert.Equal(1000, d.GetProperty("suffix").GetInt32());
            Assert.False(d.TryGetProperty("table", out _));
            Assert.Equal(1, Result(await new FieldDictionaryTools().LacerteFieldDictionary("C", "a note")).GetProperty("count").GetInt32());

            // taxYear keeps entries verified for that year plus all-year entries.
            var ty23 = Result(await new FieldDictionaryTools().LacerteFieldDictionary("C", null, 2023));
            Assert.Equal(2, ty23.GetProperty("count").GetInt32());
            Assert.Equal(3, ty23.GetProperty("totalEntries").GetInt32());

            var none = Result(await new FieldDictionaryTools().LacerteFieldDictionary("C", "officer compensation"));
            Assert.Equal(0, none.GetProperty("count").GetInt32());
            Assert.Contains("Do not guess", none.GetProperty("note").GetString());
        }

        [Fact]
        public async Task InvalidFile_SurfacesLoadError_AndIsTreatedAsEmpty()
        {
            using var dict = new TempFieldDictionary();
            dict.WriteText('P', "{ \"module\": \"C\", \"entries\": [] }");
            var r = Result(await new FieldDictionaryTools().LacerteFieldDictionary("P"));
            Assert.Equal(0, r.GetProperty("count").GetInt32());
            Assert.Contains("belongs to module 'P'", r.GetProperty("loadError").GetString());
            Assert.Contains("rejected", r.GetProperty("note").GetString());
        }
    }
}
