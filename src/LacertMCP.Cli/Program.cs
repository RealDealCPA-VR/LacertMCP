using System;
using System.CommandLine;
using System.Threading.Tasks;
using LacertMCP.Cli.Commands;

namespace LacertMCP.Cli
{
    /// <summary>
    /// Operator CLI dispatcher — <c>lacerte-mcp register | login | status | field-access | doctor</c>.
    ///
    /// <para>
    /// Built on <c>System.CommandLine</c> 2.0.0-beta4 (preview API — surface is subject to
    /// minor renames before GA; the patterns used here — <c>RootCommand</c>, <c>Command</c>,
    /// <c>Option&lt;T&gt;</c>, <c>SetHandler</c>, <c>InvokeAsync</c> — are the stable core).
    /// </para>
    ///
    /// <para>
    /// Each subcommand lives under <c>Commands/</c> and exposes a static <c>Build()</c>
    /// factory; this file just wires them into the root. Operator surface designed per
    /// <c>docs/research/agents/02-auth-flow.md</c> and <c>Todo.md</c> Phase 10 tasks #69-74.
    /// </para>
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var root = new RootCommand(
                "lacerte-mcp — operator CLI for one-time setup, recovery, and ops.\n" +
                "Use this in an interactive PowerShell for registration / login (Intuit dialogs).\n" +
                "The MCP server itself is headless and never surfaces UI.");

            root.AddCommand(RegisterCommand.Build());
            root.AddCommand(LoginCommand.Build());
            root.AddCommand(StatusCommand.Build());
            root.AddCommand(FieldAccessCommand.Build());
            root.AddCommand(DoctorCommand.Build());

            return await root.InvokeAsync(args).ConfigureAwait(false);
        }
    }
}
