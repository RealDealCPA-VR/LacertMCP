using System;
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
            // target constant from LacertMCP.Cli.Storage.AppIdStore (Server can't
            // reference Cli). If the Cli constant ever drifts, this test catches
            // it before it bites at runtime. Registration only reads creds, so
            // only the target needs parity — the account half lives on the Cli
            // writer side.
            Assert.Equal("lacerte-mcp", Registration.CredentialTarget);
            Assert.Equal(LacertMCP.Cli.Storage.AppIdStore.CredentialTarget,
                Registration.CredentialTarget);
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
    }
}
