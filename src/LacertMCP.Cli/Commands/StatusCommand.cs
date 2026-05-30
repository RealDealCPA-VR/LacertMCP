using System;
using System.CommandLine;
using System.Text.Json;
using LacertMCP.Cli.Storage;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp status</c>
    ///
    /// <para>
    /// Snapshot the install state — registration, mode, storage backend — without doing
    /// any wire I/O. Output is a JSON payload (machine-parseable) so external tooling
    /// (Doctor, CI) can act on it. The AppID is masked by default; pass <c>--show-app-id</c>
    /// to see the full GUID (useful for backup before a reinstall).
    /// </para>
    /// </summary>
    public static class StatusCommand
    {
        public static Command Build()
        {
            var cmd = new Command("status",
                "Print registration state, AppID (masked), mode, and storage backend.");

            var showFullOpt = new Option<bool>("--show-app-id", () => false,
                "Print the full AppID GUID instead of the masked form.");

            cmd.AddOption(showFullOpt);

            cmd.SetHandler((context) =>
            {
                var showFull = context.ParseResult.GetValueForOption(showFullOpt);
                context.ExitCode = Run(showFull, Console.Out);
            });

            return cmd;
        }

        public static int Run(bool showFull, System.IO.TextWriter writer)
        {
            var appId = AppIdStore.Get();
            var mode = IsLiveMode() ? "live" : "simulation";

            var payload = new
            {
                registered = appId.HasValue,
                appId = appId.HasValue
                    ? (showFull ? appId.Value.ToString("D") : Mask(appId.Value))
                    : null,
                appName = Environment.GetEnvironmentVariable("LACERTE_APP_NAME"),
                mode,
                storageBackend = AppIdStore.UsesCredentialManager
                    ? "windows-credential-manager"
                    : "file",
                storageLocation = AppIdStore.UsesCredentialManager
                    ? $"target={AppIdStore.CredentialTarget} account={AppIdStore.CredentialAccount}"
                    : AppIdStore.FallbackConfigPath,
                platform = Environment.OSVersion.Platform.ToString(),
                lastRegisterAttempt = (string?)null, // wire-tracked in Phase 9; not surfaced here
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            writer.WriteLine(json);
            return 0;
        }

        private static bool IsLiveMode()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            if (Environment.GetEnvironmentVariable("LACERTE_SIMULATION") == "1") return false;
            return Environment.GetEnvironmentVariable("LACERTE_LIVE") == "1";
        }

        private static string Mask(Guid g)
        {
            // Show the first 8 chars only — enough to disambiguate two installs without leaking the full GUID.
            var s = g.ToString("D");
            return s.Substring(0, 8) + "-****-****-****-************";
        }
    }
}
