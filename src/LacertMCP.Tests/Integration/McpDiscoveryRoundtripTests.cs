using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Prompts;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-049 — end-to-end MCP discovery roundtrip. Stands up an in-process
    /// MCP server bound to a pair of anonymous pipe streams, drives it through
    /// the real MCP client over JSON-RPC, and asserts that the SDK's auto-
    /// discovery (<c>WithPromptsFromAssembly</c> + <c>WithToolsFromAssembly</c>
    /// in <see cref="LacertMCP.Server.Program"/>) actually surfaces the
    /// [McpServerPromptType] / [McpServerToolType] classes over the wire.
    ///
    /// <para>
    /// The existing <see cref="LacertMCP.Tests.Prompts.WorkflowPromptsTests"/>
    /// only exercises the static methods directly — it would still pass if a
    /// refactor dropped an attribute, renamed an assembly, or otherwise broke
    /// the discovery seam. This test catches that exact failure mode.
    /// </para>
    ///
    /// <para>
    /// <b>If you add a new [McpServerPromptType] or [McpServerToolType] class,
    /// extend the asserted counts here.</b>
    /// </para>
    /// </summary>
    public class McpDiscoveryRoundtripTests
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Builds + starts an in-process MCP server bound to two anonymous
        /// pipe streams (server-in / server-out), creates a matching
        /// <see cref="IMcpClient"/> on the crossed ends, and returns a tuple
        /// the test body uses + disposes.
        /// </summary>
        private static async Task<(IMcpClient client, IHost host)> StartServerAndConnectClientAsync(CancellationToken ct)
        {
            // Two anonymous pipes give us a duplex byte channel without
            // exposing a real file or network handle. Server reads from
            // serverIn (== clientOut from the other side) and writes to
            // serverOut (== clientIn from the other side).
            var clientToServer = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.Out,
                System.IO.HandleInheritability.None);
            var serverReadsClientToServer = new System.IO.Pipes.AnonymousPipeClientStream(
                System.IO.Pipes.PipeDirection.In,
                clientToServer.GetClientHandleAsString());

            var serverToClient = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.Out,
                System.IO.HandleInheritability.None);
            var clientReadsServerToClient = new System.IO.Pipes.AnonymousPipeClientStream(
                System.IO.Pipes.PipeDirection.In,
                serverToClient.GetClientHandleAsString());

            // Server: same shape as Program.cs minus stdio.
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<LacerteSessionManager>(_ =>
                new LacerteSessionManager(new Dictionary<string, string?>()));
            builder.Services
                .AddMcpServer()
                .WithStreamServerTransport(serverReadsClientToServer, serverToClient)
                .WithToolsFromAssembly(typeof(SessionTools).Assembly)
                .WithPromptsFromAssembly(typeof(WorkflowPrompts).Assembly);

            var host = builder.Build();
            // Don't await — RunAsync blocks until shutdown; we want it
            // pumping in the background so the client can drive it.
            _ = host.StartAsync(ct);

            // Client: pair the OTHER ends of the two pipes.
            var transport = new StreamClientTransport(
                serverOutput: clientReadsServerToClient,
                serverInput: clientToServer,
                loggerFactory: NullLoggerFactory.Instance);

            var client = await McpClientFactory.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "lacertemcp-tests", Version = "0.0.0" },
                },
                NullLoggerFactory.Instance,
                ct).ConfigureAwait(false);

            return (client, host);
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        [Fact]
        public async Task PromptsList_ExposesAllWorkflowPrompts()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, host) = await StartServerAndConnectClientAsync(cts.Token);
            try
            {
                var prompts = await client.ListPromptsAsync(cts.Token);
                var names = prompts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

                // Catches: dropped [McpServerPromptType], renamed assembly,
                // missing WithPromptsFromAssembly, or a new prompt that
                // someone forgot to register the test for.
                Assert.Contains("lacerte_client_intake", names);
                Assert.Contains("lacerte_review_ef_status", names);
                Assert.Contains("lacerte_bulk_field_update", names);
                Assert.Contains("lacerte_csv_intake", names);
                Assert.Equal(4, names.Length);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }

        [Fact]
        public async Task PromptsGet_ClientIntake_BindsArgsOverTheWire()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, host) = await StartServerAndConnectClientAsync(cts.Token);
            try
            {
                var prompt = await client.GetPromptAsync(
                    "lacerte_client_intake",
                    new Dictionary<string, object?> { ["clientName"] = "Acme LLC" },
                    cancellationToken: cts.Token);

                Assert.Single(prompt.Messages);
                Assert.Equal(Role.User, prompt.Messages[0].Role);
                var text = Assert.IsType<TextContentBlock>(prompt.Messages[0].Content);
                Assert.Contains("Acme LLC", text.Text);
                // Sanity: the canonical CSV mapping section (P15-038 part 3)
                // also rides this prompt's body and should survive the JSON-RPC
                // roundtrip intact.
                Assert.Contains("C1_1", text.Text);
                Assert.Contains("TBD-F5", text.Text);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }

        [Fact]
        public async Task PromptsGet_ReviewEfStatus_BindsTaxYearArg()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, host) = await StartServerAndConnectClientAsync(cts.Token);
            try
            {
                var prompt = await client.GetPromptAsync(
                    "lacerte_review_ef_status",
                    new Dictionary<string, object?> { ["taxYear"] = 2023 },
                    cancellationToken: cts.Token);

                var text = ((TextContentBlock)prompt.Messages[0].Content).Text;
                Assert.Contains("2023", text);
                Assert.Contains("lacerte_ef_status", text);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }

        [Fact]
        public async Task PromptsGet_BulkFieldUpdate_BindsAllScalarArgs()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, host) = await StartServerAndConnectClientAsync(cts.Token);
            try
            {
                var prompt = await client.GetPromptAsync(
                    "lacerte_bulk_field_update",
                    new Dictionary<string, object?>
                    {
                        ["table"] = 1,
                        ["field"] = "C1_5",
                        ["previewCount"] = 7,
                    },
                    cancellationToken: cts.Token);

                var text = ((TextContentBlock)prompt.Messages[0].Content).Text;
                Assert.Contains("C1_5", text);
                Assert.Contains("7", text);
                Assert.Contains("dryRun", text);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }

        [Fact]
        public async Task PromptsGet_CsvIntake_BindsSourceLabelAndRowCount()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, host) = await StartServerAndConnectClientAsync(cts.Token);
            try
            {
                var prompt = await client.GetPromptAsync(
                    "lacerte_csv_intake",
                    new Dictionary<string, object?>
                    {
                        ["sourceLabel"] = "2024-organizer.csv",
                        ["rowCount"] = 87,
                    },
                    cancellationToken: cts.Token);

                var text = ((TextContentBlock)prompt.Messages[0].Content).Text;
                Assert.Contains("2024-organizer.csv", text);
                Assert.Contains("87", text);
                Assert.Contains("lacerte_field_access_map", text);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }

        [Fact]
        public async Task ToolsList_ExposesAllRegisteredTools()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (client, host) = await StartServerAndConnectClientAsync(cts.Token);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

                // Sanity-floor: the project ships >= 13 [McpServerToolType]
                // tool classes; some classes carry multiple [McpServerTool]
                // methods so the surface is larger than 13. This is a
                // discovery-seam check, not a per-tool inventory — keep the
                // floor loose so new tools don't trip the test.
                Assert.True(
                    tools.Count >= 13,
                    $"expected at least 13 tools registered via WithToolsFromAssembly; got {tools.Count}");

                // Touchstone names — each comes from a different tool class so
                // a regression that drops one class shows up here.
                var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                Assert.Contains("lacerte_session_status", names);
                Assert.Contains("lacerte_client_list", names);
                Assert.Contains("lacerte_client_create", names);
                Assert.Contains("lacerte_field_write", names);
                Assert.Contains("lacerte_ef_status", names);
                Assert.Contains("lacerte_field_access_map", names);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
            }
        }
    }
}
