using System;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LacertMCP.Server
{
    /// <summary>
    /// MCP server entrypoint. Wires the Model Context Protocol stdio transport,
    /// auto-discovers tool classes (anything marked <c>[McpServerToolType]</c>),
    /// and stands up <see cref="LacerteSessionManager"/> as a singleton (one
    /// instance per process per Boundary Invariant 3).
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Logs must go to stderr — stdout is the MCP transport.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // One LacerteSessionManager per process. Constructor reads env to
            // resolve sim vs live; live mode currently stubs the bridge fields
            // until Phase 9 wires the COM client.
            builder.Services.AddSingleton<LacerteSessionManager>();

            // MCP server: stdio transport + attribute-driven tool/prompt discovery across this assembly.
            // WithPromptsFromAssembly picks up WorkflowPrompts (Phase 11 task #79).
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly()
                .WithPromptsFromAssembly();

            await builder.Build().RunAsync().ConfigureAwait(false);
        }
    }
}
