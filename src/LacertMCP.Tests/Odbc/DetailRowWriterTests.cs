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
    }
}
