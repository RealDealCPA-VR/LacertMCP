using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-024 — <c>lacerte_help</c> regression tests. Pin (a) the topic
    /// vocabulary, (b) the "what this MCP does NOT do" payload presence,
    /// (c) the toolsByCategory list against the real assembly so the
    /// categorization can't silently drift, (d) a hard size budget so the
    /// payload stays small enough to be cheap on every /clear.
    /// </summary>
    public class HelpToolsTests
    {
        private static JsonElement Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        // -----------------------------------------------------------------
        // Overview shape — null topic returns every section.
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_NullTopic_ReturnsFullOverview()
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp(null);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var result = root.GetProperty("result");
            Assert.True(result.TryGetProperty("whatThisIs", out _));
            Assert.True(result.TryGetProperty("whatThisDoesNotDo", out _));
            Assert.True(result.TryGetProperty("modes", out _));
            Assert.True(result.TryGetProperty("toolCount", out _));
            Assert.True(result.TryGetProperty("toolsByCategory", out _));
            Assert.True(result.TryGetProperty("envVarsRequired", out _));
            Assert.True(result.TryGetProperty("firstCallExample", out _));
            Assert.True(result.TryGetProperty("relatedPrompts", out _));
            Assert.True(result.TryGetProperty("relatedCli", out _));
        }

        [Fact]
        public async Task Help_EmptyStringTopic_TreatedAsNullOverview()
        {
            // Defensive: an LLM that passes "" rather than null should land
            // on the same overview, not crash on an unknown-topic error.
            var tool = new HelpTools();
            var json = await tool.LacerteHelp("");
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("whatThisIs", out _));
        }

        // -----------------------------------------------------------------
        // Per-topic filtering — each topic returns only its own slice plus
        // the echoed topic name for LLM identification.
        // -----------------------------------------------------------------

        [Theory]
        [InlineData("modes", "modes")]
        [InlineData("tools", "toolsByCategory")]
        [InlineData("env", "envVarsRequired")]
        [InlineData("limits", "whatThisDoesNotDo")]
        [InlineData("first-call", "firstCallExample")]
        [InlineData("prompts", "relatedPrompts")]
        public async Task Help_Topic_ReturnsCorrectSliceAndEchoesTopic(string topic, string expectedKey)
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp(topic);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var result = root.GetProperty("result");
            Assert.Equal(topic, result.GetProperty("topic").GetString());
            Assert.True(result.TryGetProperty(expectedKey, out _),
                $"topic='{topic}' should expose key '{expectedKey}', got: {result}");
        }

        // -----------------------------------------------------------------
        // Alias resolution — the canonical hint vocabulary
        // ("envvars" / "quickstart" / etc.) maps to canonical topics so an
        // LLM that guesses from the README never gets a hard error.
        // -----------------------------------------------------------------

        [Theory]
        [InlineData("envvars", "env")]
        [InlineData("environment", "env")]
        [InlineData("quickstart", "first-call")]
        [InlineData("scope", "limits")]
        [InlineData("MODES", "modes")] // case-insensitive
        public async Task Help_TopicAlias_ResolvesToCanonical(string alias, string canonical)
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp(alias);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(canonical, root.GetProperty("result").GetProperty("topic").GetString());
        }

        [Theory]
        [InlineData("overview")]
        [InlineData("all")]
        public async Task Help_OverviewAlias_ReturnsFullOverview(string alias)
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp(alias);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("whatThisIs", out _));
        }

        // -----------------------------------------------------------------
        // Unknown topic — fails with the synthetic InvalidArgument code and
        // surfaces the valid vocabulary so the LLM can retry once.
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_UnknownTopic_ReturnsInvalidArgumentWithVocabulary()
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp("nope-not-a-topic");
            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean(), json);
            Assert.Equal(SyntheticStatusCodes.InvalidArgument,
                root.GetProperty("statusCode").GetInt32());
            // The context dict should list the canonical vocabulary verbatim.
            var ctx = root.GetProperty("context");
            Assert.True(ctx.TryGetProperty("validTopics", out var valid));
            var names = valid.EnumerateArray().Select(e => e.GetString()).ToHashSet();
            Assert.Contains("modes", names);
            Assert.Contains("limits", names);
            Assert.Contains("first-call", names);
        }

        // -----------------------------------------------------------------
        // The killer payload: whatThisDoesNotDo must surface the README's
        // negative-space scoping bullets. If these go missing the whole
        // point of the tool is gone — that's why it gets its own test.
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_Limits_SurfacesEFileAndPrintAndArchiveScoping()
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp("limits");
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var bullets = root.GetProperty("result").GetProperty("whatThisDoesNotDo")
                .EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            Assert.NotEmpty(bullets);

            // The six anchor scoping concerns that the LLM most commonly
            // asks about — verify each is mentioned by keyword.
            var joined = string.Join("\n", bullets);
            Assert.Contains("e-filing", joined, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("print", joined, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("organizer", joined, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Single-operator", joined, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("archive", joined, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UI", joined, System.StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------
        // first-call should recommend lacerte_session_status (per the
        // refinedDesign — "not a write tool").
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_FirstCall_RecommendsSessionStatus()
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp("first-call");
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var example = root.GetProperty("result").GetProperty("firstCallExample");
            Assert.Equal("lacerte_session_status", example.GetProperty("tool").GetString());
        }

        // -----------------------------------------------------------------
        // Parity test: every tool name in toolsByCategory MUST exist as a
        // real [McpServerTool] on the assembly. Locks the categorization
        // against drift — if a tool is renamed or removed without updating
        // HelpTools, this test fails loudly.
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_ToolsByCategory_AllListedToolsExistOnAssembly()
        {
            var asm = typeof(SessionTools).Assembly;
            var realTools = asm.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Select(m => ToSnakeCase(m.Name))
                .ToHashSet();

            var tool = new HelpTools();
            var json = await tool.LacerteHelp("tools");
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var listed = new List<string>();
            var byCategory = root.GetProperty("result").GetProperty("toolsByCategory");
            foreach (var category in byCategory.EnumerateObject())
            {
                foreach (var entry in category.Value.EnumerateArray())
                {
                    listed.Add(entry.GetString() ?? "");
                }
            }

            Assert.NotEmpty(listed);
            var missing = listed.Where(t => !realTools.Contains(t)).ToList();
            Assert.True(missing.Count == 0,
                $"toolsByCategory lists tools that don't exist on the assembly: {string.Join(", ", missing)}. " +
                $"Either the tool was removed/renamed (update HelpTools) or the listing is a typo.");
        }

        // -----------------------------------------------------------------
        // Size budget: the whole point of this tool is to be a CHEAP
        // /clear-recovery payload. Hard cap at 8KB; the refinedDesign
        // targeted ~5KB but we keep a small safety margin for future
        // additions (one more env var, one more tool category).
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_OverviewPayload_StaysUnder8Kb()
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp(null);
            var bytes = Encoding.UTF8.GetByteCount(json);
            Assert.True(bytes <= 8 * 1024,
                $"Overview payload is {bytes} bytes — exceeds the 8KB budget. " +
                "lacerte_help is meant to be cheap on every /clear; trim content or split into more topic filters.");
        }

        // -----------------------------------------------------------------
        // toolCount must match the sum of the toolsByCategory entries —
        // catches the case where one is updated and the other isn't.
        // -----------------------------------------------------------------

        [Fact]
        public async Task Help_ToolCount_MatchesToolsByCategorySum()
        {
            var tool = new HelpTools();
            var json = await tool.LacerteHelp(null);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var result = root.GetProperty("result");
            int declared = result.GetProperty("toolCount").GetInt32();

            int counted = 0;
            foreach (var cat in result.GetProperty("toolsByCategory").EnumerateObject())
                counted += cat.Value.GetArrayLength();

            Assert.Equal(counted, declared);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// MCP tools register under their C# method name converted to
        /// lower_snake_case by the SDK — same convention used by the
        /// existing CLI <c>ToolsCommand</c> reflection. Method names like
        /// <c>LacerteHelp</c> -> <c>lacerte_help</c>.
        /// </summary>
        private static string ToSnakeCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Mirror ToolsCommand.ToSnakeCase exactly so name parity tracks
            // what the SDK actually registers.
            var withUnderscores = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2");
            return withUnderscores.ToLowerInvariant();
        }
    }
}
