using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LacertMCP.Server.Web
{
    public sealed class ActivityEvent
    {
        public DateTime At { get; set; }
        /// <summary>info | success | warn | error</summary>
        public string Level { get; set; } = "info";
        /// <summary>tool | write | access | connection | security | system</summary>
        public string Category { get; set; } = "system";
        public string Message { get; set; } = string.Empty;
        public string? Device { get; set; }
        public string? Detail { get; set; }
        /// <summary>A write Lacerte read back and confirmed (counted as a verified write).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Verified { get; set; }
    }

    /// <summary>
    /// Operator-facing timeline for the control page: tool calls (name, device,
    /// outcome — never argument values beyond the client number / field for
    /// writes), access changes, connections, refused requests and Tailscale
    /// health transitions. Kept in memory (last 1000) and appended to
    /// %APPDATA%\lacerte-mcp\activity.jsonl so it survives restarts. Past 5 MB
    /// the file is rotated to activity.1.jsonl (one generation kept), never
    /// truncated in place.
    /// </summary>
    public sealed class ActivityLog
    {
        private const int Capacity = 1000;
        private const int MaxThrottleKeys = 1000;
        private readonly long _maxFileBytes;
        private readonly object _gate = new object();
        private readonly LinkedList<ActivityEvent> _events = new LinkedList<ActivityEvent>();
        private readonly Dictionary<string, (DateTime At, int Suppressed)> _throttle = new Dictionary<string, (DateTime, int)>(StringComparer.Ordinal);
        private readonly string? _path;

        public ActivityLog(bool persist = true)
            : this(persist ? Path.Combine(WebPaths.DataDir, "activity.jsonl") : null)
        {
        }

        /// <summary>Test seam: an explicit file (null = memory only) and rotation size.</summary>
        internal ActivityLog(string? path, long maxFileBytes = 5 * 1024 * 1024)
        {
            _maxFileBytes = maxFileBytes;
            _path = path;
            if (_path == null) return;
            try
            {
                // The rotated generation first, so a just-rotated log still restores a full history.
                var lines = new List<string>();
                if (File.Exists(RotatedPath)) lines.AddRange(File.ReadLines(RotatedPath));
                if (File.Exists(_path)) lines.AddRange(File.ReadLines(_path));
                foreach (var line in lines.Skip(Math.Max(0, lines.Count - Capacity)))
                {
                    try
                    {
                        var e = JsonSerializer.Deserialize<ActivityEvent>(line);
                        if (e != null) _events.AddLast(e);
                    }
                    catch { /* skip a torn line */ }
                }
            }
            catch { /* history is best-effort */ }
        }

        public string? FilePath => _path;

        internal string RotatedPath => Path.Combine(Path.GetDirectoryName(_path!)!, Path.GetFileNameWithoutExtension(_path!) + ".1" + Path.GetExtension(_path!));

        public void Record(string level, string category, string message, string? device = null, string? detail = null)
            => Append(new ActivityEvent
            {
                At = DateTime.UtcNow,
                Level = level,
                Category = category,
                Message = message,
                Device = device,
                Detail = detail,
            });

        /// <summary>
        /// Records at most one event per <paramref name="key"/> per
        /// <paramref name="window"/>, so a misconfigured or hostile device
        /// retrying in a loop can't flood the timeline (and push real history
        /// out). Suppressed repeats are counted and reported on the next event
        /// that gets through. Returns true when the event was recorded.
        /// </summary>
        public bool RecordThrottled(string key, TimeSpan window, string level, string category, string message, string? device = null, string? detail = null)
        {
            var now = DateTime.UtcNow;
            int suppressed;
            lock (_gate)
            {
                if (_throttle.TryGetValue(key, out var t) && now - t.At < window)
                {
                    _throttle[key] = (t.At, t.Suppressed + 1);
                    return false;
                }
                suppressed = t.Suppressed;
                if (_throttle.Count >= MaxThrottleKeys)
                {
                    foreach (var stale in _throttle.Where(kv => now - kv.Value.At >= window).Select(kv => kv.Key).ToList())
                        _throttle.Remove(stale);
                    if (_throttle.Count >= MaxThrottleKeys) _throttle.Clear();
                }
                _throttle[key] = (now, 0);
            }
            if (suppressed > 0)
            {
                var note = suppressed + " more like this since the last entry";
                detail = string.IsNullOrEmpty(detail) ? note : detail + " (" + note + ")";
            }
            Record(level, category, message, device, detail);
            return true;
        }

        /// <summary>Adds an already-built event (ToolActivity uses this to set <see cref="ActivityEvent.Verified"/>).</summary>
        internal void Append(ActivityEvent e)
        {
            lock (_gate)
            {
                _events.AddLast(e);
                while (_events.Count > Capacity) _events.RemoveFirst();
                if (_path == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (File.Exists(_path) && new FileInfo(_path).Length > _maxFileBytes)
                    {
                        if (File.Exists(RotatedPath)) File.Delete(RotatedPath);
                        File.Move(_path, RotatedPath);
                    }
                    File.AppendAllText(_path, JsonSerializer.Serialize(e) + Environment.NewLine);
                }
                catch { /* never break a tool call over the activity file */ }
            }
        }

        public IReadOnlyList<ActivityEvent> Recent(int max = 300)
        {
            lock (_gate)
            {
                return _events.Reverse().Take(max).ToList();
            }
        }

        public int CountSince(DateTime sinceUtc, Func<ActivityEvent, bool> predicate)
        {
            lock (_gate)
            {
                return _events.Count(e => e.At >= sinceUtc && predicate(e));
            }
        }
    }
}
