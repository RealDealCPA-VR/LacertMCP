using System;
using System.IO;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Storage;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// P15-028 — pins the register/login Phase-9 stub exit-code contract.
    ///
    /// <para>
    /// On Windows the stubs return exit 3 ("partial — AppID minted but Intuit consent
    /// ceremony not wired") so wrapping cron / install scripts can distinguish "fully
    /// complete" from "register succeeded but the ticket will never refresh." Off-Windows
    /// the stubs intentionally keep exit 0 (cross-platform dev convenience — the operator
    /// can't run the ceremony off-Windows anyway, and exit 0 keeps `register` usable in
    /// dev smoke loops).
    /// </para>
    /// </summary>
    [Collection(CliCollection.Name)]
    public class RegisterLoginStubExitCodeTests : IDisposable
    {
        private readonly string _tempAppData;
        private readonly string? _originalAppData;
        private readonly string? _originalForceFlag;

        public RegisterLoginStubExitCodeTests()
        {
            _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            _originalForceFlag = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            _tempAppData = Path.Combine(Path.GetTempPath(), "lacerte-mcp-stub-exit-test-" + Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void Register_OnWin32NT_AfterPhase9StubFires_ReturnsExitCode3()
        {
            if (!OnWindows) return; // Branch is Win32NT-only.

            var originalOut = Console.Out;
            var originalErr = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                exit = RegisterCommand.Run("Test App", DateTime.UtcNow.Year - 1, reset: true);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            Assert.Equal(3, exit);
            // Partial message routed to stderr (not stdout) so wrappers parsing stdout
            // for the minted-GUID block aren't polluted with stub diagnostics.
            Assert.Contains("[partial]", stderr.ToString());
            Assert.DoesNotContain("[partial]", stdout.ToString());
        }

        [Fact]
        public void Register_OffWin32NT_ReturnsExitCode0()
        {
            if (OnWindows) return; // Cross-platform branch test.

            var originalOut = Console.Out;
            var stdout = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(stdout);
                exit = RegisterCommand.Run("Test App", DateTime.UtcNow.Year - 1, reset: true);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            // Off-Windows keeps exit 0 — preserves dev-smoke-loop convenience.
            Assert.Equal(0, exit);
        }

        [Fact]
        public void Login_OnWin32NT_AfterPhase9StubFires_ReturnsExitCode3()
        {
            if (!OnWindows) return; // Branch is Win32NT-only.

            // Pre-mint an AppID so we don't trip the "no AppID, exit 1" path.
            AppIdStore.Set(Guid.NewGuid());

            var originalOut = Console.Out;
            var originalErr = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                exit = LoginCommand.Run("Test App", DateTime.UtcNow.Year - 1);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            Assert.Equal(3, exit);
            Assert.Contains("[partial]", stderr.ToString());
        }

        [Fact]
        public void Login_OffWin32NT_ReturnsExitCode0()
        {
            if (OnWindows) return;

            AppIdStore.Set(Guid.NewGuid());

            var originalOut = Console.Out;
            var stdout = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(stdout);
                exit = LoginCommand.Run("Test App", DateTime.UtcNow.Year - 1);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Equal(0, exit);
        }

        // ----- P16-022: differentiated mint vs re-use message -----

        /// <summary>
        /// P16-022: on Windows, a re-run of `register` (existing AppID, no
        /// --reset) must emit "Existing AppID re-used" on stderr, not the
        /// "AppID minted" text — that's a factual lie on the re-run path
        /// because nothing was minted. Cumulative with P15-028: still exit 3.
        /// </summary>
        [Fact]
        public void Register_OnWin32NT_ReRun_StderrSays_Reused_NotMinted()
        {
            if (!OnWindows) return;

            // First call mints; second call re-uses.
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            try
            {
                Console.SetOut(new StringWriter());
                Console.SetError(new StringWriter());
                RegisterCommand.Run("Test App", DateTime.UtcNow.Year - 1, reset: false);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            // Now re-run with reset:false and inspect the stderr.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                exit = RegisterCommand.Run("Test App", DateTime.UtcNow.Year - 1, reset: false);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            Assert.Equal(3, exit);
            var stderrText = stderr.ToString();
            Assert.Contains("Existing AppID re-used", stderrText);
            // The new-mint line must NOT appear on the re-use path.
            Assert.DoesNotContain("AppID minted, but", stderrText);
        }

        // ----- P16-004: corrected error-code references -----

        /// <summary>
        /// P16-004: the login stub's stderr message must reference
        /// LoginExpired (9007) — the actual proactive expiry code — not
        /// TicketExpired (9103, which is InvalidAppId; TicketExpired is
        /// not a defined StatusInfo name anywhere).
        /// </summary>
        [Fact]
        public void Login_OnWin32NT_StderrReferences_LoginExpired_9007_NotTicketExpired_9103()
        {
            if (!OnWindows) return;

            AppIdStore.Set(Guid.NewGuid());

            var originalOut = Console.Out;
            var originalErr = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                LoginCommand.Run("Test App", DateTime.UtcNow.Year - 1);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            var stderrText = stderr.ToString();
            Assert.Contains("LoginExpired", stderrText);
            Assert.Contains("9007", stderrText);
            Assert.DoesNotContain("TicketExpired", stderrText);
            Assert.DoesNotContain("9103", stderrText);
        }
    }
}
