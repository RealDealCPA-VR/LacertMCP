using System;
using System.IO;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Storage;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// R6-05 — pins the WIRED register/login consent-ceremony exit-code contract
    /// (replaces the retired <c>RegisterLoginStubExitCodeTests</c> that pinned the
    /// pre-wiring Phase-9 stub's exit 3).
    ///
    /// <para>
    /// The ceremony itself (COM <c>ITPRegistration.Register</c> + interactive Intuit
    /// dialogs) can't run under xUnit, so these tests drive the injectable-ceremony
    /// seam on <see cref="RegisterCommand.Run(string,int,bool,Func{string,string,int,int})"/>
    /// / <see cref="LoginCommand.Run(string,int,Func{string,string,int,int})"/> — the
    /// same seam pattern DoctorCommand uses for probe-result injection. Matrix:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>0 — AccessGranted (201), or off-Windows (mint-only, ceremony skipped).</description></item>
    ///   <item><description>1 — ceremony ran but denied/failed (ConsentDenied, NotSignedIn, throw).</description></item>
    ///   <item><description>2 — PARTIAL: Windows but SDK COM surface unavailable (BridgeUnavailable 9201).</description></item>
    ///   <item><description>3 — RETIRED. The Phase-9 "[partial] ... task #63" stub exit no longer exists.</description></item>
    /// </list>
    /// </summary>
    [Collection(CliCollection.Name)]
    public class RegisterLoginCeremonyExitCodeTests : IDisposable
    {
        private readonly string _tempAppData;
        private readonly string? _originalAppData;
        private readonly string? _originalForceFlag;

        public RegisterLoginCeremonyExitCodeTests()
        {
            _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            _originalForceFlag = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            _tempAppData = Path.Combine(Path.GetTempPath(), "lacerte-mcp-ceremony-exit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempAppData);
            Environment.SetEnvironmentVariable("APPDATA", _tempAppData);
            Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");
        }

        public void Dispose()
        {
            try { AppIdStore.Reset(); } catch { /* best-effort */ }
            Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
            Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", _originalForceFlag);
            try { if (Directory.Exists(_tempAppData)) Directory.Delete(_tempAppData, recursive: true); } catch { }
        }

        private static bool OnWindows =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// Runs <paramref name="action"/> with Console.Out/Error captured; restores
        /// the originals in a finally. CLI commands write to Console directly.
        /// </summary>
        private static (int Exit, string Stdout, string Stderr) Capture(Func<int> action)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exit = action();
                return (exit, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }

        /// <summary>A ceremony stand-in that must never be reached (off-Windows paths).</summary>
        private static Func<string, string, int, int> MustNotBeCalled =>
            (_, _, _) => throw new InvalidOperationException("ceremony delegate must not be invoked on this path");

        // =====================================================================
        // register
        // =====================================================================

        [Fact]
        public void Register_OffWindows_Exit0_GracefulSkip_CeremonyNotInvoked()
        {
            if (OnWindows) return; // Cross-platform branch test.

            var r = Capture(() => RegisterCommand.Run("Test App", DateTime.UtcNow.Year - 1, reset: true, MustNotBeCalled));

            // Off-Windows keeps exit 0 — preserves dev-smoke-loop convenience (P15-028).
            Assert.Equal(0, r.Exit);
            Assert.Contains("[skipped]", r.Stdout);
            AssertNoStaleStubText(r.Stdout + r.Stderr);
        }

        [Fact]
        public void Register_OnWindows_AccessGranted_Exit0_PassesMintedAppIdToCeremony()
        {
            if (!OnWindows) return; // Ceremony branch is Win32NT-only.

            string? seenAppName = null, seenAppId = null;
            int seenTaxYear = 0, calls = 0;
            var r = Capture(() => RegisterCommand.Run("Test App", 2025, reset: true, (name, id, year) =>
            {
                calls++;
                seenAppName = name;
                seenAppId = id;
                seenTaxYear = year;
                return StatusCodes.AccessGranted;
            }));

            Assert.Equal(0, r.Exit);
            Assert.Equal(1, calls);
            Assert.Equal("Test App", seenAppName);
            Assert.Equal(2025, seenTaxYear);
            // The freshly-minted AppID must be the one handed to the ceremony, in "D" format.
            Assert.Equal(AppIdStore.Get()!.Value.ToString("D"), seenAppId);
            Assert.Contains("AccessGranted", r.Stdout);
            AssertNoStaleStubText(r.Stdout + r.Stderr);
        }

        [Theory]
        [InlineData(StatusCodes.ConsentDenied, "ConsentDenied")]
        [InlineData(StatusCodes.NotSignedIn, "NotSignedIn")]
        public void Register_OnWindows_NonSuccessStatus_Exit1_ReportsCatalogNameOnStderr(int status, string expectedName)
        {
            if (!OnWindows) return;

            var r = Capture(() => RegisterCommand.Run("Test App", 2025, reset: true, (_, _, _) => status));

            Assert.Equal(1, r.Exit);
            Assert.Contains(expectedName, r.Stderr);
            // Failure diagnostics stay on stderr; the minted-GUID stdout block stays clean.
            Assert.DoesNotContain(expectedName, r.Stdout);
        }

        [Fact]
        public void Register_OnWindows_BridgeUnavailable_Exit2_Partial_PointsAtDoctor()
        {
            if (!OnWindows) return;

            var r = Capture(() => RegisterCommand.Run("Test App", 2025, reset: true,
                (_, _, _) => throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable)));

            // PARTIAL bucket (2) — mirrors doctor's partial convention: AppID persisted,
            // ceremony impossible until the SDK is installed.
            Assert.Equal(2, r.Exit);
            Assert.Contains("PARTIAL", r.Stderr);
            Assert.Contains("doctor", r.Stderr);
            AssertNoStaleStubText(r.Stdout + r.Stderr);
            // AppID survived the failed ceremony — operator does not have to re-mint.
            Assert.NotNull(AppIdStore.Get());
        }

        [Fact]
        public void Register_OnWindows_UnexpectedException_Exit1()
        {
            if (!OnWindows) return;

            var r = Capture(() => RegisterCommand.Run("Test App", 2025, reset: true,
                (_, _, _) => throw new InvalidOperationException("boom-unexpected")));

            Assert.Equal(1, r.Exit);
            Assert.Contains("boom-unexpected", r.Stderr);
        }

        /// <summary>
        /// R6-05 closure pin: the pre-wiring stub strings ("[partial]", "Phase 9 task #63")
        /// and the stub-only exit 3 must never come back on any register path.
        /// </summary>
        [Fact]
        public void Register_Output_NeverContains_StalePhase9StubText_AndNeverExits3()
        {
            // On Windows the fake ceremony fires; off-Windows the skip branch fires —
            // either way the stub text must be gone and exit must not be 3.
            var r = Capture(() => RegisterCommand.Run("Test App", 2025, reset: true,
                (_, _, _) => StatusCodes.AccessGranted));

            Assert.NotEqual(3, r.Exit);
            AssertNoStaleStubText(r.Stdout + r.Stderr);
        }

        [Fact]
        public void Register_OnWindows_Reset_MintsFreshGuid_ReRunWithoutReset_ReusesIt()
        {
            if (!OnWindows) return;

            string? firstId = null, secondId = null, thirdId = null;

            Capture(() => RegisterCommand.Run("Test App", 2025, reset: true, (_, id, _) => { firstId = id; return StatusCodes.AccessGranted; }));
            Capture(() => RegisterCommand.Run("Test App", 2025, reset: true, (_, id, _) => { secondId = id; return StatusCodes.AccessGranted; }));
            var reuse = Capture(() => RegisterCommand.Run("Test App", 2025, reset: false, (_, id, _) => { thirdId = id; return StatusCodes.AccessGranted; }));

            // --reset mints a fresh GUID each time; plain re-run re-uses the stored one
            // but STILL runs the ceremony (that's the whole point of the R6-05 wiring).
            Assert.NotNull(firstId);
            Assert.NotEqual(firstId, secondId);
            Assert.Equal(secondId, thirdId);
            Assert.Contains("re-using existing GUID", reuse.Stdout);
            Assert.Equal(0, reuse.Exit);
        }

        // =====================================================================
        // login
        // =====================================================================

        [Fact]
        public void Login_NoAppId_Exit1_CeremonyNotInvoked()
        {
            AppIdStore.Reset();

            var r = Capture(() => LoginCommand.Run("Test App", 2025, MustNotBeCalled));

            Assert.Equal(1, r.Exit);
            Assert.Contains("No AppID found", r.Stderr);
        }

        [Fact]
        public void Login_OffWindows_Exit0_GracefulSkip()
        {
            if (OnWindows) return;

            AppIdStore.Set(Guid.NewGuid());
            var r = Capture(() => LoginCommand.Run("Test App", 2025, MustNotBeCalled));

            Assert.Equal(0, r.Exit);
            Assert.Contains("[skipped]", r.Stdout);
            AssertNoStaleStubText(r.Stdout + r.Stderr);
        }

        [Fact]
        public void Login_OnWindows_AccessGranted_Exit0_UsesStoredAppId()
        {
            if (!OnWindows) return;

            var stored = Guid.NewGuid();
            AppIdStore.Set(stored);

            string? seenAppId = null;
            var r = Capture(() => LoginCommand.Run("Test App", 2025, (_, id, _) => { seenAppId = id; return StatusCodes.AccessGranted; }));

            Assert.Equal(0, r.Exit);
            Assert.Equal(stored.ToString("D"), seenAppId);
            Assert.Contains("AccessGranted", r.Stdout);
        }

        [Fact]
        public void Login_OnWindows_NotSignedIn_Exit1_ReportsLoginRefreshFailure()
        {
            if (!OnWindows) return;

            AppIdStore.Set(Guid.NewGuid());
            var r = Capture(() => LoginCommand.Run("Test App", 2025, (_, _, _) => StatusCodes.NotSignedIn));

            Assert.Equal(1, r.Exit);
            Assert.Contains("NotSignedIn", r.Stderr);
            Assert.Contains("login refresh", r.Stderr);
        }

        /// <summary>
        /// P16-004 carry-over: the SDK-unavailable stderr must still reference the real
        /// proactive-expiry code LoginExpired (9007), never the retired TicketExpired/9103.
        /// </summary>
        [Fact]
        public void Login_OnWindows_BridgeUnavailable_Exit2_References_LoginExpired_9007()
        {
            if (!OnWindows) return;

            AppIdStore.Set(Guid.NewGuid());
            var r = Capture(() => LoginCommand.Run("Test App", 2025,
                (_, _, _) => throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable)));

            Assert.Equal(2, r.Exit);
            Assert.Contains("PARTIAL", r.Stderr);
            Assert.Contains("LoginExpired", r.Stderr);
            Assert.Contains("9007", r.Stderr);
            Assert.DoesNotContain("TicketExpired", r.Stderr);
            Assert.DoesNotContain("9103", r.Stderr);
        }

        [Fact]
        public void Login_Output_NeverContains_StalePhase9StubText_AndNeverExits3()
        {
            AppIdStore.Set(Guid.NewGuid());
            var r = Capture(() => LoginCommand.Run("Test App", 2025,
                (_, _, _) => StatusCodes.AccessGranted));

            Assert.NotEqual(3, r.Exit);
            AssertNoStaleStubText(r.Stdout + r.Stderr);
        }

        // =====================================================================
        // shared pin
        // =====================================================================

        private static void AssertNoStaleStubText(string combinedOutput)
        {
            Assert.DoesNotContain("Phase 9 task #63", combinedOutput);
            Assert.DoesNotContain("[partial]", combinedOutput);
            Assert.DoesNotContain("not yet wired", combinedOutput);
        }
    }
}
