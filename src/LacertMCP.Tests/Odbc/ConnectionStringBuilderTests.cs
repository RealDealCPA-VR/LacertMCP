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
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\24data", "Individual", 2024);
            Assert.Equal(
                @"Driver={LacerteDSIIDriver};Datapath=C:\Lacerte\24data;ReturnType=Individual;TaxYear=24",
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

        [Theory]
        [InlineData(2018, "18")]
        [InlineData(2021, "21")]
        [InlineData(2024, "24")]
        [InlineData(2099, "99")]
        public void Build_Renders_TaxYear_As_Two_Digits(int year, string expectedTwoDigit)
        {
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\YYdata", "Individual", year);
            Assert.EndsWith($"TaxYear={expectedTwoDigit}", connStr);
        }

        [Fact]
        public void Build_Wraps_Datapath_With_Semicolon_In_Braces()
        {
            // Pathological input — a path with a semicolon in it — must be brace-wrapped
            // or the ODBC provider treats everything after ';' as a new key.
            var connStr = ConnectionStringBuilder.Build(@"C:\Weird;Path\24data", "Individual", 2024);
            Assert.Contains(@"Datapath={C:\Weird;Path\24data}", connStr);
        }

        [Fact]
        public void Build_Driver_Name_Is_Always_Brace_Wrapped()
        {
            var connStr = ConnectionStringBuilder.Build(@"C:\Lacerte\24data", "Individual", 2024);
            Assert.StartsWith("Driver={LacerteDSIIDriver};", connStr);
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
