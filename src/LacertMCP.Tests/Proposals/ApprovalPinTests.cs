using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LacertMCP.Server.Proposals;
using Xunit;
using static LacertMCP.Tests.Proposals.ProposalTestSupport;

namespace LacertMCP.Tests.Proposals
{
    /// <summary>The approval PIN: hashing, rules, lockout, the shared file.</summary>
    public sealed class ApprovalPinTests : IDisposable
    {
        private readonly string _dir = NewTempDir();

        public void Dispose() => TryDelete(_dir);

        private ApprovalPin Pin(DateTimeOffset? now = null)
        {
            var pin = new ApprovalPin(_dir) { Iterations = 1000 };
            if (now.HasValue) pin.Clock = () => now.Value;
            return pin;
        }

        [Fact]
        public void NotSet_UntilSet_ThenVerifies()
        {
            var pin = Pin();
            Assert.False(pin.Status().IsSet);
            Assert.Equal(ApprovalPinOutcome.NotSet, pin.Verify("246810").Outcome);

            Assert.True(pin.Set("246810", null).IsOk);
            var s = pin.Status();
            Assert.True(s.IsSet);
            Assert.NotNull(s.SetAtUtc);
            Assert.Equal(s.SetAtUtc, s.ChangedAtUtc);
            Assert.True(pin.Verify("246810").IsOk);
            Assert.Equal(ApprovalPinOutcome.Wrong, pin.Verify("246811").Outcome);
            Assert.Equal(ApprovalPinOutcome.Wrong, pin.Verify("").Outcome);
            Assert.Equal(ApprovalPinOutcome.Wrong, pin.Verify(null).Outcome);
        }

        [Fact]
        public void File_HoldsNoPlaintext_SaltedPbkdf2_DefaultIterations()
        {
            var pin = new ApprovalPin(_dir); // production iteration count
            Assert.Equal(310_000, pin.Iterations);
            pin.Set("correct horse battery", null);
            var text = File.ReadAllText(Path.Combine(_dir, ApprovalPin.FileName));
            Assert.DoesNotContain("correct horse", text);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal("PBKDF2-SHA256", root.GetProperty("algorithm").GetString());
            Assert.Equal(310_000, root.GetProperty("iterations").GetInt32());
            Assert.Equal(16, Convert.FromBase64String(root.GetProperty("salt").GetString()!).Length);
            Assert.Equal(32, Convert.FromBase64String(root.GetProperty("hash").GetString()!).Length);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            Assert.Empty(Directory.GetFiles(_dir, "*.lock"));

            // Same PIN again: a fresh salt, so a different hash.
            var first = root.GetProperty("hash").GetString();
            pin.Set("correct horse battery", "correct horse battery");
            using var doc2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, ApprovalPin.FileName)));
            Assert.NotEqual(first, doc2.RootElement.GetProperty("hash").GetString());
            Assert.True(pin.Verify("correct horse battery").IsOk);
        }

        [Theory]
        [InlineData("12345", "at least 6")]
        [InlineData("111111", "repeated")]
        [InlineData("aaaaaaaa", "repeated")]
        [InlineData("123456", "straight run")]
        [InlineData("987654", "straight run")]
        [InlineData("0123456789", "straight run")]
        [InlineData("      ", "only spaces")]
        public void Rules_RejectWeakPins(string pin, string because)
        {
            var problem = ApprovalPin.CheckRules(pin);
            Assert.NotNull(problem);
            Assert.Contains(because, problem);
            Assert.DoesNotContain(pin.Trim().Length > 0 ? pin : "\u0000", problem);
            var ex = Assert.Throws<ArgumentException>(() => Pin().Set(pin, null));
            Assert.DoesNotContain(pin.Trim().Length > 0 ? pin : "\u0000", ex.Message);
            Assert.False(Pin().Status().IsSet);
        }

        [Theory]
        [InlineData("135790")]
        [InlineData("123457")]
        [InlineData("my approval phrase")]
        public void Rules_AcceptReasonablePins(string pin) => Assert.Null(ApprovalPin.CheckRules(pin));

        [Fact]
        public void Rules_LengthBounds()
        {
            Assert.Null(ApprovalPin.CheckRules(new string('a', 63) + "b"));
            Assert.Contains("at most 64", ApprovalPin.CheckRules(new string('a', 64) + "b"));
        }

        [Fact]
        public void Change_RequiresTheCurrentPin()
        {
            var pin = Pin();
            pin.Set("246810", null);
            var changedAt = pin.Status().ChangedAtUtc;

            var wrong = pin.Set("135799", "000000x");
            Assert.Equal(ApprovalPinOutcome.Wrong, wrong.Outcome);
            Assert.True(pin.Verify("246810").IsOk); // unchanged
            Assert.Equal(ApprovalPinOutcome.Wrong, pin.Set("135799", null).Outcome);

            Assert.True(pin.Set("135799", "246810").IsOk);
            Assert.True(pin.Verify("135799").IsOk);
            Assert.Equal(ApprovalPinOutcome.Wrong, pin.Verify("246810").Outcome);
            Assert.True(pin.Status().ChangedAtUtc >= changedAt);
        }

        [Fact]
        public void FiveWrong_LockFor15Minutes_RightPinResetsCounter_LockExpires()
        {
            var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
            var pin = Pin(now);
            pin.Set("246810", null);

            Assert.Equal(4, pin.Verify("x").RemainingAttempts);
            Assert.Equal(3, pin.Verify("x").RemainingAttempts);
            Assert.True(pin.Verify("246810").IsOk); // resets the counter
            Assert.Equal(0, pin.Status().FailedAttempts);

            for (int i = 4; i >= 1; i--) Assert.Equal(i, pin.Verify("x").RemainingAttempts);
            var locking = pin.Verify("x");
            Assert.Equal(ApprovalPinOutcome.Locked, locking.Outcome);
            Assert.Equal(now.AddMinutes(15), locking.LockedUntilUtc);

            // Locked: the right PIN is refused too, and attempts aren't counted.
            var locked = pin.Verify("246810");
            Assert.Equal(ApprovalPinOutcome.Locked, locked.Outcome);
            Assert.Equal(now.AddMinutes(15), pin.Status().LockedUntilUtc);
            Assert.Equal(ApprovalPinOutcome.Locked, pin.Set("135799", "246810").Outcome);
            Assert.Contains("locked until", ApprovalPin.Describe(locked));

            // 15 minutes later: open again, with a fresh set of attempts.
            pin.Clock = () => now.AddMinutes(15).AddSeconds(1);
            Assert.Null(pin.Status().LockedUntilUtc);
            Assert.Equal(0, pin.Status().FailedAttempts);
            Assert.Equal(4, pin.Verify("x").RemainingAttempts);
            Assert.True(pin.Verify("246810").IsOk);
        }

        [Fact]
        public void TwoInstances_ShareTheFile_AndTheLockout_NoCaching()
        {
            var server = Pin();
            var cli = Pin();
            Assert.False(server.Status().IsSet);
            cli.Set("246810", null);
            Assert.True(server.Status().IsSet);           // re-read, not cached
            Assert.True(server.Verify("246810").IsOk);

            for (int i = 0; i < 3; i++) server.Verify("bad");
            Assert.Equal(3, cli.Status().FailedAttempts);
            Assert.Equal(1, cli.Verify("bad").RemainingAttempts); // the CLI continues the server's count
            Assert.Equal(ApprovalPinOutcome.Locked, server.Verify("bad").Outcome);
            Assert.Equal(ApprovalPinOutcome.Locked, cli.Verify("246810").Outcome);

            cli.Reset();
            Assert.False(server.Status().IsSet);
            Assert.Null(server.Status().LockedUntilUtc);
            Assert.Equal(ApprovalPinOutcome.NotSet, server.Verify("246810").Outcome);
        }

        [Fact]
        public void Reset_ClearsThePin_KeepsTheChangeTime()
        {
            var t0 = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
            var pin = Pin(t0);
            pin.Set("246810", null);
            pin.Clock = () => t0.AddDays(2);
            pin.Reset();
            var s = pin.Status();
            Assert.False(s.IsSet);
            Assert.Equal(t0, s.SetAtUtc);
            Assert.Equal(t0.AddDays(2), s.ChangedAtUtc);
            // A new PIN can be set without the old one.
            Assert.True(pin.Set("135799", null).IsOk);
        }

        [Fact]
        public void DamagedFile_RefusesInsteadOfLookingUnset()
        {
            File.WriteAllText(Path.Combine(_dir, ApprovalPin.FileName), "{ not json");
            var pin = Pin();
            Assert.True(pin.Status().IsSet);
            Assert.Equal(ApprovalPinOutcome.Wrong, pin.Verify("246810").Outcome);
        }

        [Fact]
        public void ResolveDirectory_IsTheParentOfTheProposalFolder()
        {
            Assert.Equal(@"D:\data", ApprovalPin.ResolveDirectory(new Dictionary<string, string?> { ["LACERTE_HTTP_DATA_DIR"] = @"D:\data" }));
            Assert.Equal(Path.Combine(@"C:\x", "lacerte-mcp"), ApprovalPin.ResolveDirectory(new Dictionary<string, string?> { ["APPDATA"] = @"C:\x" }));
            Assert.Null(ApprovalPin.ResolveDirectory(new Dictionary<string, string?>()));
            Assert.Equal(Path.GetFullPath(_dir), ApprovalPin.ForProposalDirectory(Path.Combine(_dir, "proposals")).DirectoryPath);
            Assert.Equal(Path.GetFullPath(_dir), new ProposalStore(Path.Combine(_dir, "proposals")).Pin.DirectoryPath);
        }
    }
}
