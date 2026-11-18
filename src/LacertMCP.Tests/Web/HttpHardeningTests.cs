using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using LacertMCP.Server.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace LacertMCP.Tests.Web
{
    /// <summary>Owner detection must ignore the shared "tagged-devices" user.</summary>
    public class TailscaleTaggedOwnerTests
    {
        private const string TaggedSelfStatus = @"{""BackendState"":""Running"",""Self"":{""ID"":""nSelf"",""HostName"":""lacerte-pc"",""TailscaleIPs"":[""100.114.92.33""],""UserID"":9000,""OS"":""windows"",""Tags"":[""tag:server""]},
""Peer"":{""k1"":{""ID"":""nTagged"",""HostName"":""build-box"",""TailscaleIPs"":[""100.70.0.9""],""UserID"":9000,""OS"":""linux"",""Online"":true,""Tags"":[""tag:ci""]},
""k2"":{""ID"":""nMine"",""HostName"":""laptop"",""TailscaleIPs"":[""100.70.0.10""],""UserID"":42,""OS"":""windows"",""Online"":true}},
""User"":{""9000"":{""LoginName"":""tagged-devices""},""42"":{""LoginName"":""vr@""}}}";

        private const string UntaggedSelfStatus = @"{""BackendState"":""Running"",""Self"":{""ID"":""nSelf"",""HostName"":""lacerte-pc"",""TailscaleIPs"":[""100.114.92.33""],""UserID"":42,""OS"":""windows""},
""Peer"":{""k1"":{""ID"":""nMine"",""HostName"":""laptop"",""TailscaleIPs"":[""100.70.0.10""],""UserID"":42,""OS"":""windows"",""Online"":true}},
""User"":{""42"":{""LoginName"":""vr@""}}}";

        private const string TaggedWhois = @"{""Node"":{""ID"":7,""StableID"":""nTagged"",""ComputedName"":""build-box"",""Addresses"":[""100.70.0.9/32""],""Tags"":[""tag:ci""]},""UserProfile"":{""ID"":9000,""LoginName"":""tagged-devices"",""DisplayName"":""Tagged Devices""}}";
        private const string TaggedLoginOnlyWhois = @"{""Node"":{""ID"":7,""StableID"":""nTagged"",""ComputedName"":""build-box"",""Addresses"":[""100.70.0.9/32""]},""UserProfile"":{""ID"":42,""LoginName"":""tagged-devices""}}";
        private const string MineWhois = @"{""Node"":{""ID"":8,""StableID"":""nMine"",""ComputedName"":""laptop"",""Addresses"":[""100.70.0.10/32""]},""UserProfile"":{""ID"":42,""LoginName"":""vr@""}}";

        private static async Task<(TailnetDevice? Self, IReadOnlyList<TailnetDevice> Peers)> Status(string json)
        {
            var ts = new Tailscale { Run = _ => Task.FromResult(json) };
            var (self, peers, error) = await ts.StatusAsync();
            Assert.Null(error);
            return (self, peers);
        }

        private static async Task<TailnetDevice> Whois(string json)
        {
            var ts = new Tailscale { Run = _ => Task.FromResult(json) };
            var r = await ts.WhoisAsync(IPAddress.Parse("100.70.0.9"));
            Assert.False(r.Failed);
            return r.Device!;
        }

        [Fact]
        public async Task TaggedPc_NoRemoteDeviceIsOwner_EvenAnotherTaggedNodeWithTheSameUser()
        {
            var (self, peers) = await Status(TaggedSelfStatus);
            Assert.True(self!.IsTagged);
            var tagged = await Whois(TaggedWhois);
            Assert.True(tagged.IsTagged);
            Assert.Equal(self.OwnerId, tagged.OwnerId); // the trap: same shared user id
            Assert.False(TailnetDevice.SameOwner(self, tagged));
            Assert.False(TailnetDevice.SameOwner(self, await Whois(MineWhois)));
            Assert.All(peers, p => Assert.False(TailnetDevice.SameOwner(self, p)));
        }

        [Fact]
        public async Task UntaggedPc_TaggedCaller_IsNotOwner_ButSameLoginIs()
        {
            var (self, _) = await Status(UntaggedSelfStatus);
            Assert.False(self!.IsTagged);
            Assert.True(TailnetDevice.SameOwner(self, await Whois(MineWhois)));
            Assert.False(TailnetDevice.SameOwner(self, await Whois(TaggedWhois)));
        }

        [Fact]
        public async Task TaggedDevicesLogin_WithoutTags_StillCountsAsTagged()
        {
            var d = await Whois(TaggedLoginOnlyWhois);
            Assert.True(d.IsTagged);
            var (self, _) = await Status(UntaggedSelfStatus);
            Assert.Equal(self!.OwnerId, d.OwnerId);
            Assert.False(TailnetDevice.SameOwner(self, d));
        }

        [Fact]
        public async Task Status_PeerTags_AreParsed()
        {
            var (_, peers) = await Status(TaggedSelfStatus);
            Assert.True(peers.Single(p => p.StableId == "nTagged").IsTagged);
            Assert.False(peers.Single(p => p.StableId == "nMine").IsTagged);
        }

        [Fact]
        public async Task Whois_CliNotRunnable_IsAFailureNamingTheCause_NotUnknownDevice()
        {
            var ts = new Tailscale { Run = _ => throw new TailscaleCliUnavailableException("tailscale CLI not found or not runnable (The system cannot find the file specified). Is Tailscale installed?") };
            var r = await ts.WhoisAsync(IPAddress.Parse("100.99.1.1"));
            Assert.True(r.Failed);
            Assert.Contains("not runnable", r.Error);
            Assert.NotNull(ts.LastError);
        }

        [Fact]
        public async Task Whois_PeerNotFound_IsAnAnswer()
        {
            var ts = new Tailscale { Run = _ => throw new InvalidOperationException("peer not found") };
            var r = await ts.WhoisAsync(IPAddress.Parse("100.99.1.1"));
            Assert.False(r.Failed);
            Assert.Null(r.Device);
        }
    }

    public class LimitedReadStreamTests
    {
        [Fact]
        public void ReadsUpToTheCap()
        {
            var data = new byte[1000];
            using var s = new LimitedReadStream(new MemoryStream(data), 1000);
            var buf = new byte[4096];
            int total = 0, n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) total += n;
            Assert.Equal(1000, total);
        }

        [Fact]
        public void ThrowsPastTheCap_WithoutReadingTheRest()
        {
            var inner = new MemoryStream(new byte[1_000_000]);
            using var s = new LimitedReadStream(inner, 1000);
            var buf = new byte[64 * 1024];
            Assert.Throws<RequestTooLargeException>(() => { while (s.Read(buf, 0, buf.Length) > 0) { } });
            Assert.True(inner.Position <= 1001, "read " + inner.Position + " bytes for a 1000-byte cap");
        }

        [Fact]
        public async Task JsonDeserialize_SurfacesTheLimit_NotAJsonError()
        {
            // A chunked body has no Content-Length: only the counted read stops it.
            var big = "{\"jsonrpc\":\"2.0\",\"method\":\"x\",\"params\":{\"pad\":\"" + new string('a', 10_000) + "\"}}";
            using var s = new LimitedReadStream(new MemoryStream(Encoding.UTF8.GetBytes(big)), 1024);
            await Assert.ThrowsAsync<RequestTooLargeException>(async () =>
                await JsonSerializer.DeserializeAsync<JsonRpcMessage>(s, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
        }
    }

    public class ActivityLogHardeningTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lmcp-activity-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Throttled_RecordsOncePerWindow_AndCountsTheRepeats()
        {
            var log = new ActivityLog(persist: false);
            Assert.True(log.RecordThrottled("noaccess:n1", TimeSpan.FromMinutes(5), "warn", "security", "Refused", "pc1"));
            for (int i = 0; i < 50; i++) Assert.False(log.RecordThrottled("noaccess:n1", TimeSpan.FromMinutes(5), "warn", "security", "Refused", "pc1"));
            Assert.True(log.RecordThrottled("noaccess:n2", TimeSpan.FromMinutes(5), "warn", "security", "Refused", "pc2"));
            Assert.Equal(2, log.Recent().Count);

            // Once the window has passed, the next event carries the suppressed count.
            Assert.True(log.RecordThrottled("noaccess:n1", TimeSpan.Zero, "warn", "security", "Refused", "pc1"));
            Assert.Contains("50 more", log.Recent().First().Detail);
        }

        [Fact]
        public void OverSizeFile_IsRotated_NotTruncated()
        {
            var path = Path.Combine(_dir, "activity.jsonl");
            var log = new ActivityLog(path, maxFileBytes: 2000);
            for (int i = 0; i < 100; i++) log.Record("info", "tool", "event " + i);
            var rotated = Path.Combine(_dir, "activity.1.jsonl");
            Assert.True(File.Exists(rotated));
            Assert.True(new FileInfo(path).Length <= 2000 + 400);
            var all = File.ReadAllLines(rotated).Concat(File.ReadAllLines(path)).ToList();
            Assert.Contains(all, l => l.Contains("event 99"));

            // A restart restores history across both generations.
            var reloaded = new ActivityLog(path, maxFileBytes: 2000);
            Assert.Equal("event 99", reloaded.Recent().First().Message);
            Assert.True(reloaded.Recent(1000).Count > File.ReadAllLines(path).Length);
        }
    }

    public class ToolActivityHardeningTests
    {
        private static CallToolResult Result(string json) => new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
        };

        private static CallToolRequestParams Call(string name, string argsJson)
            => new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson) };

        [Fact]
        public void RefusedWrite_Target_IsSanitized_Truncated_AndSsnMasked()
        {
            var log = new ActivityLog(persist: false);
            ToolActivity.Record(log,
                Call("lacerte_field_write", @"{""clientNumber"":""123-45-6789"",""field"":""" + new string('F', 500) + @"<script>"",""table"":1}"),
                Result(@"{""success"":false,""statusName"":""FieldIsReadOnly""}"));
            var d = log.Recent().Single().Detail!;
            Assert.DoesNotContain("123-45-6789", d);
            Assert.Contains("*****6789", d);
            Assert.DoesNotContain("<script>", d);
            Assert.True(d.Length < 200, d);
        }

        [Fact]
        public void OnlyVerifiedWrites_AreFlaggedVerified()
        {
            var log = new ActivityLog(persist: false);
            ToolActivity.Record(log, Call("lacerte_field_write", @"{""clientNumber"":""1001""}"),
                Result(@"{""success"":true,""result"":{""verified"":true}}"));
            ToolActivity.Record(log, Call("lacerte_client_create", @"{""clientNumber"":""1002""}"),
                Result(@"{""success"":true,""result"":{}}"));
            ToolActivity.Record(log, Call("lacerte_field_write_bulk", @"{""entries"":[1,2]}"),
                Result(@"{""success"":true,""result"":{""succeeded"":1,""failed"":1}}"));
            var events = log.Recent();
            Assert.Equal(1, events.Count(e => e.Verified));
            Assert.Contains("1001", events.Single(e => e.Verified).Detail!);
        }
    }

    public class McpHttpSessionCapTests
    {
        private static McpHttpSession NewSession(string device, DateTime lastSeen)
        {
            var id = McpHttpSessions.NewId();
            var t = new StreamableHttpServerTransport(NullLoggerFactory.Instance) { SessionId = id };
            var server = McpServer.Create(t, new McpServerOptions(), NullLoggerFactory.Instance, null);
            return new McpHttpSession(id, t, server, new SessionScope(), new Caller(false, device, device, false, "100.64.0.1"))
            {
                LastSeenAt = lastSeen,
            };
        }

        [Fact]
        public async Task DeviceAtItsCap_EvictsItsOwnOldestIdle_NotOtherDevices()
        {
            var reg = new McpHttpSessions(new ActivityLog(persist: false));
            var t0 = DateTime.UtcNow.AddHours(-1);
            var other = NewSession("nOther", t0.AddMinutes(-30)); // globally oldest
            Assert.True(await reg.TryAddAsync(other));
            var mine = Enumerable.Range(0, McpHttpSessions.MaxSessionsPerDevice).Select(i => NewSession("nA", t0.AddMinutes(i))).ToList();
            foreach (var s in mine) Assert.True(await reg.TryAddAsync(s));

            Assert.True(await reg.TryAddAsync(NewSession("nA", DateTime.UtcNow)));
            Assert.Equal(McpHttpSessions.MaxSessionsPerDevice, reg.ForDevice("nA").Count);
            Assert.False(reg.TryGet(mine[0].Id, out _));
            Assert.True(reg.TryGet(other.Id, out _));
        }

        [Fact]
        public async Task DeviceAtItsCap_AllBusy_IsRefused()
        {
            var reg = new McpHttpSessions(new ActivityLog(persist: false));
            var held = new List<IDisposable>();
            for (int i = 0; i < McpHttpSessions.MaxSessionsPerDevice; i++)
            {
                var s = NewSession("nA", DateTime.UtcNow);
                Assert.True(await reg.TryAddAsync(s));
                held.Add(s.Enter());
            }
            var extra = NewSession("nA", DateTime.UtcNow);
            Assert.False(await reg.TryAddAsync(extra));
            Assert.False(reg.TryGet(extra.Id, out _));
            await extra.DisposeAsync();
            held.ForEach(h => h.Dispose());
            await reg.CloseAllAsync();
        }

        [Fact]
        public async Task GlobalCap_PrefersTheCallersOwnIdleSession()
        {
            var reg = new McpHttpSessions(new ActivityLog(persist: false));
            var t0 = DateTime.UtcNow.AddHours(-2);
            var devices = McpHttpSessions.MaxSessions / McpHttpSessions.MaxSessionsPerDevice;
            McpHttpSession? callersOldest = null;
            for (int d = 0; d < devices; d++)
            {
                for (int i = 0; i < McpHttpSessions.MaxSessionsPerDevice; i++)
                {
                    // Device 0 has the globally oldest sessions; the caller (device 1) is newer.
                    var s = NewSession("n" + d, t0.AddMinutes(d * 100 + i));
                    if (d == 1 && i == 0) callersOldest = s;
                    Assert.True(await reg.TryAddAsync(s));
                }
            }
            Assert.Equal(McpHttpSessions.MaxSessions, reg.Count);
            // Device 1 is at its own cap too, so it must lose its own session either way;
            // a fresh device at the global cap takes the globally oldest idle one.
            Assert.True(await reg.TryAddAsync(NewSession("n1", DateTime.UtcNow)));
            Assert.False(reg.TryGet(callersOldest!.Id, out _));
            Assert.Equal(McpHttpSessions.MaxSessionsPerDevice, reg.ForDevice("n0").Count);

            Assert.True(await reg.TryAddAsync(NewSession("nNew", DateTime.UtcNow)));
            Assert.Equal(McpHttpSessions.MaxSessions, reg.Count);
            Assert.Equal(McpHttpSessions.MaxSessionsPerDevice - 1, reg.ForDevice("n0").Count);
            await reg.CloseAllAsync();
        }
    }

    public class WritableFieldHealthTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lmcp-fam-" + Guid.NewGuid().ToString("N"));

        public WritableFieldHealthTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void CuratedRowsOnly_WarnsToAddFields()
        {
            File.WriteAllText(Path.Combine(_dir, "individual.json"),
                @"{""I|1|C1_G1"":{""access"":""ReadOnly"",""source"":""sdk-pdf"",""description"":""x"",""addedAt"":""2026-11-15""},
                   ""I|1|C1_15"":{""access"":""ReadWrite"",""source"":""sdk-pdf"",""description"":""y"",""addedAt"":""2026-11-15""}}");
            var c = HttpServer.WritableFieldCheck(_dir);
            Assert.Equal("warn", c.Status);
            Assert.Contains("2 built-in", c.Summary);
            Assert.Contains("1 writable", c.Summary);
            // The dumper's live probe is a stub, so the fix must point at the
            // one path that works: field-access set, one field at a time.
            Assert.Contains("field-access set", c.Fix);
            Assert.DoesNotContain("FieldAccessDumper", c.Fix);
        }

        [Fact]
        public void DumperRows_AreOk_AndCountedAsEntriesNotStrings()
        {
            File.WriteAllText(Path.Combine(_dir, "individual.json"),
                @"{""I|1|C1_15"":{""access"":""ReadWrite"",""source"":""live-probe"",""addedAt"":""2026-11-15""},
                   ""I|1|C1_16"":{""access"":""ReadOnly"",""source"":""live-probe"",""addedAt"":""2026-11-15""}}");
            var c = HttpServer.WritableFieldCheck(_dir);
            Assert.Equal("ok", c.Status);
            Assert.StartsWith("2 field rules", c.Summary);
            Assert.Contains("1 writable", c.Summary);
        }

        [Fact]
        public void EmptyMap_Warns()
        {
            Assert.Equal("warn", HttpServer.WritableFieldCheck(_dir).Status);
        }

        [Fact]
        public void ShippedShards_AreReportedAsBuiltInOnly()
        {
            var shipped = Path.Combine(LacertMCP.Server.Schema.FieldAccessMapLoader.GetSchemaRoot(), "base");
            if (!Directory.Exists(shipped)) return;
            var c = HttpServer.WritableFieldCheck(shipped);
            Assert.Equal("warn", c.Status);
        }
    }

    public class SchemaReloadReadOnlyTests
    {
        [Fact]
        public async Task ReadOnlyDevice_CannotReloadTheSchema()
        {
            SessionScope.Current = new SessionScope { ReadOnlyAccess = true };
            try
            {
                var json = await new SchemaReloadTools().LacerteReloadSchema();
                using var doc = JsonDocument.Parse(json);
                Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(SyntheticStatusCodes.FieldIsReadOnly, doc.RootElement.GetProperty("statusCode").GetInt32());
            }
            finally
            {
                SessionScope.Current = null;
            }
        }
    }

    public class WorkstationSetupUninstallTests
    {
        [Fact]
        public void Uninstall_TellsTheServer_AndClaudeCodeIsResolvedAsAProgram()
        {
            var s = WorkstationSetup.SetupScript("http://100.88.12.7:8767");
            Assert.Contains("/access-check?uninstall=1", s);
            Assert.Contains("-CommandType Application", s);
            Assert.True(s.All(c => c < 128));
        }
    }

    /// <summary>
    /// Real HttpListener round trips over loopback (no Tailscale: the fake CLI
    /// reports no tailnet address, so only localhost is bound).
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class HttpServerRoundTripTests : IAsyncLifetime
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lmcp-http-" + Guid.NewGuid().ToString("N"));
        private LacerteSessionManager? _mgr;
        private McpHttpSessions? _sessions;
        private HttpServer? _server;
        private int _port;

        public async Task InitializeAsync()
        {
            _mgr = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_SIMULATION"] = "1" });
            var activity = new ActivityLog(persist: false);
            _sessions = new McpHttpSessions(activity);
            var ts = new Tailscale { Run = _ => Task.FromResult(@"{""BackendState"":""Running"",""Self"":{""ID"":""nSelf"",""HostName"":""pc"",""TailscaleIPs"":[],""UserID"":1}}") };
            _server = new HttpServer(_mgr, new ServiceCollection().BuildServiceProvider(), Options.Create(new McpServerOptions()),
                NullLoggerFactory.Instance, ts, new DeviceAccess(Path.Combine(_dir, "access.json")), activity, _sessions);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _server.Port = _port;
            await _server.StartAsync(default);
        }

        public async Task DisposeAsync()
        {
            if (_server != null)
            {
                await _server.StopAsync(default);
                _server.Dispose();
            }
            _mgr?.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class RawResponse
        {
            public int Status;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body = string.Empty;
        }

        private async Task<RawResponse> SendAsync(string method, string path, string? body = null, IDictionary<string, string>? headers = null, bool chunked = false)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _port);
            var stream = client.GetStream();
            var bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: localhost:").Append(_port).Append("\r\n");
            sb.Append("Connection: close\r\n");
            if (headers != null) foreach (var h in headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            if (body != null)
            {
                sb.Append("Content-Type: application/json\r\n");
                if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
                else sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            }
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            try
            {
                await stream.WriteAsync(head, 0, head.Length);
                if (body != null && chunked)
                {
                    const int chunk = 16 * 1024;
                    for (int off = 0; off < bytes.Length; off += chunk)
                    {
                        var n = Math.Min(chunk, bytes.Length - off);
                        var size = Encoding.ASCII.GetBytes(n.ToString("X") + "\r\n");
                        await stream.WriteAsync(size, 0, size.Length);
                        await stream.WriteAsync(bytes, off, n);
                        await stream.WriteAsync(new byte[] { 13, 10 }, 0, 2);
                    }
                    var end = Encoding.ASCII.GetBytes("0\r\n\r\n");
                    await stream.WriteAsync(end, 0, end.Length);
                }
                else if (bytes.Length > 0)
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (IOException)
            {
                // The server may answer (413) and stop reading before the body is all sent.
            }

            var ms = new MemoryStream();
            var buf = new byte[8192];
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            }
            catch (IOException) when (ms.Length > 0) { /* reset after the response */ }
            var text = Encoding.UTF8.GetString(ms.ToArray());
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var lines = (split >= 0 ? text.Substring(0, split) : text).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var r = new RawResponse { Status = int.Parse(lines[0].Split(' ')[1]), Body = split >= 0 ? text.Substring(split + 4) : string.Empty };
            foreach (var l in lines.Skip(1))
            {
                var c = l.IndexOf(':');
                if (c > 0) r.Headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
            return r;
        }

        private static readonly Dictionary<string, string> McpAccept = new Dictionary<string, string> { ["Accept"] = "application/json, text/event-stream" };

        private async Task<string> InitializeSessionAsync()
        {
            var init = await SendAsync("POST", "/mcp",
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2025-03-26"",""capabilities"":{},""clientInfo"":{""name"":""test"",""version"":""1""}}}",
                McpAccept);
            Assert.Equal(200, init.Status);
            Assert.True(init.Headers.TryGetValue("Mcp-Session-Id", out var sid), "no Mcp-Session-Id");
            return sid!;
        }

        [Fact]
        public async Task Notification_Gets202_WithContentLengthZero()
        {
            var sid = await InitializeSessionAsync();
            var r = await SendAsync("POST", "/mcp", @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}",
                new Dictionary<string, string>(McpAccept) { ["Mcp-Session-Id"] = sid });
            Assert.Equal(202, r.Status);
            Assert.True(r.Headers.TryGetValue("Content-Length", out var cl), "202 has no Content-Length");
            Assert.Equal("0", cl);
            Assert.False(r.Headers.ContainsKey("Transfer-Encoding"));

            var del = await SendAsync("DELETE", "/mcp", null, new Dictionary<string, string> { ["Mcp-Session-Id"] = sid });
            Assert.Equal(200, del.Status);
            Assert.NotEqual("-1", del.Headers.TryGetValue("Content-Length", out var dcl) ? dcl : null);
            Assert.False(_sessions!.TryGet(sid, out _));
        }

        [Fact]
        public async Task GetMcp_Is405_AndHoldsNoSession()
        {
            var sid = await InitializeSessionAsync();
            var r = await SendAsync("GET", "/mcp", null, new Dictionary<string, string> { ["Accept"] = "text/event-stream", ["Mcp-Session-Id"] = sid });
            Assert.Equal(405, r.Status);
            Assert.Contains("POST", r.Headers["Allow"]);
            Assert.True(_sessions!.TryGet(sid, out var s));
            Assert.Equal(0, s.ActiveRequests);
        }

        [Theory]
        [InlineData("X-Forwarded-For", "100.70.0.9")]
        [InlineData("Forwarded", "for=100.70.0.9")]
        [InlineData("Tailscale-User-Login", "someone@example.com")]
        [InlineData("Via", "1.1 proxy")]
        public async Task LoopbackRequest_RelayedByAProxy_IsRefused(string header, string value)
        {
            var r = await SendAsync("GET", "/api/state", null, new Dictionary<string, string> { [header] = value });
            Assert.Equal(403, r.Status);
            Assert.Contains("proxy", r.Body);
            var mcp = await SendAsync("POST", "/mcp", @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}", new Dictionary<string, string>(McpAccept) { [header] = value });
            Assert.Equal(403, mcp.Status);
        }

        [Fact]
        public async Task ChunkedApiBody_OverTheCap_Is413()
        {
            var body = "{\"pad\":\"" + new string('a', 100 * 1024) + "\"}";
            var r = await SendAsync("POST", "/api/access", body, new Dictionary<string, string> { ["X-Lacerte-Admin"] = "1" }, chunked: true);
            Assert.Equal(413, r.Status);
        }

        [Fact]
        public async Task AccessChange_AppliesToLiveSessionsAtOnce()
        {
            var sid = await InitializeSessionAsync();
            Assert.True(_sessions!.TryGet(sid, out var s));
            // Pretend the session belongs to a tailnet device (loopback sessions are "local").
            var remote = new McpHttpSession(McpHttpSessions.NewId(), s.Transport, s.Server, new SessionScope(), new Caller(false, "nDev", "laptop", false, "100.70.0.10"));
            Assert.True(await _sessions.TryAddAsync(remote));

            Assert.Null(await _server!.ApplyAccessToLiveSessionsAsync("nDev", AccessLevel.Read));
            Assert.True(remote.Scope.ReadOnlyAccess);

            remote.Scope.Context = new SessionContext(@"C:\Lacerte\24Tax\IData", 2024, 'I', ReadOnly: true);
            var note = await _server.ApplyAccessToLiveSessionsAsync("nDev", AccessLevel.Write);
            Assert.False(remote.Scope.ReadOnlyAccess);
            Assert.Contains("lacerte_set_context", note);

            Assert.Null(await _server.ApplyAccessToLiveSessionsAsync("nDev", AccessLevel.None));
            Assert.False(_sessions.TryGet(remote.Id, out _));
        }
    }
}
