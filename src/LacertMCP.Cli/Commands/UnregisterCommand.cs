using System;
using System.CommandLine;
using System.IO;
using LacertMCP.Cli.Storage;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp unregister --confirm</c>
    ///
    /// <para>
    /// Operator recovery / decommission verb: wipes the persisted AppID GUID from
    /// the active storage backend (Windows Credential Manager on Windows, the
    /// file fallback off-Windows). Mirrors <c>register --reset</c> but as a
    /// discoverable, typed verb — the <c>--reset</c> flag on register is hidden
    /// inside a help table the operator may not scan, while <c>unregister</c>
    /// shows up in the top-level help block. Required for clean uninstall
    /// (today the operator otherwise has to invoke <c>cmdkey /delete</c> or
    /// edit the file by hand).
    /// </para>
    ///
    /// <para>
    /// Refuses to act without <c>--confirm</c>: the AppID is the bearer credential
    /// the Lacerte SDK was registered under, and recovery requires re-running the
    /// full Intuit consent ceremony. Typed-confirmation guards against a stray
    /// shell-history replay.
    /// </para>
    ///
    /// <para>
    /// Per P14-046 refinedRecommendation: implementable off-Windows using the
    /// existing <see cref="AppIdStore.Reset"/> primitive — no SDK required.
    /// </para>
    /// </summary>
    public static class UnregisterCommand
    {
        public static Command Build()
        {
            var cmd = new Command("unregister",
                "Wipe the persisted AppID GUID from the storage backend. Requires --confirm.");

            var confirmOpt = new Option<bool>("--confirm", () => false,
                "Required to actually delete. Without it the command refuses (exit 1) and prints what would happen.");

            cmd.AddOption(confirmOpt);

            cmd.SetHandler((context) =>
            {
                var confirmed = context.ParseResult.GetValueForOption(confirmOpt);
                context.ExitCode = Run(confirmed, Console.Out, Console.Error);
            });

            return cmd;
        }

        public static int Run(bool confirm, TextWriter stdout, TextWriter stderr)
        {
            var existing = AppIdStore.Get();
            var backend = AppIdStore.UsesCredentialManager
                ? "Windows Credential Manager (" + AppIdStore.CredentialTarget + ")"
                : "file (" + AppIdStore.FallbackConfigPath + ")";

            if (!existing.HasValue)
            {
                stdout.WriteLine("No AppID persisted — nothing to unregister.");
                stdout.WriteLine($"  Storage backend probed: {backend}");
                return 0;
            }

            if (!confirm)
            {
                // P16-082: stderr-for-non-errors is a POSIX foot-gun on a documented
                // preview workflow. Keep only the one-line refusal banner on stderr
                // (so PowerShell's red diagnostic coloring still flags the exit-1
                // state), and route the preview body to stdout so it's redirectable
                // (`lacerte-mcp unregister > preview.txt`), pipeable (`| clip`), and
                // doesn't look like a fault to a non-shell-expert tax-pro operator.
                stderr.WriteLine("Refusing to wipe the persisted AppID without --confirm.");
                stdout.WriteLine($"  Would delete AppID {AppIdStore.Mask(existing.Value)} from {backend}.");
                stdout.WriteLine("  Re-run with --confirm to proceed. After unregister you'll need");
                stdout.WriteLine("  `lacerte-mcp register` to mint a fresh GUID (forces a new Intuit");
                stdout.WriteLine("  consent ceremony on Windows + live SDK).");
                return 1;
            }

            // Snapshot the masked form before we wipe so the confirmation log shows
            // *which* install was decommissioned (no full-GUID leak — Mask gives the
            // first 8 chars which is enough to disambiguate).
            var maskedBefore = AppIdStore.Mask(existing.Value);
            AppIdStore.Reset();

            stdout.WriteLine($"Unregistered AppID {maskedBefore}.");
            stdout.WriteLine($"  Storage backend wiped: {backend}");
            stdout.WriteLine();
            stdout.WriteLine("Re-run `lacerte-mcp register` to mint a fresh GUID and re-do the");
            stdout.WriteLine("Intuit consent ceremony when you're ready.");
            return 0;
        }
    }
}
