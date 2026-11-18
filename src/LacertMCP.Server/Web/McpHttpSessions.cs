using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Web
{
    /// <summary>Who is calling: this PC, or a tailnet device identified by whois.</summary>
    public sealed record Caller(bool IsLocal, string StableId, string Name, bool IsOwner, string Address)
    {
        public static Caller Local(string address) => new Caller(true, "local", "this computer", true, address);
    }

    /// <summary>One MCP connection over HTTP: its transport, server, and private context.</summary>
    public sealed class McpHttpSession
    {
        public string Id { get; }
        public StreamableHttpServerTransport Transport { get; }
        public McpServer Server { get; }
        public SessionScope Scope { get; }
        public Caller Caller { get; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public Task RunTask { get; set; } = Task.CompletedTask;
        public CancellationTokenSource Stop { get; } = new CancellationTokenSource();
        private int _active;

        public McpHttpSession(string id, StreamableHttpServerTransport transport, McpServer server, SessionScope scope, Caller caller)
        {
            Id = id;
            Transport = transport;
            Server = server;
            Scope = scope;
            Caller = caller;
        }

        public int ActiveRequests => Volatile.Read(ref _active);
        public string ClientName => Server.ClientInfo?.Name ?? "(connecting)";

        public IDisposable Enter()
        {
            Interlocked.Increment(ref _active);
            LastSeenAt = DateTime.UtcNow;
            return new Exit(this);
        }

        private sealed class Exit : IDisposable
        {
            private McpHttpSession? _s;
            public Exit(McpHttpSession s) { _s = s; }
            public void Dispose()
            {
                var s = Interlocked.Exchange(ref _s, null);
                if (s == null) return;
                s.LastSeenAt = DateTime.UtcNow;
                Interlocked.Decrement(ref s._active);
            }
        }

        public async Task DisposeAsync()
        {
            try { Stop.Cancel(); } catch { /* already cancelled */ }
            try { await Transport.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
            try { await Server.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
            try { await RunTask.ConfigureAwait(false); } catch { /* cancelled */ }
        }
    }

    /// <summary>
    /// Registry of live MCP HTTP sessions with idle reaping, a per-device cap and
    /// a global cap. When a cap is reached an IDLE session is evicted (the
    /// device's own first, see <see cref="TryAddAsync"/>) rather than refusing
    /// everyone (clients that restart without DELETE otherwise pile up until the
    /// server is restarted — a failure found in the sibling QuickBooks hub).
    /// </summary>
    public sealed class McpHttpSessions
    {
        public const int MaxSessions = 32;
        public const int MaxSessionsPerDevice = 8;
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, McpHttpSession> _sessions =
            new ConcurrentDictionary<string, McpHttpSession>(StringComparer.Ordinal);
        private readonly ActivityLog _activity;

        public McpHttpSessions(ActivityLog activity)
        {
            _activity = activity;
        }

        public int Count => _sessions.Count;
        public IReadOnlyList<McpHttpSession> All => _sessions.Values.OrderBy(s => s.CreatedAt).ToList();

        public bool TryGet(string id, out McpHttpSession session) => _sessions.TryGetValue(id, out session!);

        public static string NewId()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Adds <paramref name="session"/>, making room first: a device at its own
        /// cap (<see cref="MaxSessionsPerDevice"/>) loses its own oldest idle
        /// session; at the global cap the caller's own oldest idle session goes
        /// first, then the globally oldest idle one. One device can therefore
        /// never push other devices' agents out. The capacity check and the add
        /// happen under one lock. False (nothing added) when there's no idle
        /// session to evict; the caller then disposes <paramref name="session"/>.
        /// </summary>
        public async Task<bool> TryAddAsync(McpHttpSession session)
        {
            McpHttpSession? victim = null;
            string reason = string.Empty;
            lock (_gate)
            {
                var device = session.Caller.StableId;
                var mine = _sessions.Values.Where(s => s.Caller.StableId == device).ToList();
                if (mine.Count >= MaxSessionsPerDevice)
                {
                    victim = OldestIdle(mine);
                    if (victim == null) return false;
                    reason = "closed to make room (limit " + MaxSessionsPerDevice + " connections per device)";
                }
                else if (_sessions.Count >= MaxSessions)
                {
                    victim = OldestIdle(mine) ?? OldestIdle(_sessions.Values);
                    if (victim == null) return false;
                    reason = "closed to make room (connection limit " + MaxSessions + ")";
                }
                // A concurrent DELETE / reap may already have removed (and
                // closed) the victim: then room exists and nothing is closed twice.
                if (victim != null && !_sessions.TryRemove(victim.Id, out _)) victim = null;
                _sessions[session.Id] = session;
            }
            if (victim != null) await CloseAsync(victim, reason).ConfigureAwait(false);
            return true;
        }

        private static McpHttpSession? OldestIdle(IEnumerable<McpHttpSession> candidates)
            => candidates.Where(s => s.ActiveRequests == 0).OrderBy(s => s.LastSeenAt).FirstOrDefault();

        /// <summary>Every live session of one device.</summary>
        public IReadOnlyList<McpHttpSession> ForDevice(string stableId)
            => _sessions.Values.Where(s => s.Caller.StableId == stableId).ToList();

        public async Task RemoveAsync(string id, string reason)
        {
            if (_sessions.TryRemove(id, out var s))
            {
                await CloseAsync(s, reason).ConfigureAwait(false);
            }
        }

        private async Task CloseAsync(McpHttpSession s, string reason)
        {
            _activity.Record("info", "connection", "Agent connection closed: " + reason, s.Caller.Name, s.ClientName);
            await s.DisposeAsync().ConfigureAwait(false);
        }

        public async Task ReapIdleAsync()
        {
            var now = DateTime.UtcNow;
            foreach (var s in _sessions.Values.ToList())
            {
                if (s.ActiveRequests == 0 && now - s.LastSeenAt > IdleTimeout)
                {
                    await RemoveAsync(s.Id, "idle for more than " + (int)IdleTimeout.TotalMinutes + " minutes").ConfigureAwait(false);
                }
            }
        }

        public async Task CloseAllAsync()
        {
            foreach (var id in _sessions.Keys.ToList())
            {
                await RemoveAsync(id, "server shutting down").ConfigureAwait(false);
            }
        }
    }
}
