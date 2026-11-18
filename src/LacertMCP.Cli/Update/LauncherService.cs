using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
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

        /// <summary>
        /// PIDs of servers running from <paramref name="serverDir"/> that the launcher did
        /// NOT start (e.g. a stdio server under Claude Desktop). <see cref="Stop"/> leaves them alone.
        /// </summary>
        IReadOnlyList<int> ForeignServers(ServiceInfo service, string serverDir);

        /// <summary>
        /// Stops the launcher loop and the server it started. Throws
        /// <see cref="ServiceStopException"/> — before stopping anything, when it can
        /// tell — if one of them can't be stopped by this user (e.g. it runs elevated).
        /// </summary>
        void Stop(ServiceInfo service, string serverDir);

        void Start(ServiceInfo service);

        /// <summary>True once <c>/healthz</c> answers on localhost (any HTTP status — same rule as install-http.ps1).</summary>
        Task<bool> WaitHealthyAsync(ServiceInfo service, TimeSpan timeout, CancellationToken ct);
    }

    /// <summary>One process as <see cref="LauncherService.Plan"/> sees it.</summary>
    public sealed class ProcInfo
    {
        public int Pid { get; set; }
        public int ParentPid { get; set; }
        public int SessionId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>Full image path, or null when it can't be read.</summary>
        public string? ImagePath { get; set; }

        /// <summary>Command line, or null when it can't be read (typically: the process runs elevated).</summary>
        public string? CommandLine { get; set; }

        /// <summary>This user may terminate it (OpenProcess(PROCESS_TERMINATE) succeeded).</summary>
        public bool CanTerminate { get; set; }

        /// <summary>The parent process still exists (and is older than this one, so the PID wasn't reused).</summary>
        public bool ParentAlive { get; set; }
    }

    /// <summary>What <see cref="LauncherService.Stop"/> will do, worked out before it does anything.</summary>
    public sealed class StopPlan
    {
        public List<int> Launchers { get; } = new List<int>();

        /// <summary>Servers the launcher started (or orphaned servers whose parent is gone).</summary>
        public List<int> Servers { get; } = new List<int>();

        /// <summary>Servers from this install with a live non-launcher parent (an MCP host's stdio server).</summary>
        public List<int> Foreign { get; } = new List<int>();

        /// <summary>Reasons the stop can't be done by this user; non-empty = refuse before killing anything.</summary>
        public List<string> Blockers { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// The Startup-folder <c>LacertMCP.vbs</c> launcher written by
    /// <c>scripts/install-http.ps1</c>: a hidden wscript loop that runs
    /// <c>server\LacertMCP.Server.exe --http</c> and restarts it 15 s after it
    /// exits. Stopping = kill that wscript (so it can't respawn the server) and
    /// then the server it started; starting = have Explorer open the .vbs, exactly
    /// as Windows does at logon. No elevation needed — the URL reservation and
    /// firewall rule persist.
    /// </summary>
    public sealed class LauncherService : IServiceControl
    {
        public const string LauncherFileName = "LacertMCP.vbs";
        public const int DefaultPort = 8767;

        private static readonly Regex PortLine = new Regex(
            "LACERTE_HTTP_PORT\"\\)\\s*=\\s*\"(\\d{1,5})\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly string[] ScrubbedVariables = { "GITHUB_TOKEN", "GH_TOKEN" };

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

        /// <summary>
        /// Pure planning step over a process snapshot (only <paramref name="sessionId"/>
        /// counts — another signed-in user's processes are never ours):
        /// launchers = wscript whose command line names the full launcher path;
        /// servers to stop = LacertMCP.Server under <paramref name="serverDir"/> whose
        /// parent is one of those launchers (or whose parent is gone); every other
        /// server from the install is <see cref="StopPlan.Foreign"/>.
        /// </summary>
        public static StopPlan Plan(IEnumerable<ProcInfo> processes, string launcherPath, string serverDir, int sessionId)
        {
            var plan = new StopPlan();
            var dir = Path.GetFullPath(serverDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var launcherFull = Path.GetFullPath(launcherPath);
            var mine = processes.Where(p => p.SessionId == sessionId).ToList();

            foreach (var w in mine.Where(p => string.Equals(p.Name, "wscript.exe", StringComparison.OrdinalIgnoreCase)))
            {
                if (w.CommandLine == null)
                {
                    // Can't read its command line — normally because it runs elevated
                    // (e.g. started from an admin shell). It may be this launcher.
                    if (!w.CanTerminate)
                    {
                        plan.Blockers.Add($"wscript.exe (PID {w.Pid}) runs with higher rights than this console, so it can't be checked or stopped — it may be the launcher, started elevated.");
                    }
                    continue;
                }
                if (w.CommandLine.IndexOf(launcherFull, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!w.CanTerminate)
                {
                    plan.Blockers.Add($"The launcher (wscript.exe PID {w.Pid}) runs with higher rights than this console and can't be stopped.");
                }
                plan.Launchers.Add(w.Pid);
            }

            foreach (var s in mine.Where(p => string.Equals(p.Name, ReleaseInstaller.ServerExe, StringComparison.OrdinalIgnoreCase)))
            {
                if (s.ImagePath == null)
                {
                    plan.Warnings.Add($"LacertMCP.Server (PID {s.Pid}) is running but its path can't be read; assuming it isn't this install's.");
                    continue;
                }
                if (!Path.GetFullPath(s.ImagePath).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;

                if (plan.Launchers.Contains(s.ParentPid) || !s.ParentAlive)
                {
                    if (!s.CanTerminate)
                    {
                        plan.Blockers.Add($"The server (LacertMCP.Server PID {s.Pid}) runs with higher rights than this console and can't be stopped.");
                    }
                    plan.Servers.Add(s.Pid);
                }
                else
                {
                    plan.Foreign.Add(s.Pid);
                }
            }
            return plan;
        }

        public IReadOnlyList<int> ForeignServers(ServiceInfo service, string serverDir)
            => Plan(ProcessInspector.Snapshot(), service.LauncherPath, serverDir, ProcessInspector.CurrentSessionId).Foreign;

        public void Stop(ServiceInfo service, string serverDir)
        {
            var plan = Plan(ProcessInspector.Snapshot(), service.LauncherPath, serverDir, ProcessInspector.CurrentSessionId);
            if (plan.Blockers.Count > 0)
            {
                throw new ServiceStopException(string.Join(" ", plan.Blockers));
            }

            // The wscript loop first, or it respawns the server 15 s later.
            foreach (var pid in plan.Launchers) KillAndWait(pid);
            try
            {
                foreach (var pid in plan.Servers) KillAndWait(pid);
            }
            catch (ServiceStopException) when (plan.Launchers.Count > 0)
            {
                // The loop is already gone but its server survived: put the loop
                // back so the still-running server is restarted when it next exits.
                Start(service);
                throw;
            }
        }

        public void Start(ServiceInfo service)
        {
            var before = ProcessInspector.LauncherPids(service.LauncherPath, ProcessInspector.CurrentSessionId);
            if (before == null)
            {
                // Can't tell whether a shell start worked, so don't risk two loops.
                if (!StartViaShell(service.LauncherPath)) StartDirect(service.LauncherPath);
                return;
            }
            if (StartViaShell(service.LauncherPath) && WaitForNewLauncher(service.LauncherPath, before, TimeSpan.FromSeconds(15)))
            {
                return;
            }
            // Explorer may just be slow (busy shell, logon storm): look once more
            // before starting directly, so two loops never fight over the port.
            var now = ProcessInspector.LauncherPids(service.LauncherPath, ProcessInspector.CurrentSessionId);
            if (now != null && now.Any(pid => !before.Contains(pid))) return;
            StartDirect(service.LauncherPath);
        }

        /// <summary>
        /// Asks the signed-in user's Explorer to open the .vbs — the way the Startup
        /// folder runs it at logon. The launcher then gets Explorer's (logon)
        /// environment, working directory, normal priority and non-elevated token,
        /// and lives outside this process (and outside a scheduled task's job),
        /// whatever shell, rights or priority this update runs with.
        /// </summary>
        private static bool StartViaShell(string launcherPath)
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!File.Exists(explorer)) return false;
            try
            {
                var psi = new ProcessStartInfo(explorer, "\"" + launcherPath + "\"")
                {
                    UseShellExecute = false,
                    WorkingDirectory = UserProfileDir(),
                };
                ScrubEnvironment(psi.Environment);
                using var _ = Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static bool WaitForNewLauncher(string launcherPath, HashSet<int> before, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var now = ProcessInspector.LauncherPids(launcherPath, ProcessInspector.CurrentSessionId);
                if (now == null || now.Any(p => !before.Contains(p))) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        /// <summary>
        /// Fallback when Explorer didn't start it (no shell, or .vbs opens in an
        /// editor on this PC): wscript directly, with a scrubbed environment, the
        /// profile as working directory and normal priority.
        /// </summary>
        private static void StartDirect(string launcherPath)
        {
            var wscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe");
            var psi = new ProcessStartInfo(File.Exists(wscript) ? wscript : "wscript.exe", "\"" + launcherPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = UserProfileDir(),
            };
            ScrubEnvironment(psi.Environment);

            // A child inherits BELOW_NORMAL / IDLE priority from its creator (a
            // scheduled task runs below normal); lift this process first so the
            // launcher — and the server it starts — run at normal priority.
            try
            {
                using var self = Process.GetCurrentProcess();
                if (self.PriorityClass == ProcessPriorityClass.BelowNormal || self.PriorityClass == ProcessPriorityClass.Idle)
                {
                    self.PriorityClass = ProcessPriorityClass.Normal;
                }
            }
            catch { /* best-effort */ }

            using var p = Process.Start(psi);
            try { if (p != null) p.PriorityClass = ProcessPriorityClass.Normal; }
            catch { /* already running its loop; best-effort */ }
        }

        /// <summary>
        /// Removes what must not leak from this console into the always-on server:
        /// every <c>LACERTE_*</c> variable (a dev shell's <c>LACERTE_SIMULATION=1</c>
        /// would override the launcher's <c>LACERTE_LIVE=1</c> — the .vbs sets its
        /// own) and GitHub tokens.
        /// </summary>
        public static void ScrubEnvironment(IDictionary<string, string> env)
        {
            foreach (var key in env.Keys.ToList())
            {
                if (key.StartsWith("LACERTE_", StringComparison.OrdinalIgnoreCase)
                    || ScrubbedVariables.Any(v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase)))
                {
                    env.Remove(key);
                }
            }
        }

        private static string UserProfileDir()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) || !Directory.Exists(profile) ? Path.GetTempPath() : profile;
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
                throw new ServiceStopException($"Couldn't stop process {pid} ({ex.Message}).", ex);
            }
        }
    }

    /// <summary>Process facts the updater needs, readable without elevation.</summary>
    public static class ProcessInspector
    {
        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        public static int CurrentSessionId
        {
            get
            {
                using var self = Process.GetCurrentProcess();
                return self.SessionId;
            }
        }

        /// <summary>
        /// Full image path via <c>QueryFullProcessImageName</c> with limited-query
        /// access — which Windows grants for a same-user process even when it runs
        /// elevated (unlike <c>Process.MainModule</c>). Null when unreadable.
        /// </summary>
        public static string? ImagePath(int pid)
        {
            var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var size = 1024;
                var sb = new StringBuilder(size);
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>Whether this user may terminate <paramref name="pid"/> — asked without terminating it.</summary>
        public static bool CanTerminate(int pid)
        {
            var h = OpenProcess(ProcessTerminate, false, pid);
            if (h == IntPtr.Zero) return false;
            CloseHandle(h);
            return true;
        }

        /// <summary>wscript.exe and LacertMCP.Server.exe processes (every session; <see cref="LauncherService.Plan"/> filters).</summary>
        public static List<ProcInfo> Snapshot()
        {
            var all = new Dictionary<int, (int Parent, DateTime? Created)>();
            var picked = new List<ProcInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, SessionId, Name, CreationDate, CommandLine FROM Win32_Process");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var pid = Convert.ToInt32(mo["ProcessId"]);
                        var parent = Convert.ToInt32(mo["ParentProcessId"]);
                        all[pid] = (parent, ParseWmiDate(mo["CreationDate"] as string));
                        var name = mo["Name"] as string ?? string.Empty;
                        var isLauncher = string.Equals(name, "wscript.exe", StringComparison.OrdinalIgnoreCase);
                        if (!isLauncher && !string.Equals(name, ReleaseInstaller.ServerExe, StringComparison.OrdinalIgnoreCase)) continue;
                        picked.Add(new ProcInfo
                        {
                            Pid = pid,
                            ParentPid = parent,
                            SessionId = Convert.ToInt32(mo["SessionId"]),
                            Name = name,
                            CommandLine = isLauncher ? mo["CommandLine"] as string : null,
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException || ex is COMException)
            {
                throw new UpdateException("Couldn't list processes to stop the launcher (" + ex.Message + ").", ex);
            }

            foreach (var p in picked)
            {
                p.ImagePath = ImagePath(p.Pid);
                p.CanTerminate = CanTerminate(p.Pid);
                var created = all.TryGetValue(p.Pid, out var me) ? me.Created : null;
                p.ParentAlive = all.TryGetValue(p.ParentPid, out var parent)
                                && (parent.Created == null || created == null || parent.Created <= created);
            }
            return picked;
        }

        /// <summary>PIDs of wscript loops running <paramref name="launcherPath"/> in the session; null if they can't be listed.</summary>
        public static HashSet<int>? LauncherPids(string launcherPath, int sessionId)
        {
            var full = Path.GetFullPath(launcherPath);
            try
            {
                var pids = new HashSet<int>();
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, SessionId, CommandLine FROM Win32_Process WHERE Name='wscript.exe'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (Convert.ToInt32(mo["SessionId"]) != sessionId) continue;
                        var cmd = mo["CommandLine"] as string;
                        if (cmd != null && cmd.IndexOf(full, StringComparison.OrdinalIgnoreCase) >= 0) pids.Add(Convert.ToInt32(mo["ProcessId"]));
                    }
                }
                return pids;
            }
            catch (Exception ex) when (ex is ManagementException || ex is COMException)
            {
                return null;
            }
        }

        private static DateTime? ParseWmiDate(string? dmtf)
        {
            if (string.IsNullOrEmpty(dmtf)) return null;
            try { return ManagementDateTimeConverter.ToDateTime(dmtf); }
            catch { return null; }
        }
    }

    /// <summary>Finds LacertMCP.Server processes running from a given install.</summary>
    public static class RunningServers
    {
        public static IReadOnlyList<int> PidsUnder(string serverDir) => PidsUnder(serverDir, null);

        /// <summary>
        /// PIDs of LacertMCP.Server processes in THIS Windows session whose image lives
        /// under <paramref name="serverDir"/>. One whose path can't be read is not
        /// counted as ours; it is mentioned on <paramref name="warnings"/>.
        /// </summary>
        public static IReadOnlyList<int> PidsUnder(string serverDir, TextWriter? warnings)
        {
            var dir = Path.GetFullPath(serverDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var session = ProcessInspector.CurrentSessionId;
            var pids = new List<int>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ReleaseInstaller.ServerExe)))
            {
                using (p)
                {
                    int sid;
                    try { sid = p.SessionId; }
                    catch (InvalidOperationException) { continue; } // exited
                    if (sid != session) continue;

                    var path = ProcessInspector.ImagePath(p.Id);
                    if (path == null)
                    {
                        warnings?.WriteLine($"Note: LacertMCP.Server (PID {p.Id}) is running but its path can't be read; assuming it isn't this install's.");
                        continue;
                    }
                    if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id);
                }
            }
            return pids;
        }
    }
}
