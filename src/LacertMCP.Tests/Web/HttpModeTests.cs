using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Web;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LacertMCP.Tests.Web
{
    public class TailscaleTests
    {
        private const string WhoisJson = @"{""Node"":{""ID"":1,""StableID"":""nAbC123"",""Name"":""desktop-0ngsgb4.tail1234.ts.net."",""ComputedName"":""desktop-0ngsgb4"",""Addresses"":[""100.124.82.41/32"",""fd7a:115c:a1e0::1/128""]},""UserProfile"":{""ID"":42,""LoginName"":""vr@"",""DisplayName"":""VR""}}";

        private const string StatusJson = @"{""BackendState"":""Running"",""Self"":{""ID"":""nSelf"",""HostName"":""vr"",""DNSName"":""vr.tail1234.ts.net."",""TailscaleIPs"":[""100.114.92.33""],""UserID"":42,""OS"":""windows"",""Online"":true},
""Peer"":{""k1"":{""ID"":""nAbC123"",""HostName"":""desktop-0ngsgb4"",""DNSName"":""desktop-0ngsgb4.tail1234.ts.net."",""TailscaleIPs"":[""100.124.82.41""],""UserID"":42,""OS"":""windows"",""Online"":true},
""k2"":{""ID"":""nOther"",""HostName"":""shared-box"",""TailscaleIPs"":[""100.70.0.5""],""UserID"":77,""OS"":""linux"",""Online"":false}},
""User"":{""42"":{""LoginName"":""vr@""},""77"":{""LoginName"":""someone@""}}}";

        [Theory]
        [InlineData("100.64.0.1", true)]
        [InlineData("100.127.255.254", true)]
        [InlineData("100.128.0.1", false)]
        [InlineData("192.168.1.10", false)]
        [InlineData("10.10.10.208", false)]
        [InlineData("fd7a:115c:a1e0::1", true)]
        [InlineData("::1", false)]
        public void IsTailnetAddress(string ip, bool expected)
        {
            Assert.Equal(expected, Tailscale.IsTailnetAddress(IPAddress.Parse(ip)));
        }

        [Fact]
        public async Task Whois_ParsesStableIdOwnerAndName()
        {
            var ts = new Tailscale { Run = _ => Task.FromResult(WhoisJson) };
            var r = await ts.WhoisAsync(IPAddress.Parse("100.124.82.41"));
            Assert.False(r.Failed);
            Assert.Equal("nAbC123", r.Device!.StableId);
            Assert.Equal("desktop-0ngsgb4", r.Device.Name);
            Assert.Equal("42", r.Device.OwnerId);
            Assert.Contains("100.124.82.41", r.Device.Addresses);
        }

        [Fact]
        public async Task Whois_CliFailure_IsAnError_NotUnknownDevice_AndIsNotCached()
        {
            int calls = 0;
            var ts = new Tailscale
            {
                Run = _ =>
                {
                    calls++;
                    if (calls == 1) throw new InvalidOperationException("dial unix /var/run/tailscale/tailscaled.sock: connect: connection refused");
                    return Task.FromResult(WhoisJson);
                },
            };
            var first = await ts.WhoisAsync(IPAddress.Parse("100.124.82.41"));
            Assert.True(first.Failed);
            Assert.Contains("connection refused", first.Error);
            Assert.NotNull(ts.LastError);

            // Recovery is immediate: the failure was not cached.
            var second = await ts.WhoisAsync(IPAddress.Parse("100.124.82.41"));
            Assert.False(second.Failed);
            Assert.NotNull(second.Device);
            Assert.Null(ts.LastError);
        }

        [Fact]
        public async Task Whois_NoSuchPeer_IsAnAnswer_NotAnError()
        {
            var ts = new Tailscale { Run = _ => throw new InvalidOperationException("no match for IP:port 100.99.1.1") };
            var r = await ts.WhoisAsync(IPAddress.Parse("100.99.1.1"));
            Assert.False(r.Failed);
            Assert.Null(r.Device);
        }

        [Fact]
        public async Task Status_ParsesSelfPeersAndOwners()
        {
            var ts = new Tailscale { Run = _ => Task.FromResult(StatusJson) };
            var (self, peers, error) = await ts.StatusAsync();
            Assert.Null(error);
            Assert.Equal("vr", self!.Name);
            Assert.Equal("42", self.OwnerId);
            Assert.Equal(2, peers.Count);
            var other = peers.Single(p => p.StableId == "nOther");
            Assert.Equal("someone@", other.OwnerLogin);
            Assert.False(other.Online);
        }

        [Fact]
        public async Task Status_NotRunning_IsReportedAsError()
        {
            var ts = new Tailscale { Run = _ => Task.FromResult(@"{""BackendState"":""Stopped""}") };
            var (_, _, error) = await ts.StatusAsync();
            Assert.Contains("Stopped", error);
        }
    }

    public class DeviceAccessTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lmcp-access-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Grants_Persist_AndUnknownDevicesHaveNoAccess()
        {
            var path = Path.Combine(_dir, "device-access.json");
            var a = new DeviceAccess(path);
            Assert.Equal(AccessLevel.None, a.LevelFor("nAbC123"));
            a.Set("nAbC123", "desktop-0ngsgb4", AccessLevel.Read, "this computer");
            a.Set("nXyz", "laptop", AccessLevel.Write, "this computer");

            var reloaded = new DeviceAccess(path);
            Assert.Equal(AccessLevel.Read, reloaded.LevelFor("nAbC123"));
            Assert.Equal(AccessLevel.Write, reloaded.LevelFor("nXyz"));
            Assert.Equal(AccessLevel.None, reloaded.LevelFor("nUnknown"));
        }

        [Fact]
        public void SettingNone_RemovesTheGrant()
        {
            var a = new DeviceAccess(Path.Combine(_dir, "a.json"));
            a.Set("n1", "pc", AccessLevel.Write, "me");
            a.Set("n1", "pc", AccessLevel.None, "me");
            Assert.Empty(a.All);
            Assert.Equal(AccessLevel.None, a.LevelFor("n1"));
        }

        [Fact]
        public void CorruptFile_FailsClosed()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "bad.json");
            File.WriteAllText(path, "{ this is not json");
            var a = new DeviceAccess(path);
            Assert.Equal(AccessLevel.None, a.LevelFor("anything"));
        }
    }

    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionScopeTests
    {
        [Fact]
        public async Task EachScope_HasItsOwnContext_AndStdioGlobalIsUntouched()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_SIMULATION"] = "1" });
            await mgr.Register();
            var a = new SessionScope();
            var b = new SessionScope();
            try
            {
                SessionScope.Current = a;
                mgr.SetContext(@"C:\Lacerte\24Tax\IData", 2024, 'I');
                SessionScope.Current = b;
                mgr.SetContext(@"C:\Lacerte\24Tax\CData", 2024, 'C');

                SessionScope.Current = a;
                Assert.Equal('I', mgr.Context!.Module);
                SessionScope.Current = b;
                Assert.Equal('C', mgr.Context!.Module);
            }
            finally
            {
                SessionScope.Current = null;
            }
            Assert.Null(mgr.Context);
        }

        [Fact]
        public async Task ReadOnlyDevice_CannotOpenWritableContext_OrWrite()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_SIMULATION"] = "1" });
            await mgr.Register();
            SessionScope.Current = new SessionScope { ReadOnlyAccess = true };
            try
            {
                mgr.SetContext(@"C:\Lacerte\24Tax\IData", 2024, 'I', readOnly: false);
                Assert.True(mgr.Context!.ReadOnly);
                var ex = await Assert.ThrowsAsync<LacertMCP.Server.Errors.LacerteSDKError>(() =>
                    mgr.WriteField(1, "1001", "C1_15", "x"));
                Assert.Equal(LacertMCP.Server.Errors.SyntheticStatusCodes.FieldIsReadOnly, ex.StatusCode);
            }
            finally
            {
                SessionScope.Current = null;
            }
        }

        [Fact]
        public async Task DowngradeToReadOnly_MidSession_BlocksWritesOnExistingContext()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_SIMULATION"] = "1" });
            await mgr.Register();
            var scope = new SessionScope();
            SessionScope.Current = scope;
            try
            {
                mgr.SetContext(@"C:\Lacerte\24Tax\IData", 2024, 'I');
                scope.ReadOnlyAccess = true; // access changed on the control page
                var ex = await Assert.ThrowsAsync<LacertMCP.Server.Errors.LacerteSDKError>(() =>
                    mgr.CreateClient(new CreateClientArgs("NEWC", "Individual", "A", "B", null)));
                Assert.Equal(LacertMCP.Server.Errors.SyntheticStatusCodes.FieldIsReadOnly, ex.StatusCode);
            }
            finally
            {
                SessionScope.Current = null;
            }
        }
    }

    public class ToolActivityTests
    {
        private static CallToolResult Result(string json) => new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
        };

        private static CallToolRequestParams Call(string name, string argsJson)
            => new CallToolRequestParams
            {
                Name = name,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson),
            };

        [Fact]
        public void VerifiedWrite_RecordsTargetButNotValue()
        {
            var log = new ActivityLog(persist: false);
            ToolActivity.Record(log,
                Call("lacerte_field_write", @"{""table"":1,""clientNumber"":""1001"",""field"":""C1_15"",""value"":""SECRET-VALUE""}"),
                Result(@"{""success"":true,""result"":{""statusName"":""Success"",""dryRun"":false,""verified"":true}}"));
            var e = log.Recent().Single();
            Assert.Equal("write", e.Category);
            Assert.Equal("success", e.Level);
            Assert.Contains("verified", e.Message);
            Assert.Contains("1001", e.Detail);
            Assert.DoesNotContain("SECRET-VALUE", e.Detail);
        }

        [Fact]
        public void FailedCall_IsError_WithStatusName()
        {
            var log = new ActivityLog(persist: false);
            ToolActivity.Record(log,
                Call("lacerte_field_write", @"{""clientNumber"":""1001"",""field"":""C1_15""}"),
                Result(@"{""success"":false,""statusName"":""WriteNotVerified""}"));
            var e = log.Recent().Single();
            Assert.Equal("error", e.Level);
            Assert.Contains("WriteNotVerified", e.Message);
        }

        [Fact]
        public void DryRunWrite_IsNotCountedAsAWrite()
        {
            var log = new ActivityLog(persist: false);
            ToolActivity.Record(log,
                Call("lacerte_field_write", @"{""clientNumber"":""1001""}"),
                Result(@"{""success"":true,""result"":{""dryRun"":true}}"));
            Assert.Equal("tool", log.Recent().Single().Category);
        }
    }
}
