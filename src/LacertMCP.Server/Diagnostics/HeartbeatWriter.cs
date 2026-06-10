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
    /// <c>%TEMP%/lacerte-mcp-heartbeat-{pid}.json</c>). A supervisor — Task
    /// Scheduler, NSSM, an operator's PowerShell one-liner — can
    /// <c>Get-Item</c> the file for an mtime staleness probe and
    /// <c>ConvertFrom-Json</c> the contents for a peek at
    /// <c>{pid, mode, registered, expiresAt, minutesUntilExpiry, buildVersion,
    /// ts}</c> without writing a full MCP client. Closes the "the operator
    /// runs the MCP unattended and never notices the 24h Intuit auth quietly
    /// expired 6 hours ago" failure mode the audit flagged.
    /// </para>
    ///
    /// <para>
    /// P17-003 — Payload projects from <see cref="LacerteSessionManager.SessionStatus"/>
    /// so the README-advertised auth-countdown + build-version fields are
    /// actually populated, and <c>registered</c> is computed as
    /// <c>IsRegistered &amp;&amp; ExpiresAt &gt; UtcNow</c> so the heartbeat
    /// truthfully flips to <c>false</c> the moment the ticket expires —
    /// independent of whether a tool call has fired since the lapse. Watchers
    /// reading only the file no longer see a stale green light across an idle
    /// overnight window.
    /// </para>
    ///
    /// <para>
    /// P17-014 — File name is PID-suffixed so a single operator running
    /// Claude Desktop + Cursor (or any other dual-host MCP setup) gets one
    /// heartbeat file per server instance rather than two processes silently
    /// clobbering the same file every 30s and collapsing the liveness contract.
    /// External watchers enumerate <c>lacerte-mcp-heartbeat-*.json</c>.
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

        /// <summary>Default cadence when the env var is unset.</summary>
        public const int DefaultIntervalSeconds = 30;

        /// <summary>
        /// R6-17 — upper clamp on the cadence (24h). <c>Task.Delay(TimeSpan)</c>
        /// throws <see cref="ArgumentOutOfRangeException"/> for anything above
        /// Int32.MaxValue milliseconds (~24.8 days); pre-clamp, an operator
        /// typo like <c>LACERTE_HEARTBEAT_SECONDS=99999999999</c> (parse fails
        /// → default, fine) or <c>=2147484</c> (parses, &gt; Int32.MaxValue ms)
        /// crashed the whole host on the first loop iteration because only
        /// TaskCanceledException was caught and Hosting's default
        /// BackgroundServiceExceptionBehavior.StopHost kills the process.
        /// </summary>
        public const int MaxIntervalSeconds = 86_400;

        /// <summary>
        /// P17-014 — Glob pattern external watchers enumerate to find every
        /// live MCP instance's heartbeat file (one per PID under
        /// <see cref="Path.GetTempPath"/>). Backed by
        /// <see cref="BuildHeartbeatFileName"/> which is PID-suffixed so two
        /// concurrent MCP hosts on the same box do not clobber each other.
        /// </summary>
        public const string HeartbeatFileGlob = "lacerte-mcp-heartbeat-*.json";

        /// <summary>
        /// P17-014 — Canonical file name (PID-suffixed) for THIS process,
        /// written under <see cref="Path.GetTempPath"/>. Computed once per
        /// call site; format is <c>lacerte-mcp-heartbeat-{pid}.json</c>.
        /// </summary>
        public static string HeartbeatFileName => BuildHeartbeatFileName(SafePid());

        /// <summary>
        /// Compose the PID-suffixed heartbeat file name. Exposed for tests that
        /// pin the format without round-tripping through Process.GetCurrentProcess.
        /// </summary>
        internal static string BuildHeartbeatFileName(int pid) =>
            "lacerte-mcp-heartbeat-" + pid.ToString(CultureInfo.InvariantCulture) + ".json";

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
            // substituting the default 30s cadence (R6-17 — the previous
            // comment claimed a "1ms sentinel that exits the loop", which was
            // never what the code did). Also clamp to MaxIntervalSeconds so
            // Task.Delay never receives > Int32.MaxValue ms even when a
            // caller bypasses the IntervalSeconds parser.
            var effective = intervalSeconds > 0 ? intervalSeconds : DefaultIntervalSeconds;
            if (effective > MaxIntervalSeconds) effective = MaxIntervalSeconds;
            _interval = TimeSpan.FromSeconds(effective);
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>Effective heartbeat file path.</summary>
        public string HeartbeatPath => _path;

        /// <summary>Effective cadence in seconds.</summary>
        public int IntervalSecondsValue => (int)_interval.TotalSeconds;

        /// <summary>
        /// Resolve the default heartbeat path under <see cref="Path.GetTempPath"/>.
        /// File name is PID-suffixed per P17-014 so concurrent MCP server
        /// instances do not clobber each other. External watchers should
        /// enumerate <see cref="HeartbeatFileGlob"/> rather than expecting a
        /// single canonical file.
        /// </summary>
        public static string DefaultPath() => Path.Combine(Path.GetTempPath(), HeartbeatFileName);

        /// <summary>
        /// Parse the env-var value into a cadence in seconds. Returns
        /// <see cref="DefaultIntervalSeconds"/> when unset; returns <c>0</c>
        /// (disabled) when the operator sets <c>"0"</c>, a negative value, or
        /// an unparseable value; clamps anything above
        /// <see cref="MaxIntervalSeconds"/> down to the 24h ceiling.
        ///
        /// <para>
        /// R6-17 — unparseable now DISABLES instead of silently enabling at
        /// the default cadence, matching the class doc and the Program.cs
        /// gating comment ("0 / negative / unparseable disables"). An operator
        /// who typed garbage gets the documented behavior instead of a
        /// surprise 30s writer. The clamp keeps <c>Task.Delay</c> inside its
        /// Int32.MaxValue-ms envelope so a huge-but-parseable value can no
        /// longer crash the host (see <see cref="MaxIntervalSeconds"/>).
        /// </para>
        /// </summary>
        public static int IntervalSeconds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultIntervalSeconds;
            if (int.TryParse(raw!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                // Negative is treated as disabled (same as 0) — operator may
                // type "-1" expecting "off" and we should honour that intent.
                if (n < 0) return 0;
                return n > MaxIntervalSeconds ? MaxIntervalSeconds : n;
            }
            // Unparseable disables — documented contract (class doc +
            // Program.cs). Long values that overflow int (e.g. "1e12"-style
            // digit strings) fail TryParse and land here: disabled, not crash.
            return 0;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // R6-35 — opportunistic startup hygiene: sweep sibling heartbeat
            // files (lacerte-mcp-heartbeat-*.json) older than 24h. Dead PIDs
            // (crashes, hard kills) previously left their file behind
            // forever, so a watcher enumerating the glob saw ghost instances.
            // Best-effort; every failure is swallowed.
            CleanStaleSiblingFiles();

            // Write once immediately so a watcher polling on startup doesn't
            // wait a full interval before seeing the file.
            TryWrite();

            try
            {
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
                    catch (ArgumentOutOfRangeException)
                    {
                        // Defensive belt to the MaxIntervalSeconds clamp —
                        // never let a delay-range bug take down the host
                        // (Hosting 8.0.1 default StopHost behavior). Stop the
                        // loop instead; the heartbeat is a diagnostic surface.
                        break;
                    }
                    if (stoppingToken.IsCancellationRequested) break;
                    TryWrite();
                }
            }
            finally
            {
                // R6-35 — best-effort delete of OUR per-PID file on graceful
                // shutdown so watchers don't keep seeing a stale instance.
                // A crash still leaves the file behind; the startup sweep
                // above (run by the next instance) covers that case.
                TryDeleteHeartbeatFile();
            }
        }

        /// <summary>
        /// R6-35 — best-effort delete of this instance's heartbeat file.
        /// Internal test seam; swallows all IO errors.
        /// </summary>
        internal bool TryDeleteHeartbeatFile()
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "HeartbeatWriter: delete of {Path} failed", _path);
                return false;
            }
        }

        /// <summary>
        /// R6-35 — best-effort sweep of stale sibling heartbeat files
        /// (<see cref="HeartbeatFileGlob"/> under this instance's directory)
        /// whose last write is older than 24 hours. Our own file and any
        /// recently-touched sibling (a live concurrent instance writes at
        /// least every <see cref="MaxIntervalSeconds"/> = 24h) are left
        /// alone. Internal test seam; swallows all IO errors.
        /// </summary>
        internal int CleanStaleSiblingFiles()
        {
            int deleted = 0;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;

                var cutoffUtc = DateTime.UtcNow.AddHours(-24);
                foreach (var file in Directory.GetFiles(dir!, HeartbeatFileGlob))
                {
                    try
                    {
                        if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // never sweep our own file
                        }
                        if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                        {
                            continue; // plausibly a live concurrent instance
                        }
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // Per-file failures (AV lock, races with a concurrent
                        // sweep) are swallowed — hygiene must never throw.
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "HeartbeatWriter: stale-sibling sweep failed");
            }
            return deleted;
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
            // P17-003 — Project from SessionStatus() so the README-advertised
            // {pid, mode, registered, expiresAt, minutesUntilExpiry,
            // buildVersion, ts} schema is actually populated. Crucially,
            // `registered` is computed as IsRegistered && ExpiresAt > UtcNow
            // (not the raw IsRegistered latch) so an idle server whose 24h
            // Intuit ticket lapsed at 02:00 flips to registered:false the
            // moment it lapses — without waiting for the next tool call's
            // ThrowIfAuthExpired probe. The whole point of P15-054 was to
            // surface silent auth-expiry to an external watcher; the bare
            // latch lied across overnight idle windows.
            //
            // SessionStatus() is documented as pure in-memory and never
            // throws (edition probe failure is swallowed at line 445-451)
            // so calling it from the heartbeat loop is safe. We still guard
            // for the unexpected — TryWrite's catch swallows any rethrow.
            var snapshot = _session.SessionStatus();
            var now = DateTimeOffset.UtcNow;
            var registeredAndFresh = snapshot.IsRegistered
                && snapshot.ExpiresAt.HasValue
                && snapshot.ExpiresAt.Value > now;

            var doc = new
            {
                pid = SafePid(),
                mode = snapshot.Mode.ToString().ToLowerInvariant(),
                registered = registeredAndFresh,
                // ISO-8601 round-trippable; null when never registered.
                expiresAt = snapshot.ExpiresAt.HasValue
                    ? snapshot.ExpiresAt.Value.ToString("o", CultureInfo.InvariantCulture)
                    : null,
                // Already clamped >= 0 by SessionStatus at LacerteSessionManager.cs:432.
                minutesUntilExpiry = snapshot.MinutesUntilExpiry,
                buildVersion = snapshot.BuildVersion,
                ts = now.ToString("o", CultureInfo.InvariantCulture),
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
