using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LacertMCP.Server.Web
{
    /// <summary>What a tailnet device may do through the MCP endpoint.</summary>
    public enum AccessLevel
    {
        None,
        Read,
        Write,
    }

    public sealed class DeviceGrant
    {
        public string StableId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Level { get; set; } = "none";
        public DateTime GrantedAt { get; set; }
        public string GrantedBy { get; set; } = string.Empty;

        [JsonIgnore]
        public AccessLevel Parsed => ParseLevel(Level);

        public static AccessLevel ParseLevel(string? s) => (s ?? string.Empty).ToLowerInvariant() switch
        {
            "write" => AccessLevel.Write,
            "read" => AccessLevel.Read,
            _ => AccessLevel.None,
        };
    }

    /// <summary>
    /// Per-device grants, pinned to the Tailscale StableID (an IP can be reused;
    /// the StableID can't). Persisted as JSON under %APPDATA%\lacerte-mcp and
    /// re-read on every request through <see cref="LevelFor"/> so a revoke on the
    /// control page takes effect on the very next tool call. This computer is
    /// always full access and is never stored here.
    /// </summary>
    public sealed class DeviceAccess
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private List<DeviceGrant> _grants;

        public DeviceAccess(string? path = null)
        {
            _path = path ?? Path.Combine(WebPaths.DataDir, "device-access.json");
            _grants = Load(_path);
        }

        public string FilePath => _path;

        public IReadOnlyList<DeviceGrant> All
        {
            get { lock (_gate) { return _grants.Select(g => g).ToList(); } }
        }

        public AccessLevel LevelFor(string stableId)
        {
            lock (_gate)
            {
                return _grants.FirstOrDefault(g => g.StableId == stableId)?.Parsed ?? AccessLevel.None;
            }
        }

        public void Set(string stableId, string name, AccessLevel level, string by)
        {
            if (string.IsNullOrWhiteSpace(stableId)) throw new ArgumentException("A device id is required.");
            lock (_gate)
            {
                var next = _grants.Where(g => g.StableId != stableId).ToList();
                if (level != AccessLevel.None)
                {
                    next.Add(new DeviceGrant
                    {
                        StableId = stableId,
                        Name = name,
                        Level = level.ToString().ToLowerInvariant(),
                        GrantedAt = DateTime.UtcNow,
                        GrantedBy = by,
                    });
                }
                Save(next);
                _grants = next;
            }
        }

        private static List<DeviceGrant> Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<DeviceGrant>();
                var list = JsonSerializer.Deserialize<List<DeviceGrant>>(File.ReadAllText(path));
                return list?.Where(g => !string.IsNullOrEmpty(g.StableId)).ToList() ?? new List<DeviceGrant>();
            }
            catch
            {
                // A corrupt file must fail CLOSED (nobody remote gets in), never open.
                return new List<DeviceGrant>();
            }
        }

        private void Save(List<DeviceGrant> grants)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(grants, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
    }

    internal static class WebPaths
    {
        public static string DataDir
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable("LACERTE_HTTP_DATA_DIR");
                if (!string.IsNullOrEmpty(overrideDir)) return overrideDir!;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lacerte-mcp");
            }
        }
    }
}
