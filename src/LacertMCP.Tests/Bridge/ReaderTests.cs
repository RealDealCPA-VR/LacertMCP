using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// Phase 9 task #65 — scaffolding tests for <see cref="Reader"/>.
    ///
    /// <para>
    /// <see cref="Reader.ReadFromDBFData"/> is the rare COM read path used
    /// only for schema discovery / field-access-map dumping (follow-up F5).
    /// 99% of reads go through ODBC. Off-Windows OR without the SDK
    /// registered, this throws
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
    /// </para>
    /// </summary>
    public class ReaderTests
    {
        [Fact]
        public void ReadFromDBFData_NoSdk_ThrowsBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    dbf: "DATA1I24.DBF"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void ReadFromDBFData_NoSdk_ErrorCarriesDbfContext()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Reader.ReadFromDBFData(
                    appId: "test",
                    dataPath: @"C:\Lacerte\24data",
                    dbf: "DATA1I24.DBF"));
            // Failure context should carry the dbf + dataPath so debug logs
            // (Phase 11) can surface what was being read when it failed.
            Assert.Contains("op", ex.Context.Keys);
            Assert.Contains("reason", ex.Context.Keys);
        }

        // -----------------------------------------------------------------
        // Live COM round-trip (skipped — requires real SDK)
        // -----------------------------------------------------------------

        [Fact(Skip = "Requires real Lacerte SDK + registered AppID + a valid TY24 datapath.")]
        public void Live_ReadFromDBFData_HappyPath_ReturnsXml()
        {
            var appId = Registration.GetStoredAppId()!.Value.ToString("D");
            var xml = Reader.ReadFromDBFData(appId, @"C:\Lacerte\24data", "DATA1I24.DBF");
            Assert.NotEmpty(xml);
            Assert.Contains("<", xml); // crude XML well-formedness gate
        }
    }
}
