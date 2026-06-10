using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// P16-004: keep the operator-facing docs in lockstep with the error codes that
    /// the production code actually throws. Three drift sources got us to round 3:
    /// <list type="bullet">
    ///   <item><description>README/ARCHITECTURE/REGRESSION_CHECKLIST all referenced
    ///     <c>9101 ReAuthRequired</c> as the 24h expiry surfacing code — but
    ///     <c>9101</c> is a reserved-but-never-thrown constant. The actual code
    ///     thrown by <c>LacerteSessionManager.ThrowIfAuthExpired</c> is
    ///     <c>9007 LoginExpired</c>.</description></item>
    ///   <item><description>README + <c>LoginCommand</c> stderr referenced
    ///     <c>TicketExpired (9103)</c> — wrong on both counts: <c>9103</c> is
    ///     <c>InvalidAppId</c> (the bridge-boundary shape check), and
    ///     <c>TicketExpired</c> is not a defined <c>StatusInfo</c> name anywhere.</description></item>
    ///   <item><description>ARCHITECTURE diagram comment said "Idempotency cache (synth 9101 reuse)"
    ///     — wrong number; idempotency conflicts use <c>9102</c>.</description></item>
    /// </list>
    /// Tests assert the wrong identifiers no longer appear in the canonical docs.
    /// </summary>
    public class StatusCodeDocsAlignmentTests
    {
        private static string RepoRoot()
        {
            // AppContext.BaseDirectory is .../src/LacertMCP.Tests/bin/x64/Debug/net48/
            // Walk up four levels to land on the test project dir, then up two more
            // to land on the repo root (.../LacertMCP/).
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LacertMCP.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                $"Could not locate LacertMCP.sln by walking up from {AppContext.BaseDirectory}.");
        }

        [Fact]
        public void Readme_DoesNotReference_RetiredCodeNames_InActiveClaims()
        {
            var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
            // The README's troubleshooting section MAY mention `TicketExpired` and
            // `(9101)` / `(9103)` once in a meta-explanatory note that names what
            // the old (incorrect) docs said, so future operators searching for the
            // old strings still find context. The load-bearing assert is that no
            // ACTIVE claim like `Symptom: ... TicketExpired` or `tools return NotSignedIn (9101)`
            // remains — those are the user-facing fingerprints operators try to match.
            Assert.DoesNotContain("Symptom:** Reads + writes that worked yesterday now fail with status `NotSignedIn (9101)`", readme);
            Assert.DoesNotContain("9101 ReAuthRequired", readme);
            Assert.DoesNotContain("statusCode: 9101 NotSignedIn", readme);
        }

        [Fact]
        public void Architecture_DoesNotReference_9101_AsExpiryOrIdempotency()
        {
            var arch = File.ReadAllText(Path.Combine(RepoRoot(), "ARCHITECTURE.md"));
            // The diagram and resilience rows both used to claim "9101"; after
            // the P16-004 fix the canonical codes are 9007 (LoginExpired) and
            // 9102 (IdempotencyConflict).
            Assert.DoesNotContain("synth 9101", arch);
            Assert.DoesNotContain("synthetic 9101", arch);
            Assert.DoesNotContain("statusCode 9101", arch);
        }

        [Fact]
        public void RegressionChecklist_DoesNotReference_9101_ReAuthRequired()
        {
            var checklist = File.ReadAllText(Path.Combine(RepoRoot(), "REGRESSION_CHECKLIST.md"));
            Assert.DoesNotContain("9101 ReAuthRequired", checklist);
        }

        [Fact]
        public void LoginCommand_DoesNotReference_TicketExpired()
        {
            // Pre-P16-004 LoginCommand.cs stderr literally read
            // "tools will return TicketExpired (9103)". The post-fix string
            // says "LoginExpired (9007)". Source grep is the most direct
            // way to lock this in alongside the unit test in RegisterLoginCeremonyExitCodeTests.
            var loginCmd = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "LacertMCP.Cli", "Commands", "LoginCommand.cs"));
            // Comments may still mention TicketExpired (explaining what was
            // wrong); only the user-facing emitted strings must not contain it.
            // Strip the file to non-comment lines for the assertion.
            var nonComment = string.Join("\n",
                loginCmd.Split('\n')
                    .Where(l =>
                    {
                        var trimmed = l.TrimStart();
                        return !trimmed.StartsWith("//") && !trimmed.StartsWith("///");
                    }));
            Assert.DoesNotContain("TicketExpired", nonComment);
        }
    }
}
