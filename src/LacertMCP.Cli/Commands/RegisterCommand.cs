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
            // P16-022: track whether this invocation actually minted a fresh GUID so
            // the downstream [partial] stderr line in InvokeLiveRegister can be honest
            // about which path fired ("AppID minted" vs "Existing AppID re-used").
            bool wasJustMinted;

            if (existing.HasValue && !reset)
            {
                appId = existing.Value;
                wasJustMinted = false;
                Console.WriteLine("AppID already minted — re-using existing GUID.");
                // Re-use path: print only the masked form. The operator already saw the full GUID
                // once at original-mint time (the new-mint branch below) and was told to back it up.
                // Use `lacerte-mcp status --show-app-id` if a re-reveal is needed.
                Console.WriteLine($"  AppID  : {AppIdStore.Mask(appId)}");
                Console.WriteLine($"  AppName: {appName}");
                Console.WriteLine($"  TaxYear: {taxYear}");
                // P16-022: dropped the "Pass --reset to mint a new one" stdout hint that
                // previously dangled here — discoverable via --help, and on the re-use
                // path its juxtaposition with the immediately-following stderr [partial]
                // block created a "what do I actually do?" moment. If the operator
                // intended to re-do the ceremony, the [partial] block now says so.
            }
            else
            {
                wasJustMinted = true;
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
            return InvokeLiveRegister(appName, appId, taxYear, wasJustMinted);
        }

        private static int InvokeLiveRegister(string appName, Guid appId, int taxYear, bool wasJustMinted)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // Off-Windows is an intentional cross-platform dev convenience: the
                // operator can't complete the Intuit consent ceremony anyway, so we
                // keep the AppID-minted state usable for downstream sim work and
                // exit 0. (P15-028 refinedRecommendation: only Win32NT flips to 3.)
                Console.WriteLine("[stub] Live Intuit registration requires Windows + Lacerte SDK.");
                Console.WriteLine("       AppID is persisted; re-run `lacerte-mcp register` on the Windows box to complete.");
                return 0;
            }

            // P15-028 / P16-022 / P16-004: On Windows the operator reasonably expects
            // ITPRegistration.Register() to fire. It doesn't yet (Phase 9 task #63).
            // Returning 0 here makes the silent no-op look like success to any
            // `if ($LASTEXITCODE -eq 0)` wrapper, and the operator only discovers the
            // failure 24h later when tools start returning LoginExpired (9007) via the
            // ThrowIfAuthExpired probe. Route the "partial" message to stderr and
            // return exit 3 so wrapping scripts can distinguish "AppID minted, consent
            // skipped" from "fully complete". P16-022: differentiate the message based
            // on whether we just minted vs re-used so the stderr line is honest.
            // PHASE-9-CLEANUP: when Phase 9 task #63 lands and ITPRegistration.Register()
            // fires, delete the [partial] stderr block + the wasJustMinted branch and
            // return 0.
            if (wasJustMinted)
            {
                Console.Error.WriteLine("[partial] AppID minted, but the Intuit consent ceremony is not yet wired into the CLI (Phase 9 task #63).");
            }
            else
            {
                Console.Error.WriteLine("[partial] Existing AppID re-used; the Intuit consent ceremony still is not wired into the CLI (Phase 9 task #63). Nothing changed on this run.");
            }
            Console.Error.WriteLine("          First MCP tool call will return LoginExpired (9007) once Phase 9 ships and the existing 24h ticket lapses; until then a freshly-minted AppID will return AppNotRegistered (9006) on every call.");
            Console.Error.WriteLine("          Track progress: Todo.md Phase 9 #63 (Bridge/Registration.cs).");
            if (!wasJustMinted)
            {
                Console.Error.WriteLine("          If you intended to re-do the Intuit consent ceremony, re-run with --reset.");
            }
            return 3;
        }
    }
}
