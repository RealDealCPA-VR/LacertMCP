using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LacertMCP.Cli.Update
{
    /// <summary>The always-on launcher <c>install-http.ps1</c> sets up for an install.</summary>
    public sealed class ServiceInfo
    {
        public ServiceInfo(string launcherPath, int port)
        {
            LauncherPath = launcherPath;
            Port = port;
        }

        public string LauncherPath { get; }
        public int Port { get; }
    }

    /// <summary>Stop / start / health-check of the always-on server. Seam for tests.</summary>
    public interface IServiceControl
    {
        /// <summary>The launcher that runs <paramref name="serverDir"/>'s server, or null (stdio / MCP-host install).</summary>
        ServiceInfo? Find(string serverDir);

        void Stop(ServiceInfo service, string serverDir);

        void Start(ServiceInfo service);

        /// <summary>True once <c>/healthz</c> answers on localhost (any HTTP status — same rule as install-http.ps1).</summary>
        Task<bool> WaitHealthyAsync(ServiceInfo service, TimeSpan timeout, CancellationToken ct);
    }

    /// <summary>
    /// The Startup-folder <c>LacertMCP.vbs</c> launcher written by
    /// <c>scripts/install-http.ps1</c>: a hidden wscript loop that runs
    /// <c>server\LacertMCP.Server.exe --http</c> and restarts it 15 s after it
    /// exits. Stopping = kill that wscript (so it can't respawn the server) and
    /// then the server; starting = run the .vbs again, as the installer does.
    /// No elevation needed — the URL reservation and firewall rule persist.
    /// </summary>
    public sealed class LauncherService : IServiceControl
    {
        public const string LauncherFileName = "LacertMCP.vbs";
        public const int DefaultPort = 8767;

        private static readonly Regex PortLine = new Regex(
            "LACERTE_HTTP_PORT\"\\)\\s*=\\s*\"(\\d{1,5})\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly string _startupDir;

        public LauncherService(string? startupDir = null)
        {
            _startupDir = startupDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        public ServiceInfo? Find(string serverDir)
        {
            if (string.IsNullOrEmpty(_startupDir)) return null;
            var launcher = Path.Combine(_startupDir, LauncherFileName);
            if (!File.Exists(launcher)) return null;
            string text;
            try { text = File.ReadAllText(launcher); }
            catch { return null; }
            return Parse(launcher, text, serverDir);
        }

        /// <summary>Pure parse step: does this launcher run <paramref name="serverDir"/>'s server, and on what port?</summary>
        public static ServiceInfo? Parse(string launcherPath, string text, string serverDir)
        {
            var exe = Path.Combine(Path.GetFullPath(serverDir), ReleaseInstaller.ServerExe);
            if (text.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0) return null;
            var m = PortLine.Match(text);
            var port = m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0 && p < 65536 ? p : DefaultPort;
            return new ServiceInfo(launcherPath, port);
        }

        public void Stop(ServiceInfo service, string serverDir)
        {
            // 1. The wscript loop first, or it respawns the server 15 s later.
            var name = Path.GetFileName(service.LauncherPath);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='wscript.exe'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var cmd = mo["CommandLine"] as string;
                        if (cmd == null || cmd.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        KillAndWait(Convert.ToInt32(mo["ProcessId"]));
                    }
                }
            }
            catch (ManagementException ex)
            {
                throw new UpdateException("Couldn't list wscript processes to stop the launcher (" + ex.Message + ").", ex);
            }

            // 2. Then the server itself.
            foreach (var pid in RunningServers.PidsUnder(serverDir)) KillAndWait(pid);
        }

        public void Start(ServiceInfo service)
        {
            using var _ = Process.Start(new ProcessStartInfo("wscript.exe", "\"" + service.LauncherPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }

        public async Task<bool> WaitHealthyAsync(ServiceInfo service, TimeSpan timeout, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = "http://localhost:" + service.Port + "/healthz";
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                    return true; // any answer means the listener is up
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            return false;
        }

        private static void KillAndWait(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(10_000);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already exited */ }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new UpdateException($"Couldn't stop process {pid} ({ex.Message}).", ex);
            }
        }
    }

    /// <summary>Finds LacertMCP.Server processes running from a given install.</summary>
    public static class RunningServers
    {
        /// <summary>PIDs of LacertMCP.Server processes whose image lives under <paramref name="serverDir"/>.</summary>
        public static IReadOnlyList<int> PidsUnder(string serverDir)
        {
            var dir = Path.GetFullPath(serverDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var pids = new List<int>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ReleaseInstaller.ServerExe)))
            {
                using (p)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (path == null || path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id);
                    }
                    catch
                    {
                        // Can't read its path (another user / elevation) — assume it's ours.
                        pids.Add(p.Id);
                    }
                }
            }
            return pids;
        }
    }
}
