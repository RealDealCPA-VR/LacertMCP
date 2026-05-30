using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using Xunit;

namespace LacertMCP.Tests.Odbc
{
    public class SqlTemplatesTests
    {
        // -------------------------------------------------------------------
        // Identifier validation
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("SMIT")]
        [InlineData("JONE")]
        [InlineData("A")]
        [InlineData("12345678")] // 8 chars, all digits — at the ceiling
        [InlineData("ABC123")]
        public void ValidateClient_Accepts_Valid_ClientIDs(string client)
        {
            // Should not throw.
            SqlTemplates.ValidateClient(client);
        }

        [Theory]
        [InlineData("TEST'; drop")]       // SQL injection attempt
        [InlineData("smith")]              // lowercase rejected — driver expects uppercase
        [InlineData("ABCDEFGHI")]          // 9 chars, exceeds 8-char ceiling
        [InlineData("AB CD")]              // whitespace
        [InlineData("ABC-DEF")]            // hyphen
        [InlineData("")]                   // empty
        [InlineData("ABC]DEF")]            // closing bracket — would break the [client-Detail] form
        public void ValidateClient_Rejects_Invalid_ClientIDs(string client)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => SqlTemplates.ValidateClient(client));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
            Assert.Equal("InValidClientName", ex.StatusName);
        }

        [Fact]
        public void ValidateClient_Rejects_Null()
        {
            var ex = Assert.Throws<LacerteSDKError>(() => SqlTemplates.ValidateClient(null!));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        // -------------------------------------------------------------------
        // DATA1 templates — bracket quoting + WHERE shape
        // -------------------------------------------------------------------

        [Fact]
        public void Data1All_Has_Expected_Bracket_Quoting()
        {
            Assert.Equal("select * from [DATA1-Client Info]", SqlTemplates.Data1All);
        }

        [Fact]
        public void Data1Where_Substitutes_Validated_Client_Into_Quoted_Literal()
        {
            var sql = SqlTemplates.Data1Where("SMIT");
            Assert.Equal("select * from [DATA1-Client Info] where C1_0 = 'SMIT'", sql);
        }

        // -------------------------------------------------------------------
        // DATA2 pre-TY21 + TY21+ branching
        // -------------------------------------------------------------------

        [Fact]
        public void Data2OldForm_Uses_Global_Table_With_Filter()
        {
            var sql = SqlTemplates.Data2OldForm("JONE");
            Assert.Equal("select * from [DATA2-Subclient Info] where C2_0 = 'JONE'", sql);
        }

        [Fact]
        public void Data2NewForm_Uses_PerClient_Table_Prefix()
        {
            var sql = SqlTemplates.Data2NewForm("JONE");
            Assert.Equal("select * from [JONE-SubClientInfo]", sql);
        }

        // -------------------------------------------------------------------
        // DATA3 pre-TY21 + TY21+ branching (and state filter)
        // -------------------------------------------------------------------

        [Fact]
        public void Data3OldForm_Uses_Global_Table_With_Filter()
        {
            var sql = SqlTemplates.Data3OldForm("SMIT");
            Assert.Equal("select * from [DATA3-State Client Info] where C3_0 = 'SMIT'", sql);
        }

        [Fact]
        public void Data3OldFormByState_Filters_By_Client_And_State()
        {
            var sql = SqlTemplates.Data3OldFormByState("SMIT", "CA");
            Assert.Equal(
                "select * from [DATA3-State Client Info] where C3_0 = 'SMIT' and C3_G1 = 'CA'",
                sql);
        }

        [Fact]
        public void Data3NewForm_Uses_PerClient_Table_Prefix()
        {
            var sql = SqlTemplates.Data3NewForm("SMIT");
            Assert.Equal("select * from [SMIT-StateClientInfo]", sql);
        }

        [Theory]
        [InlineData("california")] // lowercase
        [InlineData("c")]          // too short
        [InlineData("USA")]        // too long
        [InlineData("C1")]         // digit
        public void Data3OldFormByState_Throws_On_Bad_State(string state)
        {
            var ex = Assert.Throws<LacerteSDKError>(
                () => SqlTemplates.Data3OldFormByState("SMIT", state));
            Assert.Equal(StatusCodes.InValidStateAbbrev, ex.StatusCode);
        }

        // -------------------------------------------------------------------
        // DATA4 pre-TY21 + TY21+ branching
        // -------------------------------------------------------------------

        [Fact]
        public void Data4OldForm_Filters_By_Client_And_State()
        {
            var sql = SqlTemplates.Data4OldForm("SMIT", "NY");
            Assert.Equal(
                "select * from [DATA4-State Subclient Info] where C4_0 = 'SMIT' and C4_G1 = 'NY'",
                sql);
        }

        [Fact]
        public void Data4NewForm_Uses_PerClient_Table_Prefix()
        {
            var sql = SqlTemplates.Data4NewForm("SMIT");
            Assert.Equal("select * from [SMIT-StateSubClientInfo]", sql);
        }

        // -------------------------------------------------------------------
        // DATA5/6/7 — global with filter (both forms identical)
        // -------------------------------------------------------------------

        [Fact]
        public void Data5_Uses_Global_Table_With_Filter()
        {
            Assert.Equal(
                "select * from [DATA5-Status Info] where C5_0 = 'BROW'",
                SqlTemplates.Data5("BROW"));
        }

        [Fact]
        public void Data6_Uses_Global_Table_With_Filter()
        {
            Assert.Equal(
                "select * from [DATA6-Display Info] where C6_0 = 'BROW'",
                SqlTemplates.Data6("BROW"));
        }

        [Fact]
        public void Data7_Uses_Global_Table_With_Filter()
        {
            Assert.Equal(
                "select * from [DATA7-Form Info] where C7_0 = 'BROW'",
                SqlTemplates.Data7("BROW"));
        }

        // -------------------------------------------------------------------
        // EF status JOIN — bracket-quoted aliases per agent 03 §4
        // -------------------------------------------------------------------

        [Fact]
        public void EfStatusJoin_Contains_Expected_Tables_And_Joins()
        {
            Assert.Contains("[DATA1-Client Info]", SqlTemplates.EfStatusJoin);
            Assert.Contains("[EFilings]", SqlTemplates.EfStatusJoin);
            Assert.Contains("[EFStatuses]", SqlTemplates.EfStatusJoin);
            Assert.Contains("left outer join", SqlTemplates.EfStatusJoin);
            Assert.Contains("ci.C1_0 = ef.ClientId", SqlTemplates.EfStatusJoin);
            Assert.Contains("ef.Id = s.EFilingId", SqlTemplates.EfStatusJoin);
        }

        // -------------------------------------------------------------------
        // Detail tables — per-client [<CLIENT>-Detail] logical table per §5
        // -------------------------------------------------------------------

        [Fact]
        public void DetailAll_References_PerClient_Detail_Table_With_Full_Field_List()
        {
            var sql = SqlTemplates.DetailAll("JOHNS");
            Assert.Contains("[JOHNS-Detail]", sql);
            // Field list straight from spec §2.
            Assert.Contains("Series", sql);
            Assert.Contains("Prefix", sql);
            Assert.Contains("Code", sql);
            Assert.Contains("Suffix", sql);
            Assert.Contains("StateAbbr", sql);
            Assert.Contains("CityAbbr", sql);
            Assert.Contains("Source", sql);
            Assert.Contains("TSJ", sql);
            Assert.Contains("ImportSrc", sql);
            Assert.Contains("Amount", sql);
            Assert.Contains("Description", sql);
        }

        [Fact]
        public void DetailBySeries_Filters_By_Series_Integer()
        {
            var sql = SqlTemplates.DetailBySeries("JOHNS", 11);
            Assert.Equal("select * from [JOHNS-Detail] where Series = 11", sql);
        }

        [Fact]
        public void DetailByCode_Filters_By_Code_Integer()
        {
            var sql = SqlTemplates.DetailByCode("JOHNS", 800);
            Assert.Equal("select * from [JOHNS-Detail] where Code = 800", sql);
        }

        [Fact]
        public void All_Client_Bearing_Templates_Throw_On_Bad_Client()
        {
            // Spot-check that every entry-point validates — a bad ClientID must never reach SQL.
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data1Where("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data2OldForm("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data2NewForm("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data3OldForm("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data3NewForm("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data4NewForm("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data5("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data6("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data7("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.DetailAll("test"));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.DetailBySeries("test", 11));
            Assert.Throws<LacerteSDKError>(() => SqlTemplates.DetailByCode("test", 800));
        }
    }
}
