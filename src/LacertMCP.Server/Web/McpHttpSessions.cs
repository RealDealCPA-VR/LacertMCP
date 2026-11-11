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
    /// Registry of live MCP HTTP sessions with idle reaping and a hard cap. When
    /// the cap is reached the oldest IDLE session is evicted rather than refusing
    /// everyone (clients that restart without DELETE otherwise pile up until the
    /// server is restarted — a failure found in the sibling QuickBooks hub).
    /// </summary>
    public sealed class McpHttpSessions
    {
        public const int MaxSessions = 32;
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

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

        /// <summary>Makes room for one more session; false when every session is busy.</summary>
        public async Task<bool> EnsureCapacityAsync()
        {
            if (_sessions.Count < MaxSessions) return true;
            var victim = _sessions.Values
                .Where(s => s.ActiveRequests == 0)
                .OrderBy(s => s.LastSeenAt)
                .FirstOrDefault();
            if (victim == null) return false;
            await RemoveAsync(victim.Id, "closed to make room (connection limit " + MaxSessions + ")").ConfigureAwait(false);
            return true;
        }

        public void Add(McpHttpSession session)
        {
            _sessions[session.Id] = session;
        }

        public async Task RemoveAsync(string id, string reason)
        {
            if (_sessions.TryRemove(id, out var s))
            {
                _activity.Record("info", "connection", "Agent connection closed: " + reason, s.Caller.Name, s.ClientName);
                await s.DisposeAsync().ConfigureAwait(false);
            }
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
