using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Proposals;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp proposal {list | show | approve | reject | return | resolve}</c> —
    /// the operator's review channel for proposals an agent prepared
    /// (DECISIONS 2026-11-18 "Approval model"). Works on the same folder the
    /// server uses (<c>LACERTE_HTTP_DATA_DIR</c> or <c>%APPDATA%\lacerte-mcp</c>,
    /// <c>\proposals</c>; <c>--dir</c> overrides).
    ///
    /// <para>The CLI approves, rejects, returns and resolves; it NEVER commits — the
    /// agent (<c>lacerte_proposal_commit</c>) or the control page does that. Approvals
    /// are recorded with channel "cli" and by = user@machine.</para>
    ///
    /// <para><c>approve --confirm</c> and <c>resolve --confirm</c> need the approval PIN
    /// (<see cref="ApprovalPin"/>, in the parent of the proposal folder) typed at a
    /// console: they print the return the entries go into, ask for the client
    /// number (approve) and the PIN without echo, and refuse when input is
    /// redirected.</para>
    /// </summary>
    public static class ProposalCommand
    {
        public static Command Build()
        {
            var cmd = new Command("proposal",
                "Review the proposals an AI agent prepared and approve, reject or return them. Approval is only possible here or on the control page; " +
                "the agent commits an approved proposal afterwards. The CLI never writes to Lacerte.");
            cmd.AddCommand(BuildList());
            cmd.AddCommand(BuildShow());
            cmd.AddCommand(BuildApprove());
            cmd.AddCommand(BuildReject());
            cmd.AddCommand(BuildReturn());
            cmd.AddCommand(BuildResolve());
            return cmd;
        }

        private static Option<string?> DirOption()
            => new Option<string?>("--dir", "Proposal folder. Default: LACERTE_HTTP_DATA_DIR\\proposals, else %APPDATA%\\lacerte-mcp\\proposals (the server's folder).");

        private static Command BuildList()
        {
            var c = new Command("list", "List proposals, newest first.");
            var dir = DirOption();
            var client = new Option<string?>("--client", "Only this ClientNumber.");
            var status = new Option<string?>("--status", "Only this status (Draft, Submitted, Approved, Rejected, ReturnedForChanges, Committing, Committed, PartiallyCommitted, CommitFailed).");
            c.AddOption(dir);
            c.AddOption(client);
            c.AddOption(status);
            c.SetHandler(ctx =>
            {
                var r = ctx.ParseResult;
                var store = OpenStore(r.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = store == null ? 1 : RunList(store, r.GetValueForOption(client), r.GetValueForOption(status), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildShow()
        {
            var c = new Command("show", "Print a proposal for review: every line with its target, value, source, reasoning, confidence and dry-run result; manual items; questions; approval state.");
            var id = new Argument<string>("id", "The proposal id.");
            var dir = DirOption();
            c.AddArgument(id);
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var r = ctx.ParseResult;
                var store = OpenStore(r.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = store == null ? 1 : RunShow(store, r.GetValueForArgument(id), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildApprove()
        {
            var c = new Command("approve",
                "Approve a Submitted proposal. Every line is approved except the ones you --reject (and lines whose dry run failed, which can't be approved). " +
                "Without --confirm it only prints what would be approved. With --confirm it shows the return the entries go into and asks you to type the client number " +
                "and your approval PIN (interactive only; see `lacerte-mcp approval-pin`).");
            var id = new Argument<string>("id", "The proposal id.");
            var reject = new Option<string[]>("--reject", () => Array.Empty<string>(), "Line id to reject (repeatable), e.g. --reject L3 --reject L7.");
            var note = new Option<string?>("--note", "Note recorded with the approval.");
            var version = new Option<int?>("--version", "Approve only if the proposal is still at this version (the one `show` printed).");
            var confirm = new Option<bool>("--confirm", () => false, "Actually approve. Without it nothing changes.");
            var dir = DirOption();
            c.AddArgument(id);
            c.AddOption(reject);
            c.AddOption(note);
            c.AddOption(version);
            c.AddOption(confirm);
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var r = ctx.ParseResult;
                var store = OpenStore(r.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = store == null ? 1 : RunApprove(store, r.GetValueForArgument(id), r.GetValueForOption(reject) ?? Array.Empty<string>(),
                    r.GetValueForOption(note), r.GetValueForOption(version), r.GetValueForOption(confirm), Reviewer(), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildReject()
        {
            var c = new Command("reject", "Reject a proposal (final; the agent must start a new one). Nothing is written.");
            var id = new Argument<string>("id", "The proposal id.");
            var note = new Option<string>("--note", "Why it is rejected (required).") { IsRequired = true };
            var dir = DirOption();
            c.AddArgument(id);
            c.AddOption(note);
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var r = ctx.ParseResult;
                var store = OpenStore(r.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = store == null ? 1 : RunReject(store, r.GetValueForArgument(id), r.GetValueForOption(note), Reviewer(), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildReturn()
        {
            var c = new Command("return", "Send a proposal back to the agent for changes, with a note saying what to change. Clears any approval.");
            var id = new Argument<string>("id", "The proposal id.");
            var note = new Option<string>("--note", "What to change (required).") { IsRequired = true };
            var dir = DirOption();
            c.AddArgument(id);
            c.AddOption(note);
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var r = ctx.ParseResult;
                var store = OpenStore(r.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = store == null ? 1 : RunReturn(store, r.GetValueForArgument(id), r.GetValueForOption(note), Reviewer(), Console.Out, Console.Error);
            });
            return c;
        }

        private static Command BuildResolve()
        {
            var c = new Command("resolve",
                "After checking the client's detail rows in Lacerte, resolve an Uncertain line (a row that may or may not have been written): " +
                "--in-return if the row IS there (it is never written again), --missing if it is NOT (the next commit writes it once). " +
                "With --confirm it asks for your approval PIN (interactive only).");
            var id = new Argument<string>("id", "The proposal id.");
            var line = new Option<string>("--line", "The Uncertain line id.") { IsRequired = true };
            var inReturn = new Option<bool>("--in-return", () => false, "The row is in the return.");
            var missing = new Option<bool>("--missing", () => false, "The row is not in the return.");
            var note = new Option<string?>("--note", "Note for the record.");
            var confirm = new Option<bool>("--confirm", () => false, "Actually record it. Without it nothing changes.");
            var dir = DirOption();
            c.AddArgument(id);
            c.AddOption(line);
            c.AddOption(inReturn);
            c.AddOption(missing);
            c.AddOption(note);
            c.AddOption(confirm);
            c.AddOption(dir);
            c.SetHandler(ctx =>
            {
                var r = ctx.ParseResult;
                var store = OpenStore(r.GetValueForOption(dir), Console.Error);
                ctx.ExitCode = store == null ? 1 : RunResolve(store, r.GetValueForArgument(id), r.GetValueForOption(line) ?? string.Empty,
                    r.GetValueForOption(inReturn), r.GetValueForOption(missing), r.GetValueForOption(note), r.GetValueForOption(confirm),
                    Reviewer(), Console.Out, Console.Error);
            });
            return c;
        }

        // ---- shared ---------------------------------------------------------------------------

        /// <summary>user@machine — recorded as the reviewer.</summary>
        public static string Reviewer() => Environment.UserName + "@" + Environment.MachineName;

        /// <summary>The store at <paramref name="dir"/>, else the server's default folder; null (and a message) when none resolves.</summary>
        public static ProposalStore? OpenStore(string? dir, TextWriter stderr, IDictionary<string, string?>? env = null)
        {
            if (!string.IsNullOrWhiteSpace(dir)) return new ProposalStore(Path.GetFullPath(dir!.Trim()));
            var resolved = ProposalStore.ResolveDirectory(env ?? ProcessEnv());
            if (resolved == null)
            {
                stderr.WriteLine($"No proposal folder: pass --dir, or set {ProposalStore.DataDirEnvVar} or APPDATA.");
                return null;
            }
            return new ProposalStore(resolved);
        }

        private static IDictionary<string, string?> ProcessEnv()
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables()) d[(string)kv.Key] = kv.Value as string;
            return d;
        }

        private static int Fail(TextWriter stderr, LacerteSDKError ex)
        {
            var reason = ex.Context != null && ex.Context.TryGetValue("reason", out var r) ? r as string : null;
            stderr.WriteLine($"Refused ({ex.StatusCode} {ex.StatusName}): {reason ?? ex.UserMessage}");
            return 1;
        }

        private static Proposal? Load(ProposalStore store, string id, TextWriter stderr)
        {
            var p = store.Get((id ?? string.Empty).Trim());
            if (p == null) stderr.WriteLine($"No proposal '{id}' in {store.DirectoryPath}. List them with `lacerte-mcp proposal list`.");
            return p;
        }

        // ---- list -----------------------------------------------------------------------------

        public static int RunList(ProposalStore store, string? client, string? status, TextWriter stdout, TextWriter stderr)
        {
            ProposalStatus? s = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ProposalStatus>(status!.Trim(), true, out var parsed) || !Enum.IsDefined(typeof(ProposalStatus), parsed))
                {
                    stderr.WriteLine($"Unknown status '{status}'. One of: {string.Join(", ", Enum.GetNames(typeof(ProposalStatus)))}.");
                    return 1;
                }
                s = parsed;
            }
            var rows = store.List(new ProposalFilter { ClientNumber = client, Status = s });
            stdout.WriteLine($"# {store.DirectoryPath}");
            if (rows.Count == 0)
            {
                stdout.WriteLine("  (no proposals)");
                return 0;
            }
            foreach (var p in rows)
            {
                stdout.WriteLine($"- {p.Id}  {p.Status,-18} v{p.Version}  client {p.ClientNumber}{(string.IsNullOrEmpty(p.ClientName) ? "" : " (" + p.ClientName + ")")}  {p.Context.TaxYear} module {p.Context.Module}  \"{p.Title}\"");
                stdout.WriteLine($"    lines {p.LineCount} (approved {p.ApprovedLineCount}, rejected {p.RejectedLineCount}, pending {p.PendingLineCount}, failed dry run {p.InvalidLineCount}), " +
                                 $"manual items {p.ManualItemCount}, questions {p.QuestionCount}; by {p.Author}, updated {Time(p.UpdatedAt)}");
            }
            return 0;
        }

        // ---- show -----------------------------------------------------------------------------

        public static int RunShow(ProposalStore store, string id, TextWriter stdout, TextWriter stderr)
        {
            var p = Load(store, id, stderr);
            if (p == null) return 1;
            WriteReview(p, stdout);
            return 0;
        }

        /// <summary>The readable review of a proposal (also printed by approve before it asks for --confirm).</summary>
        public static void WriteReview(Proposal p, TextWriter o)
        {
            var state = ProposalStore.Evaluate(p);
            o.WriteLine($"Proposal {p.Id}: {p.Title}");
            o.WriteLine($"  Entries go into: {ProposalStore.DescribeTargetReturn(p)}");
            if (p.TargetReturn == null) o.WriteLine($"  {ProposalStore.NoTargetReturnText}");
            o.WriteLine($"  Client {p.ClientNumber} | tax year {p.Context.TaxYear} | module {p.Context.Module} | {p.Context.DataPath}");
            o.WriteLine($"  Status {p.Status} | version {p.Version} | prepared by {p.Author} | created {Time(p.CreatedAt)} | updated {Time(p.UpdatedAt)}");
            o.WriteLine($"  Content hash {state.ContentHash}");
            if (p.Approval != null)
            {
                o.WriteLine($"  Approved by {p.Approval.By} ({p.Approval.Channel}{(p.Approval.PinVerified ? ", approval PIN verified" : "")}) at {Time(p.Approval.At)} for version {p.Approval.Version}" +
                            (p.Approval.TargetReturn == null ? "" : $" and client {p.Approval.TargetReturn.ClientNumber} · {p.Approval.TargetReturn.ClientName}") +
                            (state.ApprovalCurrent ? "" : " -- OUTDATED: the proposal changed after approval") +
                            (string.IsNullOrEmpty(p.Approval.Note) ? "" : $" -- \"{p.Approval.Note}\""));
            }
            if (p.LastReview != null)
            {
                o.WriteLine($"  Last review: {p.LastReview.Action} by {p.LastReview.By} ({p.LastReview.Channel}) at {Time(p.LastReview.At)}" +
                            (string.IsNullOrEmpty(p.LastReview.Note) ? "" : $": {p.LastReview.Note}"));
            }
            if (!string.IsNullOrWhiteSpace(p.Notes)) o.WriteLine($"  Notes: {p.Notes}");
            o.WriteLine();

            o.WriteLine($"Lines ({p.Lines.Count}):");
            if (p.Lines.Count == 0) o.WriteLine("  (none)");
            foreach (var l in p.Lines)
            {
                o.WriteLine($"  {l.LineId,-4} {DescribeTarget(l)}   [{l.ReviewerDecision}]");
                if (!string.IsNullOrEmpty(l.ReviewerNote)) o.WriteLine($"       reviewer note: {l.ReviewerNote}");
                o.WriteLine($"       source:     {DescribeSource(l.Source)}");
                if (!string.IsNullOrEmpty(l.DictionaryId)) o.WriteLine($"       dictionary: {l.DictionaryId}");
                if (!string.IsNullOrEmpty(l.Reasoning)) o.WriteLine($"       reasoning:  {l.Reasoning}");
                o.WriteLine($"       confidence: {l.Confidence}");
                o.WriteLine($"       dry run:    {(l.Preview.Ok ? "OK" : "FAILED")} ({l.Preview.StatusCode} {l.Preview.StatusName}) {l.Preview.Message}");
                foreach (var w in l.Warnings) o.WriteLine($"       warning:    {w}");
                if (l.Commit != null)
                {
                    o.WriteLine($"       commit:     {l.Commit.Status}" +
                                (l.Commit.Verified == true ? " (read back, verified)" : "") +
                                (l.Commit.StatusCode.HasValue ? $" ({l.Commit.StatusCode} {l.Commit.StatusName})" : "") +
                                (string.IsNullOrEmpty(l.Commit.Note) ? "" : $" {l.Commit.Note}"));
                }
            }
            o.WriteLine();

            o.WriteLine($"Manual items -- enter these by hand in Lacerte ({p.ManualItems.Count}):");
            if (p.ManualItems.Count == 0) o.WriteLine("  (none)");
            foreach (var m in p.ManualItems)
            {
                o.WriteLine($"  {m.ItemId,-4} {m.Description}" + (m.Amount.HasValue ? $"   {Money(m.Amount.Value)}" : ""));
                o.WriteLine($"       reason: {m.Reason}");
                if (m.Source != null) o.WriteLine($"       source: {DescribeSource(m.Source)}");
            }
            o.WriteLine();

            o.WriteLine($"Questions ({p.Questions.Count}):");
            if (p.Questions.Count == 0) o.WriteLine("  (none)");
            foreach (var q in p.Questions)
            {
                o.WriteLine($"  {q.QuestionId,-4} {q.Text}" + (q.LineIds != null && q.LineIds.Count > 0 ? $"   (lines {string.Join(", ", q.LineIds)})" : ""));
            }
            o.WriteLine();

            o.WriteLine($"State: {state.ApprovedLineCount} approved, {state.RejectedLineCount} rejected, {state.PendingLineCount} pending, {state.InvalidLineCount} failed dry run.");
            if (state.UncertainLineIds.Count > 0)
                o.WriteLine($"  MAY ALREADY BE IN THE RETURN (check in Lacerte, then `lacerte-mcp proposal resolve`): {string.Join(", ", state.UncertainLineIds)}");
            foreach (var b in state.Blockers) o.WriteLine($"  - {b}");
        }

        private static string DescribeTarget(ProposalLine l)
        {
            var t = l.Target;
            if (l.Kind == ProposalLineKind.Field)
            {
                var where = $"table {t.Table} {t.Field}"
                            + (t.SubClientId.HasValue ? $" subclient {t.SubClientId}" : "")
                            + (string.IsNullOrEmpty(t.StateAbbrev) ? "" : $" state {t.StateAbbrev}");
                return $"field   {where} = {DescribeValue(l.Value)}";
            }
            var d = t.Detail ?? new ProposalDetailRow();
            var parts = new List<string> { $"Series {d.Series}", $"Code {d.Code}" };
            if (!string.IsNullOrEmpty(d.Prefix)) parts.Add($"Prefix {d.Prefix}");
            if (!string.IsNullOrEmpty(d.Suffix)) parts.Add($"Suffix {d.Suffix}");
            if (!string.IsNullOrEmpty(d.StateAbbrev)) parts.Add($"State {d.StateAbbrev}");
            if (!string.IsNullOrEmpty(d.CityAbbr)) parts.Add($"City {d.CityAbbr}");
            if (!string.IsNullOrEmpty(d.Tsj)) parts.Add($"TSJ {d.Tsj}");
            if (!string.IsNullOrEmpty(d.Source)) parts.Add($"Source {d.Source}");
            return $"detail  {string.Join(" ", parts)}  amount {(d.Amount.HasValue ? Money(d.Amount.Value) : "(none)")}  \"{d.Description}\"";
        }

        private static string DescribeValue(JsonElement? v)
        {
            if (v == null) return "(null)";
            var e = v.Value;
            return e.ValueKind == JsonValueKind.String ? "\"" + e.GetString() + "\"" : e.GetRawText();
        }

        private static string DescribeSource(SourceCitation? s)
        {
            if (s == null) return "(none)";
            var parts = new List<string> { s.Document };
            if (s.Page.HasValue) parts.Add("page " + s.Page.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.Location)) parts.Add(s.Location!);
            var text = string.Join(", ", parts);
            if (!string.IsNullOrEmpty(s.Excerpt)) text += $" -- \"{s.Excerpt}\"";
            return text;
        }

        private static string Money(double d) => d.ToString("#,0.00##", CultureInfo.InvariantCulture);

        private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        // ---- approve / reject / return ---------------------------------------------------------

        public static int RunApprove(ProposalStore store, string id, IReadOnlyCollection<string> reject, string? note, int? expectVersion,
            bool confirm, string by, TextWriter stdout, TextWriter stderr)
        {
            var p = Load(store, id, stderr);
            if (p == null) return 1;
            if (expectVersion.HasValue && expectVersion.Value != p.Version)
            {
                stderr.WriteLine($"The proposal is at version {p.Version}, not {expectVersion.Value}: it changed since you reviewed it. Run `lacerte-mcp proposal show {p.Id}` again.");
                return 1;
            }
            if (p.Status != ProposalStatus.Submitted)
            {
                stderr.WriteLine($"Only a Submitted proposal can be approved; this one is {p.Status}.");
                return 1;
            }
            var rejectSet = new HashSet<string>(reject.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            var unknown = rejectSet.Where(r => !p.Lines.Any(l => string.Equals(l.LineId, r, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                stderr.WriteLine($"No such line(s): {string.Join(", ", unknown)}. Lines: {string.Join(", ", p.Lines.Select(l => l.LineId))}.");
                return 1;
            }

            var plan = new List<(ProposalLine Line, ReviewerDecision Decision, string Why)>();
            foreach (var l in p.Lines)
            {
                if (rejectSet.Contains(l.LineId)) plan.Add((l, ReviewerDecision.Rejected, "--reject"));
                else if (!l.Preview.Ok) plan.Add((l, ReviewerDecision.Rejected, "dry run failed; can't be approved"));
                else if (l.ReviewerDecision == ReviewerDecision.Rejected) plan.Add((l, ReviewerDecision.Rejected, "already rejected on the control page"));
                else plan.Add((l, ReviewerDecision.Approved, ""));
            }

            WriteReview(p, stdout);
            stdout.WriteLine();
            stdout.WriteLine($"Decisions for version {p.Version}:");
            foreach (var (line, decision, why) in plan)
            {
                stdout.WriteLine($"  {line.LineId,-4} {decision}" + (why.Length == 0 ? "" : $"  ({why})"));
            }
            var approvedCount = plan.Count(x => x.Decision == ReviewerDecision.Approved);
            if (approvedCount == 0)
            {
                stderr.WriteLine("No line would be approved; there is nothing to approve. Reject or return the proposal instead.");
                return 1;
            }
            if (!confirm)
            {
                stdout.WriteLine();
                stdout.WriteLine($"Nothing changed. To approve these {approvedCount} line(s) of version {p.Version}, run again with --confirm (add --version {p.Version} to be sure nothing changed meanwhile).");
                return 0;
            }

            if (ConsoleInput.IsInputRedirected)
            {
                stderr.WriteLine("Approving needs your approval PIN. " + ConsoleInput.RedirectedMessage);
                return 1;
            }
            if (p.TargetReturn == null)
            {
                stderr.WriteLine(ProposalStore.NoTargetReturnText);
                return 1;
            }
            var pinStatus = store.Pin.Status();
            if (!pinStatus.IsSet)
            {
                stderr.WriteLine(ApprovalPin.SetupInstruction);
                return 1;
            }

            stdout.WriteLine();
            stdout.WriteLine($"Entries go into: {ProposalStore.DescribeTargetReturn(p)}");
            var typed = ConsoleInput.ReadLine("Type the client number to confirm: ", stdout);
            if (typed == null)
            {
                stderr.WriteLine("Cancelled. Nothing was approved.");
                return 1;
            }
            var pin = ConsoleInput.ReadSecret("Approval PIN: ", stdout);
            if (pin == null)
            {
                stderr.WriteLine("Cancelled. Nothing was approved.");
                return 1;
            }

            try
            {
                var decisions = plan.ToDictionary(x => x.Line.LineId, x => x.Decision, StringComparer.Ordinal);
                var approved = store.Approve(p.Id, by, ProposalStore.ChannelCli, p.Version, pin, typed, note, decisions);
                stdout.WriteLine();
                stdout.WriteLine($"Approved version {approved.Version} ({approvedCount} line(s)) by {by} for {ProposalStore.DescribeTargetReturn(approved)}. Content hash {approved.Approval!.ContentHash}.");
                stdout.WriteLine("Nothing is written yet: the agent commits it with lacerte_proposal_commit (or use the control page).");
                return 0;
            }
            catch (LacerteSDKError ex)
            {
                return Fail(stderr, ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"Couldn't check the approval PIN: {ex.Message}");
                return 1;
            }
        }

        public static int RunReject(ProposalStore store, string id, string? note, string by, TextWriter stdout, TextWriter stderr)
        {
            if (string.IsNullOrWhiteSpace(note))
            {
                stderr.WriteLine("--note is required: say why the proposal is rejected.");
                return 1;
            }
            if (Load(store, id, stderr) == null) return 1;
            try
            {
                var p = store.Reject(id.Trim(), by, ProposalStore.ChannelCli, note);
                stdout.WriteLine($"Rejected {p.Id} (version {p.Version}) by {by}. Nothing was written.");
                return 0;
            }
            catch (LacerteSDKError ex)
            {
                return Fail(stderr, ex);
            }
        }

        public static int RunReturn(ProposalStore store, string id, string? note, string by, TextWriter stdout, TextWriter stderr)
        {
            if (string.IsNullOrWhiteSpace(note))
            {
                stderr.WriteLine("--note is required: say what the agent should change.");
                return 1;
            }
            if (Load(store, id, stderr) == null) return 1;
            try
            {
                var p = store.ReturnForChanges(id.Trim(), by, ProposalStore.ChannelCli, note);
                stdout.WriteLine($"Returned {p.Id} (version {p.Version}) to the agent for changes. Ask the agent to read it with lacerte_proposal_get, make the changes and submit it again.");
                return 0;
            }
            catch (LacerteSDKError ex)
            {
                return Fail(stderr, ex);
            }
        }

        public static int RunResolve(ProposalStore store, string id, string lineId, bool inReturn, bool missing, string? note, bool confirm,
            string by, TextWriter stdout, TextWriter stderr)
        {
            if (inReturn == missing)
            {
                stderr.WriteLine("Pass exactly one of --in-return (the row IS in the client's detail rows) or --missing (it is NOT).");
                return 1;
            }
            var p = Load(store, id, stderr);
            if (p == null) return 1;
            var line = p.Lines.FirstOrDefault(l => string.Equals(l.LineId, (lineId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (line == null)
            {
                stderr.WriteLine($"No line '{lineId}'. Lines: {string.Join(", ", p.Lines.Select(l => l.LineId))}.");
                return 1;
            }
            stdout.WriteLine($"{line.LineId}: {DescribeTarget(line)}");
            stdout.WriteLine($"  commit: {line.Commit?.Status.ToString() ?? "(none)"} {line.Commit?.Note}");
            if (!confirm)
            {
                stdout.WriteLine(inReturn
                    ? "Would record that this row IS in the return (it is never written again). Run again with --confirm."
                    : "Would record that this row is NOT in the return (the next commit writes it once, with a new idempotency key). Run again with --confirm.");
                return 0;
            }
            if (ConsoleInput.IsInputRedirected)
            {
                stderr.WriteLine("Resolving a line needs your approval PIN. " + ConsoleInput.RedirectedMessage);
                return 1;
            }
            if (!store.Pin.Status().IsSet)
            {
                stderr.WriteLine(ApprovalPin.SetupInstruction);
                return 1;
            }
            stdout.WriteLine($"Entries go into: {ProposalStore.DescribeTargetReturn(p)}");
            var pin = ConsoleInput.ReadSecret("Approval PIN: ", stdout);
            if (pin == null)
            {
                stderr.WriteLine("Cancelled. Nothing changed.");
                return 1;
            }
            try
            {
                var updated = store.ResolveUncertainLine(p.Id, line.LineId, inReturn, by, ProposalStore.ChannelCli, pin, note);
                stdout.WriteLine($"Recorded. Proposal {updated.Id} is now {updated.Status}.");
                return 0;
            }
            catch (LacerteSDKError ex)
            {
                return Fail(stderr, ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"Couldn't check the approval PIN: {ex.Message}");
                return 1;
            }
        }
    }
}
