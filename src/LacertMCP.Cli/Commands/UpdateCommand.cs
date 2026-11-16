using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    ///   <c>backups\</c>, and swaps the files in place, keeping the operator's
    ///   writable-field map. When the always-on launcher from <c>install-http.ps1</c>
    ///   runs this install, it is stopped for the swap and restarted after (then
    ///   <c>/healthz</c> is checked). Otherwise (a stdio server under Claude Desktop)
    ///   the update refuses while the server is running unless <c>--allow-running</c>.</description></item>
    ///   <item><description><b>Source checkout</b> (CLI running from
    ///   <c>src\LacertMCP.Cli\bin\...</c> in a git working copy) — fetches and
    ///   fast-forwards the current branch to its upstream, then prints the rebuild
    ///   command. Refuses on uncommitted changes or a diverged branch.</description></item>
    /// </list>
    /// <para>
    /// <c>--schedule daily</c> registers a per-user Windows scheduled task that runs
    /// <c>lacerte-mcp update</c> every day (default 03:00), so a new GitHub release
    /// reaches this PC without anyone touching it; <c>--schedule off</c> removes it.
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
            public void Stop(ServiceInfo service, string serverDir) { }
            public void Start(ServiceInfo service) { }
            public Task<bool> WaitHealthyAsync(ServiceInfo service, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        }

        public static Command Build()
        {
            var cmd = new Command("update",
                "Update this install from GitHub: downloads + verifies the latest release and swaps it in " +
                "(restarting the always-on server if install-http.ps1 set one up), or fast-forwards a git " +
                "working copy. --schedule daily makes it automatic. Exit 2 with --check = update available.");

            var checkOpt = new Option<bool>("--check", () => false,
                "Only report whether an update is available (exit 2 if so); change nothing.");
            var tagOpt = new Option<string?>("--tag", () => null,
                "Install this release (e.g. 0.2.1 or v0.2.1) instead of the latest. Allows downgrades.");
            var forceOpt = new Option<bool>("--force", () => false,
                "Reinstall even when the installed version is already current.");
            var resetMapOpt = new Option<bool>("--reset-field-map", () => false,
                "Take the release's field-access map instead of keeping the one built on this PC (the old one stays in the backup).");
            var allowRunningOpt = new Option<bool>("--allow-running", () => false,
                "No always-on launcher: swap files even while LacertMCP.Server.exe runs (e.g. under Claude Desktop). Restart the host right after.");
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
                    RunningServers = Update.RunningServers.PidsUnder,
                };
                context.ExitCode = await RunAsync(o, env, Console.Out, Console.Error, context.GetCancellationToken())
                    .ConfigureAwait(false);
            });

            return cmd;
        }

        public static async Task<int> RunAsync(Options o, Deps env, TextWriter stdout, TextWriter stderr, CancellationToken ct)
        {
            try
            {
                var install = ReleaseInstaller.Detect(o.InstallDir, env.ExeDir);

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
                        return 1;
                }

                try
                {
                    return await RunReleaseAsync(o, install, env, stdout, stderr, ct).ConfigureAwait(false);
                }
                catch (UpdateException ex)
                {
                    if (!o.Check) AppendLog(install.Root, "FAILED: " + ex.Message);
                    throw;
                }
            }
            catch (UpdateException ex)
            {
                stderr.WriteLine("Update failed: " + ex.Message);
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

            var installedLabel = env.InstalledVersionOverride ?? ReadInstalledVersion(install.ServerDir);
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
            }
            else
            {
                var pids = env.RunningServers(install.ServerDir);
                if (pids.Count > 0 && !o.AllowRunning)
                {
                    stderr.WriteLine($"LacertMCP.Server is running from this install (PID {string.Join(", ", pids)}).");
                    stderr.WriteLine("Quit Claude Desktop / your MCP host, then re-run `lacerte-mcp update`.");
                    stderr.WriteLine("(Or pass --allow-running and restart the host immediately after — a running server can't load mixed versions safely.)");
                    return 1;
                }
            }

            var asset = ReleaseInstaller.SelectZipAsset(release, target)
                        ?? throw new UpdateException($"Release v{target} has no LacertMCP-*.zip asset.");

            using var updateLock = AcquireLock(install.Root);

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

                ApplyResult result;
                try
                {
                    // Everything is downloaded and checked before the server goes down.
                    if (service != null)
                    {
                        stdout.WriteLine("Stopping the Lacerte MCP server...");
                        env.Service.Stop(service, install.ServerDir);
                    }

                    stdout.WriteLine($"{(cmp < 0 ? "Downgrading" : "Installing")} v{target}...");
                    result = ReleaseInstaller.Apply(staged, install.Root,
                        installed != null ? installed.ToString() : "unknown", stdout, o.ResetFieldMap);
                }
                finally
                {
                    // Restart whichever version is now in place — the new one, or the
                    // old one after a rollback — so a failed update never leaves the
                    // PC without its server.
                    if (service != null)
                    {
                        stdout.WriteLine("Starting the Lacerte MCP server...");
                        env.Service.Start(service);
                    }
                }

                stdout.WriteLine($"Updated {installedText} -> v{target} ({result.FilesWritten} files written).");
                stdout.WriteLine($"Previous version backed up to {result.BackupDir}");
                if (result.KeptFieldMapShards.Count > 0)
                {
                    stdout.WriteLine($"Kept this PC's field-access map ({string.Join(", ", result.KeptFieldMapShards)}).");
                    stdout.WriteLine("  (Pass --reset-field-map --force to take the release's map instead.)");
                }

                var healthy = true;
                if (service != null)
                {
                    healthy = await env.Service.WaitHealthyAsync(service, env.HealthTimeout, ct).ConfigureAwait(false);
                    stdout.WriteLine(healthy
                        ? $"Server is back up: http://localhost:{service.Port}/"
                        : $"The server did not answer on http://localhost:{service.Port}/healthz within {env.HealthTimeout.TotalSeconds:0} s. " +
                          $"Run \"{Path.Combine(install.ServerDir, ReleaseInstaller.ServerExe)}\" --http in a console to see why.");
                }
                else
                {
                    stdout.WriteLine("Restart Claude Desktop / your MCP host to load the new server.");
                }

                AppendLog(install.Root, $"updated {installedText} -> v{target}{(healthy ? string.Empty : " (server did not answer /healthz)")}");
                return healthy ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* temp — best-effort */ }
            }
        }

        // ---------------------------------------------------------------- schedule

        private static int ConfigureSchedule(Options o, InstallInfo install, Deps env, TextWriter stdout, TextWriter stderr)
        {
            var mode = (o.Schedule ?? string.Empty).Trim().ToLowerInvariant();
            string args;
            if (mode == "off")
            {
                args = $"/Delete /F /TN \"{ScheduledTaskName}\"";
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
                args = BuildScheduleArgs(Path.Combine(install.CliDir, ReleaseInstaller.CliExe), o.At!);
            }
            else
            {
                stderr.WriteLine($"--schedule '{o.Schedule}' — use 'daily' or 'off'.");
                return 1;
            }

            var (code, output) = env.Schtasks(args);
            if (code != 0)
            {
                stderr.WriteLine("schtasks failed: " + output.Trim());
                return 1;
            }
            stdout.WriteLine(mode == "off"
                ? $"Removed the \"{ScheduledTaskName}\" scheduled task."
                : $"Scheduled \"{ScheduledTaskName}\": runs `lacerte-mcp update` daily at {o.At} while you're signed in. " +
                  $"Each run is logged to {Path.Combine(install.Root, LogFileName)}.");
            return 0;
        }

        /// <summary>schtasks.exe arguments for the daily task (per-user, runs only while signed in — the server needs the desktop session).</summary>
        public static string BuildScheduleArgs(string cliExe, string at)
            => $"/Create /F /SC DAILY /ST {at} /TN \"{ScheduledTaskName}\" /TR \"\\\"{cliExe}\\\" update\"";

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

        /// <summary>InformationalVersion of the installed server exe (falls back to this CLI's build).</summary>
        private static string ReadInstalledVersion(string serverDir)
        {
            try
            {
                var exe = Path.Combine(serverDir, ReleaseInstaller.ServerExe);
                var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }
            catch { /* fall through */ }
            return BuildInfo.Version;
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

            var behind = CountCommits(repoRoot, "HEAD..@{u}");
            var ahead = CountCommits(repoRoot, "@{u}..HEAD");
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

        private static int CountCommits(string repoRoot, string range)
        {
            var r = Git(repoRoot, "rev-list --count " + range);
            return r.ExitCode == 0 && int.TryParse(r.Output.Trim(), out var n) ? n : 0;
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

        private static GitResult Git(string repoRoot, string args)
        {
            var psi = new ProcessStartInfo("git", $"-C \"{repoRoot}\" {args}")
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
                var errTask = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120_000))
                {
                    try { p.Kill(); } catch { /* already gone */ }
                    return new GitResult(-1, output, "git timed out");
                }
                return new GitResult(p.ExitCode, output, errTask.Result);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new UpdateException("git is not on PATH; it is needed to update a source checkout.");
            }
        }
    }
}
