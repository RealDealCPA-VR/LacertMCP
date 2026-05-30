using System;
using System.CommandLine;
using LacertMCP.Cli.Storage;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp register [--app-name X] [--tax-year Y] [--reset]</c>
    ///
    /// <para>
    /// One-time Intuit consent + sign-in ceremony per <c>docs/research/agents/02-auth-flow.md</c> §1.
    /// Mints a per-install AppID GUID, persists it via <see cref="AppIdStore"/>, then (on Windows + live
    /// SDK) calls <c>ITPRegistration.Register(...)</c> which surfaces the consent + Intuit dialogs to
    /// the interactive shell. Off-Windows or when the SDK isn't loaded we print a clear stub message —
    /// the GUID is still minted so the rest of the operator surface (status, field-access) is usable.
    /// </para>
    /// </summary>
    public static class RegisterCommand
    {
        public static Command Build()
        {
            var cmd = new Command("register",
                "Mint the AppID GUID + run the one-time Intuit consent + sign-in ceremony.");

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

        public static int Run(string appName, int taxYear, bool reset)
        {
            var existing = AppIdStore.Get();
            Guid appId;

            if (existing.HasValue && !reset)
            {
                appId = existing.Value;
                Console.WriteLine("AppID already minted — re-using existing GUID.");
                Console.WriteLine($"  AppID  : {appId:D}");
                Console.WriteLine($"  AppName: {appName}");
                Console.WriteLine($"  TaxYear: {taxYear}");
                Console.WriteLine();
                Console.WriteLine("Pass --reset to mint a new one (this forces a fresh Intuit consent dialog).");
            }
            else
            {
                if (reset && existing.HasValue)
                {
                    Console.WriteLine($"Resetting AppID (was: {existing.Value:D}).");
                }
                appId = Guid.NewGuid();
                AppIdStore.Set(appId);
                Console.WriteLine("Minted new AppID GUID:");
                Console.WriteLine();
                Console.WriteLine($"  {appId:D}");
                Console.WriteLine();
                Console.WriteLine("*** Save this GUID somewhere safe for backup. ***");
                Console.WriteLine($"    Storage backend: {(AppIdStore.UsesCredentialManager ? "Windows Credential Manager" : AppIdStore.FallbackConfigPath)}");
            }

            Console.WriteLine();
            return InvokeLiveRegister(appName, appId, taxYear);
        }

        private static int InvokeLiveRegister(string appName, Guid appId, int taxYear)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Console.WriteLine("[stub] Live Intuit registration requires Windows + Lacerte SDK.");
                Console.WriteLine("       AppID is persisted; re-run `lacerte-mcp register` on the Windows box to complete.");
                return 0;
            }

            // The actual ITPRegistration COM call lands in Phase 9 (Bridge/Registration.cs).
            // Today the bridge is not yet wrapped — print honest status rather than throw.
            Console.WriteLine("[stub] Lacerte SDK bridge not yet wired into the CLI (Phase 9 task #63).");
            Console.WriteLine("       AppID persisted; once Phase 9 lands this command will invoke");
            Console.WriteLine("       ITPRegistration.Register(appName, appId, taxYear) and print the TAPIStatus.");
            return 0;
        }
    }
}
