using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Odbc
{
    /// <summary>FP-2 — the detail-row INSERT builder and the read-back row comparison.</summary>
    public class DetailRowWriterTests
    {
        private static DetailRow Row(
            string series = "11", string prefix = "", string code = "1", string suffix = "",
            string state = "", string city = "", string source = "", string tsj = "",
            string importSrc = "", double? amount = 100.0, string description = "W-2")
            => new DetailRow(series, prefix, code, suffix, state, city, source, tsj, importSrc, amount, description);

        [Fact]
        public void Build_MinimalRow_OmitsBlankPrefixSuffix_AndNeverWritesImportSrc()
        {
            var sql = DetailRowWriter.BuildInsertSql("SMIT", Row());
            Assert.Equal(
                "INSERT INTO [SMIT-Detail] (Series, Code, StateAbbr, CityAbbr, Source, TSJ, Amount, Description) " +
                "VALUES (11, 1, '', '', '', '', 100, 'W-2')",
                sql);
            Assert.DoesNotContain("ImportSrc", sql);
        }

        [Fact]
        public void Build_AllColumns_IntegersNormalized_VocabularyNormalized()
        {
            var sql = DetailRowWriter.BuildInsertSql("JOHNS1",
                Row(series: "040", prefix: "2", code: "-3", suffix: "1000", state: "CA", city: "LA",
                    source: "nonsource", tsj: "s", amount: -1234.56, description: "K-1 box 1"));
            Assert.Equal(
                "INSERT INTO [JOHNS1-Detail] (Series, Prefix, Code, Suffix, StateAbbr, CityAbbr, Source, TSJ, Amount, Description) " +
                "VALUES (40, 2, -3, 1000, 'CA', 'LA', 'Nonsource', 'Spouse', -1234.56, 'K-1 box 1')",
                sql);
        }

        [Fact]
        public void Build_NullAmount_OmitsTheColumn()
        {
            var sql = DetailRowWriter.BuildInsertSql("SMIT", Row(series: "14", amount: null, description: "Dependent: Ann"));
            Assert.DoesNotContain("Amount", sql);
            Assert.EndsWith("VALUES (14, 1, '', '', '', '', 'Dependent: Ann')", sql);
        }

        [Fact]
        public void Build_DoublesSingleQuotes()
        {
            var sql = DetailRowWriter.BuildInsertSql("SMIT", Row(description: "O'Brien's 'shop'"));
            Assert.EndsWith("'O''Brien''s ''shop''')", sql);
        }

        [Fact]
        public void Build_InjectionShapedDescription_StaysInsideOneLiteral()
        {
            var sql = DetailRowWriter.BuildInsertSql("SMIT", Row(description: "x'); DELETE FROM [SMIT-Detail]; --"));
            Assert.EndsWith("'x''); DELETE FROM [SMIT-Detail]; --')", sql);
        }

        [Theory]
        [InlineData(0.1, "0.1")]
        [InlineData(0.0000001, "0.0000001")]
        [InlineData(52000.5, "52000.5")]
        [InlineData(-1234.5, "-1234.5")]
        [InlineData(999999999.0, "999999999")]
        [InlineData(-999999999.0, "-999999999")]
        [InlineData(0.0, "0")]
        [InlineData(1e-12, "0.000000000001")]
        public void FormatAmount_InvariantDecimal_NoExponent(double value, string expected)
        {
            Assert.Equal(expected, DetailRowWriter.FormatAmount(value));
        }

        [Theory]
        [InlineData("tab\there")]
        [InlineData("line\nbreak")]
        [InlineData("cr\rhere")]
        [InlineData("nul\0here")]
        [InlineData("bell\u0007")]
        [InlineData("del\u007F")]
        [InlineData("c1\u0085")]
        [InlineData("ls\u2028")]
        public void Build_ControlCharacters_Rejected9008(string description)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", Row(description: description)));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Theory]
        [InlineData("smit")]
        [InlineData("SMIT]; DROP")]
        [InlineData("TOOLONGNAME")]
        [InlineData("")]
        public void Build_BadClientKey_Rejected(string key)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql(key, Row()));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        [Fact]
        public void Build_ImportSrc_Rejected()
        {
            var ex = Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", Row(importSrc: "X")));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Theory]
        [InlineData("", "1")]   // Series required
        [InlineData("11", "")]  // Code required
        [InlineData("1.5", "1")]
        [InlineData("11", "9999999999")]
        [InlineData("11", "1e3")]
        public void Build_BadIntegers_Rejected9008(string series, string code)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", Row(series: series, code: code)));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Theory]
        [InlineData(1_000_000_000.0)]
        [InlineData(double.NaN)]
        [InlineData(0.1234567890123)] // 13 decimals
        public void Build_BadAmount_Rejected9008(double amount)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", Row(amount: amount)));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public void Build_BadSourceOrTsj_Rejected9008()
        {
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", Row(source: "W2"))).StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildInsertSql("SMIT", Row(tsj: "X"))).StatusCode);
        }

        // ---------------------------------------------------------------
        // Read-back comparison
        // ---------------------------------------------------------------

        [Fact]
        public void RowMatches_DriverShapes_BlankIntegerVsZero_PaddingCaseAndAmountScale()
        {
            var written = Row(source: "Source", tsj: "Taxpayer", amount: 52000.5, description: "ACME W-2");
            var stored = new DetailRow("11", "0", "1", "0", "", "", "SOURCE", "taxpayer", "", 52000.50, "ACME W-2   ");
            Assert.True(DetailRowWriter.RowMatches(written, stored));
        }

        [Fact]
        public void RowMatches_DifferentValues_DoNotMatch()
        {
            var written = Row(amount: 100.0, description: "ACME W-2");
            Assert.False(DetailRowWriter.RowMatches(written, Row(amount: 100.01, description: "ACME W-2")));
            Assert.False(DetailRowWriter.RowMatches(written, Row(amount: 100.0, description: "ACME W")));   // truncated
            Assert.False(DetailRowWriter.RowMatches(written, Row(code: "2", amount: 100.0, description: "ACME W-2")));
            Assert.False(DetailRowWriter.RowMatches(written, Row(prefix: "1", amount: 100.0, description: "ACME W-2")));
            Assert.False(DetailRowWriter.RowMatches(written, Row(amount: null, description: "ACME W-2")));
        }

        [Fact]
        public void CountMatching_CountsEveryEqualRow()
        {
            var w = Row();
            Assert.Equal(2, DetailRowWriter.CountMatching(new[] { Row(), Row(code: "2"), Row() }, w));
        }

        // ---- restore DELETE ---------------------------------------------------------------

        [Fact]
        public void BuildDelete_SameColumnsAndLiteralsAsTheInsert_Escaped()
        {
            var row = Row(suffix: "1000", source: "source", tsj: "t", amount: 52000.5, description: "O'Brien Co W-2");
            Assert.Equal(
                "DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' " +
                "AND Source = 'Source' AND TSJ = 'Taxpayer' AND Amount = 52000.5 AND Description = 'O''Brien Co W-2'",
                DetailRowWriter.BuildDeleteSql("SMIT", row));

            // Prefix when written; Amount only when written (as the INSERT).
            Assert.Equal(
                "DELETE FROM [JOHNS1-Detail] WHERE Series = 40 AND Prefix = 2 AND Code = -3 AND Suffix = 1000 AND StateAbbr = 'CA' AND CityAbbr = 'LA' " +
                "AND Source = 'Nonsource' AND TSJ = 'Spouse' AND Description = 'K-1 box 1'",
                DetailRowWriter.BuildDeleteSql("JOHNS1",
                    Row(series: "040", prefix: "2", code: "-3", suffix: "1000", state: "CA", city: "LA", source: "nonsource", tsj: "s", amount: null, description: "K-1 box 1")));
        }

        [Fact]
        public void BuildDelete_RejectsWhatTheInsertRejects_PlusMissingSuffix()
        {
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid,
                Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildDeleteSql("SMIT", Row(suffix: ""))).StatusCode);
            Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildDeleteSql("SMIT", Row(suffix: "1000", description: "a\nb")));
            Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildDeleteSql("SMIT", Row(suffix: "1000", description: "中")));
            Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildDeleteSql("SM IT", Row(suffix: "1000")));
            Assert.Throws<LacerteSDKError>(() => DetailRowWriter.BuildDeleteSql("SMIT", Row(suffix: "1000", importSrc: "X")));
        }

        [Theory]
        [InlineData(52000.5, "O'Brien ''quoted'' AND x = 1")]
        [InlineData(-0.01, "")]
        [InlineData(null, "no amount")]
        [InlineData(999999999.0, "Renée")]
        public void ParseDelete_RoundTripsWhatBuildDeleteBuilds(double? amount, string description)
        {
            var row = Row(prefix: "3", suffix: "1000", state: "NY", source: "Source", tsj: "Joint", amount: amount, description: description);
            var sql = DetailRowWriter.BuildDeleteSql("ABC12345", row);
            Assert.True(DetailRowWriter.TryParseDeleteSql(sql, out var key, out var parsed));
            Assert.Equal("ABC12345", key);
            Assert.Equal(sql, DetailRowWriter.BuildDeleteSql(key, parsed!));
            Assert.True(DetailRowWriter.RowMatches(row, parsed!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE ")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'unterminated")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x' ")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x' AND Description = 'y'")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Amount = 1e3 AND Description = 'x'")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Amount = 100.50 AND Description = 'x'")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x\r\n'")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND  Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x'")]
        [InlineData("DELETE FROM [TOOLONG12-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x'")]
        [InlineData("DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x' AND 1 = 1")]
        public void ParseDelete_RefusesNonCanonical(string? sql)
        {
            Assert.False(DetailRowWriter.TryParseDeleteSql(sql, out var key, out var row));
            Assert.Equal(string.Empty, key);
            Assert.Null(row);
        }

        [Fact]
        public void ParseDelete_AcceptsTheMinimalCanonicalShape()
        {
            Assert.True(DetailRowWriter.TryParseDeleteSql(
                "DELETE FROM [SMIT-Detail] WHERE Series = 11 AND Code = 1 AND Suffix = 1000 AND StateAbbr = '' AND CityAbbr = '' AND Source = '' AND TSJ = '' AND Description = 'x'",
                out var key, out var row));
            Assert.Equal("SMIT", key);
            Assert.Null(row!.Amount);
        }

        [Fact]
        public void CouldDeleteMatch_IsASupersetOfRowMatches_WithPrefixAndLooseSemantics()
        {
            var w = Row(suffix: "1000", amount: 100, description: "ACME");
            Assert.True(DetailRowWriter.CouldDeleteMatch(w, w));
            Assert.True(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1000", amount: 100, description: "acme   ")));
            Assert.True(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1000", amount: 100, description: "ACME CORP"))); // prefix
            Assert.True(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1000", amount: 100.4, description: "ACME")));   // near amount
            Assert.True(DetailRowWriter.CouldDeleteMatch(w, Row(prefix: "7", suffix: "1000", amount: 100, description: "ACME"))); // Prefix not in the WHERE
            Assert.True(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1000", state: "CA", amount: 100, description: "ACME"))); // StateAbbr = '' could match any state
            Assert.False(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1001", amount: 100, description: "ACME")));
            Assert.False(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1000", amount: 200, description: "ACME")));
            Assert.False(DetailRowWriter.CouldDeleteMatch(w, Row(suffix: "1000", amount: 100, description: "OTHER")));
            Assert.False(DetailRowWriter.CouldDeleteMatch(w, Row(code: "2", suffix: "1000", amount: 100, description: "ACME")));

            // A blank written description could match ANY description under xBase prefix rules.
            var blank = Row(suffix: "1000", amount: 100, description: "");
            Assert.True(DetailRowWriter.CouldDeleteMatch(blank, Row(suffix: "1000", amount: 100, description: "anything")));
        }

        [Fact]
        public void StrictKey_ExactIdentity_CaseAndImportSrcMatter()
        {
            var a = Row(suffix: "1000", description: "ACME");
            Assert.Equal(DetailRowWriter.StrictKey(a), DetailRowWriter.StrictKey(a with { Series = "011", Amount = 100.0 }));
            Assert.NotEqual(DetailRowWriter.StrictKey(a), DetailRowWriter.StrictKey(a with { Description = "acme" }));
            Assert.NotEqual(DetailRowWriter.StrictKey(a), DetailRowWriter.StrictKey(a with { ImportSrc = "X" }));
            Assert.NotEqual(DetailRowWriter.StrictKey(a), DetailRowWriter.StrictKey(a with { Amount = null }));
            Assert.NotEqual(DetailRowWriter.StrictKey(a), DetailRowWriter.StrictKey(a with { Prefix = "0" }));
        }
    }
}
