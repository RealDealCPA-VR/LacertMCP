using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// Phase 9 task #64 — scaffolding tests for <see cref="Writer"/>.
    ///
    /// <para>
    /// Three entry points: pre-TY21 <see cref="Writer.WriteToDBFDatabase"/>,
    /// TY21+ <see cref="Writer.UpdateTaxReturn"/>, and detail-file
    /// <see cref="Writer.WriteDetail"/>. All three throw
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/> when the SDK
    /// isn't reachable. The real COM round-trip is verified only on the
    /// Windows + Lacerte box.
    /// </para>
    /// </summary>
    public class WriterTests
    {
        [Fact]
        public void WriteToDBFDatabase_NoSdk_ThrowsBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteToDBFDatabase(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    dbf: "DATA1I20.DBF",
                    rowNum: 1,
                    field: "C1_5",
                    value: "test address"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void UpdateTaxReturn_NoSdk_ThrowsBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    dbf: "DATA1I24.DBF",
                    clientNumber: "JOHNS",
                    field: "C1_5",
                    value: "test address",
                    subClientID: null,
                    stateAbbrev: null));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void UpdateTaxReturn_WithSubClientAndState_ShapeForwarded()
        {
            // Even with the optional args populated, we still throw
            // BridgeUnavailable on no-SDK — but the shape (subClientID +
            // stateAbbrev) is accepted by the method signature.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.UpdateTaxReturn(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    dbf: "DATA4I24.DBF",
                    clientNumber: "JOHNS",
                    field: "C4_15",
                    value: "test",
                    subClientID: 2,
                    stateAbbrev: "CA"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_NoSdk_ThrowsBridgeUnavailable()
        {
            var row = new DetailRow(
                Series: "S1",
                Prefix: "",
                Code: "5",
                Suffix: "",
                StateAbbr: "",
                CityAbbr: "",
                Source: "",
                TSJ: "T",
                ImportSrc: "",
                Amount: 1234.56,
                Description: "scaffolding test row");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Writer.WriteDetail(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    clientNumber: "JOHNS",
                    row: row));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_NullRow_ThrowsArgumentNullException()
        {
            // Argument validation runs BEFORE the SDK acquisition gate, so
            // we get ArgumentNullException (not BridgeUnavailable). This
            // matches every other .NET API's null-check contract.
            Assert.Throws<System.ArgumentNullException>(() =>
                Writer.WriteDetail(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    clientNumber: "JOHNS",
                    row: null!));
        }

        // -----------------------------------------------------------------
        // Live COM round-trip (skipped — requires real SDK)
        // -----------------------------------------------------------------

        [Fact(Skip = "Requires real Lacerte SDK + registered AppID + writable TY20 client.")]
        public void Live_WriteToDBFDatabase_HappyPath_ReturnsSuccess()
        {
            var appId = Registration.GetStoredAppId()!.Value.ToString("D");
            var status = Writer.WriteToDBFDatabase(
                appId,
                @"C:\Lacerte\20data",
                "DATA1I20.DBF",
                rowNum: 1,
                field: "C1_5",
                value: "MCP scaffolding test write");
            Assert.Equal(StatusCodes.Success, status);
        }

        [Fact(Skip = "Requires real Lacerte SDK + registered AppID + writable TY24 client (JOHNS).")]
        public void Live_UpdateTaxReturn_HappyPath_ReturnsSuccess()
        {
            var appId = Registration.GetStoredAppId()!.Value.ToString("D");
            var status = Writer.UpdateTaxReturn(
                appId,
                @"C:\Lacerte\24data",
                "DATA1I24.DBF",
                clientNumber: "JOHNS",
                field: "C1_5",
                value: "MCP scaffolding test write (TY24)",
                subClientID: null,
                stateAbbrev: null);
            Assert.Equal(StatusCodes.Success, status);
        }

        [Fact(Skip = "Requires real Lacerte SDK + writable TY24 client + a known-good detail-row shape.")]
        public void Live_WriteDetail_HappyPath_ReturnsSuccess()
        {
            var appId = Registration.GetStoredAppId()!.Value.ToString("D");
            var row = new DetailRow(
                Series: "Income",
                Prefix: "",
                Code: "1",
                Suffix: "",
                StateAbbr: "",
                CityAbbr: "",
                Source: "",
                TSJ: "T",
                ImportSrc: "MCP",
                Amount: 100.00,
                Description: "manual verification write");
            var status = Writer.WriteDetail(appId, @"C:\Lacerte\24data", "JOHNS", row);
            Assert.Equal(StatusCodes.Success, status);
        }
    }
}
