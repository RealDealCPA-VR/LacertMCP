using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LacertMCP.Cli.Commands;
using LacertMCP.Server.Proposals;
using Xunit;
using static LacertMCP.Tests.Proposals.ProposalTestSupport;

namespace LacertMCP.Tests.Cli
{
    /// <summary><c>lacerte-mcp approval-pin set | status | reset</c> through ConsoleInput's test hooks.</summary>
    [Collection(ConsoleInputHooksCollection.Name)]
    public sealed class ApprovalPinCommandTests : IDisposable
    {
        private readonly string _dir = NewTempDir();
        private readonly ApprovalPin _pin;
        private readonly StringWriter _out = new StringWriter();
        private readonly StringWriter _err = new StringWriter();
        private readonly Queue<string?> _pins = new Queue<string?>();
        private string? _line;
        private bool _redirected;
        private readonly List<string> _prompts = new List<string>();

        public ApprovalPinCommandTests()
        {
            _pin = new ApprovalPin(_dir) { Iterations = 1000 };
            ConsoleInput.InputRedirectedForTests = () => _redirected;
            ConsoleInput.PinReaderForTests = p => { _prompts.Add(p); return _pins.Count > 0 ? _pins.Dequeue() : null; };
            ConsoleInput.LineReaderForTests = p => { _prompts.Add(p); return _line; };
        }

        public void Dispose()
        {
            ConsoleInput.InputRedirectedForTests = null;
            ConsoleInput.PinReaderForTests = null;
            ConsoleInput.LineReaderForTests = null;
            TryDelete(_dir);
        }

        private void Type(params string?[] pins) { foreach (var p in pins) _pins.Enqueue(p); }

        private string Printed => _out.ToString() + _err.ToString();

        [Fact]
        public void Set_FirstTime_AsksTwice_ThenStatusShowsIt()
        {
            Type("my pin 2468", "my pin 2468");
            Assert.Equal(0, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.Equal(2, _prompts.Count);
            Assert.True(_pin.Verify("my pin 2468").IsOk);
            Assert.Contains("Approval PIN set.", _out.ToString());
            Assert.DoesNotContain("my pin 2468", Printed);
            Assert.DoesNotContain("my pin 2468", File.ReadAllText(_pin.FilePath));

            _out.GetStringBuilder().Clear();
            Assert.Equal(0, ApprovalPinCommand.RunStatus(_pin, _out));
            Assert.Contains("An approval PIN is set.", _out.ToString());
            Assert.Contains("Last changed:", _out.ToString());
        }

        [Fact]
        public void Set_Change_NeedsTheCurrentPin()
        {
            _pin.Set("246810", null);
            Type("000000x", "135799", "135799");
            Assert.Equal(1, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.Contains("The current PIN is wrong", _err.ToString());
            Assert.True(_pin.Verify("246810").IsOk);

            _prompts.Clear();
            Type("246810", "135799", "135799");
            Assert.Equal(0, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.StartsWith("Current approval PIN", _prompts[0]);
            Assert.True(_pin.Verify("135799").IsOk);
            Assert.Contains("Approval PIN changed.", _out.ToString());
            Assert.DoesNotContain("135799", Printed);
            Assert.DoesNotContain("246810", Printed);
        }

        [Fact]
        public void Set_Refusals_Mismatch_WeakPin_Cancel_Redirected()
        {
            Type("246810", "246811");
            Assert.Equal(1, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.Contains("different", _err.ToString());

            Type("123456");
            Assert.Equal(1, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.Contains("straight run", _err.ToString());

            Type((string?)null);
            Assert.Equal(1, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.Contains("Cancelled", _err.ToString());
            Assert.False(_pin.Status().IsSet);

            _redirected = true;
            _prompts.Clear();
            Type("246810", "246810");
            Assert.Equal(1, ApprovalPinCommand.RunSet(_pin, _out, _err));
            Assert.Contains("run it yourself in a console", _err.ToString());
            Assert.Empty(_prompts);
            Assert.False(_pin.Status().IsSet);
        }

        [Fact]
        public void Reset_NeedsConfirmAndTypedReset_InteractiveOnly()
        {
            _pin.Set("246810", null);

            Assert.Equal(1, ApprovalPinCommand.RunReset(_pin, false, _out, _err));
            Assert.True(_pin.Status().IsSet);

            _line = "reset";
            Assert.Equal(1, ApprovalPinCommand.RunReset(_pin, true, _out, _err));
            Assert.True(_pin.Status().IsSet);

            _redirected = true;
            _line = "RESET";
            Assert.Equal(1, ApprovalPinCommand.RunReset(_pin, true, _out, _err));
            Assert.True(_pin.Status().IsSet);

            _redirected = false;
            Assert.Equal(0, ApprovalPinCommand.RunReset(_pin, true, _out, _err));
            Assert.False(_pin.Status().IsSet);
            _out.GetStringBuilder().Clear();
            ApprovalPinCommand.RunStatus(_pin, _out);
            Assert.Contains("No approval PIN is set", _out.ToString());
        }

        [Fact]
        public void Open_ResolvesTheDataFolder_LikeTheServer()
        {
            var pin = ApprovalPinCommand.Open(null, _err, new Dictionary<string, string?> { ["LACERTE_HTTP_DATA_DIR"] = _dir });
            Assert.Equal(Path.Combine(Path.GetFullPath(_dir), ApprovalPin.FileName), pin!.FilePath);
            // The proposal CLI's store finds the same file (parent of its proposals folder).
            var store = ProposalCommand.OpenStore(null, _err, new Dictionary<string, string?> { ["LACERTE_HTTP_DATA_DIR"] = _dir });
            Assert.Equal(pin.FilePath, store!.Pin.FilePath);
            Assert.Null(ApprovalPinCommand.Open(null, _err, new Dictionary<string, string?>()));
        }

        [Fact]
        public void Build_RegistersSubcommands()
        {
            var cmd = ApprovalPinCommand.Build();
            Assert.Equal("approval-pin", cmd.Name);
            Assert.Equal(new[] { "reset", "set", "status" }, cmd.Subcommands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }
}
