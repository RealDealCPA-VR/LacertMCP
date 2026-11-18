using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace LacertMCP.Server.Web
{
    /// <summary>
    /// A tailnet device as seen by <c>tailscale whois</c> / <c>tailscale status</c>.
    /// <c>IsTagged</c>: the node carries ACL tags. Every tagged node in a tailnet
    /// shares the one "tagged-devices" user, so a tagged node's OwnerId says
    /// nothing about who owns it.
    /// </summary>
    public sealed record TailnetDevice(string StableId, string Name, string OwnerId, string OwnerLogin, string[] Addresses, bool Online, string Os, bool IsTagged = false)
    {
        /// <summary>The login Tailscale reports for every tagged node.</summary>
        public const string TaggedDevicesLogin = "tagged-devices";

        /// <summary>
        /// True when <paramref name="other"/> belongs to the same Tailscale login as
        /// <paramref name="self"/>. Never true when either is tagged: tagged nodes
        /// all share one user, so "same owner" would mean "any tagged node".
        /// </summary>
        public static bool SameOwner(TailnetDevice? self, TailnetDevice? other)
            => self != null && other != null
               && !self.IsTagged && !other.IsTagged
               && !string.IsNullOrEmpty(self.OwnerId)
               && other.OwnerId == self.OwnerId;
    }

    /// <summary>The Tailscale CLI itself could not be started (not installed, wrong path).</summary>
    public sealed class TailscaleCliUnavailableException : InvalidOperationException
    {
        public TailscaleCliUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Outcome of identifying a remote caller. Kept distinct on purpose: a
    /// broken Tailscale CLI/daemon (<see cref="Error"/>) must never look like
    /// "this device isn't on the tailnet" — that exact confusion cost the sibling
    /// QuickBooks hub an outage (every device got a misleading 403).
    /// </summary>
    public sealed record WhoisResult(TailnetDevice? Device, string? Error)
    {
        public bool Failed => Error != null;
    }

    /// <summary>
    /// Thin wrapper over the Tailscale CLI. Successful lookups are cached for a
    /// minute; failures are never cached so recovery is immediate.
    /// </summary>
    public sealed class Tailscale
    {
        private readonly ConcurrentDictionary<string, (TailnetDevice Device, DateTime At)> _whoisCache =
            new ConcurrentDictionary<string, (TailnetDevice, DateTime)>();
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

        /// <summary>Test seam: runs the CLI with the given args, returns stdout or throws.</summary>
        internal Func<string, Task<string>> Run { get; set; } = RunCliAsync;

        public string? LastError { get; private set; }
        public DateTime? LastErrorAt { get; private set; }
        public DateTime? LastOkAt { get; private set; }

        public static bool IsTailnetAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 100 && (b[1] & 0xC0) == 64; // 100.64.0.0/10
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
            }
            return false;
        }

        public async Task<WhoisResult> WhoisAsync(IPAddress ip)
        {
            var key = ip.ToString();
            if (_whoisCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            {
                return new WhoisResult(hit.Device, null);
            }
            string json;
            try
            {
                json = await Run("whois --json " + key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // "no such peer" is an answer, not a failure. Match only Tailscale's own
                // wording ("no match for IP:port ...", "peer not found"), never our
                // CLI-not-runnable error, which must surface as a 503 naming the cause.
                if (!(ex is TailscaleCliUnavailableException)
                    && (ex.Message.IndexOf("no match", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("peer not found", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    MarkOk();
                    return new WhoisResult(null, null);
                }
                MarkError("tailscale whois failed: " + ex.Message);
                return new WhoisResult(null, LastError);
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var node = root.GetProperty("Node");
                var profile = root.TryGetProperty("UserProfile", out var p) ? p : default;
                var login = profile.ValueKind == JsonValueKind.Object ? Str(profile, "LoginName") : string.Empty;
                var dev = new TailnetDevice(
                    Str(node, "StableID"),
                    ShortName(node),
                    profile.ValueKind == JsonValueKind.Object ? profile.GetProperty("ID").ToString() : string.Empty,
                    login,
                    StrArray(node, "Addresses").Select(a => a.Split('/')[0]).ToArray(),
                    true,
                    string.Empty,
                    IsTagged(node, login));
                MarkOk();
                _whoisCache[key] = (dev, DateTime.UtcNow);
                return new WhoisResult(dev, null);
            }
            catch (Exception ex)
            {
                MarkError("tailscale whois returned unexpected output: " + ex.Message);
                return new WhoisResult(null, LastError);
            }
        }

        /// <summary>This machine plus every peer, from <c>tailscale status --json</c>.</summary>
        public async Task<(TailnetDevice? Self, IReadOnlyList<TailnetDevice> Peers, string? Error)> StatusAsync()
        {
            string json;
            try
            {
                json = await Run("status --json").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MarkError("tailscale status failed: " + ex.Message);
                return (null, Array.Empty<TailnetDevice>(), LastError);
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var logins = new Dictionary<string, string>();
                if (root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object)
                {
                    foreach (var u in users.EnumerateObject())
                    {
                        logins[u.Name] = Str(u.Value, "LoginName");
                    }
                }
                TailnetDevice Parse(JsonElement e, bool self)
                {
                    var owner = e.TryGetProperty("UserID", out var uid) ? uid.ToString() : string.Empty;
                    var login = logins.TryGetValue(owner, out var l) ? l : string.Empty;
                    return new TailnetDevice(
                        Str(e, "ID"),
                        ShortName(e),
                        owner,
                        login,
                        StrArray(e, "TailscaleIPs"),
                        self || (e.TryGetProperty("Online", out var on) && on.ValueKind == JsonValueKind.True),
                        Str(e, "OS"),
                        IsTagged(e, login));
                }
                TailnetDevice? me = root.TryGetProperty("Self", out var s) && s.ValueKind == JsonValueKind.Object ? Parse(s, true) : null;
                var peers = new List<TailnetDevice>();
                if (root.TryGetProperty("Peer", out var peerObj) && peerObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pe in peerObj.EnumerateObject()) peers.Add(Parse(pe.Value, false));
                }
                var backend = Str(root, "BackendState");
                if (backend.Length > 0 && backend != "Running")
                {
                    MarkError("Tailscale is not connected on this PC (state: " + backend + ").");
                    return (me, peers, LastError);
                }
                MarkOk();
                return (me, peers.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
            }
            catch (Exception ex)
            {
                MarkError("tailscale status returned unexpected output: " + ex.Message);
                return (null, Array.Empty<TailnetDevice>(), LastError);
            }
        }

        private void MarkError(string message)
        {
            LastError = message;
            LastErrorAt = DateTime.UtcNow;
        }

        private void MarkOk()
        {
            LastOkAt = DateTime.UtcNow;
            LastError = null;
        }

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty : string.Empty;

        private static string[] StrArray(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).ToArray()
                : Array.Empty<string>();

        /// <summary>A node is tagged when it carries any ACL tag, or reports the shared "tagged-devices" login.</summary>
        private static bool IsTagged(JsonElement node, string login)
            => StrArray(node, "Tags").Any(t => t.Length > 0)
               || string.Equals(login, TailnetDevice.TaggedDevicesLogin, StringComparison.OrdinalIgnoreCase);

        private static string ShortName(JsonElement e)
        {
            var computed = Str(e, "ComputedName");
            if (computed.Length > 0) return computed;
            var host = Str(e, "HostName");
            if (host.Length > 0) return host;
            var dns = Str(e, "DNSName");
            if (dns.Length == 0) dns = Str(e, "Name");
            var dot = dns.IndexOf('.');
            return dot > 0 ? dns.Substring(0, dot) : dns;
        }

        private static string FindCli()
        {
            var env = Environment.GetEnvironmentVariable("LACERTE_TAILSCALE_EXE");
            if (!string.IsNullOrEmpty(env)) return env!;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var win = Path.Combine(pf, "Tailscale", "tailscale.exe");
            return File.Exists(win) ? win : "tailscale";
        }

        private static async Task<string> RunCliAsync(string args)
        {
            var psi = new ProcessStartInfo(FindCli(), args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                throw new TailscaleCliUnavailableException("tailscale CLI not found or not runnable (" + ex.Message + "). Is Tailscale installed?");
            }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => proc.WaitForExit(8000)).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(); } catch { /* already gone */ }
                throw new TimeoutException("tailscale " + args.Split(' ')[0] + " did not answer within 8s");
            }
            var outText = await stdout.ConfigureAwait(false);
            var errText = (await stderr.ConfigureAwait(false)).Trim();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(errText.Length > 0 ? errText : "exit code " + proc.ExitCode);
            }
            return outText;
        }
    }
}
