using System;
using System.IO;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Storage;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// <c>lacerte-mcp unregister --confirm</c> contract: refuse without confirm,
    /// no-op when nothing persisted, full wipe + cleanup on confirmed call.
    /// Same APPDATA + LACERTE_MCP_FORCE_FILE_STORE redirect pattern as the rest
    /// of the CLI suite so we never touch the operator's real install.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class UnregisterCommandTests : IDisposable
    {
        private readonly string _tempAppData;
        private readonly string? _originalAppData;
        private readonly string? _originalForceFlag;

        public UnregisterCommandTests()
        {
            _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            _originalForceFlag = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            _tempAppData = Path.Combine(Path.GetTempPath(), "lacerte-mcp-unregister-test-" + Guid.NewGuid().ToString("N"));
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
        public void Unregister_With_No_AppId_Returns_Zero_And_Reports_Nothing_To_Wipe()
        {
            // No AppID minted → no-op path → exit 0.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = UnregisterCommand.Run(confirm: false, stdout, stderr);

            Assert.Equal(0, exit);
            Assert.Contains("nothing to unregister", stdout.ToString());
        }

        [Fact]
        public void Unregister_Without_Confirm_Refuses_With_Exit_1()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = UnregisterCommand.Run(confirm: false, stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Contains("Refusing", stderr.ToString());
            // AppID must still be present — the refusal path is the only safe state for
            // an unconfirmed invocation.
            Assert.Equal(id, AppIdStore.Get());
        }

        [Fact]
        public void Unregister_With_Confirm_Wipes_AppId_And_Returns_Zero()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);
            Assert.NotNull(AppIdStore.Get());

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = UnregisterCommand.Run(confirm: true, stdout, stderr);

            Assert.Equal(0, exit);
            Assert.Null(AppIdStore.Get());
            Assert.Contains("Unregistered AppID", stdout.ToString());
        }

        [Fact]
        public void Unregister_Default_Does_Not_Leak_Full_AppId()
        {
            // Cross-command invariant (same family as AppIdMaskingTests): unregister
            // must use the masked form even in the confirmation banner, otherwise the
            // full GUID would leak into the shell history of every successful wipe.
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            UnregisterCommand.Run(confirm: true, stdout, stderr);

            var combined = stdout.ToString() + stderr.ToString();
            Assert.DoesNotContain(id.ToString("D"), combined);
            // Sanity: the first 8 chars (masked) should still be there for disambiguation.
            Assert.Contains(id.ToString("D").Substring(0, 8), combined);
        }

        [Fact]
        public void Unregister_Without_Confirm_Refusal_Also_Masks_AppId()
        {
            // Even the "would delete" preview must not leak the full GUID.
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            UnregisterCommand.Run(confirm: false, stdout, stderr);

            var combined = stdout.ToString() + stderr.ToString();
            Assert.DoesNotContain(id.ToString("D"), combined);
        }
    }
}
