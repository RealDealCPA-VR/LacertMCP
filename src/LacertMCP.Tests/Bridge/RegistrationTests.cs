using System;
using System.IO;
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
    ///
    /// <para>
    /// P17-017: <see cref="Registration.GetStoredAppId"/> duplicates the file/CredMan
    /// resolution logic of <see cref="LacertMCP.Cli.Storage.AppIdStore"/> (Server cannot
    /// reference Cli — Cli already references Server, reversing that would create a
    /// dependency cycle). The duplication risks drift; the P17-017 round-trip tests
    /// below pin parity: a GUID written by <c>AppIdStore.Set</c> in file-backed mode
    /// (FORCE_FILE_STORE=1) MUST be readable by <c>Registration.GetStoredAppId</c>,
    /// and vice versa. The tests share <see cref="LacertMCP.Tests.Cli.CliCollection"/>
    /// with the Cli/AppIdStore tests so they don't race on the APPDATA + FORCE_FILE_STORE
    /// env vars.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.Cli.CliCollection.Name)]
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
        // P17-017 — round-trip parity with LacertMCP.Cli.Storage.AppIdStore
        //
        // The Server's Registration.GetStoredAppId duplicates the file/CredMan
        // resolution logic of AppIdStore. These tests pin the duplication is
        // observationally exact in the file-backed branch (the only branch
        // exercisable off-Windows). Run under CliCollection so APPDATA +
        // LACERTE_MCP_FORCE_FILE_STORE env mutations don't race the AppIdStore
        // tests in parallel.
        // -----------------------------------------------------------------

        [Fact]
        public void GetStoredAppId_FileBackend_ReadsWhatAppIdStoreWrote_P17_017()
        {
            // Round-trip: AppIdStore.Set under FORCE_FILE_STORE=1 must be visible
            // to Registration.GetStoredAppId under the same env. Any divergence
            // in the file path resolution or the JSON shape would break this.
            var origAppData = Environment.GetEnvironmentVariable("APPDATA");
            var origForce = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            var tempAppData = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-p17017-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempAppData);
            try
            {
                Environment.SetEnvironmentVariable("APPDATA", tempAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");

                var id = Guid.NewGuid();
                LacertMCP.Cli.Storage.AppIdStore.Set(id);

                var roundTripped = Registration.GetStoredAppId();
                Assert.NotNull(roundTripped);
                Assert.Equal(id, roundTripped!.Value);

                // Also verify the path Registration reads from matches the path
                // AppIdStore wrote to — defense against the two impls drifting
                // on the APPDATA fallback chain.
                Assert.True(File.Exists(LacertMCP.Cli.Storage.AppIdStore.FallbackConfigPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPDATA", origAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", origForce);
                try { if (Directory.Exists(tempAppData)) Directory.Delete(tempAppData, recursive: true); }
                catch { /* best-effort */ }
            }
        }

        [Fact]
        public void GetStoredAppId_FileBackend_ReturnsNullAfterReset_P17_017()
        {
            // After AppIdStore.Reset clears the file, Registration must also
            // observe "absent" — confirms both sides resolve to the same path
            // and respect the same delete semantics.
            var origAppData = Environment.GetEnvironmentVariable("APPDATA");
            var origForce = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            var tempAppData = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-p17017-reset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempAppData);
            try
            {
                Environment.SetEnvironmentVariable("APPDATA", tempAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");

                LacertMCP.Cli.Storage.AppIdStore.Set(Guid.NewGuid());
                Assert.NotNull(Registration.GetStoredAppId());

                LacertMCP.Cli.Storage.AppIdStore.Reset();
                Assert.Null(Registration.GetStoredAppId());
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPDATA", origAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", origForce);
                try { if (Directory.Exists(tempAppData)) Directory.Delete(tempAppData, recursive: true); }
                catch { /* best-effort */ }
            }
        }

        [Fact]
        public void GetStoredAppId_FileBackend_ReturnsNullWhenStoredGuidUnparseable_P17_017()
        {
            // Both impls must treat a corrupt AppId (non-GUID string) as Absent
            // rather than throwing. AppIdStore.ReadFromFile catches the parse
            // failure and returns null; Registration.ReadFromFile must do the
            // same so a re-mint via `register --reset` is the unambiguous fix.
            var origAppData = Environment.GetEnvironmentVariable("APPDATA");
            var origForce = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            var tempAppData = Path.Combine(Path.GetTempPath(),
                "lacerte-mcp-p17017-corrupt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempAppData);
            try
            {
                Environment.SetEnvironmentVariable("APPDATA", tempAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");

                var path = LacertMCP.Cli.Storage.AppIdStore.FallbackConfigPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, "{\"AppId\":\"not-a-guid\"}");

                Assert.Null(Registration.GetStoredAppId());
                Assert.Null(LacertMCP.Cli.Storage.AppIdStore.Get());
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPDATA", origAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", origForce);
                try { if (Directory.Exists(tempAppData)) Directory.Delete(tempAppData, recursive: true); }
                catch { /* best-effort */ }
            }
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
