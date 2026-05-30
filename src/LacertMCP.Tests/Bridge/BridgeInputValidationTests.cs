using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// P14-050 — bridge-boundary defense-in-depth validation tests.
    ///
    /// <para>
    /// On this off-Windows test box, every Writer/Reader call ultimately
    /// throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/> (9201)
    /// because the COM ProgID lookup fails. The whole POINT of these tests
    /// is to assert that input-validation fires BEFORE that BridgeUnavailable
    /// throw — i.e. that the validator catches bad args even on a box where
    /// the COM bridge is cold. A test that asserts BridgeUnavailable for a
    /// known-bad input is a regression test (the validator was bypassed).
    /// </para>
    /// </summary>
    public class BridgeInputValidationTests
    {
        // Realistic-shape inputs so only the field-under-test triggers a
        // rejection; everything else passes the validator.
        private const string GoodAppId = "12345678-1234-1234-1234-123456789012";
        private const string GoodDataPath = @"C:\Lacerte\24data";
        private const string GoodDbf = "DATA1I24.DBF";
        private const string GoodClient = "JOHNS";
        private const string GoodField = "C1_15";

        // -----------------------------------------------------------------
        // WriteToDBFDatabase — pre-TY21 path
        // -----------------------------------------------------------------

        [Fact]
        public void WriteToDBFDatabase_GoodInputs_ReachesBridgeUnavailable()
        {
            // Sanity guard: with all-good inputs the call must reach the
            // COM acquisition step and throw BridgeUnavailable. If this
            // test ever throws a validation error we've over-tightened a
            // regex.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: 1, field: GoodField, value: "ok"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Theory]
        [InlineData("DATA1I24.DBF;DROP")]              // semicolon
        [InlineData("DATA1{I24}.DBF")]                 // curly braces
        [InlineData("DATA1I24.DBF`")]                  // backtick
        [InlineData("DATA1I24")]                       // no extension
        [InlineData("")]                               // empty
        [InlineData("DATA1I24.dbf")]                   // lowercase
        [InlineData("DATAVERYLONGNAMETOOLONG.DBF")]    // length blowout
        public void WriteToDBFDatabase_BadDbfName_ThrowsBeforeBridge(string badDbf)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, badDbf, rowNum: 1, field: GoodField, value: "ok"));
            // The whole point: must NOT be BridgeUnavailable — validation
            // ran first.
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.DBNotSupported, ex.StatusCode);
            Assert.Equal("dbf", ex.Context["field"]);
        }

        [Theory]
        [InlineData("C1_5;DROP")]
        [InlineData("C1_5{X}")]
        [InlineData("C1_5`")]
        [InlineData("")]
        public void WriteToDBFDatabase_BadField_ThrowsBeforeBridge(string badField)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: 1, field: badField, value: "ok"));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.FieldNotSupported, ex.StatusCode);
        }

        [Fact]
        public void WriteToDBFDatabase_NullByteInValue_ThrowsBeforeBridge()
        {
            // The NUL-in-value scenario is the canonical motivation for
            // P14-050: COM marshallers C-string-truncate at the first NUL,
            // so "OK\0; DROP TABLE" would silently become "OK" in the DBF.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: 1, field: GoodField,
                    value: "OK\0; DROP TABLE"));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public void WriteToDBFDatabase_OversizeValue_ThrowsBeforeBridge()
        {
            // 256-char string blows the 255-char cap.
            var oversize = new string('a', 256);
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: 1, field: GoodField,
                    value: oversize));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public void WriteToDBFDatabase_NegativeRowNum_ThrowsBeforeBridge()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: -1, field: GoodField, value: "ok"));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.OutOfIndex, ex.StatusCode);
        }

        [Theory]
        [InlineData("")]                                    // empty
        [InlineData("path\0with\0nul")]                     // NUL
        [InlineData("path\rwith\nCRLF")]                    // CR/LF
        public void WriteToDBFDatabase_BadDataPath_ThrowsBeforeBridge(string badPath)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, badPath, GoodDbf, rowNum: 1, field: GoodField, value: "ok"));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.InvalidDataPath, ex.StatusCode);
        }

        [Theory]
        [InlineData("app{id")]
        [InlineData("app}id")]
        [InlineData("app;id")]
        [InlineData("app`id")]
        [InlineData("app\0id")]
        [InlineData("app\"id")]
        [InlineData("app'id")]
        public void WriteToDBFDatabase_BadAppId_ThrowsBeforeBridge(string badAppId)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    badAppId, GoodDataPath, GoodDbf, rowNum: 1, field: GoodField, value: "ok"));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.AppNotRegistered, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // UpdateTaxReturn — TY21+ path
        // -----------------------------------------------------------------

        [Fact]
        public void UpdateTaxReturn_GoodInputs_ReachesBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    GoodAppId, GoodDataPath, GoodDbf, GoodClient, GoodField,
                    value: "ok", subClientID: null, stateAbbrev: null));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Theory]
        [InlineData("JOHNS;DROP")]
        [InlineData("JOHN{S}")]
        [InlineData("JOHN`")]
        [InlineData("johns")]                            // lowercase
        [InlineData("JOHNSMITHTOO")]                     // > 8 chars
        [InlineData("")]                                 // empty
        public void UpdateTaxReturn_BadClientNumber_ThrowsBeforeBridge(string badClient)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    GoodAppId, GoodDataPath, GoodDbf, badClient, GoodField,
                    value: "ok", subClientID: null, stateAbbrev: null));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        [Theory]
        [InlineData("Ca")]                               // mixed case
        [InlineData("california")]                       // too long
        [InlineData("C")]                                // too short
        [InlineData("CA;X")]
        public void UpdateTaxReturn_BadStateAbbrev_ThrowsBeforeBridge(string badState)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    GoodAppId, GoodDataPath, GoodDbf, GoodClient, GoodField,
                    value: "ok", subClientID: 1, stateAbbrev: badState));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.InValidStateAbbrev, ex.StatusCode);
        }

        [Fact]
        public void UpdateTaxReturn_NegativeSubClientID_ThrowsBeforeBridge()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    GoodAppId, GoodDataPath, GoodDbf, GoodClient, GoodField,
                    value: "ok", subClientID: -1, stateAbbrev: "CA"));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.InValidSubClientID, ex.StatusCode);
        }

        [Fact]
        public void UpdateTaxReturn_NullByteInValue_ThrowsBeforeBridge()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    GoodAppId, GoodDataPath, GoodDbf, GoodClient, GoodField,
                    value: "data\0evil", subClientID: null, stateAbbrev: null));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public void UpdateTaxReturn_EmptyStateAbbrev_PassesValidation()
        {
            // Empty stateAbbrev is the common shape for non-state writes;
            // must reach the bridge (BridgeUnavailable on this box).
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    GoodAppId, GoodDataPath, GoodDbf, GoodClient, GoodField,
                    value: "ok", subClientID: null, stateAbbrev: ""));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // WriteDetail — detail file path
        // -----------------------------------------------------------------

        [Fact]
        public void WriteDetail_GoodInputs_ReachesBridgeUnavailable()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "CA", CityAbbr: "", Source: "W2", TSJ: "T",
                ImportSrc: "", Amount: 1234.56, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_NullByteInDescription_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0,
                Description: "innocent\0; SELECT *");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
            Assert.Equal("row.Description", ex.Context["field"]);
        }

        [Fact]
        public void WriteDetail_OversizeDescription_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0,
                Description: new string('x', 256));
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
            Assert.Equal("row.Description", ex.Context["field"]);
        }

        [Fact]
        public void WriteDetail_OversizePrefix_ThrowsBeforeBridge()
        {
            // Prefix cap is the tighter 50-char ShortStringMaxLength.
            var row = new DetailRow(
                Series: "Wages", Prefix: new string('x', 51), Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
            Assert.Equal("row.Prefix", ex.Context["field"]);
        }

        [Fact]
        public void WriteDetail_NonNumericCode_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1; DROP", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
            Assert.Equal("row.Code", ex.Context["field"]);
        }

        [Fact]
        public void WriteDetail_BadStateAbbrInRow_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "California", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.InValidStateAbbrev, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_NaNAmount_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: double.NaN, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
            Assert.Equal("row.Amount", ex.Context["field"]);
        }

        [Fact]
        public void WriteDetail_InfinityAmount_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: double.PositiveInfinity, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_BadClientNumber_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, GoodDataPath, "BAD;CLIENT", row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_BadDataPath_ThrowsBeforeBridge()
        {
            var row = new DetailRow(
                Series: "Wages", Prefix: "", Code: "1", Suffix: "",
                StateAbbr: "", CityAbbr: "", Source: "", TSJ: "",
                ImportSrc: "", Amount: 100.0, Description: "ok");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(GoodAppId, "path\0nul", GoodClient, row));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.InvalidDataPath, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // Reader.ReadFromDBFData
        // -----------------------------------------------------------------

        [Fact]
        public void ReadFromDBFData_GoodInputs_ReachesBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(GoodAppId, GoodDataPath, GoodDbf));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void ReadFromDBFData_EmptyAppId_AllowedReachesBridgeUnavailable()
        {
            // Reader cold-fallback path passes appId="" — must NOT trip the
            // appId validator (Reader has callers that don't have an appId).
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(string.Empty, GoodDataPath, GoodDbf));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Theory]
        [InlineData("DATA1I24")]                         // no extension
        [InlineData("DATA1{X}.DBF")]
        [InlineData("DATA1;X.DBF")]
        [InlineData("")]
        public void ReadFromDBFData_BadDbfName_ThrowsBeforeBridge(string badDbf)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(GoodAppId, GoodDataPath, badDbf));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(StatusCodes.DBNotSupported, ex.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("path\0nul")]
        [InlineData("path\nLF")]
        public void ReadFromDBFData_BadDataPath_ThrowsBeforeBridge(string badPath)
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(GoodAppId, badPath, GoodDbf));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.InvalidDataPath, ex.StatusCode);
        }

        [Fact]
        public void ReadFromDBFData_BadAppId_ThrowsBeforeBridge()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData("bad{appid}", GoodDataPath, GoodDbf));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.AppNotRegistered, ex.StatusCode);
        }

        [Fact]
        public void ReadFromDBFData_OversizeDataPath_ThrowsBeforeBridge()
        {
            // Build a path that exceeds the DataPathMaxLength.
            var oversize = @"C:\" + new string('a', 300);
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(GoodAppId, oversize, GoodDbf));
            Assert.NotEqual(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.Equal(SyntheticStatusCodes.InvalidDataPath, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // Non-string values are pass-through at the bridge boundary
        // (type-whitelisting is the session manager's job per
        // refinedRecommendation step 2 — bridge only sees string/number).
        // -----------------------------------------------------------------

        [Fact]
        public void WriteToDBFDatabase_NumericValue_PassesValidation()
        {
            // double, int, long, bool, etc. pass-through to the COM
            // marshaller as-is. Validation only inspects string values.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: 1, field: GoodField,
                    value: 1234.56));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void WriteToDBFDatabase_NullValue_PassesValidation()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    GoodAppId, GoodDataPath, GoodDbf, rowNum: 1, field: GoodField,
                    value: null));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }
    }
}
