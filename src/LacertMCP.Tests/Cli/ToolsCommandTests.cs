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

        // ----- P15-066: filtering surface -----

        [Fact]
        public void Tools_Filter_LimitsOutputToMatches()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            // "client" should match lacerte_client_list + others; assert no e-file
            // tools leak in (those don't contain the substring "client").
            var exit = ToolsCommand.Run(asJson: false, filter: "client",
                nameExact: null, namesOnly: false, stdout, stderr);

            Assert.Equal(0, exit);
            var output = stdout.ToString();
            Assert.Contains("lacerte_client_list", output);
            // A real e-file tool name like lacerte_ef_status should not appear since
            // it doesn't contain "client".
            foreach (var line in output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("lacerte_"))
                {
                    Assert.Contains("client", trimmed, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [Fact]
        public void Tools_NamesOnly_OneToolPerLine_NoMetadata()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = ToolsCommand.Run(asJson: false, filter: null,
                nameExact: null, namesOnly: true, stdout, stderr);

            Assert.Equal(0, exit);
            var lines = stdout.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
            Assert.NotEmpty(lines);
            // No description/parameter lines: each line is just a tool name, no ':'.
            foreach (var line in lines)
            {
                Assert.False(string.IsNullOrWhiteSpace(line));
                Assert.False(line.Contains(":"), $"--names-only line should be just a tool name; got: {line}");
                Assert.False(line.StartsWith(" "), $"--names-only line should not be indented; got: {line}");
            }
        }

        [Fact]
        public void Tools_NameExact_ReturnsSingleToolBlock()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = ToolsCommand.Run(asJson: false, filter: null,
                nameExact: "lacerte_health", namesOnly: false, stdout, stderr);

            Assert.Equal(0, exit);
            var output = stdout.ToString();
            Assert.Contains("lacerte_health", output);
            // Only one tool: should not contain another known tool name.
            Assert.DoesNotContain("lacerte_field_write", output);
        }

        [Fact]
        public void Tools_NameExact_MissingTool_ReturnsExit1()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = ToolsCommand.Run(asJson: false, filter: null,
                nameExact: "no_such_tool_lol", namesOnly: false, stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Contains("No tool matched", stderr.ToString());
        }

        [Fact]
        public void Tools_InvalidFilterRegex_ReturnsExit2()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            // "(" is an invalid regex (unclosed group) — should be caught and reported.
            var exit = ToolsCommand.Run(asJson: false, filter: "(",
                nameExact: null, namesOnly: false, stdout, stderr);

            Assert.Equal(2, exit);
            Assert.Contains("Invalid --filter regex", stderr.ToString());
        }

        // ----- P16-067: filter no-match exit-code parity + flag mutex -----

        /// <summary>
        /// P16-067: <c>--filter</c> with zero matches now exits 1 (parity with
        /// <c>--name</c>'s no-match contract) so a CI script `if ($LASTEXITCODE -ne 0)`
        /// can catch a refactor that drops the prefix it asserted on.
        /// </summary>
        [Fact]
        public void Tools_FilterNoMatches_ReturnsExit1_WithStderrMessage()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = ToolsCommand.Run(asJson: false, filter: "^no_such_prefix_zzz_",
                nameExact: null, namesOnly: false, stdout, stderr);

            Assert.Equal(1, exit);
            Assert.Contains("No tools matched filter", stderr.ToString());
            Assert.Equal("", stdout.ToString());
        }

        /// <summary>
        /// P16-067: <c>--json</c> + <c>--names-only</c> is a usage error (exit 2),
        /// not a silent pick of names-only. The previous behavior produced non-JSON
        /// output when piped into jq.
        /// </summary>
        [Fact]
        public void Tools_JsonAndNamesOnly_RejectedWithExit2()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = ToolsCommand.Run(asJson: true, filter: null,
                nameExact: null, namesOnly: true, stdout, stderr);

            Assert.Equal(2, exit);
            Assert.Contains("mutually exclusive", stderr.ToString());
            // Discovery must not have run (no tool list on stdout).
            Assert.Equal("", stdout.ToString());
        }

        // ----- P16-068: UnsafeRelaxedJsonEscaping (no `+`/`<` noise) -----

        /// <summary>
        /// P16-068: tool descriptions or types containing `+`, `<`, `>`, `&` must
        /// survive the JSON emitter verbatim (not as `+` etc.). Picks a known
        /// tool's JSON serialization and asserts the bytes don't contain `\u00`.
        /// </summary>
        [Fact]
        public void Tools_Json_DoesNotEscapeAsciiPunctuation()
        {
            var sw = new StringWriter();
            ToolsCommand.Run(asJson: true, filter: null, nameExact: null,
                namesOnly: false, sw, new StringWriter());
            var output = sw.ToString();
            // We don't expect every tool to contain `<` etc., but the encoder
            // setting itself can be pinned by asserting no `\u00` escape sequences
            // for printable ASCII appear in the payload.
            Assert.DoesNotContain("\\u003C", output);
            Assert.DoesNotContain("\\u003E", output);
            Assert.DoesNotContain("\\u002B", output);
            Assert.DoesNotContain("\\u0026", output);
        }
    }
}
