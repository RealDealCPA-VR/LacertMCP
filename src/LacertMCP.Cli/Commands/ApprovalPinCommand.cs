using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using LacertMCP.Server.Proposals;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp approval-pin {set | status | reset}</c> — the owner's approval
    /// PIN (<see cref="ApprovalPin"/>). Approving a proposal or resolving an
    /// Uncertain line (control page or <c>lacerte-mcp proposal</c>) needs it, so an
    /// AI agent with shell access can't approve on the owner's behalf.
    ///
    /// <para><c>set</c> and <c>reset</c> are interactive only: they refuse when
    /// input is redirected, read PINs without echo, and never print a PIN.
    /// <c>status</c> only reads. The file is <c>approval-pin.json</c> in the data
    /// folder (<c>LACERTE_HTTP_DATA_DIR</c>, else <c>%APPDATA%\lacerte-mcp</c>;
    /// <c>--dir</c> overrides), shared with the server.</para>
    /// </summary>
    public static class ApprovalPinCommand
    {
        public static Command Build()
        {
            var cmd = new Command("approval-pin",
                "Set, check or reset the approval PIN that approving a proposal (control page or `lacerte-mcp proposal approve`) requires. " +
                "Run it yourself in a console on the Lacerte PC; an agent can't.");
            cmd.AddCommand(BuildSet());
            cmd.AddCommand(BuildStatus());
            cmd.AddCommand(BuildReset());
            return cmd;
        }

        private static Option<string?> DirOption()
            => new Option<string?>("--dir", "Data folder holding approval-pin.json (the parent of the proposals folder). Default: LACERTE_HTTP_DATA_DIR, else %APPDATA%\\lacerte-mcp (the server's folder).");

        private static Command BuildSet()
        {
            var c = new Command("set", "Set or change the approval PIN (6-64 characters: digits or a passphrase). Asks for the current PIN when one is set. Interactive only.");
            var dir = DirOption();
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var pin = Open(ctx.ParseResult.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = pin == null ? 1 : RunSet(pin, Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildStatus()
        {
            var c = new Command("status", "Show whether an approval PIN is set, when it last changed, and whether approvals are locked after wrong PINs.");
            var dir = DirOption();
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var pin = Open(ctx.ParseResult.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = pin == null ? 1 : RunStatus(pin, Console.Out);
            });
            return c;
        }

        private static Command BuildReset()
        {
            var c = new Command("reset",
                "Clear a forgotten approval PIN (after you type RESET). Nothing can be approved until you set a new one with `lacerte-mcp approval-pin set`. Interactive only.");
            var confirm = new Option<bool>("--confirm", () => false, "Actually clear it (you are asked to type RESET).");
            var dir = DirOption();
            c.AddOption(confirm);
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var pin = Open(ctx.ParseResult.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = pin == null ? 1 : RunReset(pin, ctx.ParseResult.GetValueForOption(confirm), Console.Out, Console.Error);
            });
            return c;
        }

        /// <summary>The PIN at <paramref name="dir"/>, else in the server's data folder; null (and a message) when none resolves.</summary>
        public static ApprovalPin? Open(string? dir, TextWriter stderr, IDictionary<string, string?>? env = null)
        {
            if (!string.IsNullOrWhiteSpace(dir)) return new ApprovalPin(Path.GetFullPath(dir!.Trim()));
            var resolved = ApprovalPin.ResolveDirectory(env ?? ProcessEnv());
            if (resolved == null)
            {
                stderr.WriteLine($"No data folder: pass --dir, or set {ProposalStore.DataDirEnvVar} or APPDATA.");
                return null;
            }
            return new ApprovalPin(resolved);
        }

        private static IDictionary<string, string?> ProcessEnv()
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables()) d[(string)kv.Key] = kv.Value as string;
            return d;
        }

        // ---- set ------------------------------------------------------------------------------

        public static int RunSet(ApprovalPin pin, TextWriter stdout, TextWriter stderr)
        {
            if (ConsoleInput.IsInputRedirected)
            {
                stderr.WriteLine(ConsoleInput.RedirectedMessage);
                return 1;
            }
            try
            {
                var status = pin.Status();
                string? current = null;
                if (status.IsSet)
                {
                    if (status.LockedUntilUtc.HasValue)
                    {
                        stderr.WriteLine(ApprovalPin.Describe(new ApprovalPinCheck { Outcome = ApprovalPinOutcome.Locked, LockedUntilUtc = status.LockedUntilUtc }));
                        return 1;
                    }
                    current = ConsoleInput.ReadSecret("Current approval PIN: ", stdout);
                    if (current == null) return Cancelled(stderr);
                }
                var first = ConsoleInput.ReadSecret($"New approval PIN ({ApprovalPin.MinLength}-{ApprovalPin.MaxLength} characters, digits or a passphrase): ", stdout);
                if (first == null) return Cancelled(stderr);
                var problem = ApprovalPin.CheckRules(first);
                if (problem != null)
                {
                    stderr.WriteLine(problem + " Nothing changed.");
                    return 1;
                }
                var second = ConsoleInput.ReadSecret("Type the new PIN again: ", stdout);
                if (second == null) return Cancelled(stderr);
                if (!string.Equals(first, second, StringComparison.Ordinal))
                {
                    stderr.WriteLine("The two PINs are different. Nothing changed.");
                    return 1;
                }
                var result = pin.Set(first, current);
                if (!result.IsOk)
                {
                    stderr.WriteLine((result.Outcome == ApprovalPinOutcome.Wrong ? "The current PIN is wrong. " : "") + ApprovalPin.Describe(result) + " Nothing changed.");
                    return 1;
                }
                stdout.WriteLine(status.IsSet ? "Approval PIN changed." : "Approval PIN set.");
                stdout.WriteLine("Approving a proposal (control page or `lacerte-mcp proposal approve`) now asks for it. Never give it to an AI agent.");
                return 0;
            }
            catch (ArgumentException ex)
            {
                stderr.WriteLine(ex.Message.Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0] + " Nothing changed.");
                return 1;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"Couldn't update {pin.FilePath}: {ex.Message}");
                return 1;
            }
        }

        // ---- status ---------------------------------------------------------------------------

        public static int RunStatus(ApprovalPin pin, TextWriter stdout)
        {
            var s = pin.Status();
            stdout.WriteLine($"# {pin.FilePath}");
            if (!s.IsSet)
            {
                stdout.WriteLine("No approval PIN is set: nothing can be approved. Set one with `lacerte-mcp approval-pin set`.");
            }
            else
            {
                stdout.WriteLine("An approval PIN is set.");
            }
            if (s.SetAtUtc.HasValue) stdout.WriteLine($"  First set:    {Time(s.SetAtUtc.Value)}");
            if (s.ChangedAtUtc.HasValue) stdout.WriteLine($"  Last changed: {Time(s.ChangedAtUtc.Value)}  (if you didn't change it then, someone else did)");
            if (s.LockedUntilUtc.HasValue) stdout.WriteLine($"  LOCKED after too many wrong PINs until {Time(s.LockedUntilUtc.Value)}.");
            else if (s.IsSet) stdout.WriteLine($"  Wrong attempts since the last right one: {s.FailedAttempts} of {ApprovalPin.MaxFailedAttempts}.");
            return 0;
        }

        // ---- reset ----------------------------------------------------------------------------

        public static int RunReset(ApprovalPin pin, bool confirm, TextWriter stdout, TextWriter stderr)
        {
            if (!confirm)
            {
                stdout.WriteLine("Clears the approval PIN (for a forgotten one). Nothing can be approved until you set a new one.");
                stdout.WriteLine("Run `lacerte-mcp approval-pin reset --confirm` in a console to do it.");
                return 1;
            }
            if (ConsoleInput.IsInputRedirected)
            {
                stderr.WriteLine(ConsoleInput.RedirectedMessage);
                return 1;
            }
            var typed = ConsoleInput.ReadLine("Type RESET to clear the approval PIN: ", stdout);
            if (!string.Equals((typed ?? string.Empty).Trim(), "RESET", StringComparison.Ordinal))
            {
                stderr.WriteLine("Not reset (you didn't type RESET).");
                return 1;
            }
            try
            {
                pin.Reset();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"Couldn't update {pin.FilePath}: {ex.Message}");
                return 1;
            }
            stdout.WriteLine("Approval PIN cleared. Set a new one now with `lacerte-mcp approval-pin set`.");
            return 0;
        }

        private static int Cancelled(TextWriter stderr)
        {
            stderr.WriteLine("Cancelled. Nothing changed.");
            return 1;
        }

        private static string Time(DateTimeOffset t)
            => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (" + t.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC)";
    }
}
