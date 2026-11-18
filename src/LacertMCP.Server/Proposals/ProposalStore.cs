using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Proposals
{
    /// <summary>
    /// File-backed proposal store: one JSON file per proposal in
    /// <c>&lt;data dir&gt;\proposals\</c> (data dir = <c>LACERTE_HTTP_DATA_DIR</c>, else
    /// <c>%APPDATA%\lacerte-mcp</c> — the same folder the HTTP host keeps its device
    /// grants and activity log in). The server, the control page and the
    /// <c>lacerte-mcp proposal</c> CLI all open the same folder.
    ///
    /// <para><b>The files hold taxpayer data</b> (amounts, descriptions, source
    /// excerpts, client numbers). The store drops a README.txt saying so in the
    /// folder; keep the folder on an encrypted volume and inside the firm's
    /// retention policy (ai-preparation-roadmap item 9).</para>
    ///
    /// <para>Writes are atomic (temp file + replace). Every read-modify-write runs
    /// under a per-proposal lock: an in-process semaphore plus an exclusive
    /// <c>&lt;id&gt;.lock</c> file, so the server and the CLI never interleave.</para>
    ///
    /// <para><b>Who may call what.</b> The reviewer methods
    /// (<see cref="SetLineDecision"/>, <see cref="Approve"/>, <see cref="Reject"/>,
    /// <see cref="ReturnForChanges"/>, <see cref="ResolveUncertainLine"/>) are for
    /// HUMAN channels only — the control page and the CLI. No MCP tool calls them
    /// (pinned by a reflection test). Agent edits go through
    /// <see cref="ProposalService"/>; they are internal to this assembly.</para>
    /// </summary>
    public sealed class ProposalStore
    {
        public const string DataDirEnvVar = "LACERTE_HTTP_DATA_DIR";
        public const string FolderName = "proposals";
        public const string ChannelControlPage = "control-page";
        public const string ChannelCli = "cli";
        internal const string ChannelMcp = "mcp";
        internal const string ChannelCommit = "commit";

        public const int MaxLinesPerProposal = 1000;
        public const int MaxManualItemsPerProposal = 500;
        public const int MaxQuestionsPerProposal = 200;

        private static readonly Regex s_idShape = new Regex("^[a-z0-9]{8,64}$", RegexOptions.CultureInvariant);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly Lazy<ProposalStore?> s_shared = new Lazy<ProposalStore?>(() =>
        {
            var dir = ResolveDirectory();
            return dir == null ? null : new ProposalStore(dir);
        });

        /// <summary>The on-disk format (camelCase, enums as names, indented). Use it to read / write proposal JSON elsewhere (control page).</summary>
        public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions(indented: true);

        /// <summary>Same as <see cref="JsonOptions"/>, not indented — for API / tool responses.</summary>
        public static readonly JsonSerializerOptions WireJsonOptions = CreateJsonOptions(indented: false);

        /// <summary>Serializes any proposal object (Proposal, ProposalState, summaries, commit reports) in the wire format.</summary>
        public static JsonElement ToJsonElement(object value) => JsonSerializer.SerializeToElement(value, WireJsonOptions);

        private const string ReadmeText =
            "Lacerte MCP proposals\r\n" +
            "=====================\r\n\r\n" +
            "Each <id>.json file is one proposal: entries an AI agent prepared for a client's tax return,\r\n" +
            "with source citations, the reviewer's decisions and the approval record.\r\n\r\n" +
            "THESE FILES CONTAIN TAXPAYER DATA (amounts, descriptions, source excerpts, client numbers).\r\n" +
            "Keep this folder on an encrypted volume, limit who can read it, and delete proposals under the\r\n" +
            "firm's retention policy. Do not edit the files by hand: an edited proposal no longer matches its\r\n" +
            "approval and will not be committed.\r\n";

        public ProposalStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory is required", nameof(directory));
            DirectoryPath = Path.GetFullPath(directory);
        }

        /// <summary>The folder holding the proposal files.</summary>
        public string DirectoryPath { get; }

        /// <summary>Clock seam for tests.</summary>
        internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>How long a writer waits for another process's lock before giving up.</summary>
        internal TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The process-wide store at the default location (null when neither
        /// LACERTE_HTTP_DATA_DIR nor APPDATA resolves).
        /// </summary>
        public static ProposalStore? Shared => s_shared.Value;

        /// <summary>
        /// <c>&lt;LACERTE_HTTP_DATA_DIR or %APPDATA%\lacerte-mcp&gt;\proposals</c>. Pass
        /// <paramref name="env"/> to resolve against something other than the process
        /// environment (tests, the CLI).
        /// </summary>
        public static string? ResolveDirectory(IDictionary<string, string?>? env = null)
        {
            string? Get(string name)
            {
                if (env != null) return env.TryGetValue(name, out var v) ? v : null;
                return Environment.GetEnvironmentVariable(name);
            }

            var dataDir = Get(DataDirEnvVar);
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                var appData = Get("APPDATA");
                if (string.IsNullOrWhiteSpace(appData) && env == null)
                {
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                if (string.IsNullOrWhiteSpace(appData)) return null;
                dataDir = Path.Combine(appData!, "lacerte-mcp");
            }
            return Path.Combine(dataDir!.Trim(), FolderName);
        }

        // =================================================================
        // Reads (any caller)
        // =================================================================

        /// <summary>Summaries of every proposal matching <paramref name="filter"/>, newest update first. Unreadable files are skipped.</summary>
        public IReadOnlyList<ProposalSummary> List(ProposalFilter? filter = null)
        {
            var result = new List<ProposalSummary>();
            if (!Directory.Exists(DirectoryPath)) return result;
            foreach (var file in Directory.GetFiles(DirectoryPath, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!IsValidId(id)) continue;
                Proposal? p;
                try
                {
                    p = ReadFile(file);
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                if (p == null || !Matches(p, filter)) continue;
                result.Add(Summarize(p));
            }
            return result.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The proposal, or null when there is none with that id (an invalid id is "none").</summary>
        public Proposal? Get(string id)
        {
            if (!IsValidId(id)) return null;
            var path = PathFor(id);
            if (!File.Exists(path)) return null;
            return ReadFile(path);
        }

        /// <summary>The proposal; 9017 InvalidArgument when it doesn't exist.</summary>
        public Proposal GetRequired(string id)
            => Get(id) ?? throw NotFound(id);

        /// <summary>Computed review state: content hash, whether the approval is current, what blocks approve / commit.</summary>
        public static ProposalState Evaluate(Proposal p)
        {
            var state = new ProposalState { ContentHash = ComputeContentHash(p) };
            state.AgentEditable = IsAgentEditable(p.Status);
            foreach (var l in p.Lines)
            {
                if (!l.Preview.Ok) state.InvalidLineCount++;
                switch (l.ReviewerDecision)
                {
                    case ReviewerDecision.Pending: state.PendingLineCount++; break;
                    case ReviewerDecision.Approved: state.ApprovedLineCount++; break;
                    case ReviewerDecision.Rejected: state.RejectedLineCount++; break;
                }
            }
            state.ApprovalCurrent = p.Approval != null
                && p.Approval.Version == p.Version
                && string.Equals(p.Approval.ContentHash, state.ContentHash, StringComparison.Ordinal);

            var approvalBlockers = ApproveBlockers(p);
            state.ReadyToApprove = approvalBlockers.Count == 0;

            var approval = p.Approval;
            if (approval != null)
            {
                foreach (var l in p.Lines)
                {
                    if (!approval.LineDecisions.TryGetValue(l.LineId, out var d) || d != ReviewerDecision.Approved) continue;
                    if (IsUnfinished(l)) state.UnfinishedApprovedLineCount++;
                    if (l.Commit?.Status == LineCommitStatus.Uncertain) state.UncertainLineIds.Add(l.LineId);
                }
            }
            var commitBlockers = CommitBlockers(p, state);
            state.ReadyToCommit = commitBlockers.Count == 0;

            if (p.Status == ProposalStatus.Submitted) state.Blockers.AddRange(approvalBlockers);
            else if (p.Status == ProposalStatus.Approved || p.Status == ProposalStatus.PartiallyCommitted
                     || p.Status == ProposalStatus.CommitFailed || p.Status == ProposalStatus.Committing)
                state.Blockers.AddRange(commitBlockers);
            return state;
        }

        /// <summary>
        /// SHA-256 (lowercase hex) over everything an approval covers: id, context,
        /// client, title, notes, every line's agent-authored content (kind, target,
        /// value, dictionary id, source, reasoning, confidence), manual items and
        /// questions. NOT over previews, reviewer decisions, commit results,
        /// timestamps or history — those change without changing what was approved.
        /// </summary>
        public static string ComputeContentHash(Proposal p)
        {
            var canonical = new object?[]
            {
                "lacerte-proposal/1",
                p.Id,
                p.Context.DataPath,
                p.Context.TaxYear,
                (p.Context.Module ?? string.Empty).ToUpperInvariant(),
                p.ClientNumber,
                p.Title,
                p.Notes,
                p.Lines.Select(CanonicalLine).ToArray(),
                p.ManualItems.Select(m => new object?[]
                {
                    m.ItemId, m.Description, Num(m.Amount), CanonicalSource(m.Source), m.Reason,
                }).ToArray(),
                p.Questions.Select(q => new object?[]
                {
                    q.QuestionId, q.Text, q.LineIds?.ToArray(),
                }).ToArray(),
            };
            var json = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static object?[] CanonicalLine(ProposalLine l)
        {
            var t = l.Target ?? new ProposalTarget();
            var d = t.Detail;
            return new object?[]
            {
                l.LineId,
                l.Kind.ToString(),
                t.Table,
                t.Field,
                t.SubClientId,
                t.StateAbbrev,
                d == null ? null : new object?[]
                {
                    d.Series, d.Code, Num(d.Amount), d.Description, d.Prefix, d.Suffix,
                    d.StateAbbrev, d.CityAbbr, d.Source, d.Tsj, d.ImportSrc,
                },
                CanonicalValue(l.Value),
                l.DictionaryId,
                CanonicalSource(l.Source),
                l.Reasoning,
                l.Confidence.ToString(),
            };
        }

        private static object?[]? CanonicalSource(SourceCitation? s)
            => s == null ? null : new object?[] { s.Document, s.Page, s.Location, s.Excerpt };

        private static string? Num(double? d) => d?.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Type-tagged, spelling-stable form of a JSON scalar ("s:abc", "n:12.50", "b:true", "null").</summary>
        internal static string? CanonicalValue(JsonElement? v)
        {
            if (v == null) return null;
            var e = v.Value;
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return "s:" + e.GetString();
                case JsonValueKind.Number: return "n:" + e.GetRawText();
                case JsonValueKind.True: return "b:true";
                case JsonValueKind.False: return "b:false";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return "null";
                default: return "j:" + e.GetRawText();
            }
        }

        // =================================================================
        // Reviewer methods — HUMAN CHANNELS ONLY (control page, CLI).
        // No MCP tool may call these (ProposalApprovalBoundaryTests).
        // =================================================================

        /// <summary>
        /// Records a reviewer's decision on one line of a Submitted proposal.
        /// <paramref name="expectedVersion"/> must equal the current version (the
        /// reviewer is deciding on what they saw). A line whose dry run failed can't
        /// be Approved. Not a content change: the version doesn't move.
        /// </summary>
        public Proposal SetLineDecision(string id, string lineId, ReviewerDecision decision, string? note, int expectedVersion)
        {
            return Mutate(id, p =>
            {
                RequireStatus(p, "set a line decision", ProposalStatus.Submitted);
                RequireVersion(p, expectedVersion);
                var line = FindLine(p, lineId);
                if (decision == ReviewerDecision.Approved && !line.Preview.Ok)
                {
                    throw Conflict(p, "Line " + line.LineId + " failed its dry run (" + (line.Preview.StatusName ?? "invalid")
                        + ": " + line.Preview.Message + ") and can't be approved. Reject it, or return the proposal so the agent fixes it.");
                }
                line.ReviewerDecision = decision;
                line.ReviewerNote = Trim(note, 2000);
                Touch(p);
            });
        }

        /// <summary>
        /// Approves a Submitted proposal. Requires every line decided (Approved or
        /// Rejected), at least one Approved line, no Approved line with a failed dry
        /// run, and <paramref name="expectedVersion"/> == the current version.
        /// Records <see cref="ProposalApproval"/> (who, channel, when, version,
        /// content hash, per-line decisions).
        /// </summary>
        public Proposal Approve(string id, string by, string channel, int expectedVersion, string? note = null)
        {
            RequireHumanChannel(channel);
            by = RequireBy(by);
            return Mutate(id, p =>
            {
                RequireStatus(p, "approve", ProposalStatus.Submitted);
                RequireVersion(p, expectedVersion);
                var blockers = ApproveBlockers(p);
                if (blockers.Count > 0) throw Conflict(p, string.Join(" ", blockers));
                var now = Clock();
                p.Approval = new ProposalApproval
                {
                    By = by,
                    Channel = channel,
                    At = now,
                    Version = p.Version,
                    ContentHash = ComputeContentHash(p),
                    LineDecisions = p.Lines.ToDictionary(l => l.LineId, l => l.ReviewerDecision, StringComparer.Ordinal),
                    Note = Trim(note, 2000),
                };
                p.Status = ProposalStatus.Approved;
                AddEvent(p, by, channel, "approved", note);
            });
        }

        /// <summary>Rejects the proposal (terminal). Allowed from Submitted, Approved or ReturnedForChanges.</summary>
        public Proposal Reject(string id, string by, string channel, string? note)
        {
            RequireHumanChannel(channel);
            by = RequireBy(by);
            return Mutate(id, p =>
            {
                RequireStatus(p, "reject", ProposalStatus.Submitted, ProposalStatus.Approved, ProposalStatus.ReturnedForChanges);
                p.Approval = null;
                p.Status = ProposalStatus.Rejected;
                p.LastReview = AddEvent(p, by, channel, "rejected", note);
            });
        }

        /// <summary>Sends the proposal back to the agent with a note. Allowed from Submitted or Approved; clears any approval.</summary>
        public Proposal ReturnForChanges(string id, string by, string channel, string? note)
        {
            RequireHumanChannel(channel);
            by = RequireBy(by);
            return Mutate(id, p =>
            {
                RequireStatus(p, "return for changes", ProposalStatus.Submitted, ProposalStatus.Approved);
                p.Approval = null;
                p.Status = ProposalStatus.ReturnedForChanges;
                p.LastReview = AddEvent(p, by, channel, "returnedForChanges", note);
            });
        }

        /// <summary>
        /// A person checked Lacerte for an Uncertain detail line (the row may have
        /// landed): <paramref name="rowIsInReturn"/> true marks it Succeeded (no
        /// re-write); false marks it Failed with a fresh idempotency key generation so
        /// the next commit writes it once. Doesn't change the approved content.
        /// </summary>
        public Proposal ResolveUncertainLine(string id, string lineId, bool rowIsInReturn, string by, string channel, string? note)
        {
            RequireHumanChannel(channel);
            by = RequireBy(by);
            return Mutate(id, p =>
            {
                var line = FindLine(p, lineId);
                if (line.Commit?.Status != LineCommitStatus.Uncertain)
                {
                    throw Conflict(p, "Line " + line.LineId + " is not Uncertain (its commit status is "
                        + (line.Commit?.Status.ToString() ?? "none") + "); only an Uncertain line is resolved by hand.");
                }
                if (p.Status == ProposalStatus.Committing && CommitInProgressElsewhere(p))
                {
                    throw Conflict(p, "A commit is running; resolve the line after it finishes.");
                }
                var commit = line.Commit!;
                commit.At = Clock();
                if (rowIsInReturn)
                {
                    commit.Status = LineCommitStatus.Succeeded;
                    commit.Note = "Confirmed in Lacerte by " + by + " (" + channel + ").";
                }
                else
                {
                    commit.Status = LineCommitStatus.Failed;
                    commit.Note = "Confirmed NOT in the return by " + by + " (" + channel + "); the next commit writes it with a new idempotency key.";
                    line.KeyGeneration++;
                }
                p.Status = FinalStatus(p);
                AddEvent(p, by, channel, "lineResolved", line.LineId + (rowIsInReturn ? " is in the return" : " is not in the return") + (string.IsNullOrWhiteSpace(note) ? "" : ": " + note));
            });
        }

        // =================================================================
        // Agent-side mutations (internal: ProposalService / ProposalTools)
        // =================================================================

        internal Proposal Create(ProposalContext context, string clientNumber, string title, string? notes, string author)
        {
            Directory.CreateDirectory(DirectoryPath);
            EnsureReadme();
            var now = Clock();
            var p = new Proposal
            {
                Id = NewId(),
                CreatedAt = now,
                UpdatedAt = now,
                Author = author,
                Context = context,
                ClientNumber = clientNumber,
                Title = title,
                Notes = notes,
                Status = ProposalStatus.Draft,
                Version = 1,
            };
            AddEvent(p, author, ChannelMcp, "created", null);
            using (AcquireLock(p.Id))
            {
                WriteFile(p);
            }
            return p;
        }

        /// <summary>
        /// An agent content edit. Allowed in Draft and ReturnedForChanges; in Approved
        /// it revokes the approval and returns the proposal to Draft (a person must
        /// approve the new version). Bumps the version.
        /// </summary>
        internal Proposal EditContent(string id, string author, string what, Action<Proposal> edit)
        {
            return Mutate(id, p =>
            {
                if (!IsAgentEditable(p.Status))
                {
                    throw Conflict(p, "Agents can change a proposal only while it is Draft or ReturnedForChanges (or Approved, which revokes the approval); this one is "
                        + p.Status + ". " + (p.Status == ProposalStatus.Submitted
                            ? "It is waiting for the operator's review: ask them to return it for changes."
                            : "Create a new proposal for further changes."));
                }
                if (p.Status == ProposalStatus.Approved)
                {
                    p.Approval = null;
                    p.Status = ProposalStatus.Draft;
                    AddEvent(p, author, ChannelMcp, "approvalRevoked", "Changed after approval (" + what + "); a person must approve the new version.");
                }
                edit(p);
                p.Version++;
                AddEvent(p, author, ChannelMcp, "edited", what);
            });
        }

        internal Proposal Submit(string id, string author)
        {
            return Mutate(id, p =>
            {
                RequireStatus(p, "submit", ProposalStatus.Draft, ProposalStatus.ReturnedForChanges);
                if (p.Lines.Count == 0 && p.ManualItems.Count == 0)
                {
                    throw Conflict(p, "The proposal has no lines and no manual items; add what you found before submitting.");
                }
                p.Status = ProposalStatus.Submitted;
                AddEvent(p, author, ChannelMcp, "submitted", null);
            });
        }

        /// <summary>Low-level locked read-modify-write (commit bookkeeping). The mutation runs under the proposal's lock.</summary>
        internal Proposal Mutate(string id, Action<Proposal> mutate)
        {
            if (!IsValidId(id)) throw NotFound(id);
            using (AcquireLock(id))
            {
                var path = PathFor(id);
                if (!File.Exists(path)) throw NotFound(id);
                var p = ReadFile(path) ?? throw NotFound(id);
                mutate(p);
                p.UpdatedAt = Clock();
                WriteFile(p);
                return p;
            }
        }

        // =================================================================
        // Rules shared with ProposalService
        // =================================================================

        internal static bool IsAgentEditable(ProposalStatus s)
            => s == ProposalStatus.Draft || s == ProposalStatus.ReturnedForChanges || s == ProposalStatus.Approved;

        /// <summary>No commit result, Failed, Skipped or Running (interrupted) — the next commit runs it.</summary>
        internal static bool IsUnfinished(ProposalLine l)
            => l.Commit == null
               || l.Commit.Status == LineCommitStatus.Failed
               || l.Commit.Status == LineCommitStatus.Skipped
               || l.Commit.Status == LineCommitStatus.Running;

        internal static List<string> ApproveBlockers(Proposal p)
        {
            var b = new List<string>();
            if (p.Status != ProposalStatus.Submitted) b.Add("Only a Submitted proposal can be approved (this one is " + p.Status + ").");
            var pending = p.Lines.Where(l => l.ReviewerDecision == ReviewerDecision.Pending).Select(l => l.LineId).ToList();
            if (pending.Count > 0) b.Add("Every line needs a decision; still pending: " + string.Join(", ", pending) + ".");
            if (!p.Lines.Any(l => l.ReviewerDecision == ReviewerDecision.Approved)) b.Add("At least one line must be approved.");
            var badApproved = p.Lines.Where(l => l.ReviewerDecision == ReviewerDecision.Approved && !l.Preview.Ok).Select(l => l.LineId).ToList();
            if (badApproved.Count > 0) b.Add("Lines that failed their dry run can't be approved: " + string.Join(", ", badApproved) + ".");
            return b;
        }

        private static List<string> CommitBlockers(Proposal p, ProposalState state)
        {
            var b = new List<string>();
            if (p.Status != ProposalStatus.Approved && p.Status != ProposalStatus.PartiallyCommitted
                && p.Status != ProposalStatus.CommitFailed)
            {
                b.Add("Only an Approved (or PartiallyCommitted / CommitFailed) proposal can be committed (this one is " + p.Status + ").");
            }
            if (p.Approval == null) b.Add("There is no approval.");
            else if (!state.ApprovalCurrent) b.Add("The proposal changed after it was approved (version or content hash differs); it must be approved again.");
            if (p.Approval != null && state.ApprovalCurrent && state.UnfinishedApprovedLineCount == 0)
            {
                b.Add(state.UncertainLineIds.Count > 0
                    ? "Nothing left to write except lines that may already be in the return (" + string.Join(", ", state.UncertainLineIds) + "): check them in Lacerte and resolve them."
                    : "Every approved line is already written.");
            }
            return b;
        }

        /// <summary>Status after a commit pass from the approved lines' results.</summary>
        internal static ProposalStatus FinalStatus(Proposal p)
        {
            var approval = p.Approval;
            if (approval == null) return p.Status;
            var approved = p.Lines.Where(l => approval.LineDecisions.TryGetValue(l.LineId, out var d) && d == ReviewerDecision.Approved).ToList();
            if (approved.Count == 0) return p.Status;
            if (approved.All(l => l.Commit != null
                    && (l.Commit.Status == LineCommitStatus.Succeeded || l.Commit.Status == LineCommitStatus.SucceededUnverified)))
                return ProposalStatus.Committed;
            if (approved.Any(l => l.Commit != null && IsDoneOrMaybe(l.Commit.Status)))
                return ProposalStatus.PartiallyCommitted;
            if (approved.All(l => l.Commit == null)) return ProposalStatus.Approved;
            return ProposalStatus.CommitFailed;
        }

        private static bool IsDoneOrMaybe(LineCommitStatus s)
            => s == LineCommitStatus.Succeeded || s == LineCommitStatus.SucceededUnverified || s == LineCommitStatus.Uncertain;

        /// <summary>True when another live process (or this one) holds the commit lease.</summary>
        internal static bool CommitInProgressElsewhere(Proposal p)
        {
            var lease = p.CommitLease;
            if (lease == null || lease.FinishedAt != null) return false;
            if (lease.ProcessId == Process.GetCurrentProcess().Id) return ProposalService.IsCommitActiveInProcess(p.Id);
            try
            {
                using var other = Process.GetProcessById(lease.ProcessId);
                return !other.HasExited;
            }
            catch (ArgumentException)
            {
                return false; // no such process: the commit was interrupted
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // =================================================================
        // Helpers
        // =================================================================

        public static bool IsValidId(string? id) => id != null && s_idShape.IsMatch(id);

        internal ProposalLine FindLine(Proposal p, string lineId)
        {
            var line = p.Lines.FirstOrDefault(l => string.Equals(l.LineId, (lineId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (line == null)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, new Dictionary<string, object?>
                {
                    ["argument"] = "lineId",
                    ["value"] = lineId,
                    ["proposalId"] = p.Id,
                    ["reason"] = "The proposal has no line with this id. Lines: " + string.Join(", ", p.Lines.Select(l => l.LineId)) + ".",
                });
            }
            return line;
        }

        internal static LacerteSDKError Conflict(Proposal p, string reason)
            => new LacerteSDKError(SyntheticStatusCodes.ProposalConflict, new Dictionary<string, object?>
            {
                ["proposalId"] = p.Id,
                ["status"] = p.Status.ToString(),
                ["version"] = p.Version,
                ["reason"] = reason,
            });

        internal static LacerteSDKError NotFound(string? id)
            => new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, new Dictionary<string, object?>
            {
                ["argument"] = "proposalId",
                ["value"] = id,
                ["reason"] = "No proposal with this id. List them with lacerte_proposal_list.",
            });

        private static void RequireStatus(Proposal p, string action, params ProposalStatus[] allowed)
        {
            if (Enumerable.Contains(allowed, p.Status)) return;
            throw Conflict(p, "Can't " + action + " a proposal that is " + p.Status + " (allowed: " + string.Join(", ", allowed) + ").");
        }

        private static void RequireVersion(Proposal p, int expectedVersion)
        {
            if (expectedVersion == p.Version) return;
            throw Conflict(p, "The proposal is at version " + p.Version + ", not " + expectedVersion
                + ": it changed since you loaded it. Reload and review the current version.");
        }

        private static void RequireHumanChannel(string channel)
        {
            if (string.Equals(channel, ChannelControlPage, StringComparison.Ordinal)
                || string.Equals(channel, ChannelCli, StringComparison.Ordinal)) return;
            throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, new Dictionary<string, object?>
            {
                ["argument"] = "channel",
                ["value"] = channel,
                ["reason"] = "Approvals come only from a person: channel must be \"control-page\" or \"cli\".",
            });
        }

        private static string RequireBy(string by)
        {
            var t = (by ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, new Dictionary<string, object?>
                {
                    ["argument"] = "by",
                    ["reason"] = "Say who is reviewing (a person's name or user@machine).",
                });
            }
            return t.Length > 200 ? t.Substring(0, 200) : t;
        }

        internal static string? Trim(string? s, int max)
        {
            if (s == null) return null;
            var t = s.Trim();
            if (t.Length == 0) return null;
            return t.Length > max ? t.Substring(0, max) : t;
        }

        private void Touch(Proposal p) => p.UpdatedAt = Clock();

        internal ProposalEvent AddEvent(Proposal p, string by, string channel, string action, string? note)
        {
            var e = new ProposalEvent
            {
                At = Clock(),
                By = by,
                Channel = channel,
                Action = action,
                Version = p.Version,
                Note = Trim(note, 2000),
            };
            p.History.Add(e);
            return e;
        }

        private static bool Matches(Proposal p, ProposalFilter? f)
        {
            if (f == null) return true;
            if (!string.IsNullOrWhiteSpace(f.ClientNumber)
                && !string.Equals(p.ClientNumber, f.ClientNumber!.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (f.Status.HasValue && p.Status != f.Status.Value) return false;
            if (!string.IsNullOrWhiteSpace(f.DataPath)
                && !string.Equals(p.Context.DataPath, f.DataPath!.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (f.TaxYear.HasValue && p.Context.TaxYear != f.TaxYear.Value) return false;
            if (!string.IsNullOrWhiteSpace(f.Module)
                && !string.Equals(p.Context.Module, f.Module!.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static ProposalSummary Summarize(Proposal p) => new ProposalSummary
        {
            Id = p.Id,
            ClientNumber = p.ClientNumber,
            Title = p.Title,
            Status = p.Status,
            Version = p.Version,
            Author = p.Author,
            Context = p.Context,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            LineCount = p.Lines.Count,
            InvalidLineCount = p.Lines.Count(l => !l.Preview.Ok),
            PendingLineCount = p.Lines.Count(l => l.ReviewerDecision == ReviewerDecision.Pending),
            ApprovedLineCount = p.Lines.Count(l => l.ReviewerDecision == ReviewerDecision.Approved),
            RejectedLineCount = p.Lines.Count(l => l.ReviewerDecision == ReviewerDecision.Rejected),
            ManualItemCount = p.ManualItems.Count,
            QuestionCount = p.Questions.Count,
        };

        private string PathFor(string id) => Path.Combine(DirectoryPath, id + ".json");

        private static string NewId()
        {
            var bytes = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(24);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static Proposal? ReadFile(string path)
        {
            // FileShare.ReadWrite|Delete: the writer replaces the file atomically;
            // a reader never blocks it.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return JsonSerializer.Deserialize<Proposal>(fs, JsonOptions);
                }
                catch (IOException) when (attempt < 5 && File.Exists(path))
                {
                    Thread.Sleep(20);
                }
            }
        }

        private void WriteFile(Proposal p)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = PathFor(p.Id);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(p, JsonOptions), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void EnsureReadme()
        {
            try
            {
                var readme = Path.Combine(DirectoryPath, "README.txt");
                if (!File.Exists(readme)) File.WriteAllText(readme, ReadmeText, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>In-process semaphore + exclusive lock file; held for one read-modify-write.</summary>
        private IDisposable AcquireLock(string id)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = PathFor(id);
            var sem = s_locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            var deadline = DateTime.UtcNow + LockTimeout;
            if (!sem.Wait(LockTimeout)) throw Busy(id);
            try
            {
                var lockPath = Path.Combine(DirectoryPath, id + ".lock");
                while (true)
                {
                    try
                    {
                        var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                        return new LockHandle(sem, fs);
                    }
                    catch (IOException) when (DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(25);
                    }
                    catch (IOException)
                    {
                        throw Busy(id);
                    }
                }
            }
            catch
            {
                sem.Release();
                throw;
            }
        }

        private static LacerteSDKError Busy(string id)
            => new LacerteSDKError(SyntheticStatusCodes.ProposalConflict, new Dictionary<string, object?>
            {
                ["proposalId"] = id,
                ["reason"] = "Another process is updating this proposal right now (its .lock file is held). Try again in a moment.",
            });

        private sealed class LockHandle : IDisposable
        {
            private SemaphoreSlim? _sem;
            private FileStream? _fs;
            public LockHandle(SemaphoreSlim sem, FileStream fs) { _sem = sem; _fs = fs; }
            public void Dispose()
            {
                var fs = Interlocked.Exchange(ref _fs, null);
                fs?.Dispose();
                Interlocked.Exchange(ref _sem, null)?.Release();
            }
        }

        private static JsonSerializerOptions CreateJsonOptions(bool indented)
        {
            var o = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = null,
                WriteIndented = indented,
                PropertyNameCaseInsensitive = true,
            };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }

        private static readonly JsonSerializerOptions CanonicalJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
        };
    }
}
