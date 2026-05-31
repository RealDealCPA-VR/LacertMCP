using System;
using System.CommandLine;
using LacertMCP.Cli.Storage;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp login [--app-name X] [--tax-year Y]</c>
    ///
    /// <para>
    /// Re-runs the SDK <c>Register()</c> against the existing GUID to refresh the 24-hour
    /// Intuit session. Silent if the ticket cache is warm; surfaces the Intuit sign-in window
    /// if expired. Per agent 02 §3, this is the only path that legitimately re-opens the
    /// sign-in dialog — the MCP server itself never does.
    /// </para>
    /// </summary>
    public static class LoginCommand
    {
        public static Command Build()
        {
            var cmd = new Command("login",
                "Refresh the 24-hour Intuit sign-in for the existing AppID.");

            var appNameOpt = new Option<string>("--app-name", () => "Operator Lacerte MCP",
                "Friendly name to pass to Register (must match the one used at first register).");
            var taxYearOpt = new Option<int>("--tax-year", () => DateTime.UtcNow.Year - 1,
                "Tax year scope for the login.");

            cmd.AddOption(appNameOpt);
            cmd.AddOption(taxYearOpt);

            cmd.SetHandler((context) =>
            {
                var appName = context.ParseResult.GetValueForOption(appNameOpt)!;
                var taxYear = context.ParseResult.GetValueForOption(taxYearOpt);
                context.ExitCode = Run(appName, taxYear);
            });

            return cmd;
        }

        public static int Run(string appName, int taxYear)
        {
            var existing = AppIdStore.Get();
            if (!existing.HasValue)
            {
                Console.Error.WriteLine("No AppID found. Run `lacerte-mcp register` first.");
                return 1;
            }

            Console.WriteLine("Refreshing Lacerte SDK session:");
            // Login runs frequently (every 24h ticket refresh); always print the masked AppID.
            // The full GUID was already surfaced once at register-time for the operator's backup —
            // re-echoing it on every refresh is unnecessary credential exposure. Re-fetch the full
            // GUID via `lacerte-mcp status --show-app-id` if a backup is needed.
            Console.WriteLine($"  AppID  : {AppIdStore.Mask(existing.Value)}");
            Console.WriteLine($"  AppName: {appName}");
            Console.WriteLine($"  TaxYear: {taxYear}");
            Console.WriteLine();

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // Off-Windows: keep exit 0 (cross-platform dev convenience —
                // operator can't run the ceremony off-Windows anyway).
                Console.WriteLine("[stub] Live login requires Windows + Lacerte SDK.");
                return 0;
            }

            // P15-028: Login runs on a cron (every 24h ticket refresh). A silent exit-0
            // here lets a stale ticket lurk forever — the operator's cron logs would
            // show "success" while tools return TicketExpired (9103). Route the partial
            // message to stderr and return exit 3 so the cron wrapper can alarm.
            Console.Error.WriteLine("[partial] Login refresh not yet wired (Phase 9 task #63).");
            Console.Error.WriteLine("          Existing 24h ticket cannot be renewed until upgrade — tools will return TicketExpired (9103) once the current ticket lapses.");
            return 3;
        }
    }
}
