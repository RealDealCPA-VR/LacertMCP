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
    }
}
