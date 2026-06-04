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
            var exit = StatusCommand.Run(showFull: false, asJson: true, sw);
            Assert.Equal(0, exit);
        }

        [Fact]
        public void Status_Output_Is_Valid_Json_With_Expected_Keys()
        {
            var sw = new StringWriter();
            StatusCommand.Run(showFull: false, asJson: true, sw);
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
                StatusCommand.Run(showFull: false, asJson: true, sw);
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
                StatusCommand.Run(showFull: true, asJson: true, sw);
                using var doc = JsonDocument.Parse(sw.ToString());
                var appId = doc.RootElement.GetProperty("appId").GetString();
                Assert.Equal(id.ToString("D"), appId);
            }
            finally
            {
                AppIdStore.Reset();
            }
        }

        // P15-026: pins the new human-default + --json opt-in shape.

        [Fact]
        public void Status_Default_Output_Is_Human_Format()
        {
            var sw = new StringWriter();
            StatusCommand.Run(showFull: false, asJson: false, sw);
            var output = sw.ToString();

            // Must NOT start with the JSON object brace.
            Assert.False(output.TrimStart().StartsWith("{"),
                "Default status output should be the human summary, not JSON. Got:\n" + output);
            // Must include the human "Registered" label.
            Assert.Contains("Registered", output);
        }

        [Fact]
        public void Status_Default_Output_Masks_AppId_By_Default()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);
            try
            {
                var sw = new StringWriter();
                StatusCommand.Run(showFull: false, asJson: false, sw);
                var output = sw.ToString();

                Assert.Contains("****", output);
                Assert.DoesNotContain(id.ToString("D"), output);
            }
            finally
            {
                AppIdStore.Reset();
            }
        }

        [Fact]
        public void Status_Json_Flag_Emits_Valid_Json()
        {
            var sw = new StringWriter();
            var exit = StatusCommand.Run(showFull: false, asJson: true, sw);
            Assert.Equal(0, exit);

            using var doc = JsonDocument.Parse(sw.ToString());
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        // ----- P16-041: self-curing "next step" hint when unregistered -----

        /// <summary>
        /// P16-041: with no AppID persisted, the human output must include a
        /// "Next:" line pointing at either `lacerte-mcp register` (Windows) or
        /// `LACERTE_SIMULATION=1` (off-Windows) so the operator doesn't have to
        /// context-switch to README/doctor to know what to do next. Skip the
        /// hint in the --json path (the JSON shape is the contract).
        /// </summary>
        [Fact]
        public void Status_Unregistered_Default_Output_Includes_Next_Step_Hint()
        {
            // Ensure no AppID is persisted (the fixture sets up an isolated APPDATA).
            AppIdStore.Reset();

            var sw = new StringWriter();
            StatusCommand.Run(showFull: false, asJson: false, sw);
            var output = sw.ToString();

            Assert.Contains("Next:", output);
            // One of the two branches must fire.
            Assert.True(
                output.Contains("lacerte-mcp register") || output.Contains("LACERTE_SIMULATION=1"),
                "Expected the Next: line to mention `lacerte-mcp register` (Windows) or `LACERTE_SIMULATION=1` (off-Windows).");
        }

        /// <summary>
        /// P16-041: the --json path must NOT contain the "Next:" hint — JSON
        /// consumers have the structured `registered:false` already and the
        /// shape is contract-pinned by P15-026.
        /// </summary>
        [Fact]
        public void Status_Unregistered_Json_Output_Does_Not_Include_Next_Step_Hint()
        {
            AppIdStore.Reset();

            var sw = new StringWriter();
            StatusCommand.Run(showFull: false, asJson: true, sw);
            var output = sw.ToString();

            Assert.DoesNotContain("Next:", output);
            // Sanity: must still be parseable JSON.
            using var doc = JsonDocument.Parse(output);
            Assert.False(doc.RootElement.GetProperty("registered").GetBoolean());
        }

        // ----- P16-068: UnsafeRelaxedJsonEscaping pins `+` in buildVersion -----

        /// <summary>
        /// P16-068: the JSON encoder must NOT escape `+` (used in buildVersion
        /// `"0.1.0+abc1234"`) — the default HTML-safe encoder turned it into
        /// `+` which makes the build hash unreadable when an operator
        /// pastes the JSON into a bug report.
        /// </summary>
        [Fact]
        public void Status_Json_BuildVersion_Plus_Sign_NotEscaped()
        {
            var sw = new StringWriter();
            StatusCommand.Run(showFull: false, asJson: true, sw);
            var output = sw.ToString();

            // The literal `+` (escaped form of `+`) must not appear.
            Assert.DoesNotContain("\\u002B", output);
            // We don't assert presence of `+` itself because the build hash
            // may not always be appended (SourceRevisionId may be empty in some
            // dev builds), but the absence of the escape is the load-bearing assert.
        }
    }
}
