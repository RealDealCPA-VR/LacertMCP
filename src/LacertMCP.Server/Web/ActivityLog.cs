using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    }

    /// <summary>
    /// Operator-facing timeline for the control page: tool calls (name, device,
    /// outcome — never argument values beyond the client number / field for
    /// writes), access changes, connections, refused requests and Tailscale
    /// health transitions. Kept in memory (last 1000) and appended to
    /// %APPDATA%\lacerte-mcp\activity.jsonl so it survives restarts.
    /// </summary>
    public sealed class ActivityLog
    {
        private const int Capacity = 1000;
        private const long MaxFileBytes = 5 * 1024 * 1024;
        private readonly object _gate = new object();
        private readonly LinkedList<ActivityEvent> _events = new LinkedList<ActivityEvent>();
        private readonly string? _path;

        public ActivityLog(bool persist = true)
        {
            if (!persist) return;
            _path = Path.Combine(WebPaths.DataDir, "activity.jsonl");
            try
            {
                if (File.Exists(_path))
                {
                    foreach (var line in File.ReadLines(_path).Reverse().Take(Capacity).Reverse())
                    {
                        try
                        {
                            var e = JsonSerializer.Deserialize<ActivityEvent>(line);
                            if (e != null) _events.AddLast(e);
                        }
                        catch { /* skip a torn line */ }
                    }
                }
            }
            catch { /* history is best-effort */ }
        }

        public string? FilePath => _path;

        public void Record(string level, string category, string message, string? device = null, string? detail = null)
        {
            var e = new ActivityEvent
            {
                At = DateTime.UtcNow,
                Level = level,
                Category = category,
                Message = message,
                Device = device,
                Detail = detail,
            };
            lock (_gate)
            {
                _events.AddLast(e);
                while (_events.Count > Capacity) _events.RemoveFirst();
                if (_path == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
                    {
                        var keep = _events.Select(x => JsonSerializer.Serialize(x)).ToList();
                        File.WriteAllLines(_path, keep);
                    }
                    else
                    {
                        File.AppendAllText(_path, JsonSerializer.Serialize(e) + Environment.NewLine);
                    }
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
