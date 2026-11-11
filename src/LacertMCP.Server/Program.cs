using System;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Session;
using LacertMCP.Server.Startup;
using LacertMCP.Server.Web;
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
    /// <remarks>
    /// Startup order matters: (1) clear default providers and add a stderr-only
    /// Console logger before anything else writes a log line — stdout is the
    /// MCP JSON-RPC wire and any byte that lands there corrupts the framing.
    /// (2) Run <see cref="EnvValidator"/> next so a misconfigured
    /// <c>LACERTE_*</c> env var surfaces a warning + banner on stderr BEFORE
    /// the host starts (the agent's only visible signal channel before
    /// JSON-RPC absorbs all output). (3) Register the session manager and MCP
    /// services. (4) Build the host with <c>using var</c> so the
    /// <see cref="IServiceProvider"/> (and therefore the
    /// <see cref="LacerteSessionManager"/> singleton's <c>Dispose</c>) runs
    /// when <c>RunAsync</c> returns — covers the graceful-shutdown gap from
    /// P15-046. (5) Belt-and-suspenders <c>AppDomain.ProcessExit</c> handler
    /// disposes the session manager if the host shuts down abruptly (stdio
    /// pipe close from Claude Desktop), mirroring the pattern in
    /// <see cref="LacertMCP.Server.Logging.WireLog"/>.
    /// </remarks>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            // P15-033 — env-var sanity check + startup banner on stderr.
            // Runs before the host so warnings appear in operator terminal
            // history (the agent will absorb everything past this point into
            // JSON-RPC envelopes).
            EnvValidator.Validate(Console.Error);

            var builder = Host.CreateApplicationBuilder(args);

            // Logs must go to stderr — stdout is the MCP transport.
            // P15-053 — operator-tunable verbosity via LACERTE_LOG_LEVEL
            // (Trace|Debug|Information|Warning|Error|Critical|None). The
            // routing threshold stays at Trace so EVERY surviving log line
            // routes to stderr (never stdout); MinimumLevel is the actual
            // filter knob.
            var logLevel = LogLevelEnv.Parse(Environment.GetEnvironmentVariable(LogLevelEnv.EnvName));
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(logLevel);
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // One LacerteSessionManager per process. Constructor reads env to
            // resolve sim vs live; live mode currently stubs the bridge fields
            // until Phase 9 wires the COM client.
            builder.Services.AddSingleton<LacerteSessionManager>();

            // P15-054 — periodic liveness signal file at %TEMP%/lacerte-mcp-heartbeat-{pid}.json
            // so external watchers (Task Scheduler, NSSM, an operator
            // PowerShell snippet) can detect a hung / dead server without
            // implementing the MCP JSON-RPC handshake. Gated on a positive
            // cadence — LACERTE_HEARTBEAT_SECONDS=0 (or unparseable) disables
            // the writer entirely so it never even registers with the host.
            var heartbeatSeconds = HeartbeatWriter.IntervalSeconds(
                Environment.GetEnvironmentVariable(HeartbeatWriter.IntervalEnvName));
            if (heartbeatSeconds > 0)
            {
                builder.Services.AddHostedService<HeartbeatWriter>();
            }

            // MCP server: stdio transport + attribute-driven tool/prompt discovery across this assembly.
            // WithPromptsFromAssembly picks up WorkflowPrompts (Phase 11 task #79).
            //
            // P16-050 — set Implementation/ServerInfo on the MCP wire so the
            // host UI (Claude Desktop server list, Cursor status bar, Windsurf
            // panel) and the InitializeResult.serverInfo field carry the
            // canonical "lacerte-mcp" name and the BuildInfo.Version stamp.
            // Without this the SDK falls back to assembly defaults with no
            // visible SemVer — bug reports from operators won't carry the
            // version stamp. The SDK 0.3.0-preview.4 does not expose a fluent
            // .WithServerInfo(...) extension; ServerInfo is set inside the
            // AddMcpServer configureOptions lambda via McpServerOptions.
            // Sign in to Lacerte once at startup (Boundary Invariant 8). Without
            // this nothing outside the tests ever called Register(), so every
            // data tool failed with 203 NotSignedIn in both modes.
            builder.Services.AddHostedService<StartupRegistration>();

            var httpMode = HttpServer.IsHttpMode(args);
            var mcp = builder.Services
                .AddMcpServer(opts =>
                {
                    opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "lacerte-mcp",
                        Title = "Lacerte MCP",
                        Version = BuildInfo.Version,
                    };
                })
                .WithToolsFromAssembly()
                .WithPromptsFromAssembly();

            if (httpMode)
            {
                // HTTP mode: MCP over Streamable HTTP + the control page, on
                // loopback and this PC's tailnet address only (Web/HttpServer.cs).
                builder.Services.AddSingleton<Tailscale>();
                builder.Services.AddSingleton<DeviceAccess>(_ => new DeviceAccess());
                builder.Services.AddSingleton<ActivityLog>(_ => new ActivityLog());
                builder.Services.AddSingleton<McpHttpSessions>();
                builder.Services.AddHostedService<HttpServer>();
                mcp.WithRequestFilters(f => f.AddCallToolFilter(next => async (ctx, ct) =>
                {
                    var result = await next(ctx, ct).ConfigureAwait(false);
                    try
                    {
                        var activity = ctx.Services?.GetService<ActivityLog>();
                        if (activity != null) ToolActivity.Record(activity, ctx.Params, result);
                    }
                    catch
                    {
                        // Activity is best-effort; never fail a tool call over it.
                    }
                    return result;
                }));
            }
            else
            {
                mcp.WithStdioServerTransport();
            }

            // P15-046 — capture the host so `using` guarantees disposal on
            // graceful shutdown. Host.CreateApplicationBuilder already wires
            // ConsoleLifetime (Ctrl+C + ProcessExit -> StopAsync), so a
            // second CancelKeyPress handler would be redundant and race the
            // default. The `using` line + the ProcessExit safety net below
            // are the two pieces actually missing today.
            using var host = builder.Build();

            // Belt-and-suspenders — if the host returns from RunAsync without
            // the ConsoleLifetime path firing (e.g. stdio pipe close on some
            // MCP hosts), the ProcessExit hook still gets a chance to release
            // the COM ref / ODBC handles / idempotency semaphores. SIGKILL
            // bypasses managed code entirely, so this is the best achievable
            // for abrupt termination. Same shape as WireLog.EnsureProcessExitHook.
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try { host.Services.GetService<LacerteSessionManager>()?.Dispose(); }
                catch { /* best-effort */ }
            };

            await host.RunAsync().ConfigureAwait(false);
        }
    }
}
