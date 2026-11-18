using System;
using System.IO;
using System.Text;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// Interactive console input for the approval-PIN flows (<c>approval-pin set |
    /// reset</c>, <c>proposal approve | resolve</c>). These refuse to run when stdin
    /// is redirected: a PIN has to be typed by a person at a console, not piped in
    /// by a script or an agent. PINs are read without echo (each key shows as *).
    ///
    /// <para>Tests drive the flows through the <c>*ForTests</c> hooks; production
    /// never sets them.</para>
    /// </summary>
    internal static class ConsoleInput
    {
        /// <summary>Test hook: returns the "typed" PIN for a prompt (null = the person cancelled).</summary>
        internal static Func<string, string?>? PinReaderForTests;

        /// <summary>Test hook: returns the "typed" line for a prompt (null = end of input).</summary>
        internal static Func<string, string?>? LineReaderForTests;

        /// <summary>Test hook: overrides <see cref="Console.IsInputRedirected"/>.</summary>
        internal static Func<bool>? InputRedirectedForTests;

        public const string RedirectedMessage =
            "This needs a person at the keyboard: run it yourself in a console window on the Lacerte PC (input can't be piped or redirected).";

        public static bool IsInputRedirected => InputRedirectedForTests?.Invoke() ?? Console.IsInputRedirected;

        /// <summary>Prompts and reads a PIN without echoing it. Null when cancelled (Esc or end of input).</summary>
        public static string? ReadSecret(string prompt, TextWriter stdout)
        {
            var hook = PinReaderForTests;
            if (hook != null)
            {
                stdout.Write(prompt);
                stdout.WriteLine();
                return hook(prompt);
            }
            stdout.Flush();
            Console.Write(prompt);
            var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return sb.ToString();
                    }
                    if (key.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine();
                        return null;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Length--;
                            Console.Write("\b \b");
                        }
                        continue;
                    }
                    if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) continue;
                    if (sb.Length >= 256) continue;
                    sb.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            catch (InvalidOperationException)
            {
                // No console to read keys from.
                Console.WriteLine();
                return null;
            }
        }

        /// <summary>Prompts and reads one visible line (a typed confirmation). Null at end of input.</summary>
        public static string? ReadLine(string prompt, TextWriter stdout)
        {
            var hook = LineReaderForTests;
            if (hook != null)
            {
                stdout.Write(prompt);
                stdout.WriteLine();
                return hook(prompt);
            }
            stdout.Flush();
            Console.Write(prompt);
            return Console.ReadLine();
        }
    }
}
