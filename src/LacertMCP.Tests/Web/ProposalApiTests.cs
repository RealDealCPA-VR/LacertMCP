using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Proposals;
using LacertMCP.Tests.Proposals;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
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
    /// <summary>Minimal raw HTTP/1.1 client (so tests control every header, incl. chunked bodies).</summary>
    internal static class RawHttp
    {
        public sealed class Response
        {
            public int Status;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body = string.Empty;
            public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        }

        public static async Task<Response> SendAsync(int port, string method, string path, string? body = null, IDictionary<string, string>? headers = null, bool chunked = false)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: localhost:").Append(port).Append("\r\n");
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
            var r = new Response { Status = int.Parse(lines[0].Split(' ')[1]), Body = split >= 0 ? text.Substring(split + 4) : string.Empty };
            foreach (var l in lines.Skip(1))
            {
                var c = l.IndexOf(':');
                if (c > 0) r.Headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
            return r;
        }
    }

    /// <summary>
    /// The control page's proposal API over real HttpListener round trips. Loopback
    /// requests are "this computer"; <see cref="HttpServer.RemoteAddressForTests"/>
    /// lets a loopback socket stand in for a tailnet device, identified through the
    /// fake Tailscale CLI (owner = same untagged login as this PC).
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public sealed class ProposalApiTests : IAsyncLifetime
    {
        private const string Client = "1001";
        private const string OwnerIp = "100.70.0.10";
        private const string OtherIp = "100.70.0.20";
        private const string StatusJson = @"{""BackendState"":""Running"",""Self"":{""ID"":""nSelf"",""HostName"":""lacerte-pc"",""TailscaleIPs"":[],""UserID"":42,""OS"":""windows""},""User"":{""42"":{""LoginName"":""owner@example.com""}}}";
        private const string OwnerWhois = @"{""Node"":{""ID"":8,""StableID"":""nOwnerLaptop"",""ComputedName"":""owner-laptop"",""Addresses"":[""100.70.0.10/32""]},""UserProfile"":{""ID"":42,""LoginName"":""owner@example.com""}}";
        private const string OtherWhois = @"{""Node"":{""ID"":9,""StableID"":""nStaffPc"",""ComputedName"":""staff-pc"",""Addresses"":[""100.70.0.20/32""]},""UserProfile"":{""ID"":77,""LoginName"":""staff@example.com""}}";

        private static readonly Dictionary<string, string> Admin = new Dictionary<string, string> { ["X-Lacerte-Admin"] = "1" };

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lmcp-propapi-" + Guid.NewGuid().ToString("N"));
        private LacerteSessionManager? _mgr;
        private ProposalStore? _store;
        private ActivityLog? _activity;
        private DeviceAccess? _access;
        private HttpServer? _server;
        private HttpServer? _bareServer;
        private int _port;
        private IPAddress? _remote;

        public async Task InitializeAsync()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_15");
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: "C1_71");
            _mgr = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_SIMULATION"] = "1" });
            await _mgr.Register();
            _mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            _store = new ProposalStore(Path.Combine(_dir, "proposals"), ProposalTestSupport.NewPin(_dir));
            var services = new ServiceCollection().AddSingleton(new ProposalService(_mgr, _store)).BuildServiceProvider();
            _activity = new ActivityLog(persist: false);
            _access = new DeviceAccess(Path.Combine(_dir, "access.json"));
            (_server, _port) = await StartServerAsync(services, _activity, _access);
            _server.RemoteAddressForTests = ip => _remote ?? ip;
        }

        private async Task<(HttpServer Server, int Port)> StartServerAsync(IServiceProvider services, ActivityLog activity, DeviceAccess access)
        {
            var ts = new Tailscale
            {
                Run = args => Task.FromResult(
                    args.StartsWith("whois", StringComparison.Ordinal)
                        ? (args.Contains(OwnerIp) ? OwnerWhois : args.Contains(OtherIp) ? OtherWhois : throw new InvalidOperationException("peer not found"))
                        : StatusJson),
            };
            var server = new HttpServer(_mgr!, services, Options.Create(new McpServerOptions()), NullLoggerFactory.Instance,
                ts, access, activity, new McpHttpSessions(activity));
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            server.Port = port;
            await server.StartAsync(default);
            return (server, port);
        }

        public async Task DisposeAsync()
        {
            foreach (var s in new[] { _server, _bareServer })
            {
                if (s == null) continue;
                await s.StopAsync(default);
                s.Dispose();
            }
            _mgr?.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ---- helpers ------------------------------------------------------------------------

        private Task<RawHttp.Response> Get(string path, IDictionary<string, string>? headers = null)
            => RawHttp.SendAsync(_port, "GET", path, null, headers ?? Admin);

        private Task<RawHttp.Response> Post(string path, object body, IDictionary<string, string>? headers = null)
            => RawHttp.SendAsync(_port, "POST", path, JsonSerializer.Serialize(body), headers ?? Admin);

        private static ProposalLineInput Field(string field, object value) => new ProposalLineInput
        {
            Kind = "field", Table = 1, Field = field, Value = JsonSerializer.SerializeToElement(value),
            Source = new SourceCitationInput { Document = "Smith organizer.pdf", Page = 3, Location = "Line 7", Excerpt = "TAXPAYER-EXCERPT" },
            Confidence = "high", Reasoning = "from the organizer",
        };

        private static ProposalLineInput Detail(double amount, string description) => new ProposalLineInput
        {
            Kind = "detail",
            Detail = new ProposalDetailInput { Series = "11", Code = "1", Suffix = "1000", Amount = amount, Description = description, Source = "Source", Tsj = "Taxpayer" },
            DictionaryId = "1040.w2_wages",
            Source = new SourceCitationInput { Document = "W-2.pdf", Location = "Box 1" },
            Confidence = "low",
        };

        /// <summary>An agent-built, submitted proposal (via the MCP tools) with lines L1 (C1_15), L2 (C1_71), L3 (detail).</summary>
        private async Task<string> SubmittedProposalAsync()
        {
            var tools = new ProposalTools(_mgr!, _store!);
            var created = JsonDocument.Parse(await tools.LacerteProposalCreate(Client, "2024 organizer")).RootElement;
            var id = created.GetProperty("result").GetProperty("proposal").GetProperty("id").GetString()!;
            var added = JsonDocument.Parse(await tools.LacerteProposalAddLines(id, new[]
            {
                Field("C1_15", "APPROVED-VALUE"),
                Field("C1_71", "REJECTED-VALUE"),
                Detail(4321.5, "PROPOSAL W2 ACME"),
            })).RootElement;
            Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());
            var submitted = JsonDocument.Parse(await tools.LacerteProposalSubmit(id)).RootElement;
            Assert.True(submitted.GetProperty("success").GetBoolean(), submitted.ToString());
            return id;
        }

        private int Version(string id) => _store!.Get(id)!.Version;

        private async Task DecideAllAsync(string id, string reject = "L2")
        {
            foreach (var line in new[] { "L1", "L2", "L3" })
            {
                var r = await Post("/api/proposals/" + id + "/line", new { lineId = line, decision = line == reject ? "rejected" : "approved", note = "note " + line, expectedVersion = Version(id) });
                Assert.Equal(200, r.Status);
            }
        }

        private void AssertActivityHasNoTaxpayerValues()
        {
            var all = string.Join("\n", _activity!.Recent(1000).Select(e => e.Message + " | " + e.Detail + " | " + e.Device));
            foreach (var secret in new[] { "APPROVED-VALUE", "REJECTED-VALUE", "PROPOSAL W2", "ACME", "4321", "TAXPAYER-EXCERPT", "organizer", "W-2.pdf", "SECRET-NOTE", "note L", "Brown", ProposalTestSupport.TestPin })
                Assert.DoesNotContain(secret, all);
        }

        // ---- who may review -------------------------------------------------------------------

        [Fact]
        public async Task NonOwnerDeviceWithWriteAccess_IsRefusedOnEveryProposalEndpoint()
        {
            var id = await SubmittedProposalAsync();
            _access!.Set("nStaffPc", "staff-pc", AccessLevel.Write, "test");
            _remote = IPAddress.Parse(OtherIp);

            // The device really has write access for its agents...
            var init = await RawHttp.SendAsync(_port, "POST", "/mcp",
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2025-03-26"",""capabilities"":{},""clientInfo"":{""name"":""agent"",""version"":""1""}}}",
                new Dictionary<string, string> { ["Accept"] = "application/json, text/event-stream" });
            Assert.Equal(200, init.Status);

            // ...and still can't see, decide, approve, reject, return, resolve or commit.
            var v = Version(id);
            Assert.Equal(403, (await Get("/api/proposals")).Status);
            Assert.Equal(403, (await Get("/api/proposals/" + id)).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "approved", expectedVersion = v })).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v })).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/reject", new { note = "no" })).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/return", new { note = "no" })).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/resolve", new { lineId = "L3", rowIsInReturn = true, pin = ProposalTestSupport.TestPin })).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/commit", new { expectedVersion = v })).Status);

            var p = _store!.Get(id)!;
            Assert.Equal(ProposalStatus.Submitted, p.Status);
            Assert.Null(p.Approval);
            Assert.All(p.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));
        }

        [Fact]
        public void ReviewRule_IsOwnerOrThisComputer_NeverADeviceGrant()
        {
            Assert.True(HttpServer.MayReview(Caller.Local("127.0.0.1")));
            Assert.True(HttpServer.MayReview(new Caller(false, "n1", "laptop", true, OwnerIp, "owner@example.com")));
            Assert.False(HttpServer.MayReview(new Caller(false, "n2", "staff-pc", false, OtherIp, "staff@example.com")));
            Assert.StartsWith("this computer (", HttpServer.ReviewerIdentity(Caller.Local("127.0.0.1")));
            Assert.Equal("owner@example.com on laptop", HttpServer.ReviewerIdentity(new Caller(false, "n1", "laptop", true, OwnerIp, "owner@example.com")));
        }

        [Fact]
        public async Task ThisComputer_And_OwnerDevice_CanListAndGet_AndTheOwnerIsRecordedAsReviewer()
        {
            var id = await SubmittedProposalAsync();

            var list = await Get("/api/proposals");
            Assert.Equal(200, list.Status);
            var row = list.Json.GetProperty("proposals").EnumerateArray().Single(x => x.GetProperty("summary").GetProperty("id").GetString() == id);
            Assert.Equal("Submitted", row.GetProperty("summary").GetProperty("status").GetString());
            Assert.Equal(3, row.GetProperty("summary").GetProperty("lineCount").GetInt32());
            Assert.False(row.GetProperty("state").GetProperty("readyToApprove").GetBoolean());

            Assert.Equal(1, (await Get("/api/proposals?status=submitted&client=" + Client)).Json.GetProperty("count").GetInt32());
            Assert.Equal(0, (await Get("/api/proposals?status=Committed")).Json.GetProperty("count").GetInt32());
            Assert.Equal(0, (await Get("/api/proposals?client=NOPE")).Json.GetProperty("count").GetInt32());
            Assert.Equal(400, (await Get("/api/proposals?status=approvedish")).Status);

            var one = await Get("/api/proposals/" + id);
            Assert.Equal(200, one.Status);
            Assert.Equal("L1", one.Json.GetProperty("proposal").GetProperty("lines")[0].GetProperty("lineId").GetString());
            Assert.Equal("Low", one.Json.GetProperty("proposal").GetProperty("lines")[2].GetProperty("confidence").GetString());
            Assert.True(one.Json.GetProperty("state").GetProperty("contentHash").GetString()!.Length == 64);
            Assert.Equal(404, (await Get("/api/proposals/" + new string('a', 24))).Status);
            Assert.Equal(404, (await Get("/api/proposals/not-an-id!")).Status);

            // The owner's laptop over the tailnet: same rights as this computer.
            _remote = IPAddress.Parse(OwnerIp);
            Assert.Equal(200, (await Get("/api/proposals/" + id)).Status);
            await DecideAllAsync(id);
            var approved = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = ProposalTestSupport.TestPin, note = "SECRET-NOTE ok" });
            Assert.Equal(200, approved.Status);
            var approval = _store!.Get(id)!.Approval!;
            Assert.Equal("owner@example.com on owner-laptop", approval.By);
            Assert.Equal(ProposalStore.ChannelControlPage, approval.Channel);
            var ev = _activity!.Recent().First(e => e.Category == "proposal");
            Assert.Contains("Approved proposal " + id, ev.Message);
            Assert.Equal("owner-laptop", ev.Device);
            AssertActivityHasNoTaxpayerValues();
        }

        [Fact]
        public async Task MissingAdminHeader_OrCrossOrigin_IsRefused_ReadsIncluded()
        {
            var id = await SubmittedProposalAsync();
            var none = new Dictionary<string, string>();
            Assert.Equal(403, (await Get("/api/proposals", none)).Status);
            Assert.Equal(403, (await Get("/api/proposals/" + id, none)).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "approved", expectedVersion = Version(id) }, none)).Status);

            var crossSite = new Dictionary<string, string> { ["X-Lacerte-Admin"] = "1", ["Origin"] = "http://evil.example" };
            Assert.Equal(403, (await Get("/api/proposals/" + id, crossSite)).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "approved", expectedVersion = Version(id) }, crossSite)).Status);
            Assert.Equal(403, (await Post("/api/proposals/" + id + "/reject", new { note = "x" }, crossSite)).Status);

            // The page's own origin is fine.
            var sameOrigin = new Dictionary<string, string> { ["X-Lacerte-Admin"] = "1", ["Origin"] = "http://localhost:" + _port };
            Assert.Equal(200, (await Get("/api/proposals/" + id, sameOrigin)).Status);
            Assert.All(_store!.Get(id)!.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));
            Assert.Equal(ProposalStatus.Submitted, _store.Get(id)!.Status);
        }

        [Fact]
        public async Task OversizeBody_Is413_EvenChunked()
        {
            var id = await SubmittedProposalAsync();
            var body = "{\"note\":\"" + new string('a', 100 * 1024) + "\"}";
            Assert.Equal(413, (await RawHttp.SendAsync(_port, "POST", "/api/proposals/" + id + "/reject", body, Admin, chunked: true)).Status);
            Assert.Equal(413, (await RawHttp.SendAsync(_port, "POST", "/api/proposals/" + id + "/reject", body, Admin)).Status);
            Assert.Equal(400, (await RawHttp.SendAsync(_port, "POST", "/api/proposals/" + id + "/reject", "not json", Admin)).Status);
            Assert.Equal(ProposalStatus.Submitted, _store!.Get(id)!.Status);
        }

        [Fact]
        public async Task NoProposalStore_Is503()
        {
            var activity = new ActivityLog(persist: false);
            int port;
            (_bareServer, port) = await StartServerAsync(new ServiceCollection().BuildServiceProvider(), activity, new DeviceAccess(Path.Combine(_dir, "access2.json")));
            var r = await RawHttp.SendAsync(port, "GET", "/api/proposals", null, Admin);
            Assert.Equal(503, r.Status);
            Assert.Contains("LACERTE_HTTP_DATA_DIR", r.Body);
            var state = await RawHttp.SendAsync(port, "GET", "/api/state", null, null);
            Assert.Equal(200, state.Status);
            Assert.Equal(JsonValueKind.Null, state.Json.GetProperty("proposals").ValueKind);
        }

        // ---- review rules ---------------------------------------------------------------------

        [Fact]
        public async Task LineDecision_StaleVersion_Is409_AndBadInputIs400()
        {
            var id = await SubmittedProposalAsync();
            var v = Version(id);
            var stale = await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "approved", expectedVersion = v - 1 });
            Assert.Equal(409, stale.Status);
            Assert.Equal("ProposalConflict", stale.Json.GetProperty("statusName").GetString());
            Assert.Contains("version", stale.Json.GetProperty("error").GetString());
            Assert.Equal(v, stale.Json.GetProperty("version").GetInt32());

            Assert.Equal(400, (await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "maybe", expectedVersion = v })).Status);
            Assert.Equal(400, (await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "approved" })).Status);
            Assert.Equal(400, (await Post("/api/proposals/" + id + "/line", new { lineId = "L99", decision = "approved", expectedVersion = v })).Status);
            Assert.Equal(404, (await Post("/api/proposals/" + new string('b', 24) + "/line", new { lineId = "L1", decision = "approved", expectedVersion = 1 })).Status);
            Assert.Equal(404, (await Post("/api/proposals/" + id + "/bogus", new { })).Status);
            Assert.Equal(405, (await RawHttp.SendAsync(_port, "DELETE", "/api/proposals/" + id + "/line", null, Admin)).Status);
            Assert.All(_store!.Get(id)!.Lines, l => Assert.Equal(ReviewerDecision.Pending, l.ReviewerDecision));
        }

        [Fact]
        public async Task Approve_WithUndecidedLines_Is409_WithBlockers()
        {
            var id = await SubmittedProposalAsync();
            Assert.Equal(200, (await Post("/api/proposals/" + id + "/line", new { lineId = "L1", decision = "approved", expectedVersion = Version(id) })).Status);
            var r = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = ProposalTestSupport.TestPin });
            Assert.Equal(409, r.Status);
            var blockers = r.Json.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()!).ToList();
            Assert.Contains(blockers, b => b.Contains("L2") && b.Contains("L3"));
            Assert.Equal("Submitted", r.Json.GetProperty("proposalStatus").GetString());
            Assert.Null(_store!.Get(id)!.Approval);
            Assert.DoesNotContain(_activity!.Recent(), e => e.Category == "proposal");
        }

        [Fact]
        public async Task ReturnAndReject_NeedANote()
        {
            var id = await SubmittedProposalAsync();
            Assert.Equal(400, (await Post("/api/proposals/" + id + "/return", new { })).Status);
            Assert.Equal(400, (await Post("/api/proposals/" + id + "/return", new { note = "   " })).Status);
            Assert.Equal(400, (await Post("/api/proposals/" + id + "/reject", new { note = "" })).Status);
            Assert.Equal(ProposalStatus.Submitted, _store!.Get(id)!.Status);

            var ret = await Post("/api/proposals/" + id + "/return", new { note = "SECRET-NOTE use box 12 instead" });
            Assert.Equal(200, ret.Status);
            Assert.Equal("ReturnedForChanges", ret.Json.GetProperty("proposal").GetProperty("status").GetString());
            var p = _store.Get(id)!;
            Assert.Equal("returnedForChanges", p.LastReview!.Action);
            Assert.StartsWith("this computer (", p.LastReview.By);
            Assert.Equal(ProposalStore.ChannelControlPage, p.LastReview.Channel);

            var rej = await Post("/api/proposals/" + id + "/reject", new { note = "SECRET-NOTE wrong client" });
            Assert.Equal(200, rej.Status);
            Assert.Equal(ProposalStatus.Rejected, _store.Get(id)!.Status);
            // Terminal: rejecting again is a conflict.
            Assert.Equal(409, (await Post("/api/proposals/" + id + "/reject", new { note = "again" })).Status);

            Assert.Contains(_activity!.Recent(), e => e.Category == "proposal" && e.Message.StartsWith("Returned proposal " + id));
            Assert.Contains(_activity.Recent(), e => e.Category == "proposal" && e.Message.StartsWith("Rejected proposal " + id));
            AssertActivityHasNoTaxpayerValues();
        }

        // ---- commit ---------------------------------------------------------------------------

        [Fact]
        public async Task FullFlow_DecideApproveCommit_WritesOnlyApprovedLines()
        {
            var id = await SubmittedProposalAsync();
            var rejectedBefore = (await _mgr!.ReadTable(1, Client)).Single().TryGetValue("C1_71", out var rb) ? rb : null;
            var detailBefore = (await _mgr.ReadDetail(Client)).Count;

            // Commit before approval is refused.
            Assert.Equal(409, (await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) })).Status);

            await DecideAllAsync(id);
            var approve = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = ProposalTestSupport.TestPin });
            Assert.Equal(200, approve.Status);
            Assert.True(approve.Json.GetProperty("state").GetProperty("readyToCommit").GetBoolean());

            Assert.Equal(400, (await Post("/api/proposals/" + id + "/commit", new { })).Status);
            var stale = await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) + 1 });
            Assert.Equal(409, stale.Status);
            Assert.Equal(ProposalStatus.Approved, _store!.Get(id)!.Status);

            var commit = await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) });
            Assert.Equal(200, commit.Status);
            var report = commit.Json.GetProperty("report");
            Assert.Equal("Committed", report.GetProperty("status").GetString());
            Assert.Equal(2, report.GetProperty("ranCount").GetInt32());
            var lines = report.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("lineId").GetString()!);
            Assert.False(lines.ContainsKey("L2"));
            Assert.Equal("Succeeded", lines["L1"].GetProperty("result").GetProperty("status").GetString());
            Assert.True(lines["L1"].GetProperty("result").GetProperty("verified").GetBoolean());
            Assert.Equal("SucceededUnverified", lines["L3"].GetProperty("result").GetProperty("status").GetString());
            Assert.Equal("Committed", commit.Json.GetProperty("proposal").GetProperty("status").GetString());
            Assert.False(commit.Json.GetProperty("state").GetProperty("readyToCommit").GetBoolean());

            var row = (await _mgr.ReadTable(1, Client)).Single();
            Assert.Equal("APPROVED-VALUE", row["C1_15"]);
            Assert.Equal(rejectedBefore, row.TryGetValue("C1_71", out var ra) ? ra : null);
            Assert.Equal(detailBefore + 1, (await _mgr.ReadDetail(Client)).Count);

            var stored = _store.Get(id)!;
            Assert.Equal(ProposalStatus.Committed, stored.Status);
            Assert.StartsWith("this computer (", stored.CommitLease!.By);

            // Nothing left: a second commit is refused (409) and writes nothing.
            Assert.Equal(409, (await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) })).Status);
            Assert.Equal(detailBefore + 1, (await _mgr.ReadDetail(Client)).Count);

            var write = _activity!.Recent().First(e => e.Category == "write" && e.Message.StartsWith("Committed proposal " + id));
            Assert.Equal("success", write.Level);
            Assert.Contains("2 of 2 line(s) written (1 verified)", write.Detail);
            Assert.False(write.Verified); // the detail row was accepted but not read back
            Assert.Contains(_activity.Recent(), e => e.Message.StartsWith("Commit of proposal " + id + " refused"));
            AssertActivityHasNoTaxpayerValues();
        }

        [Fact]
        public async Task UncertainLine_IsResolvedByAPerson()
        {
            var id = await SubmittedProposalAsync();
            await DecideAllAsync(id);
            Assert.Equal(200, (await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = ProposalTestSupport.TestPin })).Status);
            Assert.Equal(200, (await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) })).Status);

            // Only an Uncertain line can be resolved.
            Assert.Equal(409, (await Post("/api/proposals/" + id + "/resolve", new { lineId = "L1", rowIsInReturn = true, pin = ProposalTestSupport.TestPin })).Status);

            // As if the detail write had failed after the row may have landed.
            _store!.Mutate(id, q =>
            {
                q.Lines.Single(l => l.LineId == "L3").Commit!.Status = LineCommitStatus.Uncertain;
                q.Status = ProposalStatus.PartiallyCommitted;
            });
            var view = await Get("/api/proposals/" + id);
            Assert.Equal("L3", view.Json.GetProperty("state").GetProperty("uncertainLineIds")[0].GetString());

            Assert.Equal(400, (await Post("/api/proposals/" + id + "/resolve", new { lineId = "L3" })).Status);
            var r = await Post("/api/proposals/" + id + "/resolve", new { lineId = "L3", rowIsInReturn = true, pin = ProposalTestSupport.TestPin, note = "SECRET-NOTE saw it" });
            Assert.Equal(200, r.Status);
            Assert.Equal("Committed", r.Json.GetProperty("proposal").GetProperty("status").GetString());
            var l3 = _store.Get(id)!.Lines.Single(l => l.LineId == "L3");
            Assert.Equal(LineCommitStatus.Succeeded, l3.Commit!.Status);
            Assert.Contains("this computer (", l3.Commit.Note);

            var ev = _activity!.Recent().First(e => e.Category == "proposal");
            Assert.StartsWith("Resolved line L3 of proposal " + id, ev.Message);
            AssertActivityHasNoTaxpayerValues();
        }

        // ---- approval PIN ---------------------------------------------------------------------

        private void AssertPinNeverLeaked(params RawHttp.Response[] responses)
        {
            var all = string.Join("\n", _activity!.Recent(1000).Select(e => e.Message + " | " + e.Detail + " | " + e.Device));
            foreach (var secret in new[] { ProposalTestSupport.TestPin, "WRONG-PIN-77" })
            {
                Assert.DoesNotContain(secret, all);
                foreach (var r in responses) Assert.DoesNotContain(secret, r.Body);
                Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_dir, ApprovalPin.FileName)));
                foreach (var f in Directory.GetFiles(Path.Combine(_dir, "proposals"), "*.json")) Assert.DoesNotContain(secret, File.ReadAllText(f));
            }
        }

        [Fact]
        public async Task Approve_RequiresPinAndClientNumber_RecordsTarget()
        {
            var id = await SubmittedProposalAsync();
            await DecideAllAsync(id);
            var v = Version(id);

            Assert.Equal(400, (await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, clientNumber = Client })).Status);
            Assert.Equal(400, (await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, pin = ProposalTestSupport.TestPin })).Status);
            var wrongClient = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, clientNumber = "1002", pin = ProposalTestSupport.TestPin });
            Assert.Equal(409, wrongClient.Status);
            Assert.Contains("not this proposal's client", wrongClient.Json.GetProperty("error").GetString());
            Assert.Equal(0, _store!.Pin.Status().FailedAttempts); // the PIN wasn't even checked
            Assert.Null(_store.Get(id)!.Approval);

            var ok = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, clientNumber = " " + Client + " ", pin = ProposalTestSupport.TestPin });
            Assert.Equal(200, ok.Status);
            Assert.StartsWith("client 1001 · Bill Brown · Individual (1040) 2024 · ", ok.Json.GetProperty("state").GetProperty("entriesGoInto").GetString());
            var approval = _store.Get(id)!.Approval!;
            Assert.True(approval.PinVerified);
            Assert.Equal("Bill Brown", approval.TargetReturn!.ClientName);
            Assert.Equal("BROW", approval.TargetReturn.ClientId);
            Assert.Equal(SeedData.DataPathIData2024, approval.TargetContext!.DataPath);
            Assert.True(ok.Json.GetProperty("approvalPin").GetProperty("isSet").GetBoolean());
            AssertPinNeverLeaked(wrongClient, ok);
            AssertActivityHasNoTaxpayerValues();
        }

        [Fact]
        public async Task Approve_WrongPin_Is403_ThenLocked429_LoggedWithoutThePin()
        {
            var id = await SubmittedProposalAsync();
            await DecideAllAsync(id);
            var v = Version(id);
            var responses = new List<RawHttp.Response>();
            for (int i = 1; i <= 4; i++)
            {
                var r = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, clientNumber = Client, pin = "WRONG-PIN-77" });
                responses.Add(r);
                Assert.Equal(403, r.Status);
                Assert.Equal(5 - i, r.Json.GetProperty("remainingAttempts").GetInt32());
                Assert.Contains("Wrong approval PIN", r.Json.GetProperty("error").GetString());
            }
            var locking = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, clientNumber = Client, pin = "WRONG-PIN-77" });
            Assert.Equal(429, locking.Status);
            Assert.True(locking.Json.TryGetProperty("lockedUntil", out _));
            // Locked: even the right PIN is refused now.
            var locked = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = v, clientNumber = Client, pin = ProposalTestSupport.TestPin });
            Assert.Equal(429, locked.Status);
            responses.Add(locking);
            responses.Add(locked);

            var p = _store!.Get(id)!;
            Assert.Equal(ProposalStatus.Submitted, p.Status);
            Assert.Null(p.Approval);
            var list = await Get("/api/proposals");
            Assert.NotEqual(JsonValueKind.Null, list.Json.GetProperty("approvalPin").GetProperty("lockedUntilUtc").ValueKind);

            var refusals = _activity!.Recent().Where(e => e.Category == "security" && e.Message.StartsWith("Approval PIN refused for proposal " + id)).ToList();
            Assert.Equal(6, refusals.Count);
            Assert.Contains(refusals, e => e.Detail!.Contains("wrong PIN; 4 attempt(s) left"));
            Assert.Contains(refusals, e => e.Detail!.Contains("locked"));
            Assert.All(refusals, e => Assert.Contains("this computer (", e.Message));
            AssertPinNeverLeaked(responses.ToArray());
        }

        [Fact]
        public async Task Approve_NoPinSet_Is409_WithTheSetupCommand()
        {
            _store!.Pin.Reset();
            var id = await SubmittedProposalAsync();
            await DecideAllAsync(id);
            var list = await Get("/api/proposals");
            Assert.False(list.Json.GetProperty("approvalPin").GetProperty("isSet").GetBoolean());
            var r = await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = "123987" });
            Assert.Equal(409, r.Status);
            Assert.Contains("lacerte-mcp approval-pin set", r.Json.GetProperty("error").GetString());
            Assert.Equal("NotSet", r.Json.GetProperty("pinCheck").GetString());
            Assert.Null(_store.Get(id)!.Approval);
            Assert.Contains(_activity!.Recent(), e => e.Category == "security" && e.Detail!.Contains("no approval PIN is set"));
        }

        [Fact]
        public async Task Resolve_RequiresThePin()
        {
            var id = await SubmittedProposalAsync();
            await DecideAllAsync(id);
            Assert.Equal(200, (await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = ProposalTestSupport.TestPin })).Status);
            Assert.Equal(200, (await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) })).Status);
            _store!.Mutate(id, q =>
            {
                q.Lines.Single(l => l.LineId == "L3").Commit!.Status = LineCommitStatus.Uncertain;
                q.Status = ProposalStatus.PartiallyCommitted;
            });

            Assert.Equal(400, (await Post("/api/proposals/" + id + "/resolve", new { lineId = "L3", rowIsInReturn = true })).Status);
            var wrong = await Post("/api/proposals/" + id + "/resolve", new { lineId = "L3", rowIsInReturn = false, pin = "WRONG-PIN-77" });
            Assert.Equal(403, wrong.Status);
            Assert.Equal(4, wrong.Json.GetProperty("remainingAttempts").GetInt32());
            var l3 = _store.Get(id)!.Lines.Single(l => l.LineId == "L3");
            Assert.Equal(LineCommitStatus.Uncertain, l3.Commit!.Status);
            Assert.Equal(0, l3.KeyGeneration);

            var ok = await Post("/api/proposals/" + id + "/resolve", new { lineId = "L3", rowIsInReturn = false, pin = ProposalTestSupport.TestPin });
            Assert.Equal(200, ok.Status);
            Assert.Equal(0, _store.Pin.Status().FailedAttempts); // a right PIN resets the counter
            Assert.Contains(_activity!.Recent(), e => e.Category == "security" && e.Message.Contains("(resolve)"));
            AssertPinNeverLeaked(wrong, ok);
        }

        [Fact]
        public async Task Commit_RefusesWhenTheClientWasRenamedAfterApproval()
        {
            var id = await SubmittedProposalAsync();
            await DecideAllAsync(id);
            Assert.Equal(200, (await Post("/api/proposals/" + id + "/approve", new { expectedVersion = Version(id), clientNumber = Client, pin = ProposalTestSupport.TestPin })).Status);
            var detailBefore = (await _mgr!.ReadDetail(Client)).Count;
            var row = _mgr.SimulationStoreForTest!.TryGetModule(SeedData.DataPathIData2024, 2024, 'I')!.Clients[Client];
            row.Fields["C1_2"] = "Browning";

            var r = await Post("/api/proposals/" + id + "/commit", new { expectedVersion = Version(id) });
            Assert.Equal(409, r.Status);
            var error = r.Json.GetProperty("error").GetString()!;
            Assert.Contains("Approved for client 1001 · Bill Brown", error);
            Assert.Contains("Bill Browning", error);
            Assert.Equal(ProposalStatus.Approved, _store!.Get(id)!.Status);
            Assert.Equal(detailBefore, (await _mgr.ReadDetail(Client)).Count);
            Assert.NotEqual("APPROVED-VALUE", (await _mgr.ReadTable(1, Client)).Single().TryGetValue("C1_15", out var c) ? c : null);
        }

        [Fact]
        public async Task State_CountsProposalsAwaitingReview()
        {
            await SubmittedProposalAsync();
            var s = await RawHttp.SendAsync(_port, "GET", "/api/state", null, null);
            Assert.Equal(200, s.Status);
            Assert.Equal(1, s.Json.GetProperty("proposals").GetProperty("awaitingReview").GetInt32());
        }
    }

    public class ControlPageProposalsTabTests
    {
        [Fact]
        public void Page_HasTheProposalsTab_TaxpayerBanner_AndCallsTheApi()
        {
            var html = ControlPage.Html;
            Assert.Contains("data-tab=\"proposals\"", html);
            Assert.Contains("id=\"tab-proposals\"", html);
            Assert.Contains("contain taxpayer data", html);
            Assert.Contains("/api/proposals", html);
            Assert.Contains("\"X-Lacerte-Admin\": \"1\"", html);
            Assert.Contains("Yes, write to the return", html); // in-page commit confirmation
        }

        [Fact]
        public void Page_AsksForThePin_ShowsTheTargetReturn_AndThePinChangeTime()
        {
            var html = ControlPage.Html;
            Assert.Contains("i.type = \"password\"", html);
            Assert.Contains("\"Entries go into: \"", html);
            Assert.Contains("Type the client number to confirm", html);
            Assert.Contains("lacerte-mcp approval-pin set", html);
            Assert.Contains("Approval PIN last changed ", html);
            Assert.Contains("pin: pin", html);                 // approve + resolve send it
            Assert.Contains("clientNumber: typed", html);
            Assert.Contains("function takePin(i) { var v = i.value; i.value = \"\";", html); // cleared on every attempt
            Assert.DoesNotContain("localStorage", html);
        }

        [Fact]
        public void Page_NeverBuildsHtmlFromStrings()
        {
            var html = ControlPage.Html;
            foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
                Assert.DoesNotContain(sink, html);
            Assert.DoesNotContain("confirm(\"Write", html); // commit confirmation is in-page, not window.confirm
        }
    }

    public class ToolActivityProposalTests
    {
        private static CallToolResult Result(string json) => new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
        };

        private static CallToolRequestParams Call(string name, string argsJson)
            => new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson) };

        [Theory]
        [InlineData("lacerte_proposal_create")]
        [InlineData("lacerte_proposal_add_lines")]
        [InlineData("lacerte_proposal_add_manual_items")]
        [InlineData("lacerte_proposal_add_questions")]
        [InlineData("lacerte_proposal_update_line")]
        [InlineData("lacerte_proposal_remove_line")]
        [InlineData("lacerte_proposal_submit")]
        [InlineData("lacerte_proposal_get")]
        [InlineData("lacerte_proposal_list")]
        public void ProposalPreparation_IsNotAReturnWrite(string tool) => Assert.False(ToolActivity.IsWrite(tool));

        [Fact]
        public void ProposalCommit_IsAWrite_OtherWritesUnchanged()
        {
            Assert.True(ToolActivity.IsWrite("lacerte_proposal_commit"));
            Assert.True(ToolActivity.IsWrite("lacerte_field_write"));
            Assert.True(ToolActivity.IsWrite("lacerte_client_create"));
            Assert.True(ToolActivity.IsWrite("lacerte_update_return"));
            Assert.False(ToolActivity.IsWrite("lacerte_field_read"));
        }

        [Fact]
        public void ProposalCommit_VerifiedOnlyWhenEveryLineItRanWasReadBack()
        {
            var log = new ActivityLog(persist: false);
            ToolActivity.Record(log, Call("lacerte_proposal_create", @"{""clientNumber"":""1001""}"), Result(@"{""success"":true,""result"":{}}"));
            ToolActivity.Record(log, Call("lacerte_proposal_commit", @"{""proposalId"":""abcdef0123456789abcdef01""}"),
                Result(@"{""success"":true,""result"":{""status"":""Committed"",""ranCount"":2,""succeededCount"":2,""succeededUnverifiedCount"":0,""failedCount"":0,""uncertainCount"":0}}"));
            ToolActivity.Record(log, Call("lacerte_proposal_commit", @"{""proposalId"":""abcdef0123456789abcdef02""}"),
                Result(@"{""success"":true,""result"":{""status"":""Committed"",""ranCount"":2,""succeededCount"":1,""succeededUnverifiedCount"":1,""failedCount"":0,""uncertainCount"":0}}"));
            ToolActivity.Record(log, Call("lacerte_proposal_commit", @"{""proposalId"":""abcdef0123456789abcdef03""}"),
                Result(@"{""success"":true,""result"":{""status"":""CommitFailed"",""ranCount"":1,""succeededCount"":0,""succeededUnverifiedCount"":0,""failedCount"":1,""uncertainCount"":0}}"));

            var events = Enumerable.Reverse(log.Recent()).ToList();
            Assert.Equal("tool", events[0].Category); // create: preparation only

            Assert.Equal("write", events[1].Category);
            Assert.True(events[1].Verified);
            Assert.Contains("abcdef0123456789abcdef01", events[1].Detail);
            Assert.Contains("2 of 2 line(s) written (2 verified)", events[1].Detail);

            Assert.False(events[2].Verified);
            Assert.Equal("success", events[2].Level);
            Assert.Contains("(1 verified)", events[2].Detail);

            Assert.Equal("error", events[3].Level);
            Assert.False(events[3].Verified);
            Assert.Contains("CommitFailed", events[3].Message);
        }
    }
}
