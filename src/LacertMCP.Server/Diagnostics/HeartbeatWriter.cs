using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LacertMCP.Server.Diagnostics
{
    /// <summary>
    /// P15-054 — Liveness/readiness signal file for external watchers.
    ///
    /// <para>
    /// Background hosted service that periodically writes a single-line JSON
    /// document to <see cref="HeartbeatPath"/> (default
    /// <c>%TEMP%/lacerte-mcp-heartbeat.json</c>). A supervisor — Task Scheduler,
    /// NSSM, an operator's PowerShell one-liner — can <c>Get-Item</c> the file
    /// for an mtime staleness probe and <c>ConvertFrom-Json</c> the contents
    /// for a peek at <c>{pid, registered, mode, ts}</c> without writing a full
    /// MCP client. Closes the "the operator runs the MCP unattended and never
    /// notices the 24h Intuit auth quietly expired 6 hours ago" failure mode
    /// the audit flagged.
    /// </para>
    ///
    /// <para>
    /// Interval is configurable via <c>LACERTE_HEARTBEAT_SECONDS</c>; default
    /// 30s. Setting the env var to <c>0</c> (or any negative / unparseable
    /// value) disables the writer — Program.cs gates
    /// <c>AddHostedService&lt;HeartbeatWriter&gt;()</c> on
    /// <see cref="IntervalSeconds"/> &gt; 0 so a disabled heartbeat doesn't
    /// even register with the host. All file-IO failures are swallowed (logged
    /// to the standard logger when present) — heartbeat IO must never surface
    /// as an MCP tool error.
    /// </para>
    /// </summary>
    internal sealed class HeartbeatWriter : BackgroundService
    {
        /// <summary>Env var controlling heartbeat cadence in seconds.</summary>
        public const string IntervalEnvName = "LACERTE_HEARTBEAT_SECONDS";

        /// <summary>Default cadence when the env var is unset / unparseable.</summary>
        public const int DefaultIntervalSeconds = 30;

        /// <summary>Canonical file name written under <see cref="Path.GetTempPath"/>.</summary>
        public const string HeartbeatFileName = "lacerte-mcp-heartbeat.json";

        private readonly LacerteSessionManager _session;
        private readonly ILogger<HeartbeatWriter>? _logger;
        private readonly TimeSpan _interval;
        private readonly string _path;

        public HeartbeatWriter(LacerteSessionManager session, ILogger<HeartbeatWriter>? logger = null)
            : this(session, IntervalSeconds(Environment.GetEnvironmentVariable(IntervalEnvName)),
                   DefaultPath(), logger)
        {
        }

        /// <summary>Test seam — explicit interval + path injection.</summary>
        internal HeartbeatWriter(LacerteSessionManager session, int intervalSeconds, string path, ILogger<HeartbeatWriter>? logger = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logger;
            // Program.cs gates registration on intervalSeconds > 0; defend
            // against a direct ctor call with a non-positive interval by
            // forcing a sentinel value the loop will obey (1ms means the loop
            // exits almost immediately after the first write).
            _interval = TimeSpan.FromSeconds(intervalSeconds > 0 ? intervalSeconds : DefaultIntervalSeconds);
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>Effective heartbeat file path.</summary>
        public string HeartbeatPath => _path;

        /// <summary>Effective cadence in seconds.</summary>
        public int IntervalSecondsValue => (int)_interval.TotalSeconds;

        /// <summary>Resolve the default heartbeat path under <see cref="Path.GetTempPath"/>.</summary>
        public static string DefaultPath() => Path.Combine(Path.GetTempPath(), HeartbeatFileName);

        /// <summary>
        /// Parse the env-var value into a cadence in seconds. Returns
        /// <see cref="DefaultIntervalSeconds"/> when unset / unparseable; returns
        /// <c>0</c> when the operator explicitly sets <c>"0"</c> to disable.
        /// </summary>
        public static int IntervalSeconds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultIntervalSeconds;
            if (int.TryParse(raw!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                // Negative is treated as disabled (same as 0) — operator may
                // type "-1" expecting "off" and we should honour that intent.
                return n < 0 ? 0 : n;
            }
            return DefaultIntervalSeconds;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Write once immediately so a watcher polling on startup doesn't
            // wait a full interval before seeing the file.
            TryWrite();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                if (stoppingToken.IsCancellationRequested) break;
                TryWrite();
            }
        }

        /// <summary>
        /// Public test seam — performs one heartbeat write synchronously and
        /// returns true on success, false on swallowed failure. The hosted
        /// loop uses the same code path.
        /// </summary>
        internal bool TryWrite()
        {
            try
            {
                var payload = BuildPayload();
                // Atomic write via .tmp + File.Replace would be nice but net48
                // File.Replace requires the destination to exist. Use a
                // create-or-replace File.WriteAllText — the JSON is short
                // (~120 bytes) and a partial write is recoverable by the next
                // tick. Reader is single-line JSON so a torn read still
                // surfaces as a JSON parse error rather than a silent stale.
                File.WriteAllText(_path, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch (Exception ex)
            {
                // Heartbeat IO must never poison a tool call. Log and continue.
                _logger?.LogDebug(ex, "HeartbeatWriter: write to {Path} failed", _path);
                return false;
            }
        }

        private string BuildPayload()
        {
            // Keep the schema small and stable — external watchers may script
            // against these field names. {pid, registered, mode, ts} is the
            // task-level contract; we add nothing beyond it here so the
            // payload is trivially round-trippable for parity tests.
            var doc = new
            {
                pid = SafePid(),
                registered = _session.IsRegistered,
                mode = _session.Mode.ToString().ToLowerInvariant(),
                ts = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            return JsonSerializer.Serialize(doc);
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }
    }
}
