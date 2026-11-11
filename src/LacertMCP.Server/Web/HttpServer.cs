using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Web
{
    /// <summary>
    /// HTTP mode (<c>LacertMCP.Server.exe --http</c> or <c>LACERTE_HTTP=1</c>):
    /// serves the MCP Streamable HTTP endpoint at <c>/mcp</c> and the control
    /// page at <c>/</c>, on loopback and this PC's Tailscale address(es) only —
    /// never on a LAN or public interface.
    ///
    /// <para>Who may do what:</para>
    /// <list type="bullet">
    ///   <item>This computer: everything.</item>
    ///   <item>Tailnet devices owned by the same Tailscale login as this PC: may open the control page (and grant access).</item>
    ///   <item>Any tailnet device: may use /mcp only after it is granted Read or Read&amp;write on the Access tab. Read devices can never write.</item>
    ///   <item>Anything else: refused.</item>
    /// </list>
    /// </summary>
    public sealed class HttpServer : IHostedService, IDisposable
    {
        public const int DefaultPort = 8767;
        private const long MaxBodyBytes = 4 * 1024 * 1024;
        private const string AdminHeader = "X-Lacerte-Admin";

        private readonly LacerteSessionManager _session;
        private readonly IServiceProvider _services;
        private readonly IOptions<McpServerOptions> _mcpOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;
        private readonly Tailscale _tailscale;
        private readonly DeviceAccess _access;
        private readonly ActivityLog _activity;
        private readonly McpHttpSessions _sessions;
        private readonly int _port;

        private HttpListener? _listener;
        private readonly HashSet<string> _boundTailnetIps = new HashSet<string>();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _loop = Task.CompletedTask;
        private Task _maintenance = Task.CompletedTask;
        private TailnetDevice? _self;
        private string? _bindError;
        private bool _tailscaleWasFailing;
        private readonly DateTime _startedAt = DateTime.UtcNow;

        public HttpServer(
            LacerteSessionManager session,
            IServiceProvider services,
            IOptions<McpServerOptions> mcpOptions,
            ILoggerFactory loggerFactory,
            Tailscale tailscale,
            DeviceAccess access,
            ActivityLog activity,
            McpHttpSessions sessions)
        {
            _session = session;
            _services = services;
            _mcpOptions = mcpOptions;
            _loggerFactory = loggerFactory;
            _log = loggerFactory.CreateLogger<HttpServer>();
            _tailscale = tailscale;
            _access = access;
            _activity = activity;
            _sessions = sessions;
            _port = ResolvePort();
        }

        public static int ResolvePort()
        {
            var env = Environment.GetEnvironmentVariable("LACERTE_HTTP_PORT");
            return int.TryParse(env, out var p) && p > 0 && p < 65536 ? p : DefaultPort;
        }

        public static bool IsHttpMode(string[] args)
            => args.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase))
               || Environment.GetEnvironmentVariable("LACERTE_HTTP") == "1";

        // =================================================================
        // Lifecycle
        // =================================================================

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _listener = new HttpListener { IgnoreWriteExceptions = true };
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // 127.0.0.1 needs a URL reservation for non-admin users; fall back to localhost only.
                _listener = new HttpListener { IgnoreWriteExceptions = true };
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
                _bindError = "Only http://localhost works (" + ex.Message + "). Run the installer to add the URL reservation.";
            }
            await RefreshTailnetBindingAsync().ConfigureAwait(false);
            _activity.Record("info", "system", "Lacerte MCP started (" + _session.Mode.ToString().ToLowerInvariant() + " mode, build " + BuildInfo.Version + ")");
            _log.LogInformation("[http] listening on {Urls}", string.Join(", ", _listener.Prefixes));
            _loop = Task.Run(AcceptLoopAsync);
            _maintenance = Task.Run(MaintenanceLoopAsync);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stop.Cancel();
            try { _listener?.Stop(); } catch { /* already stopped */ }
            await _sessions.CloseAllAsync().ConfigureAwait(false);
            try { await Task.WhenAll(_loop, _maintenance).ConfigureAwait(false); } catch { /* cancelled */ }
        }

        public void Dispose()
        {
            try { _listener?.Close(); } catch { /* best effort */ }
            _stop.Dispose();
        }

        private async Task MaintenanceLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    await RefreshTailnetBindingAsync().ConfigureAwait(false);
                    await _sessions.ReapIdleAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[http] maintenance pass failed");
                }
            }
        }

        /// <summary>
        /// Re-resolves this PC's tailnet identity and addresses and binds any new
        /// address. Runs at start and every 30s, so a server started before
        /// Tailscale was up becomes reachable on its own once Tailscale connects.
        /// </summary>
        private async Task RefreshTailnetBindingAsync()
        {
            var (self, _, error) = await _tailscale.StatusAsync().ConfigureAwait(false);
            if (error != null)
            {
                if (!_tailscaleWasFailing) _activity.Record("error", "system", "Tailscale check failed. Other devices can't reach the Lacerte MCP until this is fixed.", null, error);
                _tailscaleWasFailing = true;
                return;
            }
            if (_tailscaleWasFailing) _activity.Record("success", "system", "Tailscale is working again.");
            _tailscaleWasFailing = false;
            if (self != null) _self = self;
            var ips = (self?.Addresses ?? Array.Empty<string>())
                .Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
            foreach (var ip in ips)
            {
                lock (_boundTailnetIps)
                {
                    if (_boundTailnetIps.Contains(ip) || _listener == null) continue;
                }
                try
                {
                    _listener.Prefixes.Add($"http://{ip}:{_port}/");
                    lock (_boundTailnetIps) _boundTailnetIps.Add(ip);
                    _bindError = null;
                    _activity.Record("success", "system", "Listening on the tailnet at http://" + ip + ":" + _port + "/");
                }
                catch (Exception ex)
                {
                    _bindError = "Couldn't listen on the tailnet address " + ip + ": " + ex.Message
                        + ". Run the installer (it adds the URL reservation `netsh http add urlacl url=http://+:" + _port + "/ user=<you>`).";
                    _activity.Record("error", "system", "Couldn't listen on the tailnet address " + ip, null, ex.Message);
                }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[http] accept failed");
                    continue;
                }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        // =================================================================
        // Request pipeline
        // =================================================================

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                SetSecurityHeaders(res);
                if (!HostAllowed(req))
                {
                    await SendJson(res, 421, new { error = "Unrecognized Host header. Use this PC's Tailscale IP address, not a name." }).ConfigureAwait(false);
                    return;
                }
                var path = req.Url?.AbsolutePath ?? "/";
                var remote = req.RemoteEndPoint?.Address ?? IPAddress.None;

                if (path == "/healthz")
                {
                    if (!IPAddress.IsLoopback(remote)) { await SendJson(res, 403, new { error = "healthz is only answered on this computer." }).ConfigureAwait(false); return; }
                    var h = await HealthAsync(deep: true).ConfigureAwait(false);
                    await SendJson(res, h.All(c => c.Status != "bad") ? 200 : 503, new { ok = h.All(c => c.Status != "bad"), checks = h }).ConfigureAwait(false);
                    return;
                }

                var (caller, refusal, refusalStatus) = await IdentifyAsync(remote).ConfigureAwait(false);
                if (caller == null)
                {
                    await SendJson(res, refusalStatus, new { error = refusal }).ConfigureAwait(false);
                    return;
                }

                if (path == "/mcp")
                {
                    await HandleMcpAsync(ctx, caller).ConfigureAwait(false);
                    return;
                }

                if (!caller.IsOwner)
                {
                    await SendJson(res, 403, new { error = "Only this computer and the owner's tailnet devices can open the Lacerte MCP control page." }).ConfigureAwait(false);
                    return;
                }

                if (path == "/" && req.HttpMethod == "GET")
                {
                    await SendText(res, 200, "text/html; charset=utf-8", ControlPage.Html).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    await HandleApiAsync(ctx, caller, path).ConfigureAwait(false);
                    return;
                }
                await SendJson(res, 404, new { error = "Not found." }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[http] request failed");
                try { await SendJson(res, 500, new { error = "Internal error: " + ex.Message }).ConfigureAwait(false); } catch { /* client gone */ }
            }
            finally
            {
                try { res.Close(); } catch { /* client gone */ }
            }
        }

        private bool HostAllowed(HttpListenerRequest req)
        {
            var host = (req.Headers["Host"] ?? string.Empty).ToLowerInvariant();
            if (host == "localhost:" + _port || host == "127.0.0.1:" + _port) return true;
            lock (_boundTailnetIps)
            {
                return _boundTailnetIps.Any(ip => host == ip + ":" + _port);
            }
        }

        /// <summary>
        /// Identifies the caller. Distinguishes "Tailscale itself is broken"
        /// (503, real cause in the message) from "not a tailnet device" (403).
        /// </summary>
        private async Task<(Caller? Caller, string Refusal, int Status)> IdentifyAsync(IPAddress remote)
        {
            if (IPAddress.IsLoopback(remote)) return (Caller.Local(remote.ToString()), string.Empty, 0);
            if (!Tailscale.IsTailnetAddress(remote))
            {
                _activity.Record("warn", "security", "Refused a request from outside the tailnet", remote.ToString());
                return (null, "Only this computer and devices on its tailnet may connect.", 403);
            }
            var who = await _tailscale.WhoisAsync(remote).ConfigureAwait(false);
            if (who.Failed)
            {
                return (null, "The Lacerte MCP can't verify devices right now because " + who.Error
                    + " Fix Tailscale on the Lacerte PC (is it running and signed in?).", 503);
            }
            if (who.Device == null)
            {
                return (null, "No tailnet device answers to " + remote + ".", 403);
            }
            var ownerId = _self?.OwnerId;
            var isOwner = !string.IsNullOrEmpty(ownerId) && who.Device.OwnerId == ownerId;
            return (new Caller(false, who.Device.StableId, who.Device.Name, isOwner, remote.ToString()), string.Empty, 0);
        }

        private AccessLevel LevelFor(Caller caller) => caller.IsLocal ? AccessLevel.Write : _access.LevelFor(caller.StableId);

        // =================================================================
        // MCP endpoint
        // =================================================================

        private async Task HandleMcpAsync(HttpListenerContext ctx, Caller caller)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            if (!string.IsNullOrEmpty(req.Headers["Origin"]))
            {
                // Browsers never need /mcp; refusing any Origin closes DNS-rebinding / drive-by CSRF.
                await SendJson(res, 403, new { error = "Cross-origin requests are not allowed." }).ConfigureAwait(false);
                return;
            }

            var level = LevelFor(caller);
            if (level == AccessLevel.None)
            {
                _activity.Record("warn", "security", "Refused an agent: this device has no Lacerte access yet", caller.Name);
                await SendJsonRpcError(res, 403,
                    "This device (" + caller.Name + ") hasn't been given access to Lacerte. On the Lacerte PC open the Lacerte MCP control page, Access tab, and choose Read only or Read & write for it.").ConfigureAwait(false);
                return;
            }

            var sessionId = req.Headers["Mcp-Session-Id"];
            switch (req.HttpMethod)
            {
                case "POST":
                    await HandleMcpPostAsync(ctx, caller, level, sessionId).ConfigureAwait(false);
                    return;
                case "GET":
                    await HandleMcpGetAsync(ctx, caller, sessionId).ConfigureAwait(false);
                    return;
                case "DELETE":
                    if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGet(sessionId!, out var del) && SameCaller(del, caller))
                    {
                        await _sessions.RemoveAsync(sessionId!, "the agent disconnected").ConfigureAwait(false);
                    }
                    res.StatusCode = 200;
                    return;
                default:
                    await SendJson(res, 405, new { error = "Method not allowed." }).ConfigureAwait(false);
                    return;
            }
        }

        private static bool SameCaller(McpHttpSession s, Caller c) => s.Caller.StableId == c.StableId;

        private async Task HandleMcpPostAsync(HttpListenerContext ctx, Caller caller, AccessLevel level, string? sessionId)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            if (req.ContentLength64 > MaxBodyBytes)
            {
                await SendJsonRpcError(res, 413, "Request too large.").ConfigureAwait(false);
                return;
            }
            JsonRpcMessage? message;
            try
            {
                message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(req.InputStream, McpJsonUtilities.DefaultOptions).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                message = null;
            }
            if (message == null)
            {
                await SendJsonRpcError(res, 400, "Bad Request: the body is not a JSON-RPC message.").ConfigureAwait(false);
                return;
            }

            McpHttpSession? session;
            if (string.IsNullOrEmpty(sessionId))
            {
                if (!(message is JsonRpcRequest r && r.Method == RequestMethods.Initialize))
                {
                    await SendJsonRpcError(res, 400, "Bad Request: start with an initialize request (no Mcp-Session-Id header was sent).").ConfigureAwait(false);
                    return;
                }
                if (!await _sessions.EnsureCapacityAsync().ConfigureAwait(false))
                {
                    await SendJsonRpcError(res, 503, "Too many agent connections are busy right now (limit " + McpHttpSessions.MaxSessions + "). Try again shortly.").ConfigureAwait(false);
                    return;
                }
                session = CreateSession(caller);
                _sessions.Add(session);
                _activity.Record("info", "connection", "Agent connected (" + (level == AccessLevel.Read ? "read only" : "read & write") + ")", caller.Name);
            }
            else if (!_sessions.TryGet(sessionId!, out session) || !SameCaller(session, caller))
            {
                await SendJsonRpcError(res, 404, "Session not found. Reconnect (the server may have restarted).", -32001).ConfigureAwait(false);
                return;
            }

            // Access can change between requests (revoked / downgraded on the page).
            session.Scope.ReadOnlyAccess = level == AccessLevel.Read;
            res.Headers["Mcp-Session-Id"] = session.Id;

            using (session.Enter())
            {
                res.ContentType = "text/event-stream";
                res.Headers["Cache-Control"] = "no-cache,no-store";
                res.SendChunked = true;
                // The tool call inherits this AsyncLocal because the transport
                // flows the request's ExecutionContext (FlowExecutionContextFromRequests).
                SessionScope.Current = session.Scope;
                try
                {
                    var wrote = await session.Transport.HandlePostRequestAsync(message, res.OutputStream, _stop.Token).ConfigureAwait(false);
                    if (!wrote)
                    {
                        res.SendChunked = false;
                        res.ContentType = null;
                        res.StatusCode = 202;
                    }
                }
                finally
                {
                    SessionScope.Current = null;
                }
            }
        }

        private async Task HandleMcpGetAsync(HttpListenerContext ctx, Caller caller, string? sessionId)
        {
            var res = ctx.Response;
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGet(sessionId!, out var session) || !SameCaller(session, caller))
            {
                await SendJsonRpcError(res, 404, "Session not found.", -32001).ConfigureAwait(false);
                return;
            }
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-cache,no-store";
            res.SendChunked = true;
            using (session.Enter())
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, session.Stop.Token))
            {
                try
                {
                    await session.Transport.HandleGetRequestAsync(res.OutputStream, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* client or server closed */ }
                catch (HttpListenerException) { /* client went away */ }
                catch (InvalidOperationException)
                {
                    // Second concurrent GET for the same session.
                }
            }
        }

        private McpHttpSession CreateSession(Caller caller)
        {
            var id = McpHttpSessions.NewId();
            var transport = new StreamableHttpServerTransport(_loggerFactory)
            {
                SessionId = id,
                FlowExecutionContextFromRequests = true,
            };
            var server = McpServer.Create(transport, _mcpOptions.Value, _loggerFactory, _services);
            var scope = new SessionScope { DeviceName = caller.Name };
            var session = new McpHttpSession(id, transport, server, scope, caller);
            session.RunTask = Task.Run(async () =>
            {
                try { await server.RunAsync(session.Stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* closed */ }
                catch (Exception ex) { _log.LogWarning(ex, "[http] MCP session {Id} ended with an error", id); }
            });
            return session;
        }

        // =================================================================
        // Control page API
        // =================================================================

        private async Task HandleApiAsync(HttpListenerContext ctx, Caller caller, string path)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            if (req.HttpMethod == "GET" && path == "/api/state")
            {
                await SendJson(res, 200, await StateAsync().ConfigureAwait(false)).ConfigureAwait(false);
                return;
            }
            if (req.HttpMethod != "POST")
            {
                await SendJson(res, 405, new { error = "Method not allowed." }).ConfigureAwait(false);
                return;
            }
            // Writes: same-origin page only (custom header can't be sent cross-site without a preflight we never answer).
            var origin = req.Headers["Origin"];
            if (req.Headers[AdminHeader] != "1" || (!string.IsNullOrEmpty(origin) && origin != "http://" + req.Headers["Host"]))
            {
                await SendJson(res, 403, new { error = "Refused: this request did not come from the control page." }).ConfigureAwait(false);
                return;
            }
            if (req.ContentLength64 > 64 * 1024)
            {
                await SendJson(res, 413, new { error = "Request too large." }).ConfigureAwait(false);
                return;
            }
            JsonElement body;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.InputStream).ConfigureAwait(false);
                body = doc.RootElement.Clone();
            }
            catch
            {
                body = default;
            }

            switch (path)
            {
                case "/api/access":
                {
                    var stableId = Str(body, "stableId");
                    var name = Str(body, "name");
                    var level = DeviceGrant.ParseLevel(Str(body, "level"));
                    if (stableId.Length == 0 || stableId == "local")
                    {
                        await SendJson(res, 400, new { error = "Pick a tailnet device." }).ConfigureAwait(false);
                        return;
                    }
                    _access.Set(stableId, name, level, caller.Name);
                    var label = level == AccessLevel.Write ? "read & write" : level == AccessLevel.Read ? "read only" : "no access";
                    _activity.Record(level == AccessLevel.None ? "warn" : "success", "access", "Set " + name + " to " + label, caller.Name);
                    // Close the device's live connections when access is removed, so it takes effect now.
                    if (level == AccessLevel.None)
                    {
                        foreach (var s in _sessions.All.Where(s => s.Caller.StableId == stableId))
                            await _sessions.RemoveAsync(s.Id, "access removed").ConfigureAwait(false);
                    }
                    await SendJson(res, 200, new { ok = true }).ConfigureAwait(false);
                    return;
                }
                case "/api/disconnect":
                {
                    var id = Str(body, "sessionId");
                    await _sessions.RemoveAsync(id, "disconnected from the control page by " + caller.Name).ConfigureAwait(false);
                    await SendJson(res, 200, new { ok = true }).ConfigureAwait(false);
                    return;
                }
                case "/api/test-folder":
                {
                    var result = await TestFolderAsync(Str(body, "path"), IntOr(body, "taxYear", 0), Str(body, "module")).ConfigureAwait(false);
                    await SendJson(res, 200, result).ConfigureAwait(false);
                    return;
                }
                case "/api/reconnect":
                {
                    var ok = await StartupRegistration.TryRegisterAsync(_session, _log, CancellationToken.None).ConfigureAwait(false);
                    _activity.Record(ok ? "success" : "error", "system", ok ? "Reconnected to Lacerte" : "Reconnect to Lacerte failed", caller.Name, StartupRegistration.LastResult);
                    await SendJson(res, 200, new { ok, message = StartupRegistration.LastResult }).ConfigureAwait(false);
                    return;
                }
                case "/api/signin":
                {
                    var started = StartCliLogin(out var message);
                    _activity.Record(started ? "info" : "error", "system", started ? "Started Lacerte sign-in on this PC" : "Couldn't start Lacerte sign-in", caller.Name, message);
                    await SendJson(res, started ? 200 : 500, new { ok = started, message }).ConfigureAwait(false);
                    return;
                }
                default:
                    await SendJson(res, 404, new { error = "Not found." }).ConfigureAwait(false);
                    return;
            }
        }

        private async Task<object> TestFolderAsync(string path, int taxYear, string module)
        {
            if (path.Length == 0 || taxYear == 0 || module.Length != 1)
                return new { ok = false, message = "Pick a data folder first." };
            var sw = Stopwatch.StartNew();
            try
            {
                var rows = await _session.ListClientsAt(path, taxYear, char.ToUpperInvariant(module[0]), limit: 100_000).ConfigureAwait(false);
                return new { ok = true, clients = rows.Count, ms = sw.ElapsedMilliseconds, message = "Read " + rows.Count + " client(s) in " + sw.ElapsedMilliseconds + " ms." };
            }
            catch (LacerteSDKError ex)
            {
                return new { ok = false, message = ex.StatusName + ": " + ex.UserMessage, remediation = ex.Remediation };
            }
            catch (Exception ex)
            {
                return new { ok = false, message = ex.Message };
            }
        }

        /// <summary>Launches <c>lacerte-mcp login</c> on this PC's desktop (the Intuit dialogs appear there).</summary>
        private bool StartCliLogin(out string message)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "lacerte-mcp.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "cli", "lacerte-mcp.exe")),
            };
            var exe = candidates.FirstOrDefault(File.Exists);
            if (exe == null)
            {
                message = "lacerte-mcp.exe was not found next to the server (looked in " + string.Join(", ", candidates) + ").";
                return false;
            }
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, "login") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                message = "The Intuit sign-in window is opening on the Lacerte PC. Finish it there, then press Reconnect.";
                if (p != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Run(() => p.WaitForExit()).ConfigureAwait(false);
                        var code = p.ExitCode;
                        _activity.Record(code == 0 ? "success" : "error", "system",
                            code == 0 ? "Lacerte sign-in finished" : "Lacerte sign-in did not complete (exit " + code + ")");
                        if (code == 0) await StartupRegistration.TryRegisterAsync(_session, _log, CancellationToken.None).ConfigureAwait(false);
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        // =================================================================
        // State + health
        // =================================================================

        public sealed record HealthCheck(string Name, string Status, string Summary, string? Fix);

        private async Task<List<HealthCheck>> HealthAsync(bool deep)
        {
            var list = new List<HealthCheck>();
            var snap = _session.SessionStatus();

            if (snap.Mode == SessionMode.Live)
                list.Add(new HealthCheck("Mode", "ok", "Live: agents work with your real Lacerte returns.", null));
            else
                list.Add(new HealthCheck("Mode", "warn", "Simulation: agents see built-in sample clients, not your returns.",
                    snap.ModeResolutionReason ?? "Set LACERTE_LIVE=1 for the server (the installer does this) and restart it."));

            if (snap.IsRegistered && snap.MinutesUntilExpiry is double m)
            {
                var hours = Math.Floor(m / 60);
                list.Add(m < 120
                    ? new HealthCheck("Lacerte sign-in", "warn", "Signed in; the 24-hour Intuit login ends in " + (int)m + " min.", "Use Sign in to Lacerte before it runs out.")
                    : new HealthCheck("Lacerte sign-in", "ok", "Signed in (" + hours + " h left on the 24-hour Intuit login).", null));
            }
            else
            {
                list.Add(new HealthCheck("Lacerte sign-in", "bad", "Not signed in. Agents get NotSignedIn errors.",
                    (StartupRegistration.LastResult ?? string.Empty) + " Use Sign in to Lacerte, finish the Intuit window on this PC, then Reconnect."));
            }

            if (snap.Mode == SessionMode.Live && IsWindows())
            {
                list.Add(OdbcDriverInstalled()
                    ? new HealthCheck("Lacerte ODBC driver", "ok", "LacerteDSIIDriver is installed.", null)
                    : new HealthCheck("Lacerte ODBC driver", "bad", "The 64-bit Lacerte ODBC driver (LacerteDSIIDriver) is not installed, so nothing can be read.", "Install the 64-bit Lacerte SDK MSI from Intuit."));
                list.Add(Type.GetTypeFromProgID("LacerteSDK_Access.LacerteAccess") != null
                    ? new HealthCheck("Lacerte SDK", "ok", "The Lacerte SDK (COM) is registered.", null)
                    : new HealthCheck("Lacerte SDK", "bad", "The Lacerte SDK COM component isn't registered, so writes and sign-in can't work.", "Install the 64-bit Lacerte SDK MSI, then restart this server."));
            }

            list.Add(WritableFieldCheck());

            var root = DataRoot();
            var folders = Tools.SessionTools.DiscoverDatapaths(root);
            list.Add(folders.Count > 0
                ? new HealthCheck("Data folders", "ok", folders.Count + " Lacerte data folder(s) under " + root + ".", null)
                : new HealthCheck("Data folders", "warn", "No {YY}Tax\\{Module}Data folders under " + root + ".", "If Lacerte data lives elsewhere (for example a file server), set LACERTE_DATA_ROOT and restart."));

            if (deep || _tailscale.LastError != null)
            {
                if (deep) await _tailscale.StatusAsync().ConfigureAwait(false);
            }
            list.Add(_tailscale.LastError == null
                ? new HealthCheck("Tailscale", "ok", "Tailscale is connected" + (_self != null ? " as " + _self.Name : string.Empty) + ".", null)
                : new HealthCheck("Tailscale", "bad", _tailscale.LastError, "Open Tailscale on this PC and make sure it is running and signed in."));

            bool bound;
            lock (_boundTailnetIps) bound = _boundTailnetIps.Count > 0;
            list.Add(bound
                ? new HealthCheck("Reachable from the tailnet", "ok", "Other devices can connect at " + string.Join(", ", BoundIps().Select(ip => "http://" + ip + ":" + _port + "/mcp")) + ".", null)
                : new HealthCheck("Reachable from the tailnet", "bad", _bindError ?? "Not listening on a Tailscale address yet.", "Run the installer again (it adds the URL reservation and firewall rule), or check Tailscale."));
            return list;
        }

        /// <summary>
        /// Field writes are refused for any field not in the access map (fail
        /// closed). The shipped base shards are empty until FieldAccessDumper is
        /// run against the real SDK, so surface that instead of letting every
        /// write fail with 9012 and nobody knowing why.
        /// </summary>
        private static HealthCheck WritableFieldCheck()
        {
            int entries = 0;
            try
            {
                var dir = Path.Combine(Schema.FieldAccessMapLoader.GetSchemaRoot(), "base");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        entries += CountLeaves(doc.RootElement);
                    }
                }
            }
            catch
            {
                return new HealthCheck("Writable fields", "warn", "Couldn't read the field access map.", null);
            }
            return entries > 0
                ? new HealthCheck("Writable fields", "ok", entries + " field rules loaded; writes to any other field are refused.", null)
                : new HealthCheck("Writable fields", "warn", "The field access map is empty, so every field write is refused (reads work).",
                    "On this PC run dumper\\LacertMCP.FieldAccessDumper.exe --live --year <year> --out \"" + Path.Combine(Schema.FieldAccessMapLoader.GetSchemaRoot(), "base") + "\" for each tax year you use, then restart the server.");
        }

        private static int CountLeaves(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: return e.EnumerateObject().Sum(p => CountLeaves(p.Value));
                case JsonValueKind.Array: return e.EnumerateArray().Sum(CountLeaves);
                case JsonValueKind.String: return 1;
                default: return 0;
            }
        }

        private List<string> BoundIps()
        {
            lock (_boundTailnetIps) return _boundTailnetIps.ToList();
        }

        private static bool IsWindows() => Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static bool OdbcDriverInstalled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\LacerteDSIIDriver");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        private static string DataRoot()
        {
            var env = Environment.GetEnvironmentVariable("LACERTE_DATA_ROOT");
            return string.IsNullOrWhiteSpace(env) ? @"C:\Lacerte" : env!;
        }

        private async Task<object> StateAsync()
        {
            var snap = _session.SessionStatus();
            var health = await HealthAsync(deep: false).ConfigureAwait(false);
            var (self, peers, tsError) = await _tailscale.StatusAsync().ConfigureAwait(false);
            var grants = _access.All;
            var today = DateTime.UtcNow.Date;
            var urls = new List<string>();
            lock (_boundTailnetIps) urls.AddRange(_boundTailnetIps.Select(ip => "http://" + ip + ":" + _port + "/mcp"));

            return new
            {
                version = BuildInfo.Version,
                startedAt = _startedAt,
                now = DateTime.UtcNow,
                pc = self?.Name ?? Environment.MachineName,
                mode = snap.Mode.ToString().ToLowerInvariant(),
                signedIn = snap.IsRegistered,
                signInExpiresAt = snap.ExpiresAt,
                lastSignInResult = StartupRegistration.LastResult,
                mcpUrls = urls,
                localUrl = "http://localhost:" + _port + "/mcp",
                health,
                tailscaleError = tsError,
                counts = new
                {
                    connections = _sessions.Count,
                    devicesWithAccess = grants.Count(g => g.Parsed != AccessLevel.None),
                    toolCallsToday = _activity.CountSince(today, e => e.Category == "tool" || e.Category == "write"),
                    writesToday = _activity.CountSince(today, e => e.Category == "write" && e.Level == "success"),
                    problemsToday = _activity.CountSince(today, e => e.Level == "error"),
                },
                connections = _sessions.All.Select(s => new
                {
                    id = s.Id,
                    device = s.Caller.Name,
                    client = s.ClientName,
                    since = s.CreatedAt,
                    lastSeen = s.LastSeenAt,
                    busy = s.ActiveRequests > 0,
                    readOnly = s.Scope.ReadOnlyAccess,
                    context = s.Scope.Context == null ? null : new
                    {
                        dataPath = s.Scope.Context.DataPath,
                        taxYear = s.Scope.Context.Year,
                        module = s.Scope.Context.Module.ToString(),
                    },
                }),
                devices = peers.Select(p => new
                {
                    stableId = p.StableId,
                    name = p.Name,
                    os = p.Os,
                    online = p.Online,
                    address = p.Addresses.FirstOrDefault(a => a.Contains('.')),
                    sameOwner = self != null && p.OwnerId == self.OwnerId,
                    level = (grants.FirstOrDefault(g => g.StableId == p.StableId)?.Parsed ?? AccessLevel.None).ToString().ToLowerInvariant(),
                }),
                // Grants for devices no longer on the tailnet, so they can still be removed.
                orphanGrants = grants.Where(g => peers.All(p => p.StableId != g.StableId)).Select(g => new { g.StableId, g.Name, g.Level }),
                dataRoot = DataRoot(),
                dataFolders = Tools.SessionTools.DiscoverDatapaths(DataRoot()),
                activity = _activity.Recent(300),
                storage = new
                {
                    accessFile = _access.FilePath,
                    activityFile = _activity.FilePath,
                    appIdStore = "Windows Credential Manager (lacerte-mcp)",
                    idempotencyCache = Environment.GetEnvironmentVariable("LACERTE_IDEMPOTENCY_PERSIST_PATH")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lacerte-mcp", "idempotency-cache.json"),
                    debugLog = snap.DebugWireEnabled ? snap.WireLogPath : null,
                },
            };
        }

        // =================================================================
        // Helpers
        // =================================================================

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static void SetSecurityHeaders(HttpListenerResponse res)
        {
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            res.Headers["Cache-Control"] = "no-store";
        }

        private static Task SendJson(HttpListenerResponse res, int status, object body)
            => SendText(res, status, "application/json; charset=utf-8", JsonSerializer.Serialize(body, JsonOpts));

        private static Task SendJsonRpcError(HttpListenerResponse res, int status, string message, int code = -32000)
            => SendText(res, status, "application/json; charset=utf-8",
                JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code, message }, id = (object?)null }));

        private static async Task SendText(HttpListenerResponse res, int status, string contentType, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static int IntOr(JsonElement e, string name, int fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;
    }
}
