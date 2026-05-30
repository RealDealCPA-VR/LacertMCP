using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Cli.Commands;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// <c>lacerte-mcp tools</c> contract: the CLI must enumerate every MCP tool the
    /// server registers, without booting the stdio transport. Pins the reflection
    /// path against the canonical Server assembly so a future tool addition / rename
    /// or a regression in the snake_case transform is caught at the test boundary.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class ToolsCommandTests
    {
        [Fact]
        public void Tools_Returns_Zero_Exit_Code()
        {
            var sw = new StringWriter();
            var exit = ToolsCommand.Run(asJson: false, sw);
            Assert.Equal(0, exit);
        }

        [Fact]
        public void Tools_Output_Contains_Known_Tool_Names()
        {
            var sw = new StringWriter();
            ToolsCommand.Run(asJson: false, sw);
            var output = sw.ToString();

            // The MCP SDK converts PascalCase method names to snake_case by default.
            // These three are load-bearing across phases 0/5/6 — if any of them
            // disappear or get renamed the smoke surface is broken.
            Assert.Contains("lacerte_health", output);
            Assert.Contains("lacerte_client_list", output);
            Assert.Contains("lacerte_field_write", output);
        }

        [Fact]
        public void Tools_Json_Output_Is_Parseable_Array_With_Expected_Shape()
        {
            var sw = new StringWriter();
            var exit = ToolsCommand.Run(asJson: true, sw);
            Assert.Equal(0, exit);

            using var doc = JsonDocument.Parse(sw.ToString());
            var root = doc.RootElement;

            Assert.Equal(JsonValueKind.Array, root.ValueKind);
            Assert.True(root.GetArrayLength() > 0, "Expected at least one tool to be discovered.");

            // Every entry must have name + declaringType; description + parameters are optional
            // (some future tool might be parameterless or undocumented — don't lock that out).
            foreach (var entry in root.EnumerateArray())
            {
                Assert.True(entry.TryGetProperty("name", out var name));
                Assert.False(string.IsNullOrEmpty(name.GetString()));
                Assert.True(entry.TryGetProperty("declaringType", out _));
            }
        }

        [Fact]
        public void DiscoverTools_Includes_Health_With_Description()
        {
            var tools = ToolsCommand.DiscoverTools();
            var health = tools.FirstOrDefault(t => t.Name == "lacerte_health");
            Assert.NotNull(health);
            // HealthTools.LacerteHealth has a [Description(...)] attribute — the discoverer must surface it.
            Assert.False(string.IsNullOrEmpty(health!.Description),
                "lacerte_health tool should expose the [Description(...)] attribute text.");
        }

        [Fact]
        public void ToSnakeCase_Matches_SDK_Convention()
        {
            // Pin the conversion. If the SDK ever changes its default, this test will
            // start lying — but the CLI output will keep matching whatever this helper
            // emits, so we want the lock-step here.
            Assert.Equal("lacerte_health", ToolsCommand.ToSnakeCase("LacerteHealth"));
            Assert.Equal("lacerte_client_list", ToolsCommand.ToSnakeCase("LacerteClientList"));
            Assert.Equal("lacerte_field_write", ToolsCommand.ToSnakeCase("LacerteFieldWrite"));
            // Edge: all-lowercase input passes through unchanged.
            Assert.Equal("foo", ToolsCommand.ToSnakeCase("foo"));
            // Edge: empty / null guards.
            Assert.Equal("", ToolsCommand.ToSnakeCase(""));
        }
    }
}
