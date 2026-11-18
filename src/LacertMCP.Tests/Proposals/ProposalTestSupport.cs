using System;
using System.IO;
using System.Text.Json;
using LacertMCP.Server.Proposals;

namespace LacertMCP.Tests.Proposals
{
    /// <summary>Temp proposal folders and hand-built proposals for the store / CLI tests.</summary>
    internal static class ProposalTestSupport
    {
        public static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lacerte-proposals-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The approval PIN the test stores are set up with.</summary>
        public const string TestPin = "test-pin-2468";

        /// <summary>An approval PIN in <paramref name="dir"/> with cheap PBKDF2 (tests only), set to <see cref="TestPin"/> unless <paramref name="set"/> is false.</summary>
        public static ApprovalPin NewPin(string dir, bool set = true)
        {
            var pin = new ApprovalPin(dir) { Iterations = 1000 };
            if (set) pin.Set(TestPin, null);
            return pin;
        }

        /// <summary>A store whose proposals AND approval-pin.json live in <paramref name="dir"/> (never the shared temp folder).</summary>
        public static ProposalStore NewStore(string dir, bool pinSet = true) => new ProposalStore(dir, NewPin(dir, pinSet));

        public static ProposalContext Ctx() => new ProposalContext { DataPath = @"C:\Lacerte\24Tax\IData", TaxYear = 2024, Module = "I" };

        public static ProposalTargetReturn Target(string clientNumber = "1001", string name = "John Smith")
            => new ProposalTargetReturn { ClientNumber = clientNumber, ClientName = name, ClientId = "SMIT" };

        public static ProposalLine FieldLine(string field, object value, bool ok = true) => new ProposalLine
        {
            Kind = ProposalLineKind.Field,
            Target = new ProposalTarget { Table = 1, Field = field },
            Value = JsonSerializer.SerializeToElement(value),
            DictionaryId = "test." + field,
            Source = new SourceCitation { Document = "organizer.pdf", Page = 2, Location = "Line 7", Excerpt = "excerpt" },
            Reasoning = "because",
            Confidence = ProposalConfidence.High,
            Preview = new LinePreview { Ok = ok, StatusCode = ok ? 1 : 9012, StatusName = ok ? "Success" : "UnknownFieldAccess", Message = ok ? "ok" : "not writable" },
        };

        public static ProposalLine DetailLine(double amount, string description) => new ProposalLine
        {
            Kind = ProposalLineKind.Detail,
            Target = new ProposalTarget
            {
                Detail = new ProposalDetailRow { Series = "11", Code = "1", Suffix = "1000", Amount = amount, Description = description, Source = "Source", Tsj = "Taxpayer" },
            },
            Source = new SourceCitation { Document = "W-2.pdf", Location = "Box 1" },
            Confidence = ProposalConfidence.Medium,
            Preview = new LinePreview { Ok = true, StatusCode = 1, StatusName = "Success", Message = "ok" },
        };

        /// <summary>A Draft proposal with the given lines (ids L1..Ln), created through the store's agent path.</summary>
        public static Proposal NewProposal(ProposalStore store, params ProposalLine[] lines)
        {
            var p = store.Create(Ctx(), "1001", "test proposal", "notes", "stdio", Target());
            if (lines.Length == 0) return p;
            return store.EditContent(p.Id, "stdio", "lines", q =>
            {
                foreach (var l in lines)
                {
                    l.LineId = "L" + q.NextLineNumber;
                    q.NextLineNumber++;
                    q.Lines.Add(l);
                }
            });
        }

        /// <summary>Submit, decide every line (approve unless listed in <paramref name="reject"/>), approve.</summary>
        public static Proposal SubmitAndApprove(ProposalStore store, Proposal p, params string[] reject)
        {
            p = store.Submit(p.Id, "stdio");
            foreach (var l in p.Lines)
            {
                var d = Array.IndexOf(reject, l.LineId) >= 0 ? ReviewerDecision.Rejected : ReviewerDecision.Approved;
                p = store.SetLineDecision(p.Id, l.LineId, d, null, p.Version);
            }
            return store.Approve(p.Id, "reviewer@pc", ProposalStore.ChannelCli, p.Version, TestPin, p.ClientNumber, "looks right");
        }
    }
}
