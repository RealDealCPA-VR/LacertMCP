using System;
using System.CommandLine;
using LacertMCP.Cli.Storage;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp register [--app-name X] [--tax-year Y] [--reset]</c>
    ///
    /// <para>
    /// One-time Intuit consent + sign-in ceremony per <c>docs/research/agents/02-auth-flow.md</c> §1.
    /// Mints a per-install AppID GUID, persists it via <see cref="AppIdStore"/>, then (on Windows)
    /// calls <c>ITPRegistration.Register(appName, appId, taxYear)</c> through
    /// <see cref="Registration.Register"/>, which surfaces the consent + Intuit sign-in dialogs on
    /// the interactive desktop. R6-05 closed: the ceremony is now actually wired — the command
    /// reports the SDK's TPSDKStatus outcome (AccessGranted / ConsentDenied / NotSignedIn / ...)
    /// via <see cref="StatusCodes.GetStatusInfo"/> instead of the former Phase-9 stub.
    /// </para>
    ///
    /// <para>
    /// Exit codes (aligned with doctor's 0 green / 1 red / 2 partial convention):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>0 — AccessGranted (ceremony complete), or off-Windows (AppID minted;
    ///   ceremony impossible by definition — kept exit 0 per P15-028 so dev smoke loops stay green).</description></item>
    ///   <item><description>1 — ceremony ran but did not grant access (ConsentDenied, NotSignedIn,
    ///   RegVersionBlocked, LacerteTaxSetupRequired, unknown status), or it threw a real error.</description></item>
    ///   <item><description>2 — PARTIAL: on Windows but the Lacerte SDK COM surface is unavailable
    ///   (BridgeUnavailable — ProgID missing / activation failed). AppID is persisted; install the
    ///   SDK and re-run.</description></item>
    /// </list>
    /// </summary>
    public static class RegisterCommand
    {
        public static Command Build()
        {
            var cmd = new Command("register",
                "Mint the AppID GUID + run the one-time Intuit consent + sign-in ceremony. " +
                "Exit 0 on AccessGranted (or off-Windows mint-only), 1 on a denied/failed ceremony, " +
                "2 when the Lacerte SDK is unavailable on this Windows box.");

            var appNameOpt = new Option<string>("--app-name", () => "Operator Lacerte MCP",
                "Friendly name shown in the Intuit consent dialog.");
            var taxYearOpt = new Option<int>("--tax-year", () => DateTime.UtcNow.Year - 1,
                "Tax year the registration is scoped to. Defaults to the prior calendar year.");
            var resetOpt = new Option<bool>("--reset", () => false,
                "Mint a fresh GUID even if one already exists. Forces a new Intuit consent ceremony.");

            cmd.AddOption(appNameOpt);
            cmd.AddOption(taxYearOpt);
            cmd.AddOption(resetOpt);

            cmd.SetHandler((context) =>
            {
                var appName = context.ParseResult.GetValueForOption(appNameOpt)!;
                var taxYear = context.ParseResult.GetValueForOption(taxYearOpt);
                var reset = context.ParseResult.GetValueForOption(resetOpt);
                context.ExitCode = Run(appName, taxYear, reset);
            });

            return cmd;
        }

        /// <summary>
        /// Production entry point — runs the real <see cref="Registration.Register"/> ceremony.
        /// </summary>
        public static int Run(string appName, int taxYear, bool reset)
            => Run(appName, taxYear, reset, Registration.Register);

        /// <summary>
        /// Testable seam (same pattern as <see cref="DoctorCommand"/>'s results-injectable
        /// <c>Run</c> overload): <paramref name="ceremony"/> stands in for
        /// <see cref="Registration.Register"/> — <c>(appName, appId "D" string, taxYear) → TPSDKStatus int</c> —
        /// so tests can pin the exit-code matrix without COM or interactive dialogs.
        /// </summary>
        public static int Run(string appName, int taxYear, bool reset, Func<string, string, int, int> ceremony)
        {
            if (ceremony == null) throw new ArgumentNullException(nameof(ceremony));

            var existing = AppIdStore.Get();
            Guid appId;

            if (existing.HasValue && !reset)
            {
                appId = existing.Value;
                Console.WriteLine("AppID already minted — re-using existing GUID.");
                // Re-use path: print only the masked form. The operator already saw the full GUID
                // once at original-mint time (the new-mint branch below) and was told to back it up.
                // Use `lacerte-mcp status --show-app-id` if a re-reveal is needed.
                Console.WriteLine($"  AppID  : {AppIdStore.Mask(appId)}");
                Console.WriteLine($"  AppName: {appName}");
                Console.WriteLine($"  TaxYear: {taxYear}");
            }
            else
            {
                if (reset && existing.HasValue)
                {
                    // Print the OLD GUID in masked form only — the operator's confirmation that we're
                    // wiping the right install comes from the first 8 chars; we don't need to leak the
                    // full bearer credential just to announce a reset.
                    Console.WriteLine($"Resetting AppID (was: {AppIdStore.Mask(existing.Value)}).");
                }
                appId = Guid.NewGuid();
                AppIdStore.Set(appId);
                // New-mint path: this is the ONE channel where the full GUID is intentionally
                // surfaced — the operator must see it once to back it up (per P14-065 refined
                // recommendation, this print is preserved on purpose).
                Console.WriteLine("Minted new AppID GUID:");
                Console.WriteLine();
                Console.WriteLine($"  {appId:D}");
                Console.WriteLine();
                Console.WriteLine("*** Save this GUID somewhere safe for backup. ***");
                Console.WriteLine($"    Storage backend: {(AppIdStore.UsesCredentialManager ? "Windows Credential Manager" : AppIdStore.FallbackConfigPath)}");
            }

            Console.WriteLine();
            return InvokeLiveRegister(appName, appId, taxYear, ceremony);
        }

        private static int InvokeLiveRegister(string appName, Guid appId, int taxYear,
            Func<string, string, int, int> ceremony)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // Off-Windows is an intentional cross-platform dev convenience: the
                // operator can't complete the Intuit consent ceremony anyway, so we
                // keep the AppID-minted state usable for downstream sim work and
                // exit 0. (P15-028 refinedRecommendation: only Win32NT runs the ceremony.)
                Console.WriteLine("[skipped] The Intuit consent ceremony requires Windows + the Lacerte SDK.");
                Console.WriteLine("          AppID is persisted; re-run `lacerte-mcp register` on the Windows + Lacerte box to complete it.");
                return 0;
            }

            Console.WriteLine("Running the Intuit consent ceremony (ITPRegistration.Register)...");
            Console.WriteLine("Watch this desktop for the Intuit consent + sign-in dialogs.");
            Console.WriteLine();

            // Same invocation shape as LacerteSessionManager.Register():
            // Registration.Register(appName, appId.ToString("D"), taxYear) → TPSDKStatus int.
            int status;
            try
            {
                status = ceremony(appName, appId.ToString("D"), taxYear);
            }
            catch (LacerteSDKError ex) when (ex.StatusCode == SyntheticStatusCodes.BridgeUnavailable)
            {
                // SDK-unavailable detection mirrors the bridge: Registration.Register throws
                // BridgeUnavailable when the TPRegistration ProgID doesn't resolve / COM
                // activation fails. PARTIAL outcome (exit 2) — same bucket doctor uses for
                // advisory/inapplicable probes: the AppID is persisted and intact, only the
                // ceremony couldn't run on this box.
                Console.Error.WriteLine("PARTIAL: AppID is persisted, but the consent ceremony could not run —");
                Console.Error.WriteLine("         the Lacerte SDK COM surface is unavailable on this machine.");
                Console.Error.WriteLine($"         {ex.UserMessage}");
                Console.Error.WriteLine("         Install the 64-bit Lacerte SDK MSI, confirm `lacerte-mcp doctor` shows");
                Console.Error.WriteLine("         the \"Lacerte COM ProgID\" probe green, then re-run `lacerte-mcp register`.");
                return 2;
            }
            catch (LacerteSDKError ex)
            {
                Console.Error.WriteLine($"FAIL: consent ceremony failed with {ex.StatusName} ({ex.StatusCode}).");
                Console.Error.WriteLine($"      {ex.UserMessage}");
                if (!string.IsNullOrEmpty(ex.Remediation))
                {
                    Console.Error.WriteLine($"      → {ex.Remediation}");
                }
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: consent ceremony threw unexpectedly: " + ex.Message);
                Console.Error.WriteLine("      Re-run `lacerte-mcp doctor` to verify the install, then retry.");
                return 1;
            }

            return ReportCeremonyStatus(status);
        }

        /// <summary>
        /// Shared TPSDKStatus → exit-code/report mapping for <c>register</c> and <c>login</c>
        /// (both verbs end in the same <c>ITPRegistration.Register</c> wire call). AccessGranted
        /// (201) → exit 0; everything else (ConsentDenied, NotSignedIn, RegVersionBlocked,
        /// LacerteTaxSetupRequired, unknown) → exit 1 with the catalog name + remediation from
        /// <see cref="StatusCodes.GetStatusInfo"/>.
        /// </summary>
        internal static int ReportCeremonyStatus(int status, string verb = "consent ceremony")
        {
            var info = StatusCodes.GetStatusInfo(status);
            if (status == StatusCodes.AccessGranted)
            {
                Console.WriteLine($"AccessGranted ({status}) — {verb} complete.");
                Console.WriteLine("The 24-hour sign-in ticket is live. Run `lacerte-mcp login` to refresh it");
                Console.WriteLine("after it lapses; MCP tools can now reach the live SDK.");
                return 0;
            }

            Console.Error.WriteLine($"FAIL: {verb} returned {info.Name} ({status}).");
            Console.Error.WriteLine($"      {info.UserMessage}");
            if (!string.IsNullOrEmpty(info.Remediation))
            {
                Console.Error.WriteLine($"      → {info.Remediation}");
            }
            return 1;
        }
    }
}
