using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Prompts
{
    /// <summary>
    /// P18-017 / P18-023 — small <c>lacerte_prompts</c> MCP tool that lists or
    /// returns the body of the canonical workflow recipes registered in
    /// <see cref="WorkflowPrompts"/>. Many MCP host UIs (Cursor pre-0.47,
    /// Windsurf, custom CLI clients) do not query <c>prompts/list</c> per
    /// session or do not expose the results as ambient context. This tool
    /// re-emits each prompt's name + description + body through the
    /// <c>tools/list</c> channel the LLM always polls, so the recipes remain
    /// discoverable on hosts without a Prompts UI.
    ///
    /// <para>
    /// Two modes:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>lacerte_prompts()</c> — list all available recipes with
    ///     <c>{ name, description, whenToUse }</c> per row, so an LLM can
    ///     decide which one to fetch.
    ///   </description></item>
    ///   <item><description>
    ///     <c>lacerte_prompts(name: "lacerte_client_intake")</c> — return the
    ///     full prompt body so the LLM can follow the steps inline without
    ///     needing the host to surface <c>prompts/get</c>.
    ///   </description></item>
    /// </list>
    /// </para>
    /// </summary>
    [McpServerToolType]
    public static class PromptsCatalogTool
    {
        // Single source of truth — same Name strings the WorkflowPrompts
        // class registers via [McpServerPrompt(Name = ...)]. Adding a new
        // prompt means adding one row here, so the listing tool stays in
        // lock-step with the prompts surface.
        internal static readonly IReadOnlyList<PromptCatalogEntry> Catalog = new[]
        {
            new PromptCatalogEntry(
                name: "lacerte_client_intake",
                description: "Walk an operator through creating a brand-new Lacerte client end-to-end (context, create, seed fields, seed detail rows, verify).",
                whenToUse: "Operator wants to create ONE new client end-to-end with the canonical safety gates."),
            new PromptCatalogEntry(
                name: "lacerte_review_ef_status",
                description: "Triage e-file status across the active module's client roster, surface rejected returns first.",
                whenToUse: "Operator wants an e-file status sweep and per-row next-action triage."),
            new PromptCatalogEntry(
                name: "lacerte_field_write_bulk",
                description: "Apply the same field write across many clients with dry-run preview, per-row idempotency keys, and stop-on-first-error.",
                whenToUse: "Operator wants to bulk-update one field across a list of clients via a single MCP round-trip."),
            new PromptCatalogEntry(
                name: "lacerte_csv_intake",
                description: "Bulk-onboard clients from a CSV / Tax-Organizer export with one-shot column→field mapping and per-row idempotency.",
                whenToUse: "Operator wants to bulk-onboard new clients from a CSV / Tax-Organizer export."),
        };

        [McpServerTool(Name = "lacerte_prompts")]
        [Description(
            "List or fetch canonical multi-step workflow recipes (the prompts registered via prompts/list). " +
            "Use BEFORE improvising a complex flow — there may be a curated recipe with idempotency + dry-run " +
            "gates already written. Call with no arguments for a catalog of {name, description, whenToUse} rows. " +
            "Call with name=<promptName> to retrieve the full prompt body inline (useful on hosts like Cursor / " +
            "Windsurf that do not surface MCP prompts as slash-commands).")]
        public static Task<string> LacertePrompts(
            [Description("Optional prompt name. Omit to list all available recipes; pass e.g. \"lacerte_client_intake\" to fetch the full body.")]
            string? name = null)
            => WrapToolHandler.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    var rows = Catalog
                        .Select(c => new
                        {
                            name = c.Name,
                            description = c.Description,
                            whenToUse = c.WhenToUse,
                        })
                        .ToArray();
                    return Task.FromResult<object>(new
                    {
                        count = rows.Length,
                        rows,
                        hint = "To fetch a recipe body, call lacerte_prompts again with `name: \"<promptName>\"`.",
                    });
                }

                var requested = name!.Trim();
                var entry = Catalog.FirstOrDefault(c =>
                    string.Equals(c.Name, requested, System.StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    return Task.FromResult<object>(new
                    {
                        found = false,
                        requested,
                        availableNames = Catalog.Select(c => c.Name).ToArray(),
                        hint = "Call lacerte_prompts() with no arguments for the full catalog.",
                    });
                }

                var body = RenderPromptBody(entry.Name);
                return Task.FromResult<object>(new
                {
                    found = true,
                    name = entry.Name,
                    description = entry.Description,
                    whenToUse = entry.WhenToUse,
                    body,
                });
            });

        /// <summary>
        /// Renders the canonical prompt body by invoking the corresponding
        /// <see cref="WorkflowPrompts"/> method with default arguments and
        /// extracting the user-message text. Keeps a single source of truth —
        /// future edits to a prompt body automatically flow through this tool.
        /// </summary>
        internal static string RenderPromptBody(string promptName)
        {
            GetPromptResult result;
            switch (promptName)
            {
                case "lacerte_client_intake":
                    result = WorkflowPrompts.ClientIntake();
                    break;
                case "lacerte_review_ef_status":
                    result = WorkflowPrompts.ReviewEfStatus();
                    break;
                case "lacerte_field_write_bulk":
                    result = WorkflowPrompts.BulkFieldUpdate();
                    break;
                case "lacerte_csv_intake":
                    result = WorkflowPrompts.CsvIntake();
                    break;
                default:
                    return string.Empty;
            }

            if (result?.Messages == null || result.Messages.Count == 0)
            {
                return string.Empty;
            }

            var first = result.Messages[0];
            if (first?.Content is TextContentBlock text)
            {
                return text.Text ?? string.Empty;
            }
            return string.Empty;
        }

        internal sealed class PromptCatalogEntry
        {
            public PromptCatalogEntry(string name, string description, string whenToUse)
            {
                Name = name;
                Description = description;
                WhenToUse = whenToUse;
            }

            public string Name { get; }
            public string Description { get; }
            public string WhenToUse { get; }
        }
    }
}
