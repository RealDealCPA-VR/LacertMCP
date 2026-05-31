using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using LacertMCP.Cli.Storage;
using LacertMCP.Server.Diagnostics;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp status [--show-app-id] [--json]</c>
    ///
    /// <para>
    /// Snapshot the install state — registration, mode, storage backend — without doing
    /// any wire I/O. Default output is a 7-line human summary so an operator typing
    /// <c>lacerte-mcp status</c> in PowerShell doesn't have to eyeball-parse JSON.
    /// Pass <c>--json</c> for the machine-parseable form (existing shape preserved —
    /// any script already consuming it just needs to add <c>--json</c>).
    /// The AppID is masked by default; pass <c>--show-app-id</c> to see the full GUID
    /// (useful for backup before a reinstall).
    /// </para>
    /// <para>
    /// P15-026: flipped from JSON-only to the same human-default + <c>--json</c> opt-in
    /// shape used by <c>tools</c>. Sibling diagnostic verb <c>doctor</c> follows the
    /// same convention.
    /// </para>
    /// </summary>
    public static class StatusCommand
    {
        public static Command Build()
        {
            var cmd = new Command("status",
                "Print registration state, AppID (masked), mode, and storage backend. " +
                "Human summary by default; pass --json for machine-parseable output.");

            var showFullOpt = new Option<bool>("--show-app-id", () => false,
                "Print the full AppID GUID instead of the masked form.");
            var jsonOpt = new Option<bool>("--json", () => false,
                "Emit machine-parseable JSON instead of the human-readable summary.");

            cmd.AddOption(showFullOpt);
            cmd.AddOption(jsonOpt);

            cmd.SetHandler((context) =>
            {
                var showFull = context.ParseResult.GetValueForOption(showFullOpt);
                var asJson = context.ParseResult.GetValueForOption(jsonOpt);
                context.ExitCode = Run(showFull, asJson, Console.Out);
            });

            return cmd;
        }

        /// <summary>
        /// Back-compat overload — preserved for older test call sites that pre-date
        /// the P15-026 <c>asJson</c> parameter. Defaults to the new human format.
        /// </summary>
        public static int Run(bool showFull, TextWriter writer)
            => Run(showFull, asJson: false, writer);

        public static int Run(bool showFull, bool asJson, TextWriter writer)
        {
            var appId = AppIdStore.Get();
            var mode = IsLiveMode() ? "live" : "simulation";
            var backend = AppIdStore.UsesCredentialManager
                ? "windows-credential-manager"
                : "file";
            var location = AppIdStore.UsesCredentialManager
                ? $"target={AppIdStore.CredentialTarget} account={AppIdStore.CredentialAccount}"
                : AppIdStore.FallbackConfigPath;
            var appName = Environment.GetEnvironmentVariable("LACERTE_APP_NAME");
            var platform = Environment.OSVersion.Platform.ToString();

            if (asJson)
            {
                var payload = new
                {
                    registered = appId.HasValue,
                    appId = appId.HasValue
                        ? (showFull ? appId.Value.ToString("D") : AppIdStore.Mask(appId.Value))
                        : null,
                    appName,
                    mode,
                    storageBackend = backend,
                    storageLocation = location,
                    platform,
                    // P14-040: build stamp lets the operator disambiguate dev (sim) and
                    // operator (live) binaries when filing bug reports. Sourced from
                    // Directory.Build.props -> AssemblyInformationalVersion.
                    buildVersion = BuildInfo.Version,
                    lastRegisterAttempt = (string?)null, // wire-tracked in Phase 9; not surfaced here
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                writer.WriteLine(json);
                return 0;
            }

            // --- Default: human-readable summary (P15-026) ---
            writer.WriteLine("lacerte-mcp status");
            writer.WriteLine(new string('-', 60));
            if (appId.HasValue)
            {
                writer.WriteLine("  Registered : yes");
                var idStr = showFull
                    ? appId.Value.ToString("D")
                    : AppIdStore.Mask(appId.Value) + (showFull ? "" : "  (pass --show-app-id for full GUID)");
                writer.WriteLine($"  AppID      : {idStr}");
            }
            else
            {
                writer.WriteLine("  Registered : no");
            }
            if (!string.IsNullOrEmpty(appName))
            {
                writer.WriteLine($"  App name   : {appName}");
            }
            writer.WriteLine($"  Mode       : {mode}");
            writer.WriteLine($"  Backend    : {backend}");
            writer.WriteLine($"  Location   : {location}");
            writer.WriteLine($"  Platform   : {platform}");
            writer.WriteLine($"  Build      : {BuildInfo.Version}");
            return 0;
        }

        private static bool IsLiveMode()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            if (Environment.GetEnvironmentVariable("LACERTE_SIMULATION") == "1") return false;
            return Environment.GetEnvironmentVariable("LACERTE_LIVE") == "1";
        }
    }
}
