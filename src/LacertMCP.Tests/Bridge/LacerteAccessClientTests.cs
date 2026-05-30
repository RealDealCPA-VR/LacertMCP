using System;
using System.Collections.Generic;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// Phase 9 task #62 — scaffolding tests for <see cref="LacerteAccessClient"/>.
    ///
    /// <para>
    /// The unit boundary here is "what happens when the COM library is not
    /// available." On non-Windows OR on Windows without the SDK registered,
    /// every wire call must throw <see cref="LacerteSDKError"/> with the
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/> code (9201).
    /// Disposal must be safe and idempotent in that state too.
    /// </para>
    ///
    /// <para>
    /// The actual COM round-trip is marked
    /// <c>[Fact(Skip = "Requires real Lacerte SDK")]</c> — those tests run
    /// only on a Windows + Lacerte box during the manual verification
    /// session called out in <c>REGRESSION_CHECKLIST.md</c>.
    /// </para>
    /// </summary>
    public class LacerteAccessClientTests
    {
        // -----------------------------------------------------------------
        // Construction / IsConnected
        // -----------------------------------------------------------------

        [Fact]
        public void Ctor_NoSdk_LeavesDisconnected()
        {
            using var client = new LacerteAccessClient();
            // Off-Windows always disconnected. On Windows without SDK
            // registered the ProgID lookup returns null → also disconnected.
            // Either way the test asserts the same: no COM ref.
            Assert.False(client.IsConnected,
                "Off-Windows OR Windows-without-SDK must leave the client disconnected.");
        }

        // -----------------------------------------------------------------
        // Every wire call → BridgeUnavailable when disconnected
        // -----------------------------------------------------------------

        [Fact]
        public void Register_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.Register("test-app", Guid.NewGuid().ToString("D"), 2024));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void CanUseNewApis_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.CanUseNewApis("appid", 2024, 'I'));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void ListClients_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.ListClients(@"C:\Lacerte\24data", 2024, 'I'));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void ReadClient_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.ReadClient(@"C:\Lacerte\24data", 2024, 'I', "JOHNS"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void ReadTable_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.ReadTable(@"C:\Lacerte\24data", 2024, 'I', 1));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void WriteField_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.WriteField("appid", @"C:\Lacerte\24data", 2020, 'I', 1, 1, "C1_5", "test"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void UpdateTaxReturn_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.UpdateTaxReturn("appid", @"C:\Lacerte\24data", 2024, 'I',
                    1, "JOHNS", "C1_5", "test", null, null));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void WriteDetail_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var row = new DetailRow(
                Series: "S",
                Prefix: "",
                Code: "1",
                Suffix: "",
                StateAbbr: "",
                CityAbbr: "",
                Source: "",
                TSJ: "",
                ImportSrc: "",
                Amount: 100.0,
                Description: "test");
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.WriteDetail("appid", @"C:\Lacerte\24data", 2024, 'I', "JOHNS", row));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void CreateClient_NoSdk_ThrowsBridgeUnavailable()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.CreateClient("appid", @"C:\Lacerte\24data", 2024, 'I',
                    "NEW01", "Individual", "Jane", "Doe", "jane@example.com"));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // Disposal
        // -----------------------------------------------------------------

        [Fact]
        public void Dispose_IsIdempotent_NoSdk()
        {
            var client = new LacerteAccessClient();
            client.Dispose();
            client.Dispose(); // second call must be a no-op
            // After Dispose() further wire calls throw ObjectDisposedException,
            // which is the standard .NET contract.
            Assert.Throws<ObjectDisposedException>(() =>
                client.CanUseNewApis("appid", 2024, 'I'));
        }

        [Fact]
        public void BridgeUnavailable_Error_CarriesContext()
        {
            using var client = new LacerteAccessClient();
            var ex = Assert.Throws<LacerteSDKError>(() =>
                client.Register("test-app", Guid.NewGuid().ToString("D"), 2024));
            // The error should carry a 'reason' field so the operator gets
            // a useful remediation message — not a bare 9201.
            Assert.NotEmpty(ex.Context);
            Assert.Contains("reason", ex.Context.Keys);
        }

        // -----------------------------------------------------------------
        // Live COM round-trip (skipped — requires real SDK)
        // -----------------------------------------------------------------

        [Fact(Skip = "Requires real Lacerte SDK + Windows + registered AppID — exercise on the Windows + Lacerte box during manual verification.")]
        public void Live_Register_ReturnsAccessGrantedOrNotSignedIn()
        {
            using var client = new LacerteAccessClient();
            var appId = Guid.NewGuid().ToString("D");
            var status = client.Register("Lacerte MCP Test", appId, 2024);
            Assert.True(status == StatusCodes.AccessGranted || status == StatusCodes.NotSignedIn,
                $"Expected AccessGranted (201) or NotSignedIn (203), got {status}.");
        }

        [Fact(Skip = "Requires real Lacerte SDK + Windows.")]
        public void Live_CanUseNewApis_TY24Individual_ReturnsTrue()
        {
            using var client = new LacerteAccessClient();
            var appId = LacertMCP.Server.Bridge.Registration.GetStoredAppId()?.ToString("D") ?? string.Empty;
            Assert.True(client.CanUseNewApis(appId, 2024, 'I'));
        }
    }
}
