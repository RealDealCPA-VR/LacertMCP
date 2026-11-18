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
    ///   <item>This computer: everything (unless the request was relayed by a local proxy: refused).</item>
    ///   <item>Untagged tailnet devices owned by the same Tailscale login as this (untagged) PC: may open the control page (and grant access).</item>
    ///   <item>Any tailnet device: may use /mcp only after it is granted Read or Read&amp;write on the Access tab. Read devices can never write.</item>
    ///   <item>Anything else: refused.</item>
    /// </list>
    /// </summary>
    public sealed partial class HttpServer : IHostedService, IDisposable
    {
        public const int DefaultPort = 8767;
        private const long MaxBodyBytes = 4 * 1024 * 1024;
        private const long MaxApiBodyBytes = 64 * 1024;
        private const string AdminHeader = "X-Lacerte-Admin";

        /// <summary>Repeats of the same refused / setup event from one device are logged at most this often.</summary>
        internal static readonly TimeSpan RepeatEventWindow = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Headers a reverse proxy or tunnel adds. A loopback request carrying any
        /// of them was relayed from somewhere else (tailscale serve, ssh -L, a local
        /// hub), so it must not inherit "this computer" (full access).
        /// </summary>
        internal static readonly string[] ProxyHeaders =
        {
            "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded", "Via", "X-Real-IP",
            "Tailscale-User-Login", "Tailscale-User-Name",
        };

        private readonly LacerteSessionManager _session;
        private readonly IServiceProvider _services;
        private readonly IOptions<McpServerOptions> _mcpOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;
        private readonly Tailscale _tailscale;
        private readonly DeviceAccess _access;
        private readonly ActivityLog _activity;
        private readonly McpHttpSessions _sessions;
        private int _port;

        private HttpListener? _listener;
        private readonly HashSet<string> _boundTailnetIps = new HashSet<string>();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _loop = Task.CompletedTask;
        private Task _maintenance = Task.CompletedTask;
        private TailnetDevice? _self;
        private string? _bindError;
        private string? _lastRecordedBindError;
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

        /// <summary>
        /// Test seam: maps the socket's remote address before the caller is
        /// identified, so a loopback test can stand in for a tailnet device
        /// (whois then runs against the fake Tailscale CLI). Never set outside tests.
        /// </summary>
        internal Func<IPAddress, IPAddress>? RemoteAddressForTests { get; set; }

        /// <summary>Test seam: the port to listen on (set before <see cref="StartAsync"/>).</summary>
        internal int Port
        {
            get => _port;
            set => _port = value;
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
                    _lastRecordedBindError = null;
                    _activity.Record("success", "system", "Listening on the tailnet at http://" + ip + ":" + _port + "/");
                }
                catch (Exception ex)
                {
                    _bindError = "Couldn't listen on the tailnet address " + ip + ": " + ex.Message
                        + ". Run the installer (it adds the URL reservation `netsh http add urlacl url=http://+:" + _port + "/ user=<you>`).";
                    // Retried every 30s: record it once per distinct failure, like the Tailscale-failure path.
                    if (_lastRecordedBindError != _bindError)
                    {
                        _activity.Record("error", "system", "Couldn't listen on the tailnet address " + ip, null, ex.Message);
                        _lastRecordedBindError = _bindError;
                    }
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
                if (RemoteAddressForTests != null) remote = RemoteAddressForTests(remote);

                if (IPAddress.IsLoopback(remote) && IsProxied(req.Headers))
                {
                    _activity.RecordThrottled("proxied", RepeatEventWindow, "warn", "security",
                        "Refused a request relayed to this computer through a proxy or tunnel", null, path);
                    await SendJson(res, 403, new { error = "Refused: this request was relayed through a proxy or tunnel on the Lacerte PC (it carries forwarding headers), so it can't be treated as this computer. Connect directly to this PC's Tailscale address instead." }).ConfigureAwait(false);
                    return;
                }

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

                // Workstation setup: any identified tailnet device may fetch these (they
                // carry no data, only this server's address); access is still enforced on /mcp.
                if (req.HttpMethod == "GET" && (path == "/setup.ps1" || path == "/bridge.ps1" || path == "/access-check"))
                {
                    await HandleWorkstationSetupAsync(req, res, caller, path).ConfigureAwait(false);
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

        /// <summary>
        /// The request came from the control page itself: it carries the
        /// X-Lacerte-Admin header (a cross-site page can't send a custom header
        /// without a CORS preflight, which this server never answers) and, when the
        /// browser sent an Origin, it is this server's own origin.
        /// </summary>
        private static bool IsFromControlPage(HttpListenerRequest req)
        {
            var origin = req.Headers["Origin"];
            return req.Headers[AdminHeader] == "1"
                   && (string.IsNullOrEmpty(origin) || origin == "http://" + req.Headers["Host"]);
        }

        internal static bool IsProxied(System.Collections.Specialized.NameValueCollection headers)
            => ProxyHeaders.Any(h => headers[h] != null);

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
                _activity.RecordThrottled("outside:" + remote, RepeatEventWindow, "warn", "security", "Refused a request from outside the tailnet", remote.ToString());
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
            // Tagged nodes all share one "tagged-devices" user, so ownership only
            // counts between untagged nodes: if this PC is tagged, no remote device
            // is the owner (this computer itself still is).
            var isOwner = TailnetDevice.SameOwner(_self, who.Device);
            return (new Caller(false, who.Device.StableId, who.Device.Name, isOwner, remote.ToString(), who.Device.OwnerLogin), string.Empty, 0);
        }

        /// <summary>When each tailnet device last ran the workstation setup (shown on the Access tab until it has access).</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _setupRuns = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

        private async Task HandleWorkstationSetupAsync(HttpListenerRequest req, HttpListenerResponse res, Caller caller, string path)
        {
            var level = LevelFor(caller);
            if (path == "/access-check")
            {
                // Only the setup script calls this, after it has started: the install
                // path asks for the device's access, the uninstall path says it
                // removed itself (?uninstall=1). Fetching /setup.ps1 alone records
                // nothing, because the uninstall flag lives on the workstation.
                if (!caller.IsLocal)
                {
                    if (req.QueryString["uninstall"] == "1")
                    {
                        _setupRuns.TryRemove(caller.StableId, out _);
                        _activity.RecordThrottled("unsetup:" + caller.StableId, RepeatEventWindow, "info", "access",
                            "Workstation setup removed from this device", caller.Name);
                    }
                    else
                    {
                        _setupRuns[caller.StableId] = DateTime.UtcNow;
                        _activity.RecordThrottled("setup:" + caller.StableId, RepeatEventWindow,
                            level == AccessLevel.None ? "warn" : "info", "access",
                            level == AccessLevel.None ? "Workstation setup ran. Give this device access on the Access tab." : "Workstation setup ran",
                            caller.Name);
                    }
                }
                await SendJson(res, 200, new { device = caller.Name, level = level.ToString().ToLowerInvariant() }).ConfigureAwait(false);
                return;
            }
            if (path == "/bridge.ps1")
            {
                await SendText(res, 200, "text/plain; charset=us-ascii", WorkstationSetup.BridgeScript()).ConfigureAwait(false);
                return;
            }
            // HostAllowed already checked the Host header is this PC's tailnet IP (or loopback) + port.
            var baseUrl = "http://" + req.Headers["Host"];
            await SendText(res, 200, "text/plain; charset=us-ascii", WorkstationSetup.SetupScript(baseUrl)).ConfigureAwait(false);
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
                _activity.RecordThrottled("noaccess:" + caller.StableId, RepeatEventWindow, "warn", "security", "Refused an agent: this device has no Lacerte access yet", caller.Name);
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
                    // No standalone server-to-client SSE stream: this server never sends
                    // unsolicited messages, and HttpListener never notices a dead GET
                    // client, so a held-open stream pinned its session (never reaped or
                    // evicted) until the cap refused every new agent. The Streamable
                    // HTTP spec allows 405 here; clients then use POST responses only.
                    res.Headers["Allow"] = "POST, DELETE";
                    await SendJsonRpcError(res, 405, "Method Not Allowed: this server does not offer a server-to-client event stream; use POST.").ConfigureAwait(false);
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
                // The cap is enforced on the bytes actually read, so a chunked body
                // (no Content-Length) can't bypass it and exhaust memory.
                using var body = new LimitedReadStream(req.InputStream, MaxBodyBytes);
                message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(body, McpJsonUtilities.DefaultOptions).ConfigureAwait(false);
            }
            catch (RequestTooLargeException)
            {
                await SendJsonRpcError(res, 413, "Request too large.").ConfigureAwait(false);
                return;
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
                session = CreateSession(caller);
                if (!await _sessions.TryAddAsync(session).ConfigureAwait(false))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    await SendJsonRpcError(res, 503, "Too many agent connections are busy right now (limit " + McpHttpSessions.MaxSessionsPerDevice
                        + " per device, " + McpHttpSessions.MaxSessions + " in all). Try again shortly.").ConfigureAwait(false);
                    return;
                }
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
                        // On net48, turning SendChunked off leaves ContentLength64 at -1 and
                        // HttpListener then sends "Content-Length: -1", which Node's fetch
                        // (the MCP TypeScript SDK) and curl reject as malformed.
                        res.SendChunked = false;
                        res.ContentType = null;
                        res.ContentLength64 = 0;
                        res.StatusCode = 202;
                    }
                }
                finally
                {
                    SessionScope.Current = null;
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
            if (IsProposalPath(path))
            {
                await HandleProposalApiAsync(ctx, caller, path).ConfigureAwait(false);
                return;
            }
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
            if (!IsFromControlPage(req))
            {
                await SendJson(res, 403, new { error = "Refused: this request did not come from the control page." }).ConfigureAwait(false);
                return;
            }
            if (req.ContentLength64 > MaxApiBodyBytes)
            {
                await SendJson(res, 413, new { error = "Request too large." }).ConfigureAwait(false);
                return;
            }
            JsonElement body;
            try
            {
                using var limited = new LimitedReadStream(req.InputStream, MaxApiBodyBytes);
                using var doc = await JsonDocument.ParseAsync(limited).ConfigureAwait(false);
                body = doc.RootElement.Clone();
            }
            catch (RequestTooLargeException)
            {
                await SendJson(res, 413, new { error = "Request too large." }).ConfigureAwait(false);
                return;
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
                    var note = await ApplyAccessToLiveSessionsAsync(stableId, level).ConfigureAwait(false);
                    _activity.Record(level == AccessLevel.None ? "warn" : "success", "access", "Set " + name + " to " + label, caller.Name, note);
                    await SendJson(res, 200, new { ok = true, note }).ConfigureAwait(false);
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

        /// <summary>
        /// Makes an access change take effect on the device's live connections now,
        /// not just on its next request: removing access closes them; any other
        /// level updates their read-only flag at once, so a downgrade to Read stops
        /// an in-flight bulk write at its next row. Returns a note for the operator
        /// when an upgrade can't fully apply by itself (a context opened while the
        /// device was read only stays read only until the agent sets it again).
        /// </summary>
        internal async Task<string?> ApplyAccessToLiveSessionsAsync(string stableId, AccessLevel level)
        {
            var live = _sessions.ForDevice(stableId);
            if (level == AccessLevel.None)
            {
                foreach (var s in live)
                    await _sessions.RemoveAsync(s.Id, "access removed").ConfigureAwait(false);
                return null;
            }
            foreach (var s in live) s.Scope.ReadOnlyAccess = level == AccessLevel.Read;
            if (level == AccessLevel.Write && live.Any(s => s.Scope.Context?.ReadOnly == true))
            {
                return "Agents already connected from this device opened their client file read only; each must call lacerte_set_context again (or reconnect) before it can write.";
            }
            return null;
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
        /// closed). The shipped base shards hold only a few curated writable rows
        /// (none for C/S/F), so surface that instead of letting every write fail
        /// with 9012 and nobody knowing why.
        /// </summary>
        private static HealthCheck WritableFieldCheck()
            => WritableFieldCheck(Path.Combine(Schema.FieldAccessMapLoader.GetSchemaRoot(), "base"));

        /// <summary>The source the shipped, hand-curated shard rows carry (from the SDK PDF).</summary>
        internal const string CuratedFieldSource = "sdk-pdf";

        /// <summary>
        /// Counts real field rules (shard entries: "M|table|field" → { access, source, … }),
        /// how many are ReadWrite, and how many were added on this PC (any source other
        /// than the curated release rows — `field-access set`, or the dumper once its
        /// live probe exists). The release ships only a handful of curated ReadWrite
        /// rows, so until fields are added nearly every write is refused; warn about
        /// that instead of reporting the curated rows as a loaded map.
        /// </summary>
        internal static HealthCheck WritableFieldCheck(string dir)
        {
            int entries = 0, writable = 0, probed = 0;
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        foreach (var p in doc.RootElement.EnumerateObject())
                        {
                            if (p.Value.ValueKind != JsonValueKind.Object) continue;
                            var access = Prop(p.Value, "access");
                            if (access.Length == 0) continue;
                            entries++;
                            if (string.Equals(access, "ReadWrite", StringComparison.OrdinalIgnoreCase)) writable++;
                            if (!string.Equals(Prop(p.Value, "source"), CuratedFieldSource, StringComparison.OrdinalIgnoreCase)) probed++;
                        }
                    }
                }
            }
            catch
            {
                return new HealthCheck("Writable fields", "warn", "Couldn't read the field access map.", null);
            }
            // The dumper's live probe is not implemented yet (it always fails), so
            // the only working way to add a field is field-access set, one at a time.
            var fix = "Add each field agents should write, after confirming in Lacerte that it is an input field: " +
                      "lacerte-mcp field-access set --module <I|C|P|S|F|...> --table <1-4> --field <field> --access ReadWrite, " +
                      "then call lacerte_reload_schema (or restart). The automatic map builder is not available yet.";
            if (probed > 0)
                return new HealthCheck("Writable fields", "ok", entries + " field rules loaded (" + writable + " writable); writes to any other field are refused.", null);
            if (entries > 0)
                return new HealthCheck("Writable fields", "warn", "Only the " + entries + " built-in field rules are loaded (" + writable + " writable), so almost every field write is refused (reads work). No fields have been added on this PC yet.", fix);
            return new HealthCheck("Writable fields", "warn", "The field access map is empty, so every field write is refused (reads work).", fix);
        }

        private static string Prop(JsonElement e, string name)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString() ?? string.Empty;
            }
            return string.Empty;
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
            // "Today" is the operator's day on this PC, not the UTC day (events are stored in UTC).
            var today = DateTime.Now.Date.ToUniversalTime();
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
                setupUrls = urls.Select(u => u.Substring(0, u.Length - "/mcp".Length) + "/setup.ps1").ToList(),
                health,
                tailscaleError = tsError,
                counts = new
                {
                    connections = _sessions.Count,
                    devicesWithAccess = grants.Count(g => g.Parsed != AccessLevel.None),
                    toolCallsToday = _activity.CountSince(today, e => e.Category == "tool" || e.Category == "write"),
                    writesToday = _activity.CountSince(today, e => e.Category == "write" && e.Level == "success" && e.Verified),
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
                    sameOwner = TailnetDevice.SameOwner(self, p),
                    tagged = p.IsTagged,
                    level = (grants.FirstOrDefault(g => g.StableId == p.StableId)?.Parsed ?? AccessLevel.None).ToString().ToLowerInvariant(),
                    setupAt = _setupRuns.TryGetValue(p.StableId, out var ranAt) ? ranAt : (DateTime?)null,
                }),
                // Grants for devices no longer on the tailnet, so they can still be removed.
                orphanGrants = grants.Where(g => peers.All(p => p.StableId != g.StableId)).Select(g => new { g.StableId, g.Name, g.Level }),
                proposals = ProposalCounts(),
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
