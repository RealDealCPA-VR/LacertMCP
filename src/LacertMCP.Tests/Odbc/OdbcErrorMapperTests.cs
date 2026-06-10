using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using Xunit;

namespace LacertMCP.Tests.Odbc
{
    /// <summary>
    /// Tests for the SQLSTATE → TAPIStatus mapping. We test the pure
    /// <see cref="OdbcErrorMapper.MapSqlState"/> helper rather than fabricating real
    /// <see cref="System.Data.Odbc.OdbcException"/> instances (their constructor is
    /// internal and the .NET ODBC provider doesn't expose a public way to mint one
    /// with a chosen SQLSTATE). The full <see cref="OdbcErrorMapper.Map"/> path is
    /// exercised at runtime against the live driver in Phase 9.
    /// </summary>
    public class OdbcErrorMapperTests
    {
        // -------------------------------------------------------------------
        // 08* — Connection failures → DBFNotAccessable
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("08001")]
        [InlineData("08003")]
        [InlineData("08004")]
        [InlineData("08S01")]
        public void MapSqlState_ConnectionFailures_Map_To_DBFNotAccessable(string sqlState)
        {
            var result = OdbcErrorMapper.MapSqlState(sqlState, "connection failed");
            Assert.Equal(StatusCodes.DBFNotAccessable, result.StatusCode);
            Assert.Equal("DBFNotAccessable", result.StatusName);
        }

        // -------------------------------------------------------------------
        // IM002 — DSN/Driver not found → DBFNotAccessable
        // -------------------------------------------------------------------

        [Fact]
        public void MapSqlState_IM002_Maps_To_DBFNotAccessable()
        {
            var result = OdbcErrorMapper.MapSqlState("IM002", "Data source not found");
            Assert.Equal(StatusCodes.DBFNotAccessable, result.StatusCode);
            Assert.Equal("DBFNotAccessable", result.StatusName);
        }

        // -------------------------------------------------------------------
        // 42S02 — Table not found → DBNotSupported
        // -------------------------------------------------------------------

        [Fact]
        public void MapSqlState_42S02_Maps_To_DBNotSupported()
        {
            var result = OdbcErrorMapper.MapSqlState("42S02", "Table 'foo' not found");
            Assert.Equal(StatusCodes.DBNotSupported, result.StatusCode);
            Assert.Equal("DBNotSupported", result.StatusName);
        }

        // -------------------------------------------------------------------
        // 42S22 — Column not found → NotExposed
        // -------------------------------------------------------------------

        [Fact]
        public void MapSqlState_42S22_Maps_To_NotExposed()
        {
            var result = OdbcErrorMapper.MapSqlState("42S22", "Column 'C1_99' not found");
            Assert.Equal(StatusCodes.NotExposed, result.StatusCode);
            Assert.Equal("NotExposed", result.StatusName);
        }

        // -------------------------------------------------------------------
        // 42000 — Syntax error → generic Error
        // -------------------------------------------------------------------

        [Fact]
        public void MapSqlState_42000_Maps_To_Error()
        {
            var result = OdbcErrorMapper.MapSqlState("42000", "Syntax error near 'select'");
            Assert.Equal(StatusCodes.Error, result.StatusCode);
            Assert.Equal("Error", result.StatusName);
        }

        // -------------------------------------------------------------------
        // HY* / S1* — General driver errors → CodeBaseError
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("HY000")]
        [InlineData("HY001")]
        [InlineData("HY010")]
        public void MapSqlState_HY_Family_Maps_To_CodeBaseError(string sqlState)
        {
            var result = OdbcErrorMapper.MapSqlState(sqlState, "driver-side error");
            Assert.Equal(StatusCodes.CodeBaseError, result.StatusCode);
            Assert.Equal("CodeBaseError", result.StatusName);
        }

        [Theory]
        [InlineData("S1000")]
        [InlineData("S1001")]
        public void MapSqlState_S1_LegacyFamily_Maps_To_CodeBaseError(string sqlState)
        {
            var result = OdbcErrorMapper.MapSqlState(sqlState, "legacy driver-side error");
            Assert.Equal(StatusCodes.CodeBaseError, result.StatusCode);
            Assert.Equal("CodeBaseError", result.StatusName);
        }

        // -------------------------------------------------------------------
        // R6-36 — timeouts + cancel carved out of the HY*/S1* bucket.
        // CodeBaseError's remediation ("run File -> Repair Indexes") is wrong
        // for a slow share and absurd for an intentional cancel.
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("HYT00")] // login timeout
        [InlineData("HYT01")] // connection/query timeout
        [InlineData("hyt01")] // case-insensitive
        [InlineData("S1T00")] // legacy alias of HYT00
        public void MapSqlState_Timeouts_Map_To_DBFNotAccessable_Not_CodeBaseError_R6_36(string sqlState)
        {
            var result = OdbcErrorMapper.MapSqlState(sqlState, "timeout expired");
            Assert.Equal(StatusCodes.DBFNotAccessable, result.StatusCode);
            Assert.Equal("DBFNotAccessable", result.StatusName);
            // Message carries the timeout framing + the original driver text.
            Assert.Contains("timeout", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timeout expired", result.Message);
        }

        [Theory]
        [InlineData("HY008")] // operation canceled
        [InlineData("hy008")]
        [InlineData("S1008")] // legacy alias
        public void MapSqlState_Canceled_Maps_To_GenericError_Not_CodeBaseError_R6_36(string sqlState)
        {
            var result = OdbcErrorMapper.MapSqlState(sqlState, "operation canceled");
            Assert.Equal(StatusCodes.Error, result.StatusCode);
            Assert.Equal("Error", result.StatusName);
            Assert.Contains("canceled", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("HYT00")]
        [InlineData("HYT01")]
        [InlineData("S1T00")]
        [InlineData("HY008")]
        [InlineData("S1008")]
        [InlineData("hyt00")]
        public void IsConnectionFatal_FalseForTimeoutAndCancel_R6_36(string sqlState)
        {
            // A timed-out or canceled query leaves the connection healthy —
            // recycling it just churns the provider pool.
            Assert.False(OdbcErrorMapper.IsConnectionFatal(sqlState));
        }

        [Theory]
        [InlineData("HY000")]
        [InlineData("HY010")]
        [InlineData("S1000")]
        public void IsConnectionFatal_StillTrueForOtherDriverErrors_R6_36(string sqlState)
        {
            // The carve-out must not loosen the rest of the HY*/S1* families.
            Assert.True(OdbcErrorMapper.IsConnectionFatal(sqlState));
        }

        // -------------------------------------------------------------------
        // Unknown / empty → generic Error
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("XX999")]
        [InlineData("ABCDE")]
        [InlineData("")]
        public void MapSqlState_Unknown_Maps_To_Error(string sqlState)
        {
            var result = OdbcErrorMapper.MapSqlState(sqlState, "unknown");
            Assert.Equal(StatusCodes.Error, result.StatusCode);
            Assert.Equal("Error", result.StatusName);
        }

        // -------------------------------------------------------------------
        // Case insensitivity — drivers can return upper or lower SQLSTATEs
        // -------------------------------------------------------------------

        [Fact]
        public void MapSqlState_Is_Case_Insensitive()
        {
            var upper = OdbcErrorMapper.MapSqlState("HY000", "x");
            var lower = OdbcErrorMapper.MapSqlState("hy000", "x");
            var mixed = OdbcErrorMapper.MapSqlState("Hy000", "x");
            Assert.Equal(upper.StatusCode, lower.StatusCode);
            Assert.Equal(upper.StatusCode, mixed.StatusCode);
        }

        [Fact]
        public void MapSqlState_Im002_LowerCase_Still_Maps_To_DBFNotAccessable()
        {
            var result = OdbcErrorMapper.MapSqlState("im002", "Data source not found");
            Assert.Equal(StatusCodes.DBFNotAccessable, result.StatusCode);
        }

        // -------------------------------------------------------------------
        // Message preservation
        // -------------------------------------------------------------------

        [Fact]
        public void MapSqlState_Preserves_OriginalMessage()
        {
            var result = OdbcErrorMapper.MapSqlState("HY000", "very specific driver text");
            Assert.Equal("very specific driver text", result.Message);
        }
    }
}
