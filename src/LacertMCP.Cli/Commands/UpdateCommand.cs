using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Cli.Update;
using LacertMCP.Server.Diagnostics;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp update [--check] [--tag &lt;x.y.z&gt;] [--force] [--reset-field-map]
    /// [--allow-running] [--install-dir &lt;dir&gt;] [--repo &lt;owner/name&gt;]
    /// [--schedule daily|off [--at HH:mm]]</c>
    ///
    /// <para>Brings this install up to date with what is on GitHub.</para>
    /// <list type="bullet">
    ///   <item><description><b>Release install</b> (an unzipped <c>LacertMCP-v*.zip</c>, or
    ///   <c>%LOCALAPPDATA%\LacertMCP</c> from <c>install-http.ps1</c>: <c>server\</c> +
    ///   <c>cli\</c> [+ <c>dumper\</c>]) — asks the GitHub Releases API for the latest
    ///   release (published by <c>.github/workflows/release.yml</c> on every <c>v*</c>
    ///   tag), downloads the zip, verifies size + SHA-256, backs up the install to
    ///   <c>backups\</c>, and swaps the files in place, merging this PC's
    ///   writable-field map entries into the release's. When the always-on launcher
    ///   from <c>install-http.ps1</c> runs this install, it is stopped for the swap and
    ///   restarted after; if the new server then reports the wrong version or doesn't
    ///   answer <c>/healthz</c>, the previous version is restored and restarted.
    ///   Otherwise (a stdio server under Claude Desktop) the update refuses while the
    ///   server is running unless <c>--allow-running</c>. An update interrupted
    ///   mid-swap (<c>.update-pending</c>) is restored at the start of the next run.</description></item>
    ///   <item><description><b>Source checkout</b> (CLI running from
    ///   <c>src\LacertMCP.Cli\bin\...</c> in a git working copy) — fetches and
    ///   fast-forwards the current branch to its upstream, then prints the rebuild
    ///   command. Refuses on uncommitted changes or a diverged branch.</description></item>
    /// </list>
    /// <para>
    /// <c>--schedule daily</c> registers a per-user Windows scheduled task that runs
    /// <c>lacerte-mcp update</c> every day (default 03:00, or as soon as the PC is on
    /// and you're signed in after a missed run), so a new GitHub release reaches this
    /// PC without anyone touching it; <c>--schedule off</c> removes it.
    /// Each release-install run appends one line to <c>update.log</c> in the install.
    /// </para>
    /// <para>Exit codes: 0 = updated / already current / schedule changed; 1 = failed
    /// or refused; 2 = <c>--check</c> found an update (nothing was changed).</para>
    /// </summary>
    public static class UpdateCommand
    {
        public const string DefaultRepo = "RealDealCPA-VR/LacertMCP";
        public const string ScheduledTaskName = "LacertMCP auto-update";
        public const string LogFileName = "update.log";

        private static readonly Regex RepoShape = new Regex(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex TimeShape = new Regex(@"^([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant);

        private const string SignOutAdvice =
            "This usually means install-http.ps1 (or an admin console) started it elevated. " +
            "Sign out of Windows and back in — the Startup launcher then runs without elevation — and retry `lacerte-mcp update`.";

        public sealed class Options
        {
            public bool Check { get; set; }
            public string? Tag { get; set; }
            public bool Force { get; set; }
            public bool ResetFieldMap { get; set; }
            public bool AllowRunning { get; set; }
            public string? InstallDir { get; set; }
            public string Repo { get; set; } = DefaultRepo;
            public string? Schedule { get; set; }
            public string At { get; set; } = "03:00";
        }

        /// <summary>Everything the command touches outside the install folder. Tests swap these out.</summary>
        public sealed class Deps
        {
            public Deps(IReleaseSource source, string exeDir)
            {
                Source = source;
                ExeDir = exeDir;
            }

            public IReleaseSource Source { get; }

            /// <summary>Folder of the running lacerte-mcp.exe.</summary>
            public string ExeDir { get; }

            /// <summary>Null = read the installed server exe's version.</summary>
            public string? InstalledVersionOverride { get; set; }

            /// <summary>
            /// Install's server\ dir → the version its <c>LacertMCP.Server.exe</c> reports
            /// (ProductVersion), or null when unreadable. Also the post-swap check.
            /// </summary>
            public Func<string, string?> ReadServerVersion { get; set; } = ReadServerExeVersion;

            public IServiceControl Service { get; set; } = new NoService();

            /// <summary>Install's server\ dir → PIDs running from it.</summary>
            public Func<string, IReadOnlyList<int>> RunningServers { get; set; } = _ => Array.Empty<int>();

            /// <summary>Runs <c>schtasks.exe</c> with the given argument string → (exit code, output).</summary>
            public Func<string, (int Code, string Output)> Schtasks { get; set; } = RunSchtasks;

            public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(45);
        }

        private sealed class NoService : IServiceControl
        {
            public ServiceInfo? Find(string serverDir) => null;
            public IReadOnlyList<int> ForeignServers(ServiceInfo service, string serverDir) => Array.Empty<int>();
            public void Stop(ServiceInfo service, string serverDir) { }
            public void Start(ServiceInfo service) { }
            public Task<bool> WaitHealthyAsync(ServiceInfo service, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        }

        public static Command Build()
        {
            var cmd = new Command("update",
                "Update this install from GitHub: downloads + verifies the latest release and swaps it in " +
                "(restarting the always-on server if install-http.ps1 set one up, and rolling back if it doesn't come up), " +
                "or fast-forwards a git working copy. --schedule daily makes it automatic. Exit 2 with --check = update available.");

            var checkOpt = new Option<bool>("--check", () => false,
                "Only report whether an update is available (exit 2 if so); change nothing.");
            var tagOpt = new Option<string?>("--tag", () => null,
                "Install this release (e.g. 0.2.1 or v0.2.1) instead of the latest. Allows downgrades.");
            var forceOpt = new Option<bool>("--force", () => false,
                "Reinstall even when the installed version is already current.");
            var resetMapOpt = new Option<bool>("--reset-field-map", () => false,
                "Take the release's field-access map as-is instead of merging in the entries built on this PC (the old map stays in the backup).");
            var allowRunningOpt = new Option<bool>("--allow-running", () => false,
                "Swap files even while a LacertMCP.Server.exe the launcher didn't start runs from this install (e.g. under Claude Desktop). Restart that host right after.");
            var dirOpt = new Option<string?>("--install-dir", () => null,
                "Install root to update (the folder holding server\\ and cli\\). Defaults to the install this CLI runs from.");
            var repoOpt = new Option<string>("--repo", () => DefaultRepo,
                "GitHub repository (owner/name) to take releases from.");
            var scheduleOpt = new Option<string?>("--schedule", () => null,
                "'daily' = add a Windows scheduled task that runs `lacerte-mcp update` every day; 'off' = remove it.");
            var atOpt = new Option<string>("--at", () => "03:00",
                "Time of day (HH:mm, 24h) for --schedule daily.");

            cmd.AddOption(checkOpt);
            cmd.AddOption(tagOpt);
            cmd.AddOption(forceOpt);
            cmd.AddOption(resetMapOpt);
            cmd.AddOption(allowRunningOpt);
            cmd.AddOption(dirOpt);
            cmd.AddOption(repoOpt);
            cmd.AddOption(scheduleOpt);
            cmd.AddOption(atOpt);

            cmd.SetHandler(async (context) =>
            {
                var pr = context.ParseResult;
                var o = new Options
                {
                    Check = pr.GetValueForOption(checkOpt),
                    Tag = pr.GetValueForOption(tagOpt),
                    Force = pr.GetValueForOption(forceOpt),
                    ResetFieldMap = pr.GetValueForOption(resetMapOpt),
                    AllowRunning = pr.GetValueForOption(allowRunningOpt),
                    InstallDir = pr.GetValueForOption(dirOpt),
                    Repo = pr.GetValueForOption(repoOpt) ?? DefaultRepo,
                    Schedule = pr.GetValueForOption(scheduleOpt),
                    At = pr.GetValueForOption(atOpt) ?? "03:00",
                };
                using var source = new GitHubReleaseSource("lacerte-mcp-updater/" + BuildInfo.Version);
                var env = new Deps(source, SchemaDirResolver.CliExeDir())
                {
                    Service = new LauncherService(),
                    RunningServers = dir => Update.RunningServers.PidsUnder(dir, Console.Error),
                };
                context.ExitCode = await RunAsync(o, env, Console.Out, Console.Error, context.GetCancellationToken())
                    .ConfigureAwait(false);
            });

            return cmd;
        }

        public static async Task<int> RunAsync(Options o, Deps env, TextWriter stdout, TextWriter stderr, CancellationToken ct)
        {
            InstallInfo? install = null;
            try
            {
                install = ReleaseInstaller.Detect(o.InstallDir, env.ExeDir);

                if (o.Schedule != null)
                {
                    return ConfigureSchedule(o, install, env, stdout, stderr);
                }

                switch (install.Layout)
                {
                    case InstallLayout.SourceCheckout:
                        return RunSourceCheckout(install.Root, o.Check, stdout, stderr);
                    case InstallLayout.Unknown:
                        stderr.WriteLine($"Can't tell how lacerte-mcp is installed at {install.Root}.");
                        stderr.WriteLine("Expected a release install (a folder holding server\\LacertMCP.Server.exe and cli\\lacerte-mcp.exe)");
                        stderr.WriteLine("or a git checkout of the repo. Pass --install-dir <root> to point at the release install.");
                        if (!o.Check) LogToExistingLog(install, env.ExeDir, $"refused: no release install found at {install.Root} (server\\{ReleaseInstaller.ServerExe} missing)");
                        return 1;
                }

                return await RunReleaseAsync(o, install, env, stdout, stderr, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Every failure — expected (UpdateException) or not (I/O, network, WMI) —
                // reaches update.log and ends in one concise line, exit 1.
                var message = ex is OperationCanceledException ? "cancelled"
                    : ex is UpdateException ? ex.Message
                    : ex.GetType().Name + ": " + ex.Message;
                if (install != null && install.Layout == InstallLayout.Release && !o.Check && o.Schedule == null)
                {
                    AppendLog(install.Root, "FAILED: " + message);
                }
                stderr.WriteLine("Update failed: " + message);
                return 1;
            }
        }

        private static async Task<int> RunReleaseAsync(
            Options o, InstallInfo install, Deps env, TextWriter stdout, TextWriter stderr, CancellationToken ct)
        {
            if (!RepoShape.IsMatch(o.Repo ?? string.Empty))
            {
                throw new UpdateException($"--repo '{o.Repo}' is not in owner/name form.");
            }

            string? tag = null;
            if (!string.IsNullOrWhiteSpace(o.Tag))
            {
                if (!ReleaseVersion.TryParse(o.Tag, out var wanted))
                {
                    throw new UpdateException($"--tag '{o.Tag}' is not a version like 0.2.1.");
                }
                tag = "v" + wanted;
            }

            // Taken first, so an interrupted-update marker is never mistaken for one
            // that another `lacerte-mcp update` is in the middle of writing.
            using var updateLock = o.Check ? null : AcquireLock(install.Root);

            var reinstall = false;
            if (o.Check)
            {
                if (ReleaseInstaller.HasPendingMarker(install.Root))
                {
                    stdout.WriteLine("An earlier update was interrupted mid-swap; the next `lacerte-mcp update` restores the previous version first.");
                }
            }
            else
            {
                var recovery = RecoverInterruptedUpdate(o, install, env, stdout, stderr);
                if (recovery == Recovery.Refused) return 1;
                reinstall = recovery == Recovery.Reinstall;
            }

            var installedLabel = reinstall
                ? "unknown (interrupted update)"
                : env.InstalledVersionOverride ?? env.ReadServerVersion(install.ServerDir) ?? BuildInfo.Version;
            ReleaseVersion.TryParse(installedLabel, out var installed);
            var installedText = installed != null ? "v" + installed : "unknown (" + installedLabel + ")";

            stdout.WriteLine($"Install:   {install.Root}");
            stdout.WriteLine($"Installed: {installedText}");

            var release = await env.Source.GetReleaseAsync(o.Repo!, tag, ct).ConfigureAwait(false);
            if (!ReleaseVersion.TryParse(release.Tag, out var target))
            {
                throw new UpdateException($"Release tag '{release.Tag}' is not a version.");
            }
            stdout.WriteLine($"{(tag == null ? "Latest:    " : "Requested: ")}v{target}{(release.HtmlUrl != null ? "  (" + release.HtmlUrl + ")" : string.Empty)}");

            var cmp = installed == null ? 1 : target.CompareTo(installed);

            if (o.Check)
            {
                if (cmp > 0)
                {
                    stdout.WriteLine("Update available. Run `lacerte-mcp update` to install it.");
                    return 2;
                }
                stdout.WriteLine("Up to date.");
                return 0;
            }

            if ((cmp == 0 || (cmp < 0 && tag == null)) && !o.Force)
            {
                stdout.WriteLine(cmp == 0
                    ? "Already up to date. (Pass --force to reinstall.)"
                    : "The installed build is newer than the latest release; nothing to do. (Pass --tag to pick a release.)");
                AppendLog(install.Root, $"up to date ({installedText}; latest v{target})");
                return 0;
            }

            var service = env.Service.Find(install.ServerDir);
            if (service != null)
            {
                stdout.WriteLine($"Always-on server found ({service.LauncherPath}, port {service.Port}); it will be restarted.");
                if (RefuseIfRunning(env.Service.ForeignServers(service, install.ServerDir), o, install, stderr)) return 1;
            }
            else if (RefuseIfRunning(env.RunningServers(install.ServerDir), o, install, stderr))
            {
                return 1;
            }

            var asset = ReleaseInstaller.SelectZipAsset(release, target)
                        ?? throw new UpdateException($"Release v{target} has no {ReleaseInstaller.ZipAssetName(target)} asset.");

            var work = Path.Combine(Path.GetTempPath(), "lacerte-mcp-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var zipPath = Path.Combine(work, asset.Name);
                stdout.WriteLine($"Downloading {asset.Name}{(asset.Size >= 0 ? $" ({asset.Size / 1024 / 1024.0:0.0} MB)" : string.Empty)}...");
                await env.Source.DownloadAsync(asset, zipPath, ct).ConfigureAwait(false);
                ReleaseInstaller.VerifyDownload(zipPath, asset);
                stdout.WriteLine(asset.Sha256 != null ? "  SHA-256 verified." : "  Size verified (the release publishes no SHA-256).");

                var staged = Path.Combine(work, "staged");
                ReleaseInstaller.ExtractAndValidate(zipPath, staged);

                // Everything is downloaded and checked before the server goes down. A
                // launcher that can't be stopped aborts here — nothing changed, and no
                // second launcher is started.
                if (service != null && !TryStop(service, install, env, stdout, stderr, "refused")) return 1;

                stdout.WriteLine($"{(cmp < 0 ? "Downgrading" : "Installing")} v{target}...");
                ApplyResult result;
                try
                {
                    result = ReleaseInstaller.Apply(staged, install.Root,
                        installed != null ? installed.ToString() : "unknown", stdout, o.ResetFieldMap, target.ToString());
                }
                catch
                {
                    // Apply restored the previous files; bring that version back up so a
                    // failed update never leaves the PC without its server.
                    if (service != null) StartService(service, env, stdout);
                    throw;
                }

                // The swap must have produced the release that was asked for.
                var landed = env.ReadServerVersion(install.ServerDir);
                if (!ReleaseVersion.TryParse(landed, out var landedVersion) || landedVersion.CompareTo(target) != 0)
                {
                    return await RollBackAsync(install, env, service, serviceRunning: false, result.BackupDir, installedText, target,
                        $"the installed server reports {(landed == null ? "no version" : "version " + landed)}, not v{target}",
                        stdout, stderr, ct).ConfigureAwait(false);
                }

                if (service != null) StartService(service, env, stdout);

                stdout.WriteLine($"Updated {installedText} -> v{target} ({result.FilesWritten} files written).");
                stdout.WriteLine($"Previous version backed up to {result.BackupDir}");
                var mapLines = result.FieldMap.Describe().Concat(result.Dictionary.Describe()).ToList();
                foreach (var line in mapLines) stdout.WriteLine(line);
                if (result.FieldMap.KeptFromThisPc > 0)
                {
                    stdout.WriteLine("  (Pass --reset-field-map --force to take the release's map as-is instead.)");
                }

                if (service != null)
                {
                    var healthy = await env.Service.WaitHealthyAsync(service, env.HealthTimeout, ct).ConfigureAwait(false);
                    if (!healthy)
                    {
                        stdout.WriteLine($"The new server did not answer on http://localhost:{service.Port}/healthz within {env.HealthTimeout.TotalSeconds:0} s.");
                        return await RollBackAsync(install, env, service, serviceRunning: true, result.BackupDir, installedText, target,
                            "the server did not answer /healthz", stdout, stderr, ct).ConfigureAwait(false);
                    }
                    stdout.WriteLine($"Server is back up: http://localhost:{service.Port}/");
                }
                else
                {
                    stdout.WriteLine("Restart Claude Desktop / your MCP host to load the new server.");
                }

                TryClearPending(install.Root);
                AppendLog(install.Root, $"updated {installedText} -> v{target}" +
                    (mapLines.Count > 0 ? " [field map: " + string.Join(" ", mapLines) + "]" : string.Empty));
                return 0;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* temp — best-effort */ }
            }
        }

        private enum Recovery { None, Restored, Reinstall, Refused }

        /// <summary>
        /// A <c>.update-pending</c> marker means an earlier run was interrupted
        /// mid-swap (console closed, reboot, power loss), leaving a possible mix of
        /// versions that would otherwise read as up to date. Restore its backup first.
        /// </summary>
        private static Recovery RecoverInterruptedUpdate(Options o, InstallInfo install, Deps env, TextWriter stdout, TextWriter stderr)
        {
            if (!ReleaseInstaller.HasPendingMarker(install.Root)) return Recovery.None;

            var pending = ReleaseInstaller.ReadPending(install.Root);
            if (pending == null || !Directory.Exists(Path.Combine(pending.BackupDir, "server")))
            {
                stdout.WriteLine("An earlier update was interrupted mid-swap and its backup is gone; reinstalling the release over it.");
                AppendLog(install.Root, $"found an interrupted update whose backup ({pending?.BackupDir ?? "unrecorded"}) is gone; reinstalling");
                TryClearPending(install.Root);
                return Recovery.Reinstall;
            }

            var from = VersionText(pending.FromVersion);
            stdout.WriteLine($"An earlier update ({from} -> {VersionText(pending.TargetVersion)}) was interrupted mid-swap; restoring {from} from {pending.BackupDir} first.");
            AppendLog(install.Root, $"found an interrupted update ({from} -> {VersionText(pending.TargetVersion)}); restoring from {pending.BackupDir}");

            var service = env.Service.Find(install.ServerDir);
            if (service != null)
            {
                if (RefuseIfRunning(env.Service.ForeignServers(service, install.ServerDir), o, install, stderr)) return Recovery.Refused;
                if (!TryStop(service, install, env, stdout, stderr, "refused to restore the interrupted update")) return Recovery.Refused;
            }
            else if (RefuseIfRunning(env.RunningServers(install.ServerDir), o, install, stderr))
            {
                return Recovery.Refused;
            }

            var ok = ReleaseInstaller.Restore(pending.BackupDir, install.Root, stdout, out var error);
            if (service != null) StartService(service, env, stdout);
            if (ok) TryClearPending(install.Root);
            if (!ok)
            {
                throw new UpdateException($"restoring the interrupted update from {pending.BackupDir} failed ({error}). Copy server\\, cli\\ and dumper\\ back from it by hand.");
            }
            AppendLog(install.Root, $"restored {from} after the interrupted update");
            return Recovery.Restored;
        }

        /// <summary>
        /// Puts the backup back after a swap that completed but produced a bad server
        /// (wrong version, or no /healthz), restarts it, and logs "rolled back to vX".
        /// Always exit 1.
        /// </summary>
        private static async Task<int> RollBackAsync(InstallInfo install, Deps env, ServiceInfo? service, bool serviceRunning,
            string backupDir, string previousText, ReleaseVersion target, string reason,
            TextWriter stdout, TextWriter stderr, CancellationToken ct)
        {
            stdout.WriteLine($"Rolling back to {previousText} ({reason})...");
            if (service != null && serviceRunning
                && !TryStop(service, install, env, stdout, stderr, $"FAILED: v{target}: {reason}; couldn't stop the server to roll back"))
            {
                return 1;
            }

            var ok = ReleaseInstaller.Restore(backupDir, install.Root, stdout, out var error);
            if (ok) TryClearPending(install.Root);

            var healthy = true;
            if (service != null)
            {
                StartService(service, env, stdout);
                healthy = await env.Service.WaitHealthyAsync(service, env.HealthTimeout, ct).ConfigureAwait(false);
            }

            if (ok)
            {
                AppendLog(install.Root, $"FAILED: v{target}: {reason}; rolled back to {previousText}" +
                    (healthy ? string.Empty : " (the restored server did not answer /healthz either)"));
                stderr.WriteLine($"Update to v{target} failed: {reason}. Rolled back to {previousText}.");
                if (service != null)
                {
                    stderr.WriteLine(healthy
                        ? $"The previous server is back up: http://localhost:{service.Port}/"
                        : $"The restored server did not answer either. Run \"{Path.Combine(install.ServerDir, ReleaseInstaller.ServerExe)}\" --http in a console to see why.");
                }
            }
            else
            {
                AppendLog(install.Root, $"FAILED: v{target}: {reason}; ROLLBACK FAILED ({error}); the next run retries it from {backupDir}");
                stderr.WriteLine($"Update to v{target} failed: {reason}, and restoring {previousText} failed too ({error}).");
                stderr.WriteLine($"Run `lacerte-mcp update` again to retry the restore, or copy server\\, cli\\ and dumper\\ back from {backupDir} by hand.");
            }
            return 1;
        }

        /// <summary>Stops the always-on server; on a refusal (elevated launcher) prints why, logs it and returns false.</summary>
        private static bool TryStop(ServiceInfo service, InstallInfo install, Deps env, TextWriter stdout, TextWriter stderr, string logPrefix)
        {
            stdout.WriteLine("Stopping the Lacerte MCP server...");
            try
            {
                env.Service.Stop(service, install.ServerDir);
                return true;
            }
            catch (ServiceStopException ex)
            {
                stderr.WriteLine("Couldn't stop the always-on server: " + ex.Message);
                stderr.WriteLine("No files were changed. " + SignOutAdvice);
                AppendLog(install.Root, logPrefix + ": couldn't stop the always-on server (" + ex.Message + ")");
                return false;
            }
        }

        /// <summary>
        /// Clears the in-progress marker without ever letting a failure (locked by
        /// antivirus, ACL) skip the server restart that follows or abort a
        /// finished update. The next run then sees the marker and re-restores.
        /// </summary>
        private static void TryClearPending(string root)
        {
            try { ReleaseInstaller.ClearPending(root); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AppendLog(root, "warning: couldn't remove .update-pending (" + ex.Message + ")");
            }
        }

        private static void StartService(ServiceInfo service, Deps env, TextWriter stdout)
        {
            stdout.WriteLine("Starting the Lacerte MCP server...");
            env.Service.Start(service);
        }

        /// <summary>Refuses (true) while servers the update can't stop run from the install, unless --allow-running.</summary>
        private static bool RefuseIfRunning(IReadOnlyList<int> pids, Options o, InstallInfo install, TextWriter stderr)
        {
            if (pids.Count == 0 || o.AllowRunning) return false;
            var list = string.Join(", ", pids);
            stderr.WriteLine($"LacertMCP.Server is running from this install (PID {list}).");
            stderr.WriteLine("Quit Claude Desktop / your MCP host, then re-run `lacerte-mcp update`.");
            stderr.WriteLine("(Or pass --allow-running and restart the host immediately after — a running server can't load mixed versions safely.)");
            AppendLog(install.Root, $"refused: LacertMCP.Server is running from this install (PID {list}); quit the MCP host or pass --allow-running");
            return true;
        }

        private static string VersionText(string label)
            => ReleaseVersion.TryParse(label, out var v) ? "v" + v : label;

        // ---------------------------------------------------------------- schedule

        private static int ConfigureSchedule(Options o, InstallInfo install, Deps env, TextWriter stdout, TextWriter stderr)
        {
            var mode = (o.Schedule ?? string.Empty).Trim().ToLowerInvariant();
            int code;
            string output;
            if (mode == "off")
            {
                (code, output) = env.Schtasks($"/Delete /F /TN \"{ScheduledTaskName}\"");
            }
            else if (mode == "daily")
            {
                if (install.Layout != InstallLayout.Release)
                {
                    stderr.WriteLine("--schedule needs a release install (server\\ + cli\\); this CLI isn't running from one. Pass --install-dir.");
                    return 1;
                }
                if (!TimeShape.IsMatch(o.At ?? string.Empty))
                {
                    stderr.WriteLine($"--at '{o.At}' is not a 24-hour HH:mm time.");
                    return 1;
                }
                var xml = BuildScheduleXml(Path.Combine(install.CliDir, ReleaseInstaller.CliExe), o.At!, CurrentUserId(), DateTime.Today);
                var xmlPath = Path.Combine(Path.GetTempPath(), "lacerte-mcp-task-" + Guid.NewGuid().ToString("N") + ".xml");
                // schtasks /XML reads UTF-16 (the encoding the declaration names).
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                try
                {
                    (code, output) = env.Schtasks(BuildScheduleArgs(xmlPath));
                }
                finally
                {
                    try { File.Delete(xmlPath); } catch { /* temp — best-effort */ }
                }
            }
            else
            {
                stderr.WriteLine($"--schedule '{o.Schedule}' — use 'daily' or 'off'.");
                return 1;
            }

            if (code != 0)
            {
                stderr.WriteLine("schtasks failed: " + output.Trim());
                return 1;
            }
            stdout.WriteLine(mode == "off"
                ? $"Removed the \"{ScheduledTaskName}\" scheduled task."
                : $"Scheduled \"{ScheduledTaskName}\": runs `lacerte-mcp update` daily at {o.At} while you're signed in " +
                  "(or as soon as you are, if the PC was off or asleep then). " +
                  $"Each run is logged to {Path.Combine(install.Root, LogFileName)}.");
            return 0;
        }

        /// <summary>schtasks.exe arguments that register the task from <paramref name="xmlPath"/> (see <see cref="BuildScheduleXml"/>).</summary>
        public static string BuildScheduleArgs(string xmlPath)
            => $"/Create /F /TN \"{ScheduledTaskName}\" /XML \"{xmlPath}\"";

        /// <summary>
        /// Task Scheduler XML for the daily task. <c>schtasks /SC DAILY</c> can't set
        /// <c>StartWhenAvailable</c>, so a PC asleep or off at the set time would never
        /// update; this runs the missed run as soon as the user is signed in. Per-user,
        /// interactive token, only while signed in (the server needs the desktop
        /// session), no elevation, normal priority, AC or battery.
        /// </summary>
        public static string BuildScheduleXml(string cliExe, string at, string userId, DateTime day)
        {
            var start = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + at + ":00";
            string X(string s) => SecurityElement.Escape(s) ?? string.Empty;
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                "    <Description>Keeps the Lacerte MCP up to date with its GitHub releases (lacerte-mcp update --schedule daily).</Description>\r\n" +
                "  </RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <CalendarTrigger>\r\n" +
                $"      <StartBoundary>{start}</StartBoundary>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "      <ScheduleByDay>\r\n" +
                "        <DaysInterval>1</DaysInterval>\r\n" +
                "      </ScheduleByDay>\r\n" +
                "    </CalendarTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                $"      <UserId>{X(userId)}</UserId>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>LeastPrivilege</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings>\r\n" +
                "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
                "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
                "    </IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                $"      <Command>{X(cliExe)}</Command>\r\n" +
                "      <Arguments>update</Arguments>\r\n" +
                $"      <WorkingDirectory>{X(Path.GetDirectoryName(cliExe) ?? string.Empty)}</WorkingDirectory>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";
        }

        /// <summary>The signed-in user's SID (immune to non-ASCII / renamed account names), else DOMAIN\user.</summary>
        private static string CurrentUserId()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return id.User?.Value ?? id.Name;
            }
            catch
            {
                return System.Environment.UserDomainName + "\\" + System.Environment.UserName;
            }
        }

        private static (int Code, string Output) RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, output + err.Result);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return (-1, "schtasks.exe could not be started: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static IDisposable AcquireLock(string root)
        {
            try
            {
                return new FileStream(Path.Combine(root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                throw new UpdateException("Another `lacerte-mcp update` is already running against this install.");
            }
        }

        private static void AppendLog(string root, string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(root, LogFileName),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + System.Environment.NewLine);
            }
            catch { /* the log is a convenience */ }
        }

        /// <summary>
        /// For a refusal outside a recognisable install: append to an update.log that
        /// already exists there (e.g. a scheduled run after server\ went missing), never create one.
        /// </summary>
        private static void LogToExistingLog(InstallInfo install, string exeDir, string line)
        {
            var candidates = new List<string> { install.Root };
            try
            {
                var exe = new DirectoryInfo(Path.GetFullPath(exeDir));
                if (exe.Parent != null && string.Equals(exe.Name, "cli", StringComparison.OrdinalIgnoreCase)) candidates.Add(exe.Parent.FullName);
            }
            catch { /* no usable exe dir */ }
            foreach (var dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, LogFileName)))
                {
                    AppendLog(dir, line);
                    return;
                }
            }
        }

        /// <summary>ProductVersion (InformationalVersion) of the server exe in <paramref name="serverDir"/>, or null.</summary>
        private static string? ReadServerExeVersion(string serverDir)
        {
            try
            {
                var exe = Path.Combine(serverDir, ReleaseInstaller.ServerExe);
                if (!File.Exists(exe)) return null;
                var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }
            catch { /* fall through */ }
            return null;
        }

        // ---------------------------------------------------------------- source checkout

        public static int RunSourceCheckout(string repoRoot, bool check, TextWriter stdout, TextWriter stderr)
        {
            stdout.WriteLine($"Source checkout: {repoRoot}");

            var upstream = Git(repoRoot, "rev-parse --abbrev-ref --symbolic-full-name @{u}");
            if (upstream.ExitCode != 0)
            {
                stderr.WriteLine("The current branch has no upstream to update from (git rev-parse @{u} failed).");
                return 1;
            }
            var upstreamName = upstream.Output.Trim();

            var fetch = Git(repoRoot, "fetch --quiet");
            if (fetch.ExitCode != 0)
            {
                stderr.WriteLine("git fetch failed: " + fetch.Error.Trim());
                return 1;
            }

            var behind = CountCommits(repoRoot, "HEAD..@{u}", stderr);
            var ahead = CountCommits(repoRoot, "@{u}..HEAD", stderr);
            if (behind == null || ahead == null) return 1;
            if (behind == 0)
            {
                stdout.WriteLine($"Up to date with {upstreamName}.");
                return 0;
            }
            if (check)
            {
                stdout.WriteLine($"{behind} new commit(s) on {upstreamName}. Run `lacerte-mcp update` to pull them.");
                return 2;
            }

            var dirty = Git(repoRoot, "status --porcelain --untracked-files=no");
            if (dirty.ExitCode != 0 || dirty.Output.Trim().Length > 0)
            {
                stderr.WriteLine("The working copy has uncommitted changes. Commit or stash them, then re-run.");
                return 1;
            }
            if (ahead > 0)
            {
                stderr.WriteLine($"This branch has {ahead} local commit(s) not on {upstreamName}; a fast-forward isn't possible. Merge or rebase by hand.");
                return 1;
            }

            var merge = Git(repoRoot, "merge --ff-only --quiet @{u}");
            if (merge.ExitCode != 0)
            {
                stderr.WriteLine("git merge --ff-only failed: " + (merge.Error + merge.Output).Trim());
                return 1;
            }

            stdout.WriteLine($"Pulled {behind} commit(s) from {upstreamName}.");
            stdout.WriteLine("Rebuild to use them — stop the running server first (it locks its exe), then run:");
            stdout.WriteLine("  ./scripts/build.ps1 -Configuration Release");
            return 0;
        }

        /// <summary>Commits in <paramref name="range"/>, or null (reported) when git can't count them — never a silent 0 that reads as "up to date".</summary>
        private static int? CountCommits(string repoRoot, string range, TextWriter stderr)
        {
            var r = Git(repoRoot, "rev-list --count " + range);
            if (r.ExitCode == 0 && int.TryParse(r.Output.Trim(), out var n)) return n;
            stderr.WriteLine($"git rev-list --count {range} failed: " + (r.Error + r.Output).Trim());
            return null;
        }

        private readonly struct GitResult
        {
            public GitResult(int exitCode, string output, string error)
            {
                ExitCode = exitCode;
                Output = output;
                Error = error;
            }

            public int ExitCode { get; }
            public string Output { get; }
            public string Error { get; }
        }

        private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(120);

        private static GitResult Git(string repoRoot, string args)
        {
            // A trailing backslash would escape the closing quote ("D:\repo\" → D:\repo").
            var root = repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length == 0 || root.EndsWith(":", StringComparison.Ordinal)) root += Path.DirectorySeparatorChar + ".";
            var psi = new ProcessStartInfo("git", $"-C \"{root}\" {args}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            try
            {
                using var p = Process.Start(psi)!;
                // Both streams drain in the background so the timeout below is real
                // (a blocking ReadToEnd would wait as long as git does).
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit((int)GitTimeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch { /* already gone */ }
                    return new GitResult(-1, string.Empty, $"git {args} timed out after {GitTimeout.TotalSeconds:0} s");
                }
                p.WaitForExit(); // flush the async readers
                return new GitResult(p.ExitCode, outTask.Result, errTask.Result);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new UpdateException("git is not on PATH; it is needed to update a source checkout.");
            }
        }
    }
}
