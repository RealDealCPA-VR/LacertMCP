using System;
using LacertMCP.Server.Odbc;
using Xunit;

namespace LacertMCP.Tests.Odbc
{
    public class ConnectionStringBuilderTests
    {
        [Fact]
        public void Build_Returns_Exact_Driver_Datapath_ReturnType_TaxYear_Shape()
        {
            // Datapath is unconditionally brace-wrapped (brace-wrap-always policy)
            // so the parser can't be confused by any delimiter the path might contain.
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\24data", "Individual", 2024);
            Assert.Equal(
                @"Driver={LacerteDSIIDriver};DataDir={C:\Lacerte\24data};TaxYear=2024;ReturnType=Individual",
                connStr);
        }

        [Theory]
        [InlineData("Corporate", "C")]
        [InlineData("Partnership", "P")]
        [InlineData("SCorporate", "S")]
        [InlineData("Fiduciary", "F")]
        [InlineData("Exempt", "R")]
        [InlineData("Estate", "T")]
        [InlineData("Gift", "N")]
        [InlineData("Benefit", "B")]
        public void Build_Accepts_All_ReturnType_Module_Names(string returnType, string _)
        {
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\24data", returnType, 2024);
            Assert.Contains($"ReturnType={returnType}", connStr);
        }

        // R7-SRC — the SDK Developer Instructions' connection-string example
        // uses the DataDir key and a FOUR-digit year ("TaxYear=2019").
        [Theory]
        [InlineData(2018, "2018")]
        [InlineData(2021, "2021")]
        [InlineData(2024, "2024")]
        [InlineData(2099, "2099")]
        public void Build_Renders_TaxYear_As_Four_Digits(int year, string expected)
        {
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\YYdata", "Individual", year);
            Assert.Contains($";TaxYear={expected};", connStr);
        }

        [Fact]
        public void Build_Wraps_Datapath_With_Semicolon_In_Braces()
        {
            // Pathological input — a path with a semicolon in it — must be brace-wrapped
            // or the ODBC provider treats everything after ';' as a new key.
            var connStr = ConnectionStringBuilder.Build(@"C:\Weird;Path\24data", "Individual", 2024);
            Assert.Contains(@"DataDir={C:\Weird;Path\24data}", connStr);
        }

        [Fact]
        public void Build_Always_Brace_Wraps_Datapath_Even_When_Plain()
        {
            // Brace-wrap-always policy: even a totally plain path is brace-wrapped so
            // there is no parser-confusion attack surface from the bracing heuristic.
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\24data", "Individual", 2024);
            Assert.Contains(@"DataDir={C:\Lacerte\24data}", connStr);
        }

        [Theory]
        [InlineData(@"C:\foo}bar\24data", @"DataDir={C:\foo}}bar\24data}")]
        [InlineData(@"C:\foo}; Driver={OtherDriver}\24data", @"DataDir={C:\foo}}; Driver={OtherDriver}}\24data}")]
        [InlineData(@"C:\already}}doubled\24data", @"DataDir={C:\already}}}}doubled\24data}")]
        public void Build_Escapes_Embedded_CloseBrace_By_Doubling(string dataPath, string expectedDatapathSegment)
        {
            // Embedded '}' must be doubled inside the brace-quoted value per ODBC
            // connection-string grammar; otherwise an attacker can close the brace
            // early and inject sibling keys (e.g. a second Driver={OtherDriver}).
            var connStr = ConnectionStringBuilder.Build(dataPath, "Individual", 2024);
            Assert.Contains(expectedDatapathSegment, connStr);
        }

        [Theory]
        [InlineData(@"C:\foo{bar\24data")]
        [InlineData(@"C:\foo=bar\24data")]
        [InlineData(@"C:\foo bar\24data")]
        [InlineData(@"  C:\Lacerte\24data  ")]
        public void Build_Permits_Other_Delimiters_Inside_BraceWrapped_Value(string dataPath)
        {
            // '{', '=', and whitespace are all safe inside the brace-quoted Datapath value
            // per ODBC grammar — no escaping needed. Build must accept them as-is.
            var connStr = ConnectionStringBuilder.Build(dataPath, "Individual", 2024);
            Assert.Contains("DataDir={" + dataPath + "}", connStr);
        }

        [Fact]
        public void Build_Driver_Name_Is_Always_Brace_Wrapped()
        {
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\24data", "Individual", 2024);
            Assert.StartsWith("Driver={LacerteDSIIDriver};", connStr);
        }

        [Theory]
        [InlineData("C:\\foo\0bar\\24data")]       // NUL
        [InlineData("C:\\foo\rbar\\24data")]       // CR
        [InlineData("C:\\foo\nbar\\24data")]       // LF
        [InlineData("C:\\foo\tbar\\24data")]       // TAB
        [InlineData("C:\\foobar\\24data")]   // SOH (arbitrary low control)
        public void Build_Throws_On_Control_Chars_In_DataPath(string dataPath)
        {
            // Control characters have no legitimate place in a filesystem path and are
            // the highest-risk parser-confusion characters for ODBC parsers (NUL can
            // terminate the string, CR/LF can split keys in some providers).
            Assert.Throws<ArgumentException>(() =>
                ConnectionStringBuilder.Build(dataPath, "Individual", 2024));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Build_Throws_On_Missing_DataPath(string? dataPath)
        {
            Assert.Throws<ArgumentException>(() =>
                ConnectionStringBuilder.Build(dataPath!, "Individual", 2024));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Build_Throws_On_Missing_ReturnType(string? returnType)
        {
            Assert.Throws<ArgumentException>(() =>
                ConnectionStringBuilder.Build(@"C:\Lacerte\24data", returnType!, 2024));
        }

        [Theory]
        [InlineData(2017)]
        [InlineData(2100)]
        [InlineData(0)]
        [InlineData(-1)]
        public void Build_Throws_On_OutOfRange_TaxYear(int year)
        {
            Assert.Throws<ArgumentException>(() =>
                ConnectionStringBuilder.Build(@"C:\Lacerte\24data", "Individual", year));
        }
    }
}
