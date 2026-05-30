using System;
using System.Runtime.InteropServices;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// Phase 9 task #63 — scaffolding tests for <see cref="Registration"/>.
    ///
    /// <para>
    /// Off-Windows every <see cref="Registration.Register"/> call throws
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/>. On Windows
    /// without the SDK registered the ProgID lookup returns null and we get
    /// the same code (different remediation message).
    /// </para>
    ///
    /// <para>
    /// The actual <c>ITPRegistration.Register</c> round-trip is marked
    /// <c>[Fact(Skip = "Requires real Lacerte SDK")]</c> — exercised only
    /// on the Windows + Lacerte box during manual verification.
    /// </para>
    /// </summary>
    public class RegistrationTests
    {
        [Fact]
        public void Register_NoSdk_ThrowsBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Registration.Register("Lacerte MCP Test", Guid.NewGuid().ToString("D"), 2024));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Fact]
        public void Register_NoSdk_ErrorCarriesPlatformContext()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                Registration.Register("test", Guid.NewGuid().ToString("D"), 2024));
            Assert.NotEmpty(ex.Context);
            // Must include a 'reason' field so the operator knows whether
            // the issue is platform (off-Windows) or installation (SDK missing).
            Assert.Contains("reason", ex.Context.Keys);
        }

        [Fact]
        public void RegisterWithStoredAppId_NoAppId_ThrowsAppNotRegistered()
        {
            // Force the file fallback to a non-existent directory so no AppID
            // can possibly be found, regardless of host.
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            var originalForce = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            try
            {
                Environment.SetEnvironmentVariable("APPDATA",
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "lacerte-bridge-test-noappid-" + Guid.NewGuid().ToString("N")));
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");

                var ex = Assert.Throws<LacerteSDKError>(() =>
                    Registration.RegisterWithStoredAppId("Lacerte MCP Test", 2024));
                // The first failure on a fresh box is "no AppID minted" —
                // operator must run `lacerte-mcp register` to mint one.
                Assert.Equal(SyntheticStatusCodes.AppNotRegistered, ex.StatusCode);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", originalForce);
            }
        }

        [Fact]
        public void GetStoredAppId_NoConfig_ReturnsNull()
        {
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            var originalForce = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            try
            {
                Environment.SetEnvironmentVariable("APPDATA",
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "lacerte-bridge-test-getappid-" + Guid.NewGuid().ToString("N")));
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");

                Assert.Null(Registration.GetStoredAppId());
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", originalForce);
            }
        }

        [Fact]
        public void CredentialTarget_MatchesCliAppIdStore()
        {
            // Sanity: Registration is intentionally duplicating the CredMan
            // target/account constants from LacertMCP.Cli.Storage.AppIdStore
            // (Server can't reference Cli). If the Cli constants ever change,
            // this test catches the drift before it bites at runtime.
            Assert.Equal("lacerte-mcp", Registration.CredentialTarget);
            Assert.Equal("app-id", Registration.CredentialAccount);
            Assert.Equal(LacertMCP.Cli.Storage.AppIdStore.CredentialTarget,
                Registration.CredentialTarget);
            Assert.Equal(LacertMCP.Cli.Storage.AppIdStore.CredentialAccount,
                Registration.CredentialAccount);
        }

        // -----------------------------------------------------------------
        // Live COM round-trip (skipped — requires real SDK)
        // -----------------------------------------------------------------

        [Fact(Skip = "Requires real Lacerte SDK + Windows + interactive console for consent dialog.")]
        public void Live_Register_FreshSession_PopsConsentDialog()
        {
            // On the Windows + Lacerte box: with a freshly minted AppID
            // never seen before, the Intuit consent + sign-in dialogs pop.
            // Operator clicks through, status comes back AccessGranted (201).
            var appId = Guid.NewGuid().ToString("D");
            var status = Registration.Register("Lacerte MCP Manual Test", appId, 2024);
            Assert.Equal(StatusCodes.AccessGranted, status);
        }

        [Fact(Skip = "Requires real Lacerte SDK + Windows + a previously minted+stored AppID.")]
        public void Live_RegisterWithStoredAppId_WithinCacheWindow_SilentlyRefreshes()
        {
            // Within the 24h window the Intuit dialogs do NOT pop — the
            // SDK returns AccessGranted immediately. This is the "warm
            // path" the MCP server expects on every startup after the
            // operator has registered once.
            var status = Registration.RegisterWithStoredAppId("Lacerte MCP Manual Test", 2024);
            Assert.Equal(StatusCodes.AccessGranted, status);
        }
    }
}
