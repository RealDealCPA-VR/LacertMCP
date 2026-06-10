using System.Collections.Generic;
using System.Linq;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Session;
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
        // Data1Top — TOP-based pagination push-down (P14-014)
        // -------------------------------------------------------------------

        [Fact]
        public void Data1Top_Emits_TOP_n_With_Validated_Integer()
        {
            Assert.Equal("select top 100 * from [DATA1-Client Info]", SqlTemplates.Data1Top(100));
            Assert.Equal("select top 1 * from [DATA1-Client Info]", SqlTemplates.Data1Top(1));
            Assert.Equal("select top 25000 * from [DATA1-Client Info]", SqlTemplates.Data1Top(25000));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        [InlineData(100_001)]
        public void Data1Top_Rejects_NonPositive_Or_Out_Of_Range(int limit)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => SqlTemplates.Data1Top(limit));
            Assert.Equal(StatusCodes.OutOfIndex, ex.StatusCode);
        }

        [Fact]
        public void Data1TopWhereName_Emits_TOP_With_Like_Prefix()
        {
            Assert.Equal(
                "select top 50 * from [DATA1-Client Info] where C1_2 like 'Smi%'",
                SqlTemplates.Data1TopWhereName(50, "C1_2", "Smi"));
        }

        [Fact]
        public void Data1TopWhereName_Escapes_SingleQuote_In_NamePrefix()
        {
            // O'Reilly — SQL injection attempt via embedded single quote should
            // be doubled per SQL-92 literal escape rules.
            var sql = SqlTemplates.Data1TopWhereName(10, "C1_2", "O'Re");
            Assert.Contains("'O''Re%'", sql);
        }

        [Theory]
        [InlineData("C1_2; drop")]  // semicolon
        [InlineData("col2")]         // not C{t}_N shape
        [InlineData("C8_0")]         // out of 1..7 table range
        [InlineData("")]
        public void Data1TopWhereName_Rejects_Invalid_Column(string column)
        {
            Assert.Throws<LacerteSDKError>(
                () => SqlTemplates.Data1TopWhereName(10, column, "X"));
        }

        // -------------------------------------------------------------------
        // R6-37 — LIKE wildcard hygiene. The FoxPro-style Lacerte ODBC
        // dialect has no [charlist] bracket classes and no reliable ESCAPE
        // clause, so embedded % / _ are STRIPPED (documented on the method).
        // Pre-fix, namePrefix="%" widened a prefix filter into a full-table
        // match and "Smi_h" matched any 5th character — both silently
        // diverging from the sim's literal StartsWith semantics.
        // -------------------------------------------------------------------

        [Fact]
        public void Data1TopWhereName_StripsPercentWildcard_R6_37()
        {
            var sql = SqlTemplates.Data1TopWhereName(50, "C1_2", "Smi%");
            // The only % left is the template's own trailing prefix wildcard.
            Assert.Equal(
                "select top 50 * from [DATA1-Client Info] where C1_2 like 'Smi%'",
                sql);
        }

        [Fact]
        public void Data1TopWhereName_StripsUnderscoreWildcard_R6_37()
        {
            var sql = SqlTemplates.Data1TopWhereName(50, "C1_2", "Smi_h");
            Assert.Equal(
                "select top 50 * from [DATA1-Client Info] where C1_2 like 'Smih%'",
                sql);
        }

        [Fact]
        public void Data1TopWhereName_BarePercent_CannotWidenToMatchEverything_R6_37()
        {
            // "%": pre-fix this emitted `like '%%'` — a full-table match
            // disguised as a prefix filter. Now it degrades to the unfiltered
            // prefix `like '%'`, equivalent to the sim's StartsWith("")
            // (matches all rows EXPLICITLY, with no smuggled metacharacter).
            var sql = SqlTemplates.Data1TopWhereName(10, "C1_2", "%");
            Assert.Equal(
                "select top 10 * from [DATA1-Client Info] where C1_2 like '%'",
                sql);
        }

        [Fact]
        public void Data1TopWhereName_QuoteEscaping_StillAppliesAfterStripping_R6_37()
        {
            var sql = SqlTemplates.Data1TopWhereName(10, "C1_2", "O'%Re_");
            Assert.Contains("'O''Re%'", sql);
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

        /// <summary>
        /// Audit task P14-021 — the SELECT list MUST project all 6 columns the
        /// sim path (<c>LacerteSessionManager.ReadEFStatus</c>) returns so the
        /// live and sim wire shapes stay observationally identical per
        /// CLAUDE.md two-mode parity invariant. Any future drift in the SELECT
        /// list fails this test.
        /// </summary>
        [Fact]
        public void EfStatusJoin_Projects_All_Sim_Parity_Columns()
        {
            var sql = SqlTemplates.EfStatusJoin;
            Assert.Contains("ClientId", sql);
            Assert.Contains("EFilingId", sql);
            Assert.Contains("EFId", sql);
            Assert.Contains("InstanceId", sql);
            Assert.Contains("Status", sql);
            Assert.Contains("StatusDate", sql);
            // Aliases must use the exact key names ReadEFStatus emits.
            Assert.Contains("ci.C1_0 as ClientId", sql);
            Assert.Contains("ef.Id as EFilingId", sql);
            Assert.Contains("s.EFStatus as Status", sql);
            Assert.Contains("s.EFStatusDate as StatusDate", sql);
        }

        // P15-010 — EfStatusJoinSince adds a `where s.EFStatusDate >= {d 'ISO'}`
        // clause for the operator's "watch e-file status across the firm" poll
        // workflow. Pin the SQL shape so a future refactor that mis-formats
        // the date literal (e.g. switches to `{ts 'yyyy-MM-dd HH:mm:ss'}` and
        // breaks LacerteDSII parsing) is caught here.
        [Fact]
        public void EfStatusJoinSince_Appends_Where_With_DateLiteral()
        {
            var sql = SqlTemplates.EfStatusJoinSince(new System.DateTime(2026, 4, 15));
            Assert.Contains("[DATA1-Client Info]", sql);
            Assert.Contains("left outer join [EFilings]", sql);
            Assert.Contains("left outer join [EFStatuses]", sql);
            Assert.Contains("where s.EFStatusDate >= {d '2026-04-15'}", sql);
            // Projection still matches the canonical 6 columns.
            Assert.Contains("as ClientId", sql);
            Assert.Contains("as EFilingId", sql);
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

        // -------------------------------------------------------------------
        // P14-055: Series column is typed `string` on DetailRow but the SQL
        // template emits an unquoted integer literal so the SQL engine runs
        // a NUMERIC comparison. This test pins the lighter-fix contract:
        // (1) The emitted SQL uses unquoted integer literals — no single
        //     quotes around the value — so an ODBC driver compiling the
        //     statement treats it as int = int (not string = string, which
        //     in lexicographic order would put "10" between "1" and "2").
        // (2) An equivalent in-memory predicate constructed by parsing the
        //     Series strings as ints agrees with the SQL semantics: filtering
        //     for Series=10 against a set containing "0", "1", "2", "10"
        //     returns exactly the "10" row, not "1" or "0" (which a naive
        //     lexicographic / prefix compare would mishandle).
        // -------------------------------------------------------------------

        [Fact]
        public void DetailBySeries_Emits_Unquoted_Integer_For_Numeric_Comparison()
        {
            // The integer literal is bare — no surrounding quotes. That is
            // what forces SQL to run a numeric comparison (the column itself
            // is numeric in the live DBF; the unquoted literal lines up).
            Assert.Equal("select * from [JOHNS-Detail] where Series = 0",  SqlTemplates.DetailBySeries("JOHNS", 0));
            Assert.Equal("select * from [JOHNS-Detail] where Series = 1",  SqlTemplates.DetailBySeries("JOHNS", 1));
            Assert.Equal("select * from [JOHNS-Detail] where Series = 10", SqlTemplates.DetailBySeries("JOHNS", 10));

            // No quoted variant should appear — that would shift the
            // comparison into string-equality territory and `Series = '10'`
            // would lex-order between '1' and '2', not numerically.
            Assert.DoesNotContain("'0'",  SqlTemplates.DetailBySeries("JOHNS", 0));
            Assert.DoesNotContain("'10'", SqlTemplates.DetailBySeries("JOHNS", 10));
        }

        [Fact]
        public void DetailBySeries_NumericFilter_DistinguishesZeroTenAndOne()
        {
            // Build a synthetic detail-row set with neighboring Series values
            // that a string compare would mis-order: lexicographically
            // sorted, "0" < "1" < "10" < "2"; numerically, 0 < 1 < 2 < 10.
            // Series stays `string` per the lighter-fix contract — these
            // strings are guaranteed parseable integers (^\\d+$ enforced at
            // the DetailsTools validator boundary and at the seed fixture).
            var rows = new List<DetailRow>
            {
                new DetailRow("0",  "", "0",  "", "", "", "", "", "", 100.0, "series-zero"),
                new DetailRow("1",  "", "1",  "", "", "", "", "", "", 200.0, "series-one"),
                new DetailRow("2",  "", "2",  "", "", "", "", "", "", 300.0, "series-two"),
                new DetailRow("10", "", "10", "", "", "", "", "", "", 999.0, "series-ten"),
            };

            // Numeric semantics: filter for 10 returns ONLY the "10" row.
            // (A lex/prefix compare against "10" would incorrectly include
            //  "1" if it ever degraded to a LIKE/StartsWith.)
            var ten = rows.Where(r => int.Parse(r.Series) == 10).ToList();
            Assert.Single(ten);
            Assert.Equal("series-ten", ten[0].Description);

            // Filter for 0 returns ONLY the "0" row — not "10" (which starts
            // with "1", but a buggy substring check on "0" could be tricked).
            var zero = rows.Where(r => int.Parse(r.Series) == 0).ToList();
            Assert.Single(zero);
            Assert.Equal("series-zero", zero[0].Description);

            // Filter for 1 returns ONLY the "1" row — not "10".
            var one = rows.Where(r => int.Parse(r.Series) == 1).ToList();
            Assert.Single(one);
            Assert.Equal("series-one", one[0].Description);

            // Sanity: ordering by parsed int gives 0, 1, 2, 10 (NOT the
            // lex order 0, 1, 10, 2 a naive string sort would produce).
            var sorted = rows.OrderBy(r => int.Parse(r.Series)).Select(r => r.Series).ToList();
            Assert.Equal(new[] { "0", "1", "2", "10" }, sorted);

            // And the SQL template that drives the live path emits the
            // same numeric literal that this in-memory predicate uses,
            // so live + sim agree.
            Assert.Equal("select * from [TEST-Detail] where Series = 10", SqlTemplates.DetailBySeries("TEST", 10));
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
