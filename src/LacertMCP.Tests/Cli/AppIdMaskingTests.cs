using System;
using System.IO;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Storage;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Cross-command invariant: the AppID GUID must never appear in any CLI default output
    /// (status without --show-app-id, login refresh, doctor probe). The full GUID is the SDK's
    /// bearer credential; only the explicit <c>status --show-app-id</c> opt-in may print it.
    /// Per <c>audit-findings.json</c> task P14-065 refinedRecommendation: this test pins
    /// the "AppID never leaks" invariant so a future refactor that re-introduces a stray
    /// <c>{guid:D}</c> print is caught at the test boundary.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class AppIdMaskingTests : IDisposable
    {
        private readonly string _tempAppData;
        private readonly string? _originalAppData;
        private readonly string? _originalForceFlag;

        public AppIdMaskingTests()
        {
            _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            _originalForceFlag = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            _tempAppData = Path.Combine(Path.GetTempPath(), "lacerte-mcp-mask-test-" + Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void Mask_Hides_All_But_First_Eight_Characters()
        {
            var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var masked = AppIdStore.Mask(id);

            Assert.StartsWith("11111111", masked);
            Assert.Contains("****", masked);
            // The remainder of the GUID after the first 8 must not appear anywhere in the masked form.
            Assert.DoesNotContain("2222", masked);
            Assert.DoesNotContain("3333", masked);
            Assert.DoesNotContain("4444", masked);
            Assert.DoesNotContain("555555555555", masked);
        }

        [Fact]
        public void StatusCommand_Default_Does_Not_Leak_Full_AppId()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var sw = new StringWriter();
            var exit = StatusCommand.Run(showFull: false, sw);
            Assert.Equal(0, exit);

            var output = sw.ToString();
            Assert.DoesNotContain(id.ToString("D"), output);
        }

        [Fact]
        public void StatusCommand_With_ShowAppId_Does_Reveal_Full_Guid()
        {
            // Negative-control: the opt-in flag is the ONE channel that may print the full GUID.
            // If this assertion ever flips, the opt-in semantics changed and the rest of the
            // tests in this class need re-evaluation.
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var sw = new StringWriter();
            StatusCommand.Run(showFull: true, sw);
            Assert.Contains(id.ToString("D"), sw.ToString());
        }

        [Fact]
        public void LoginCommand_Default_Does_Not_Leak_Full_AppId()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            // LoginCommand.Run writes to Console.Out directly — re-route it for the test.
            var originalOut = Console.Out;
            var sw = new StringWriter();
            try
            {
                Console.SetOut(sw);
                LoginCommand.Run("Operator Lacerte MCP", DateTime.UtcNow.Year - 1);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = sw.ToString();
            Assert.DoesNotContain(id.ToString("D"), output);
            // Sanity: the masked first-8 chars should be present so operators can still
            // disambiguate two installs.
            Assert.Contains(id.ToString("D").Substring(0, 8), output);
        }

        [Fact]
        public void DoctorCommand_Default_Does_Not_Leak_Full_AppId()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var sw = new StringWriter();
            DoctorCommand.Run(sw);

            var output = sw.ToString();
            Assert.DoesNotContain(id.ToString("D"), output);
        }
    }
}
