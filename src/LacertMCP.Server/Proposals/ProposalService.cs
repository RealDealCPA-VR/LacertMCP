using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using LacertMCP.Server.Tools.Internal;

namespace LacertMCP.Server.Proposals
{
    /// <summary>
    /// Proposal operations that need the Lacerte session: creating a proposal in
    /// the caller's context, dry-run validating lines through the real write
    /// chokepoints, and committing an approved proposal.
    ///
    /// <para>Every dry run and every commit write runs in a FRESH
    /// <see cref="SessionScope"/> whose context is the proposal's own (dataPath,
    /// taxYear, module), set through <see cref="LacerteSessionManager.SetContext"/>
    /// so its path validation applies. The caller's context is untouched, the
    /// caller's device read-only flag carries over, and in HTTP mode the write
    /// never runs scope-less (fail-closed rule).</para>
    ///
    /// <para><see cref="CommitAsync"/> is public: the MCP tool and the control page
    /// both call it. It writes only when the proposal is approved and its content
    /// hash and version still equal the approval's, and only the lines the
    /// approval approved, through WriteField (+ read-back) and
    /// WriteDetailWithOutcome (dictionary gate, backup, journal, read-back) with
    /// idempotency keys derived from the proposal, version and line.</para>
    /// </summary>
    public sealed class ProposalService
    {
        public const int MaxLinesPerCall = 200;

        private static readonly ConcurrentDictionary<string, byte> s_activeCommits =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private readonly LacerteSessionManager _session;

        public ProposalService(LacerteSessionManager session, ProposalStore store)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public ProposalStore Store { get; }

        internal static bool IsCommitActiveInProcess(string id) => s_activeCommits.ContainsKey(id);

        /// <summary>The connection's device name, or "stdio" outside HTTP mode.</summary>
        internal static string CurrentAuthor()
        {
            var name = SessionScope.Current?.DeviceName;
            return string.IsNullOrWhiteSpace(name) ? "stdio" : name!.Trim();
        }

        /// <summary>
        /// Agent create / edit / commit need write access: a read-only device (or, in
        /// HTTP mode, a call with no connection scope) is refused with 9001.
        /// </summary>
        internal static void RequireAgentWriteAccess(string op)
        {
            var scope = SessionScope.Current;
            if (scope?.ReadOnlyAccess != true && !(scope == null && SessionScope.Required)) return;
            throw new LacerteSDKError(SyntheticStatusCodes.FieldIsReadOnly, new Dictionary<string, object?>
            {
                ["operation"] = op,
                ["reason"] = "This device has read-only access: it can read proposals (lacerte_proposal_get, lacerte_proposal_list) but not create, change or commit them.",
            });
        }

        // =================================================================
        // Agent operations
        // =================================================================

        internal async Task<Proposal> CreateAsync(string clientNumber, string? title, string? notes, CancellationToken ct)
        {
            RequireAgentWriteAccess("ProposalCreate");
            var ctx = _session.Context ?? throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
            var client = (clientNumber ?? string.Empty).Trim();
            if (client.Length == 0) throw Arg("clientNumber", clientNumber, "clientNumber is required.");
            var row = await _session.ReadClient(client, ct).ConfigureAwait(false);
            if (row == null)
            {
                throw Arg("clientNumber", client, "No client with this number in the active context (" + ctx.DataPath + ", " + ctx.Year
                    + ", module " + ctx.Module + "). Find it with lacerte_client_find, or set the right context first.");
            }
            var context = new ProposalContext
            {
                DataPath = ctx.DataPath,
                TaxYear = ctx.Year,
                Module = ctx.Module.ToString(),
            };
            var t = ProposalStore.Trim(title, 200) ?? (client + " " + ctx.Year.ToString(CultureInfo.InvariantCulture) + " (module " + ctx.Module + ")");
            return Store.Create(context, client, t, ProposalStore.Trim(notes, 4000), CurrentAuthor());
        }

        internal async Task<(Proposal Proposal, IReadOnlyList<string> LineIds)> AddLinesAsync(
            string proposalId, IReadOnlyList<ProposalLineInput>? inputs, CancellationToken ct)
        {
            RequireAgentWriteAccess("ProposalAddLines");
            var p = Store.GetRequired(proposalId);
            RequireEditable(p);
            if (inputs == null || inputs.Count == 0) throw Arg("lines", null, "Pass at least one line.");
            if (inputs.Count > MaxLinesPerCall) throw Arg("lines", inputs.Count, "At most " + MaxLinesPerCall + " lines per call; split them.");
            if (p.Lines.Count + inputs.Count > ProposalStore.MaxLinesPerProposal)
                throw Arg("lines", inputs.Count, "A proposal holds at most " + ProposalStore.MaxLinesPerProposal + " lines.");

            var built = new List<ProposalLine>(inputs.Count);
            for (int i = 0; i < inputs.Count; i++) built.Add(BuildLine(inputs[i], "lines[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
            foreach (var line in built) await ValidateLineAsync(p, line, ct).ConfigureAwait(false);

            var ids = new List<string>();
            var updated = Store.EditContent(p.Id, CurrentAuthor(), "added " + built.Count + " line(s)", q =>
            {
                if (q.Lines.Count + built.Count > ProposalStore.MaxLinesPerProposal)
                    throw ProposalStore.Conflict(q, "A proposal holds at most " + ProposalStore.MaxLinesPerProposal + " lines.");
                foreach (var line in built)
                {
                    line.LineId = "L" + q.NextLineNumber.ToString(CultureInfo.InvariantCulture);
                    q.NextLineNumber++;
                    q.Lines.Add(line);
                    ids.Add(line.LineId);
                }
            });
            return (updated, ids);
        }

        internal async Task<Proposal> UpdateLineAsync(string proposalId, string lineId, ProposalLineInput? input, CancellationToken ct)
        {
            RequireAgentWriteAccess("ProposalUpdateLine");
            var p = Store.GetRequired(proposalId);
            RequireEditable(p);
            var existing = Store.FindLine(p, lineId);
            if (input == null) throw Arg("line", null, "Pass the line's new content.");
            var line = BuildLine(input, "line");
            line.LineId = existing.LineId;
            await ValidateLineAsync(p, line, ct).ConfigureAwait(false);
            return Store.EditContent(p.Id, CurrentAuthor(), "changed line " + existing.LineId, q =>
            {
                var idx = q.Lines.FindIndex(l => string.Equals(l.LineId, existing.LineId, StringComparison.Ordinal));
                if (idx < 0) throw ProposalStore.Conflict(q, "Line " + existing.LineId + " was removed meanwhile.");
                q.Lines[idx] = line; // decision back to Pending: the reviewer decides the new content
            });
        }

        internal Proposal RemoveLine(string proposalId, string lineId)
        {
            RequireAgentWriteAccess("ProposalRemoveLine");
            var p = Store.GetRequired(proposalId);
            var existing = Store.FindLine(p, lineId);
            return Store.EditContent(p.Id, CurrentAuthor(), "removed line " + existing.LineId, q =>
            {
                q.Lines.RemoveAll(l => string.Equals(l.LineId, existing.LineId, StringComparison.Ordinal));
                foreach (var question in q.Questions) question.LineIds?.RemoveAll(id => string.Equals(id, existing.LineId, StringComparison.Ordinal));
            });
        }

        internal Proposal AddManualItems(string proposalId, IReadOnlyList<ManualItemInput>? items)
        {
            RequireAgentWriteAccess("ProposalAddManualItems");
            if (items == null || items.Count == 0) throw Arg("items", null, "Pass at least one item.");
            var built = new List<ManualItem>();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i] ?? throw Arg("items[" + i + "]", null, "Item is null.");
                var desc = ProposalStore.Trim(it.Description, 1000) ?? throw Arg("items[" + i + "].description", null, "description is required.");
                var reason = ProposalStore.Trim(it.Reason, 2000) ?? throw Arg("items[" + i + "].reason", null, "reason is required (why it can't be entered automatically).");
                built.Add(new ManualItem
                {
                    Description = desc,
                    Amount = it.Amount,
                    Source = it.Source == null ? null : BuildSource(it.Source, "items[" + i + "].source", required: false),
                    Reason = reason,
                });
            }
            return Store.EditContent(proposalId, CurrentAuthor(), "added " + built.Count + " manual item(s)", q =>
            {
                if (q.ManualItems.Count + built.Count > ProposalStore.MaxManualItemsPerProposal)
                    throw ProposalStore.Conflict(q, "A proposal holds at most " + ProposalStore.MaxManualItemsPerProposal + " manual items.");
                foreach (var m in built)
                {
                    m.ItemId = "M" + q.NextItemNumber.ToString(CultureInfo.InvariantCulture);
                    q.NextItemNumber++;
                    q.ManualItems.Add(m);
                }
            });
        }

        internal Proposal AddQuestions(string proposalId, IReadOnlyList<QuestionInput>? questions)
        {
            RequireAgentWriteAccess("ProposalAddQuestions");
            if (questions == null || questions.Count == 0) throw Arg("questions", null, "Pass at least one question.");
            var built = new List<ProposalQuestion>();
            for (int i = 0; i < questions.Count; i++)
            {
                var qi = questions[i] ?? throw Arg("questions[" + i + "]", null, "Question is null.");
                var text = ProposalStore.Trim(qi.Text, 2000) ?? throw Arg("questions[" + i + "].text", null, "text is required.");
                built.Add(new ProposalQuestion
                {
                    Text = text,
                    LineIds = qi.LineIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList(),
                });
            }
            return Store.EditContent(proposalId, CurrentAuthor(), "added " + built.Count + " question(s)", q =>
            {
                if (q.Questions.Count + built.Count > ProposalStore.MaxQuestionsPerProposal)
                    throw ProposalStore.Conflict(q, "A proposal holds at most " + ProposalStore.MaxQuestionsPerProposal + " questions.");
                foreach (var question in built)
                {
                    var unknown = question.LineIds?.Where(id => !q.Lines.Any(l => l.LineId == id)).ToList();
                    if (unknown != null && unknown.Count > 0)
                        throw Arg("lineIds", string.Join(",", unknown), "No such line(s) in the proposal.");
                    question.QuestionId = "Q" + q.NextQuestionNumber.ToString(CultureInfo.InvariantCulture);
                    q.NextQuestionNumber++;
                    q.Questions.Add(question);
                }
            });
        }

        internal Proposal Submit(string proposalId)
        {
            RequireAgentWriteAccess("ProposalSubmit");
            return Store.Submit(proposalId, CurrentAuthor());
        }

        private static void RequireEditable(Proposal p)
        {
            if (ProposalStore.IsAgentEditable(p.Status)) return;
            throw ProposalStore.Conflict(p, "Agents can change a proposal only while it is Draft or ReturnedForChanges; this one is " + p.Status
                + (p.Status == ProposalStatus.Submitted ? ". It is waiting for the operator's review: ask them to return it for changes." : "."));
        }

        // =================================================================
        // Line building + dry-run validation
        // =================================================================

        private static ProposalLine BuildLine(ProposalLineInput? input, string at)
        {
            if (input == null) throw Arg(at, null, "Line is null.");
            var kindText = (input.Kind ?? string.Empty).Trim();
            ProposalLineKind kind;
            if (string.Equals(kindText, "field", StringComparison.OrdinalIgnoreCase)) kind = ProposalLineKind.Field;
            else if (string.Equals(kindText, "detail", StringComparison.OrdinalIgnoreCase)) kind = ProposalLineKind.Detail;
            else throw Arg(at + ".kind", input.Kind, "kind must be \"field\" or \"detail\".");

            if (!Enum.TryParse<ProposalConfidence>((input.Confidence ?? string.Empty).Trim(), ignoreCase: true, out var confidence)
                || !Enum.IsDefined(typeof(ProposalConfidence), confidence))
            {
                throw Arg(at + ".confidence", input.Confidence, "confidence must be \"high\", \"medium\" or \"low\".");
            }

            var line = new ProposalLine
            {
                Kind = kind,
                DictionaryId = ProposalStore.Trim(input.DictionaryId, 200),
                Source = BuildSource(input.Source, at + ".source", required: true)!,
                Reasoning = ProposalStore.Trim(input.Reasoning, 4000),
                Confidence = confidence,
                ReviewerDecision = ReviewerDecision.Pending,
            };

            if (kind == ProposalLineKind.Field)
            {
                if (input.Detail != null) throw Arg(at + ".detail", null, "A field line has no detail row; use kind \"detail\" for detail rows.");
                if (!input.Table.HasValue) throw Arg(at + ".table", null, "Field lines need table (1..5).");
                var field = (input.Field ?? string.Empty).Trim();
                if (field.Length == 0) throw Arg(at + ".field", input.Field, "Field lines need field (e.g. C1_15).");
                line.Target = new ProposalTarget
                {
                    Table = input.Table,
                    Field = field,
                    SubClientId = input.SubClientId,
                    StateAbbrev = string.IsNullOrWhiteSpace(input.StateAbbrev) ? null : input.StateAbbrev!.Trim().ToUpperInvariant(),
                };
                line.Value = ToJson(input.Value);
            }
            else
            {
                if (input.Field != null || input.Table.HasValue || input.Value != null)
                    throw Arg(at, null, "A detail line takes its amount and codes in detail{...}; table / field / value are for field lines.");
                var d = input.Detail ?? throw Arg(at + ".detail", null, "Detail lines need detail {series, code, amount, description, suffix, ...}.");
                line.Target = new ProposalTarget
                {
                    Detail = new ProposalDetailRow
                    {
                        Series = (d.Series ?? string.Empty).Trim(),
                        Code = (d.Code ?? string.Empty).Trim(),
                        Amount = d.Amount,
                        Description = d.Description ?? string.Empty,
                        Prefix = (d.Prefix ?? string.Empty).Trim(),
                        Suffix = (d.Suffix ?? string.Empty).Trim(),
                        StateAbbrev = (d.StateAbbrev ?? string.Empty).Trim(),
                        CityAbbr = d.CityAbbr ?? string.Empty,
                        Source = d.Source ?? string.Empty,
                        Tsj = d.Tsj ?? string.Empty,
                        ImportSrc = d.ImportSrc ?? string.Empty,
                    },
                };
            }
            return line;
        }

        private static SourceCitation? BuildSource(SourceCitationInput? s, string at, bool required)
        {
            if (s == null)
            {
                if (required) throw Arg(at, null, "Every line needs a source citation: {document, page?, location?, excerpt?}.");
                return null;
            }
            var doc = ProposalStore.Trim(s.Document, 300);
            if (doc == null) throw Arg(at + ".document", s.Document, "source.document is required (the document the value came from).");
            if (s.Page.HasValue && s.Page.Value < 0) throw Arg(at + ".page", s.Page, "page must be positive.");
            return new SourceCitation
            {
                Document = doc,
                Page = s.Page,
                Location = ProposalStore.Trim(s.Location, 200),
                Excerpt = ProposalStore.Trim(s.Excerpt, 1000),
            };
        }

        private static JsonElement? ToJson(object? value)
        {
            if (value == null) return null;
            if (value is JsonElement e) return e.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : e.Clone();
            return JsonSerializer.SerializeToElement(value);
        }

        private static object? FromJson(JsonElement? value) => value.HasValue ? (object)value.Value : null;

        internal static DetailRow ToDetailRow(ProposalDetailRow d)
            => new DetailRow(d.Series, d.Prefix, d.Code, d.Suffix, d.StateAbbrev, d.CityAbbr, d.Source, d.Tsj, d.ImportSrc, d.Amount, d.Description);

        /// <summary>Dictionary cross-check + dry run through the real chokepoint; sets Preview and Warnings.</summary>
        private async Task ValidateLineAsync(Proposal p, ProposalLine line, CancellationToken ct)
        {
            line.Warnings = new List<string>();
            var dictionaryProblem = CheckDictionary(p, line, line.Warnings);
            var now = Store.Clock();
            if (dictionaryProblem != null)
            {
                line.Preview = new LinePreview
                {
                    Ok = false,
                    StatusCode = SyntheticStatusCodes.InvalidArgument,
                    StatusName = "InvalidArgument",
                    Message = dictionaryProblem,
                    CheckedAt = now,
                };
                return;
            }
            try
            {
                var note = await InProposalScope(p, async ctx =>
                {
                    var t = line.Target;
                    if (line.Kind == ProposalLineKind.Field)
                    {
                        await _session.WriteField(t.Table!.Value, p.ClientNumber, t.Field!, FromJson(line.Value),
                            t.SubClientId, t.StateAbbrev, dryRun: true, idempotencyKey: null, ct, expectedContext: ctx).ConfigureAwait(false);
                        return "Dry run passed: the field is writable and the value is valid.";
                    }
                    var d = t.Detail!;
                    DetailsTools.ValidateDetailWrite(d.Series, d.Code, d.StateAbbrev, d.Tsj);
                    var outcome = await _session.WriteDetailWithOutcome(p.ClientNumber, ToDetailRow(d), null,
                        dryRun: true, idempotencyKey: null, ct, expectedContext: ctx).ConfigureAwait(false);
                    return outcome.VerificationNote ?? "Dry run passed.";
                }).ConfigureAwait(false);
                line.Preview = new LinePreview
                {
                    Ok = true,
                    StatusCode = StatusCodes.Success,
                    StatusName = StatusCodes.GetStatusInfo(StatusCodes.Success).Name,
                    Message = note,
                    CheckedAt = now,
                };
            }
            catch (LacerteSDKError ex)
            {
                line.Preview = new LinePreview
                {
                    Ok = false,
                    StatusCode = ex.StatusCode,
                    StatusName = ex.StatusName,
                    Message = Describe(ex),
                    CheckedAt = now,
                };
            }
        }

        /// <summary>Warnings into <paramref name="warnings"/>; returns a blocking problem (target contradicts the named entry) or null.</summary>
        private static string? CheckDictionary(Proposal p, ProposalLine line, List<string> warnings)
        {
            if (line.DictionaryId == null)
            {
                warnings.Add("No dictionaryId: the reviewer can't see which verified field-dictionary mapping this line relies on.");
                return null;
            }
            var module = string.IsNullOrEmpty(p.Context.Module) ? ' ' : p.Context.Module[0];
            IReadOnlyList<FieldDictionaryEntry> entries;
            try
            {
                entries = FieldDictionaryLoader.GetEntries(module);
            }
            catch (Exception)
            {
                entries = Array.Empty<FieldDictionaryEntry>();
            }
            var entry = entries.FirstOrDefault(e => string.Equals(e.Id, line.DictionaryId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                warnings.Add("Dictionary id '" + line.DictionaryId + "' is not in the verified field dictionary for module " + p.Context.Module + ".");
                return null;
            }
            if (!entry.AppliesToYear(p.Context.TaxYear))
            {
                warnings.Add("Dictionary entry '" + entry.Id + "' was not verified for tax year " + p.Context.TaxYear + ".");
            }
            var t = line.Target;
            bool matches;
            if (line.Kind == ProposalLineKind.Field)
            {
                matches = entry.IsField && entry.Table == t.Table && string.Equals(entry.Field, t.Field, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var d = t.Detail!;
                matches = !entry.IsField
                    && entry.Series == ParseInt(d.Series) && entry.Code == ParseInt(d.Code)
                    && (!entry.Prefix.HasValue || entry.Prefix == ParseInt(d.Prefix))
                    && (!entry.Suffix.HasValue || entry.Suffix == ParseInt(d.Suffix));
            }
            return matches
                ? null
                : "The target doesn't match dictionary entry '" + entry.Id + "' (" + entry.DescribeTarget() + "). Use the entry's exact target, or the right dictionary id.";
        }

        private static int? ParseInt(string? s)
            => int.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;

        private static string Describe(LacerteSDKError ex)
        {
            var reason = ex.Context != null && ex.Context.TryGetValue("reason", out var r) ? r as string : null;
            return string.IsNullOrWhiteSpace(reason) ? ex.UserMessage : ex.UserMessage + " " + reason;
        }

        /// <summary>Runs <paramref name="body"/> in a fresh scope whose context is the proposal's (see class remarks).</summary>
        private async Task<T> InProposalScope<T>(Proposal p, Func<SessionContext, Task<T>> body)
        {
            var caller = SessionScope.Current;
            var scope = new SessionScope
            {
                ReadOnlyAccess = caller?.ReadOnlyAccess ?? false,
                DeviceName = caller?.DeviceName ?? string.Empty,
            };
            SessionScope.Current = scope;
            try
            {
                var module = p.Context.Module ?? string.Empty;
                if (module.Length != 1) throw Arg("module", module, "The proposal's module is not a single letter.");
                _session.SetContext(p.Context.DataPath, p.Context.TaxYear, module[0]);
                var ctx = _session.Context ?? throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
                return await body(ctx).ConfigureAwait(false);
            }
            finally
            {
                SessionScope.Current = caller;
            }
        }

        // =================================================================
        // Commit
        // =================================================================

        /// <summary>
        /// Writes the approved lines of an approved, unchanged proposal. Allowed when
        /// the status is Approved, PartiallyCommitted or CommitFailed AND the stored
        /// approval's version and content hash equal the proposal's. Runs only
        /// approved lines that aren't finished (no result, Failed, Skipped), in order:
        /// field lines through WriteField + read-back (a failure is recorded and the
        /// commit continues); detail lines through WriteDetailWithOutcome (an error
        /// after which the row may be on the return — rowWritten yes/unknown, 9022 —
        /// marks the line Uncertain and STOPS the commit; the rest are Skipped).
        /// Idempotency keys are <c>proposal-{id}-v{version}-{lineId}</c> (plus
        /// <c>-g{n}</c> after a person resolved an Uncertain row as missing), so a
        /// re-commit never appends a detail row twice. Final status: Committed,
        /// PartiallyCommitted or CommitFailed.
        /// </summary>
        /// <param name="proposalId">The proposal.</param>
        /// <param name="committedBy">Who started the commit (the control page passes the reviewer); default: the connection's device name or "stdio".</param>
        /// <param name="cancellationToken">Stops between lines.</param>
        public async Task<ProposalCommitReport> CommitAsync(string proposalId, string? committedBy = null, CancellationToken cancellationToken = default)
        {
            if (SessionScope.Current?.ReadOnlyAccess == true) RequireAgentWriteAccess("ProposalCommit");
            var by = ProposalStore.Trim(committedBy, 200) ?? CurrentAuthor();
            if (!ProposalStore.IsValidId(proposalId)) throw ProposalStore.NotFound(proposalId);
            if (!s_activeCommits.TryAdd(proposalId, 0))
            {
                var current = Store.GetRequired(proposalId);
                throw ProposalStore.Conflict(current, "A commit of this proposal is already running.");
            }
            try
            {
                return await CommitCoreAsync(proposalId, by, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                s_activeCommits.TryRemove(proposalId, out _);
            }
        }

        private async Task<ProposalCommitReport> CommitCoreAsync(string proposalId, string by, CancellationToken ct)
        {
            var pid = Process.GetCurrentProcess().Id;
            var work = new List<string>();
            var alreadyDone = 0;
            var started = Store.Mutate(proposalId, p =>
            {
                if (p.Status == ProposalStatus.Committing)
                {
                    var lease = p.CommitLease;
                    if (lease != null && lease.FinishedAt == null && lease.ProcessId != pid && ProcessAlive(lease.ProcessId))
                        throw ProposalStore.Conflict(p, "Another server process (pid " + lease.ProcessId + ") is committing this proposal.");
                    RecoverInterrupted(p);
                    p.Status = ProposalStore.FinalStatus(p);
                    Store.AddEvent(p, by, ProposalStore.ChannelCommit, "commitRecovered", "An earlier commit was interrupted; lines it was writing were re-checked.");
                }
                if (p.Status != ProposalStatus.Approved && p.Status != ProposalStatus.PartiallyCommitted && p.Status != ProposalStatus.CommitFailed)
                {
                    throw ProposalStore.Conflict(p, "Only an Approved proposal (or a PartiallyCommitted / CommitFailed one, to finish it) can be committed; this one is " + p.Status
                        + (p.Status == ProposalStatus.Submitted ? ". It is waiting for the operator's approval." : "."));
                }
                var approval = p.Approval ?? throw ProposalStore.Conflict(p, "The proposal has no approval.");
                if (approval.Version != p.Version)
                    throw ProposalStore.Conflict(p, "The approval is for version " + approval.Version + " but the proposal is at version " + p.Version + "; it must be approved again.");
                var hash = ProposalStore.ComputeContentHash(p);
                if (!string.Equals(hash, approval.ContentHash, StringComparison.Ordinal))
                    throw ProposalStore.Conflict(p, "The proposal's content no longer matches what was approved (content hash differs); it must be reviewed and approved again.");

                foreach (var l in p.Lines)
                {
                    if (!approval.LineDecisions.TryGetValue(l.LineId, out var d) || d != ReviewerDecision.Approved) continue;
                    if (ProposalStore.IsUnfinished(l)) work.Add(l.LineId);
                    else alreadyDone++;
                }
                if (work.Count == 0)
                {
                    var state = ProposalStore.Evaluate(p);
                    throw ProposalStore.Conflict(p, state.UncertainLineIds.Count > 0
                        ? "Nothing left to write except lines that may already be in the return (" + string.Join(", ", state.UncertainLineIds)
                          + "): a person checks them in Lacerte and resolves them (control page or `lacerte-mcp proposal resolve`)."
                        : "Every approved line is already written.");
                }
                p.Status = ProposalStatus.Committing;
                p.CommitLease = new CommitLease { By = by, ProcessId = pid, StartedAt = Store.Clock() };
                Store.AddEvent(p, by, ProposalStore.ChannelCommit, "commitStarted", work.Count + " line(s) to write: " + string.Join(", ", work));
            });

            var report = new ProposalCommitReport
            {
                ProposalId = started.Id,
                Version = started.Version,
                AlreadyDoneCount = alreadyDone,
                ApprovedLineCount = started.Lines.Count(l => started.Approval!.LineDecisions.TryGetValue(l.LineId, out var d) && d == ReviewerDecision.Approved),
            };
            var ran = new HashSet<string>(StringComparer.Ordinal);
            string? stoppedAt = null;

            try
            {
                using (ProposalCommitScope.Enter(started.Id))
                {
                    foreach (var lineId in work)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            stoppedAt = "cancelled";
                            break;
                        }
                        var line = started.Lines.First(l => l.LineId == lineId);
                        var key = "proposal-" + started.Id + "-v" + started.Version.ToString(CultureInfo.InvariantCulture) + "-" + line.LineId
                            + (line.KeyGeneration > 0 ? "-g" + line.KeyGeneration.ToString(CultureInfo.InvariantCulture) : string.Empty);
                        var attempts = (line.Commit?.Attempts ?? 0) + 1;
                        Store.Mutate(started.Id, q =>
                        {
                            var l = q.Lines.First(x => x.LineId == lineId);
                            l.Commit = new LineCommitResult
                            {
                                Status = LineCommitStatus.Running,
                                IdempotencyKey = key,
                                At = Store.Clock(),
                                Attempts = attempts,
                            };
                        });
                        ran.Add(lineId);

                        var (result, stop) = await CommitLineAsync(started, line, key, ct).ConfigureAwait(false);
                        result.IdempotencyKey = key;
                        result.Attempts = attempts;
                        result.At = Store.Clock();
                        Store.Mutate(started.Id, q => q.Lines.First(x => x.LineId == lineId).Commit = result);
                        if (stop)
                        {
                            stoppedAt = lineId;
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Lines this pass didn't reach are Skipped (a re-commit runs them);
                // finish the lease and set the final status — also when a write threw
                // something unexpected, so the proposal never stays "Committing".
                var final = Store.Mutate(started.Id, q =>
                {
                    if (stoppedAt != null)
                    {
                        foreach (var lineId in work.Where(id => !ran.Contains(id)))
                        {
                            var l = q.Lines.First(x => x.LineId == lineId);
                            var prior = l.Commit;
                            l.Commit = new LineCommitResult
                            {
                                Status = LineCommitStatus.Skipped,
                                Note = stoppedAt == "cancelled"
                                    ? "Not run: the commit was cancelled."
                                    : "Not run: the commit stopped at line " + stoppedAt + " (a detail row may be on the return).",
                                At = Store.Clock(),
                                Attempts = prior?.Attempts ?? 0,
                                IdempotencyKey = prior?.IdempotencyKey,
                            };
                        }
                    }
                    RecoverInterrupted(q); // a write that threw out of CommitLineAsync
                    q.Status = ProposalStore.FinalStatus(q);
                    if (q.CommitLease != null) q.CommitLease.FinishedAt = Store.Clock();
                    Store.AddEvent(q, by, ProposalStore.ChannelCommit, "commitFinished", q.Status.ToString());
                });
                Fill(report, final, ran, stoppedAt);
            }
            return report;
        }

        /// <summary>Lines left "Running": a field is re-written next time (Failed); a detail row may have landed (Uncertain).</summary>
        private static void RecoverInterrupted(Proposal p)
        {
            foreach (var l in p.Lines)
            {
                if (l.Commit?.Status != LineCommitStatus.Running) continue;
                if (l.Kind == ProposalLineKind.Field)
                {
                    l.Commit.Status = LineCommitStatus.Failed;
                    l.Commit.Note = "The commit was interrupted while writing this field; the next commit writes it again.";
                }
                else
                {
                    l.Commit.Status = LineCommitStatus.Uncertain;
                    l.Commit.Note = "The commit was interrupted while writing this detail row: it may or may not be on the return. Check the client's detail rows in Lacerte and resolve the line.";
                }
            }
        }

        private async Task<(LineCommitResult Result, bool Stop)> CommitLineAsync(Proposal p, ProposalLine line, string key, CancellationToken ct)
        {
            var t = line.Target;
            try
            {
                return await InProposalScope(p, async ctx =>
                {
                    if (line.Kind == ProposalLineKind.Field)
                    {
                        var value = FromJson(line.Value);
                        var status = await _session.WriteField(t.Table!.Value, p.ClientNumber, t.Field!, value,
                            t.SubClientId, t.StateAbbrev, dryRun: false, idempotencyKey: key, ct, expectedContext: ctx).ConfigureAwait(false);
                        var v = await FieldWriteCore.VerifyOrThrow(_session, "WriteField", t.Table.Value, p.ClientNumber, t.Field!, value,
                            t.SubClientId, t.StateAbbrev, ct, key, ctx).ConfigureAwait(false);
                        return (new LineCommitResult
                        {
                            Status = v.Verified == true ? LineCommitStatus.Succeeded : LineCommitStatus.SucceededUnverified,
                            StatusCode = status,
                            StatusName = StatusCodes.GetStatusInfo(status).Name,
                            Verified = v.Verified,
                            Note = v.Note,
                        }, false);
                    }
                    var d = t.Detail!;
                    DetailsTools.ValidateDetailWrite(d.Series, d.Code, d.StateAbbrev, d.Tsj);
                    var outcome = await _session.WriteDetailWithOutcome(p.ClientNumber, ToDetailRow(d), null,
                        dryRun: false, idempotencyKey: key, ct, expectedContext: ctx).ConfigureAwait(false);
                    return (new LineCommitResult
                    {
                        Status = outcome.Verified == true ? LineCommitStatus.Succeeded : LineCommitStatus.SucceededUnverified,
                        StatusCode = outcome.Status,
                        StatusName = StatusCodes.GetStatusInfo(outcome.Status).Name,
                        Verified = outcome.Verified,
                        Note = outcome.VerificationNote,
                        Snapshot = outcome.Snapshot,
                    }, false);
                }).ConfigureAwait(false);
            }
            catch (LacerteSDKError ex)
            {
                var maybeLanded = line.Kind == ProposalLineKind.Detail && LacerteSessionManager.DetailRowMayHaveLanded(ex);
                var rowWritten = ex.Context != null && ex.Context.TryGetValue("rowWritten", out var w) ? w as string : null;
                return (new LineCommitResult
                {
                    Status = maybeLanded ? LineCommitStatus.Uncertain : LineCommitStatus.Failed,
                    StatusCode = ex.StatusCode,
                    StatusName = ex.StatusName,
                    Note = Describe(ex) + (maybeLanded
                        ? " The row may be on the return" + (rowWritten != null ? " (rowWritten: " + rowWritten + ")" : string.Empty)
                          + ": it is NOT retried. Check the client's detail rows in Lacerte and resolve the line; the commit stopped here."
                        : string.Empty),
                }, maybeLanded);
            }
            catch (OperationCanceledException)
            {
                if (line.Kind == ProposalLineKind.Field)
                {
                    return (new LineCommitResult { Status = LineCommitStatus.Failed, Note = "Cancelled before the write finished; the next commit writes it again." }, true);
                }
                return (new LineCommitResult
                {
                    Status = LineCommitStatus.Uncertain,
                    Note = "Cancelled while the detail row was being written: it may or may not be on the return. Check Lacerte and resolve the line.",
                }, true);
            }
        }

        private static void Fill(ProposalCommitReport report, Proposal final, HashSet<string> ran, string? stoppedAt)
        {
            report.Status = final.Status;
            report.StoppedEarly = stoppedAt != null;
            var approval = final.Approval;
            foreach (var l in final.Lines)
            {
                if (approval == null || !approval.LineDecisions.TryGetValue(l.LineId, out var d) || d != ReviewerDecision.Approved) continue;
                var didRun = ran.Contains(l.LineId);
                report.Lines.Add(new CommitLineReport { LineId = l.LineId, Kind = l.Kind, Ran = didRun, Result = l.Commit });
                if (!didRun)
                {
                    if (l.Commit?.Status == LineCommitStatus.Skipped) report.SkippedCount++;
                    continue;
                }
                report.RanCount++;
                switch (l.Commit?.Status)
                {
                    case LineCommitStatus.Succeeded: report.SucceededCount++; break;
                    case LineCommitStatus.SucceededUnverified: report.SucceededUnverifiedCount++; break;
                    case LineCommitStatus.Uncertain: report.UncertainCount++; break;
                    default: report.FailedCount++; break;
                }
            }
            report.Message = final.Status switch
            {
                ProposalStatus.Committed => "Every approved line is written. Lines with verified=null were accepted but not confirmed by a read-back: list them for the operator to check in Lacerte. Ask the operator to close and reopen the client in Lacerte.",
                ProposalStatus.PartiallyCommitted => report.UncertainCount > 0 || final.Lines.Any(l => l.Commit?.Status == LineCommitStatus.Uncertain)
                    ? "Some lines are written. At least one detail row may already be on the return after an error: it is not retried. Tell the operator which line, and that they must check it in Lacerte and resolve it. Other unfinished lines run on the next lacerte_proposal_commit."
                    : "Some lines are written; the failed / skipped ones run again on the next lacerte_proposal_commit once the cause is fixed (see each line's note).",
                ProposalStatus.CommitFailed => "Nothing was written. Fix the cause shown on each line (e.g. close the client in Lacerte) and call lacerte_proposal_commit again.",
                _ => "Commit finished with status " + final.Status + ".",
            };
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                using var other = Process.GetProcessById(pid);
                return !other.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static LacerteSDKError Arg(string argument, object? value, string reason)
            => new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, new Dictionary<string, object?>
            {
                ["argument"] = argument,
                ["value"] = value,
                ["reason"] = reason,
            });
    }
}
