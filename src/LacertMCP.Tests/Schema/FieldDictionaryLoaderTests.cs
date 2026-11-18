using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    /// <summary>FP-3 — strict parsing, serialization and the shipped (empty) dictionaries.</summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class FieldDictionaryLoaderTests
    {
        private const string ValidField =
            "{\"id\":\"1120.gross_receipts\",\"concept\":\"Gross receipts\",\"form\":\"1120\",\"line\":\"1a\",\"kind\":\"field\"," +
            "\"table\":1,\"field\":\"C1_37\",\"needsSubclient\":false,\"needsState\":false,\"dataType\":\"amount\"," +
            "\"verifiedOn\":\"TY2024 on PC, 2026-11-20\",\"source\":\"map-diff\"}";

        private const string ValidDetail =
            "{\"id\":\"1065.k1.box4\",\"concept\":\"Guaranteed payments\",\"form\":\"1065\",\"kind\":\"detail\"," +
            "\"series\":40,\"code\":4,\"suffix\":1000,\"needsSubclient\":false,\"needsState\":false,\"dataType\":\"amount\"," +
            "\"taxYears\":[2024,2023],\"verifiedOn\":\"TY2024 on PC, 2026-11-20\",\"source\":\"manual\"}";

        private static string File(char module, params string[] entries)
            => "{ \"module\": \"" + module + "\", \"entries\": [" + string.Join(",", entries) + "] }";

        private static string Replace(string entry, string from, string to)
        {
            Assert.Contains(from, entry);
            return entry.Replace(from, to);
        }

        [Fact]
        public void Parse_ValidFieldAndDetail_Entries()
        {
            var parsed = FieldDictionaryLoader.Parse(File('C', ValidField, ValidDetail.Replace("1065.", "1120.")), 'C');
            Assert.Equal('C', parsed.Module);
            Assert.Equal(2, parsed.Entries.Count);
            var f = parsed.Entries[0];
            Assert.True(f.IsField);
            Assert.Equal(1, f.Table);
            Assert.Equal("C1_37", f.Field);
            Assert.Equal("1a", f.Line);
            Assert.Empty(f.TaxYears);
            var d = parsed.Entries[1];
            Assert.False(d.IsField);
            Assert.Equal(40, d.Series);
            Assert.Equal(4, d.Code);
            Assert.Null(d.Prefix);
            Assert.Equal(1000, d.Suffix);
            Assert.Equal(new[] { 2023, 2024 }, d.TaxYears.ToArray()); // sorted
            Assert.True(d.AppliesToYear(2023));
            Assert.False(d.AppliesToYear(2022));
            Assert.True(d.AppliesToYear(null));
        }

        [Fact]
        public void Parse_EmptyEntries_IsValid()
        {
            Assert.Empty(FieldDictionaryLoader.Parse(File('P'), 'P').Entries);
        }

        [Theory]
        // Unknown vocabularies.
        [InlineData("\"kind\":\"field\"", "\"kind\":\"cell\"", "unknown kind")]
        [InlineData("\"dataType\":\"amount\"", "\"dataType\":\"money\"", "unknown dataType")]
        [InlineData("\"source\":\"map-diff\"", "\"source\":\"guess\"", "unknown source")]
        // Unknown / misplaced properties.
        [InlineData("\"line\":\"1a\"", "\"lnie\":\"1a\"", "unknown property 'lnie'")]
        [InlineData("\"line\":\"1a\"", "\"series\":11", "not valid for kind=field")]
        // Missing required properties.
        [InlineData("\"concept\":\"Gross receipts\",", "", "'concept' is required")]
        [InlineData("\"verifiedOn\":\"TY2024 on PC, 2026-11-20\",", "", "'verifiedOn' is required")]
        [InlineData("\"needsState\":false,", "", "'needsState' is required")]
        [InlineData("\"table\":1,", "", "'table' is required")]
        // Bad values.
        [InlineData("\"concept\":\"Gross receipts\"", "\"concept\":\"  \"", "must not be blank")]
        [InlineData("\"table\":1", "\"table\":5", "table must be 1..4")]
        [InlineData("\"field\":\"C1_37\"", "\"field\":\"C2_37\"", "does not belong to table 1")]
        [InlineData("\"field\":\"C1_37\"", "\"field\":\"c1_37\"", "upper-case")]
        [InlineData("\"field\":\"C1_37\"", "\"field\":\"C1_0\"", "key column")]
        [InlineData("\"needsState\":false", "\"needsState\":true", "needsState must be false")]
        [InlineData("\"id\":\"1120.gross_receipts\"", "\"id\":\"has space\"", "id 'has space'")]
        [InlineData("\"needsSubclient\":false", "\"needsSubclient\":\"no\"", "must be true or false")]
        public void Parse_RejectsInvalidFieldEntry(string from, string to, string expectedMessage)
        {
            var bad = Replace(ValidField, from, to);
            var ex = Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse(File('C', bad), 'C'));
            Assert.Contains(expectedMessage, ex.Message);
        }

        [Theory]
        [InlineData("\"series\":40,", "", "'series' is required")]
        [InlineData("\"code\":4", "\"code\":\"4\"", "'code' must be an integer")]
        [InlineData("\"code\":4", "\"code\":4,\"field\":\"C1_2\"", "not valid for kind=detail")]
        [InlineData("[2024,2023]", "[2024,2024]", "listed twice")]
        [InlineData("[2024,2023]", "[2017]", "outside 2018..2099")]
        public void Parse_RejectsInvalidDetailEntry(string from, string to, string expectedMessage)
        {
            var bad = Replace(ValidDetail, from, to);
            var ex = Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse(File('P', bad), 'P'));
            Assert.Contains(expectedMessage, ex.Message);
        }

        [Fact]
        public void Parse_RejectsDuplicateIds_CaseInsensitive()
        {
            var other = Replace(Replace(ValidField, "1120.gross_receipts", "1120.GROSS_RECEIPTS"), "C1_37", "C1_38");
            var ex = Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse(File('C', ValidField, other), 'C'));
            Assert.Contains("duplicate id", ex.Message);
        }

        [Fact]
        public void Parse_RejectsDuplicateTarget_ForOverlappingYears()
        {
            var other = Replace(ValidField, "1120.gross_receipts", "1120.sales");
            var ex = Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse(File('C', ValidField, other), 'C'));
            Assert.Contains("same target", ex.Message);

            var d2 = Replace(ValidDetail, "1065.k1.box4", "1065.other");
            ex = Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse(File('P', ValidDetail, d2), 'P'));
            Assert.Contains("same target", ex.Message);
        }

        [Fact]
        public void Parse_AllowsSameTarget_ForDisjointYears()
        {
            var a = Replace(ValidField, "\"source\"", "\"taxYears\":[2023],\"source\"");
            var b = Replace(Replace(ValidField, "1120.gross_receipts", "1120.gross_receipts.ty2024"), "\"source\"", "\"taxYears\":[2024],\"source\"");
            Assert.Equal(2, FieldDictionaryLoader.Parse(File('C', a, b), 'C').Entries.Count);
        }

        [Fact]
        public void Parse_RejectsRootProblems()
        {
            Assert.Contains("belongs to module 'I'",
                Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse(File('C'), 'I')).Message);
            Assert.Contains("'module'",
                Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse("{\"module\":\"X\",\"entries\":[]}", null)).Message);
            Assert.Contains("'entries'",
                Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse("{\"module\":\"C\"}", null)).Message);
            Assert.Contains("unknown property 'extra'",
                Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse("{\"module\":\"C\",\"entries\":[],\"extra\":1}", null)).Message);
            Assert.Contains("duplicate property",
                Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse("{\"module\":\"C\",\"module\":\"C\",\"entries\":[]}", null)).Message);
            Assert.Contains("not valid JSON",
                Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse("{ nope", null)).Message);
            Assert.Throws<JsonException>(() => FieldDictionaryLoader.Parse("[]", null));
        }

        [Fact]
        public void Serialize_IsSortedStableAndRoundTrips()
        {
            var entries = new[]
            {
                TempFieldDictionary.Field("b.second", 3, "C3_4", 2024),
                TempFieldDictionary.Detail("A.first", 40, 4, prefix: 1, suffix: 1000),
                TempFieldDictionary.Field("a.zero", 2, "C2_G5"),
            };
            var text = FieldDictionaryLoader.Serialize('C', entries);
            Assert.EndsWith("\n", text);
            Assert.DoesNotContain("\r", text);
            var parsed = FieldDictionaryLoader.Parse(text, 'C');
            Assert.Equal(new[] { "A.first", "a.zero", "b.second" }, parsed.Entries.Select(e => e.Id).ToArray());
            // Stable: re-serializing the parsed file (in any order) gives identical text.
            Assert.Equal(text, FieldDictionaryLoader.Serialize('C', parsed.Entries.Reverse()));
            Assert.Contains("\"needsState\": true", text);
            Assert.Contains("\"line\": \"K-1 box 4\"", text);
        }

        [Fact]
        public void ShippedDictionaries_ExistForEveryModule_AreValid_AndEmpty()
        {
            // Nothing has been verified against real Lacerte yet: inventing
            // field / detail codes is forbidden, so every file ships empty.
            var root = FieldDictionaryLoader.GetRoot();
            Assert.True(Directory.Exists(root), "dictionary folder missing from build output: " + root);
            foreach (var m in Modules.All)
            {
                var path = FieldDictionaryLoader.FilePathFor(m.Char, root)!;
                Assert.True(System.IO.File.Exists(path), path);
                var parsed = FieldDictionaryLoader.ValidateFile(path, m.Char, out var error);
                Assert.True(parsed != null, path + ": " + error);
                Assert.Empty(parsed!.Entries);
                // Shipped bytes are exactly what the serializer (and map-diff record) writes.
                Assert.Equal(FieldDictionaryLoader.Serialize(m.Char, parsed.Entries),
                    System.IO.File.ReadAllText(path).Replace("\r\n", "\n"));

                var loaded = FieldDictionaryLoader.Load(m.Char);
                Assert.True(loaded.FileExists);
                Assert.Null(loaded.Error);
                Assert.Empty(loaded.Entries);
            }
        }

        [Fact]
        public void Load_InvalidFile_FailsClosed_WithError_AndMissingFileIsEmpty()
        {
            using var dict = new TempFieldDictionary();
            dict.WriteText('C', "{ \"module\": \"C\", \"entries\": [ { \"id\": \"x\" } ] }");
            var loaded = FieldDictionaryLoader.Load('C');
            Assert.True(loaded.FileExists);
            Assert.NotNull(loaded.Error);
            Assert.Empty(loaded.Entries);

            var missing = FieldDictionaryLoader.Load('S');
            Assert.False(missing.FileExists);
            Assert.Null(missing.Error);
            Assert.Empty(missing.Entries);
        }

        [Fact]
        public void FindFieldEntry_MatchesTableFieldAndYear()
        {
            using var dict = new TempFieldDictionary();
            dict.Write('C', TempFieldDictionary.Field("c.one", 1, "C1_37", 2024),
                            TempFieldDictionary.Detail("c.det", 11, 1));
            Assert.Equal("c.one", FieldDictionaryLoader.FindFieldEntry('C', 1, "c1_37", 2024)?.Id);
            Assert.Null(FieldDictionaryLoader.FindFieldEntry('C', 1, "C1_37", 2023));
            Assert.Null(FieldDictionaryLoader.FindFieldEntry('C', 2, "C1_37", 2024));
            Assert.Null(FieldDictionaryLoader.FindFieldEntry('S', 1, "C1_37", 2024));
            Assert.Single(FieldDictionaryLoader.GetFieldEntries('C', 1, 2024));
            Assert.Empty(FieldDictionaryLoader.GetFieldEntries('C', 1, 2022));
        }
    }
}
