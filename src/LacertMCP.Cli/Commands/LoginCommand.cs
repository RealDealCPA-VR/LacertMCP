using System;
using System.CommandLine;
using LacertMCP.Cli.Storage;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp login [--app-name X] [--tax-year Y]</c>
    ///
    /// <para>
    /// Re-runs the SDK <c>Register()</c> against the existing GUID to refresh the 24-hour
    /// Intuit session. Silent if the ticket cache is warm; surfaces the Intuit sign-in window
    /// if expired. Per agent 02 §3, this is the only path that legitimately re-opens the
    /// sign-in dialog — the MCP server itself never does. Wired through
    /// <see cref="Registration.Register"/> (same wire call as <c>register</c>; the one-time
    /// consent is already on file so only the sign-in dialog can appear).
    /// </para>
    ///
    /// <para>
    /// Exit codes mirror <see cref="RegisterCommand"/>: 0 AccessGranted (or off-Windows no-op),
    /// 1 refresh failed (NotSignedIn etc., or no AppID minted yet), 2 PARTIAL (Lacerte SDK COM
    /// surface unavailable on this Windows box).
    /// </para>
    /// </summary>
    public static class LoginCommand
    {
        public static Command Build()
        {
            var cmd = new Command("login",
                "Refresh the 24-hour Intuit sign-in for the existing AppID. " +
                "Exit 0 on AccessGranted, 1 on a failed refresh (or no AppID), 2 when the Lacerte SDK is unavailable.");

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

        /// <summary>
        /// Production entry point — runs the real <see cref="Registration.Register"/> refresh.
        /// </summary>
        public static int Run(string appName, int taxYear)
            => Run(appName, taxYear, Registration.Register);

        /// <summary>
        /// Testable seam (mirrors <see cref="RegisterCommand"/>): <paramref name="ceremony"/>
        /// stands in for <see cref="Registration.Register"/> so tests can pin the exit-code
        /// matrix without COM or interactive dialogs.
        /// </summary>
        public static int Run(string appName, int taxYear, Func<string, string, int, int> ceremony)
        {
            if (ceremony == null) throw new ArgumentNullException(nameof(ceremony));

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
                // operator can't run the sign-in refresh off-Windows anyway).
                Console.WriteLine("[skipped] Live login requires Windows + the Lacerte SDK.");
                Console.WriteLine("          Run `lacerte-mcp login` on the Windows + Lacerte box to refresh the ticket.");
                return 0;
            }

            // P15-028 / P16-004 context preserved: login runs on a cron (every 24h
            // ticket refresh), so a silent success-looking exit on a failed refresh
            // would let a stale ticket lurk until tools return LoginExpired (9007).
            // Wired now (R6-05): the refresh either completes (AccessGranted, exit 0)
            // or fails loudly (exit 1 / 2) so the cron wrapper can alarm.
            int status;
            try
            {
                status = ceremony(appName, existing.Value.ToString("D"), taxYear);
            }
            catch (LacerteSDKError ex) when (ex.StatusCode == SyntheticStatusCodes.BridgeUnavailable)
            {
                Console.Error.WriteLine("PARTIAL: login refresh could not run — the Lacerte SDK COM surface is");
                Console.Error.WriteLine("         unavailable on this machine.");
                Console.Error.WriteLine($"         {ex.UserMessage}");
                Console.Error.WriteLine("         Run `lacerte-mcp doctor` to verify the SDK install, then retry.");
                Console.Error.WriteLine("         Until the refresh succeeds, tools will return LoginExpired (9007)");
                Console.Error.WriteLine("         once the current 24h ticket lapses.");
                return 2;
            }
            catch (LacerteSDKError ex)
            {
                Console.Error.WriteLine($"FAIL: login refresh failed with {ex.StatusName} ({ex.StatusCode}).");
                Console.Error.WriteLine($"      {ex.UserMessage}");
                if (!string.IsNullOrEmpty(ex.Remediation))
                {
                    Console.Error.WriteLine($"      → {ex.Remediation}");
                }
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: login refresh threw unexpectedly: " + ex.Message);
                Console.Error.WriteLine("      Re-run `lacerte-mcp doctor` to verify the install, then retry.");
                return 1;
            }

            return RegisterCommand.ReportCeremonyStatus(status, verb: "login refresh");
        }
    }
}
