using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp tools [--json]</c>
    ///
    /// <para>
    /// Lists every MCP tool the server registers, by reflecting over the
    /// <c>LacertMCP.Server</c> assembly the CLI already references. Mirrors the
    /// SDK's own <c>WithToolsFromAssembly()</c> discovery: types decorated with
    /// <see cref="ModelContextProtocol.Server.McpServerToolTypeAttribute"/> are
    /// scanned, and every method carrying <see cref="ModelContextProtocol.Server.McpServerToolAttribute"/>
    /// is enumerated. Tool name defaults to the PascalCase method name converted
    /// to <c>snake_case</c> (the SDK convention — overrideable via
    /// <c>[McpServerTool(Name = "...")]</c>, which we honor when present).
    /// </para>
    ///
    /// <para>
    /// Useful for two operator workflows: (a) sanity-checking the tool surface
    /// before pasting into a Claude Desktop / Cursor MCP config, and (b) quick
    /// before/after smoke between Phase milestones without booting the stdio
    /// transport.
    /// </para>
    ///
    /// <para>
    /// Per P14-046 refinedRecommendation: this is a pure off-Windows verb (no
    /// SDK / no Lacerte install needed) — the reflection runs against the
    /// already-compiled <c>LacertMCP.Server</c> assembly.
    /// </para>
    /// </summary>
    public static class ToolsCommand
    {
        public static Command Build()
        {
            var cmd = new Command("tools",
                "List every MCP tool the server registers (by reflecting on [McpServerTool] attributes).");

            var jsonOpt = new Option<bool>("--json", () => false,
                "Emit a machine-parseable JSON array instead of the human table. Mutually exclusive with --names-only.");
            // P15-066: filtering options for operators sanity-checking the tool surface
            // before pasting into a Claude Desktop / Cursor MCP config.
            // P16-067: --filter with zero matches now exits 1 so a CI script using
            // `if ($LASTEXITCODE -ne 0)` can catch a refactor that drops the prefix.
            var filterOpt = new Option<string?>("--filter", () => null,
                "Case-insensitive regex matched against tool name (e.g. '^lacerte_ef_' for e-file tools). Exit 1 if zero matches.");
            var nameOpt = new Option<string?>("--name", () => null,
                "Print one tool's full surface (all parameter descriptions). Exit 1 if no match. When combined with --filter, the name must also satisfy the filter.");
            var namesOnlyOpt = new Option<bool>("--names-only", () => false,
                "Print one tool name per line, no descriptions. Pairs with shell pipelines. Mutually exclusive with --json.");

            cmd.AddOption(jsonOpt);
            cmd.AddOption(filterOpt);
            cmd.AddOption(nameOpt);
            cmd.AddOption(namesOnlyOpt);

            cmd.SetHandler((context) =>
            {
                var asJson = context.ParseResult.GetValueForOption(jsonOpt);
                var filter = context.ParseResult.GetValueForOption(filterOpt);
                var name = context.ParseResult.GetValueForOption(nameOpt);
                var namesOnly = context.ParseResult.GetValueForOption(namesOnlyOpt);
                context.ExitCode = Run(asJson, filter, name, namesOnly, Console.Out, Console.Error);
            });

            return cmd;
        }

        public sealed class ToolEntry
        {
            public string Name { get; set; } = "";
            public string? Description { get; set; }
            public string DeclaringType { get; set; } = "";
            public List<ParamEntry> Parameters { get; set; } = new List<ParamEntry>();
        }

        public sealed class ParamEntry
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public string? Description { get; set; }
            public bool Optional { get; set; }
        }

        /// <summary>
        /// Back-compat overload — preserved for older test call sites that pre-date the
        /// P15-066 filter / name / names-only parameters.
        /// </summary>
        public static int Run(bool asJson, TextWriter writer)
            => Run(asJson, filter: null, nameExact: null, namesOnly: false, writer, Console.Error);

        public static int Run(
            bool asJson,
            string? filter,
            string? nameExact,
            bool namesOnly,
            TextWriter writer,
            TextWriter stderr)
        {
            // P16-067: enforce mutual exclusion of --json and --names-only as a usage
            // error (exit 2) rather than the previous silent --names-only wins. Same
            // exit-code convention as the invalid-regex branch below. Without this
            // an operator piping `--json --names-only` into jq got non-JSON output.
            if (asJson && namesOnly)
            {
                stderr.WriteLine("--json and --names-only are mutually exclusive; pick one.");
                return 2;
            }

            var tools = DiscoverTools();

            // P15-066: apply --filter (regex on Name) before any output branch.
            if (!string.IsNullOrEmpty(filter))
            {
                Regex re;
                try
                {
                    re = new Regex(filter!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException ex)
                {
                    stderr.WriteLine($"Invalid --filter regex: {ex.Message}");
                    return 2;
                }
                tools = tools.Where(t => re.IsMatch(t.Name)).ToList();

                // P16-067: parity with --name's no-match contract — `--filter` returning
                // zero tools is the same negative semantics ("nothing found") as
                // `--name <missing>`, so exit 1 rather than the previous silent exit 0.
                // Without this CI scripts asserting `lacerte-mcp tools --filter '^lacerte_ef_'`
                // silently passed when a refactor dropped the prefix.
                if (tools.Count == 0)
                {
                    stderr.WriteLine($"No tools matched filter '{filter}'.");
                    return 1;
                }
            }

            // --name <exact>: print one tool's full surface, or exit 1 if absent.
            if (!string.IsNullOrEmpty(nameExact))
            {
                var match = tools.FirstOrDefault(t => string.Equals(t.Name, nameExact, StringComparison.Ordinal));
                if (match == null)
                {
                    stderr.WriteLine($"No tool matched name '{nameExact}'.");
                    return 1;
                }
                writer.WriteLine(match.Name);
                if (!string.IsNullOrEmpty(match.Description))
                {
                    writer.WriteLine($"  {match.Description}");
                }
                writer.WriteLine($"  declaringType: {match.DeclaringType}");
                if (match.Parameters.Count > 0)
                {
                    writer.WriteLine("  parameters:");
                    foreach (var p in match.Parameters)
                    {
                        var opt = p.Optional ? "?" : "";
                        writer.WriteLine($"    - {p.Name}{opt}: {p.Type}");
                        if (!string.IsNullOrEmpty(p.Description))
                        {
                            writer.WriteLine($"        {p.Description}");
                        }
                    }
                }
                return 0;
            }

            // --names-only: one tool name per line. Pairs cleanly with shell pipelines.
            // Mutual exclusion with --json is now enforced as a usage error at the top
            // of Run (P16-067).
            if (namesOnly)
            {
                foreach (var t in tools.OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WriteLine(t.Name);
                }
                return 0;
            }

            if (asJson)
            {
                // camelCase keys to mirror the rest of the CLI's JSON surface (status
                // command emits camelCase via anonymous objects), so MCP clients that
                // consume both can use one casing policy.
                var json = JsonSerializer.Serialize(tools, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    // P16-068: keep `<`, `>`, `+`, `&` literal so tool descriptions
                    // and parameter types containing them (e.g. URL templates, type
                    // signatures) survive jq / clip / paste-into-bug-report unmangled.
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                writer.WriteLine(json);
                return 0;
            }

            writer.WriteLine($"lacerte-mcp tools — {tools.Count} registered");
            writer.WriteLine(new string('-', 60));
            foreach (var t in tools.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WriteLine($"  {t.Name}");
                if (!string.IsNullOrEmpty(t.Description))
                {
                    writer.WriteLine($"    {t.Description}");
                }
                if (t.Parameters.Count > 0)
                {
                    foreach (var p in t.Parameters)
                    {
                        var opt = p.Optional ? "?" : "";
                        writer.WriteLine($"      - {p.Name}{opt}: {p.Type}");
                    }
                }
                writer.WriteLine();
            }
            return 0;
        }

        /// <summary>
        /// Public for tests — discovers tools in the <c>LacertMCP.Server</c> assembly
        /// using the same mechanism the SDK's <c>WithToolsFromAssembly()</c> uses.
        /// </summary>
        public static List<ToolEntry> DiscoverTools()
        {
            // Anchor on a known Server type rather than walking AppDomain assemblies —
            // FieldAccessMapLoader is already referenced by FieldAccessCommand so this
            // keeps the reflection scope explicit and predictable.
            var serverAsm = typeof(LacertMCP.Server.Schema.FieldAccessMapLoader).Assembly;
            var result = new List<ToolEntry>();

            Type[] types;
            try
            {
                types = serverAsm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some Server types depend on COM/.NET interop that may not load off-Windows
                // (Bridge/*). Filter to the types that did load — the [McpServerToolType]
                // classes themselves are pure DI plumbing and load fine cross-platform.
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type == null) continue;
                if (!HasAttribute(type, "McpServerToolTypeAttribute")) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var toolAttr = method.GetCustomAttributes(inherit: false)
                        .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                    if (toolAttr == null) continue;

                    var entry = new ToolEntry
                    {
                        Name = ResolveToolName(toolAttr, method),
                        Description = ResolveDescription(method),
                        DeclaringType = type.FullName ?? type.Name,
                    };

                    foreach (var p in method.GetParameters())
                    {
                        // R6-38: CancellationToken is host-injected plumbing the MCP
                        // wire schema never exposes — the SDK binds it from the
                        // request context, not from tool arguments. Listing it here
                        // made the CLI surface disagree with what an MCP client
                        // actually sees in tools/list.
                        if (p.ParameterType == typeof(System.Threading.CancellationToken)) continue;

                        entry.Parameters.Add(new ParamEntry
                        {
                            Name = p.Name ?? "(unnamed)",
                            Type = FriendlyTypeName(p.ParameterType),
                            Description = ResolveDescription(p),
                            Optional = p.IsOptional || p.HasDefaultValue,
                        });
                    }

                    result.Add(entry);
                }
            }

            return result;
        }

        private static bool HasAttribute(MemberInfo m, string attrName)
        {
            return m.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == attrName);
        }

        private static string ResolveToolName(object toolAttr, MethodInfo method)
        {
            // Honor explicit [McpServerTool(Name = "...")] when present.
            var nameProp = toolAttr.GetType().GetProperty("Name");
            var explicitName = nameProp?.GetValue(toolAttr) as string;
            if (!string.IsNullOrEmpty(explicitName)) return explicitName!;

            // SDK convention: PascalCase method → snake_case tool name
            // (e.g. LacerteClientList → lacerte_client_list).
            return ToSnakeCase(method.Name);
        }

        private static string? ResolveDescription(MemberInfo m)
        {
            var descAttr = m.GetCustomAttributes(inherit: false)
                .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
            if (descAttr == null) return null;
            var prop = descAttr.GetType().GetProperty("Description");
            return prop?.GetValue(descAttr) as string;
        }

        private static string? ResolveDescription(ParameterInfo p)
        {
            var descAttr = p.GetCustomAttributes(inherit: false)
                .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
            if (descAttr == null) return null;
            var prop = descAttr.GetType().GetProperty("Description");
            return prop?.GetValue(descAttr) as string;
        }

        /// <summary>
        /// PascalCase → snake_case. <c>LacerteClientList</c> → <c>lacerte_client_list</c>.
        /// Matches the ModelContextProtocol SDK's default name transform; pinning it here
        /// (rather than calling into the SDK) keeps the CLI from booting any of the server
        /// DI plumbing just to enumerate tool names.
        /// </summary>
        public static string ToSnakeCase(string pascal)
        {
            if (string.IsNullOrEmpty(pascal)) return pascal;
            // Insert underscore between a lowercase/digit and an uppercase, then lowercase.
            var withUnderscores = Regex.Replace(pascal, "([a-z0-9])([A-Z])", "$1_$2");
            return withUnderscores.ToLowerInvariant();
        }

        private static string FriendlyTypeName(Type t)
        {
            // Strip Nullable<T> shell so the operator sees `int?` not `Nullable<Int32>`.
            var underlying = Nullable.GetUnderlyingType(t);
            if (underlying != null) return FriendlyTypeName(underlying) + "?";
            if (t == typeof(string)) return "string";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "long";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(double)) return "double";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(object)) return "object";
            return t.Name;
        }
    }
}
