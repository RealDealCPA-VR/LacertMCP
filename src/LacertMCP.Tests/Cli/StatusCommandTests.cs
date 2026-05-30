using System;
using System.IO;
using System.Text.Json;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Storage;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Verifies the <c>status</c> command emits a parseable JSON payload with the documented
    /// shape (registered/appId/mode/storageBackend). Re-routes APPDATA so the file-backed
    /// fallback isolates between tests.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class StatusCommandTests : IDisposable
    {
        private readonly string _tempAppData;
        private readonly string? _originalAppData;
        private readonly string? _originalForceFlag;

        public StatusCommandTests()
        {
            _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            _originalForceFlag = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            _tempAppData = Path.Combine(Path.GetTempPath(), "lacerte-mcp-status-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempAppData);
            Environment.SetEnvironmentVariable("APPDATA", _tempAppData);
            Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
            Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", _originalForceFlag);
            try { if (Directory.Exists(_tempAppData)) Directory.Delete(_tempAppData, recursive: true); } catch { }
        }

        [Fact]
        public void Status_Returns_Zero_Exit_Code()
        {
            var sw = new StringWriter();
            var exit = StatusCommand.Run(showFull: false, sw);
            Assert.Equal(0, exit);
        }

        [Fact]
        public void Status_Output_Is_Valid_Json_With_Expected_Keys()
        {
            var sw = new StringWriter();
            StatusCommand.Run(showFull: false, sw);
            var json = sw.ToString();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.True(root.TryGetProperty("registered", out var registered));
            Assert.Equal(JsonValueKind.False, registered.ValueKind);

            Assert.True(root.TryGetProperty("mode", out var mode));
            Assert.True(mode.ValueKind == JsonValueKind.String);
            Assert.Contains(mode.GetString(), new[] { "live", "simulation" });

            Assert.True(root.TryGetProperty("storageBackend", out var backend));
            Assert.Contains(backend.GetString(), new[] { "windows-credential-manager", "file" });

            Assert.True(root.TryGetProperty("storageLocation", out _));
            Assert.True(root.TryGetProperty("platform", out _));
        }

        [Fact]
        public void Status_With_Registered_GUID_Reports_Registered_True_And_Masked_AppId()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);
            try
            {
                var sw = new StringWriter();
                StatusCommand.Run(showFull: false, sw);
                using var doc = JsonDocument.Parse(sw.ToString());
                var root = doc.RootElement;

                Assert.True(root.GetProperty("registered").GetBoolean());
                var maskedId = root.GetProperty("appId").GetString();
                Assert.NotNull(maskedId);
                // Masked form keeps the first 8 chars and replaces the rest.
                Assert.StartsWith(id.ToString("D").Substring(0, 8), maskedId);
                Assert.Contains("****", maskedId);
                Assert.DoesNotContain(id.ToString("D").Substring(9), maskedId);
            }
            finally
            {
                AppIdStore.Reset();
            }
        }

        [Fact]
        public void Status_With_ShowAppId_Reveals_Full_Guid()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);
            try
            {
                var sw = new StringWriter();
                StatusCommand.Run(showFull: true, sw);
                using var doc = JsonDocument.Parse(sw.ToString());
                var appId = doc.RootElement.GetProperty("appId").GetString();
                Assert.Equal(id.ToString("D"), appId);
            }
            finally
            {
                AppIdStore.Reset();
            }
        }
    }
}
