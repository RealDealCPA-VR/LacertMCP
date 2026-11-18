using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// R6 gap #4: <c>lacerte_detail_write_bulk</c> — single tool call that
    /// fans out N detail-row writes through the existing
    /// <see cref="LacerteSessionManager.WriteDetail"/> chokepoint.
    ///
    /// <para>
    /// Intake seeds many W-2 / interest / dividend / dependent rows per
    /// client; the single-row <c>lacerte_detail_write</c> loop was the
    /// slowest leg of the intake prompt (audit-findings-r6.md "Missing
    /// tools" #4). This tool collapses those round-trips into one MCP call
    /// and aggregates per-row outcomes, mirroring the
    /// <see cref="FieldWriteBulkTools"/> bulk pattern (cap, dryRun,
    /// continueOnError, positional per-row idempotency keys, cancellation
    /// between rows, stoppedEarly/processedCount markers).
    /// </para>
    ///
    /// <para>
    /// Cap: shares <see cref="FieldWriteBulkTools.BulkWriteMaxEnvName"/>
    /// (LACERTE_BULK_WRITE_MAX, default 500) via
    /// <see cref="FieldWriteBulkTools.ResolveMaxBulkEntries"/> — detail-row
    /// appends are the same weight class as field writes, so the two bulk
    /// write tools deliberately share one ceiling instead of forking a
    /// second knob.
    /// </para>
    ///
    /// <para>
    /// <b>CRITICAL — per-row client-side validation runs BEFORE the session
    /// call</b>, mirroring <see cref="DetailsTools"/> exactly: the SDK does
    /// NOT validate detail-row shape (agent 06 §5) and a malformed row
    /// silently corrupts the return. Series numeric (^\d+$), Code integer
    /// (^-?\d+$), stateAbbrev 2-letter-or-empty, TSJ in {T,S,J,""} — all
    /// rejected with 9008 DetailWriteInvalid per row.
    /// </para>
    ///
    /// <para>
    /// Per-row idempotency key shape:
    /// <c>$"{idempotencyKey}-{i}-{clientNumber}-{series}-{code}"</c>. Keys
    /// are POSITIONAL — a retry must resubmit the same entries[] in the
    /// same order (R6-20a convention). This matters doubly for detail rows:
    /// an unkeyed transport retry APPENDS duplicates (detail files have no
    /// primary key), silently doubling W-2/1099 amounts.
    /// </para>
    ///
    /// <para>
    /// <b>Preview required</b> (filing-preparation decision 2): a real call
    /// (dryRun=false) commits only a batch that a dry run previewed. A dry run
    /// whose rows ALL pass returns a <c>previewToken</c> bound to the mode, the
    /// active context (dataPath, taxYear, module — the same context every row
    /// is pinned to, R7-25), a SHA-256 hash of the canonical entries exactly
    /// as they will be written (trimmed client numbers, TSJ/Source vocabulary
    /// normalized like the session does) and continueOnError. The commit must
    /// pass that token; a missing, unknown, expired, other-connection or
    /// mismatched token refuses the whole batch with 9017 before any row runs.
    /// Both modes (two-mode discipline). Tokens last 30 minutes and are
    /// reusable inside that window, so a half-failed commit can be resubmitted
    /// with the same idempotencyKey. See <see cref="BulkPreviewTokenStore"/>.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class DetailWriteBulkTools
    {
        // Validation regexes mirror DetailsTools (private there, so mirrored
        // here — the shapes ARE the contract per agent 06 §5 / P14-055).
        private static readonly Regex _rxStateAbbrev = new Regex(
            @"^[A-Z]{2}\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _rxNumeric = new Regex(
            @"^-?[0-9]+\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _rxNumericSeries = new Regex(
            @"^[0-9]+\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly LacerteSessionManager _session;

        public DetailWriteBulkTools(LacerteSessionManager session)
        {
            _session = session;
        }

        /// <summary>
        /// One entry in a bulk detail-write batch. Mirrors the single
        /// <c>lacerte_detail_write</c> parameter list: 5 required row fields
        /// (clientNumber, series, code, amount, description), the 7 optional
        /// row-key segments defaulting to empty string, and an optional
        /// replaceIndex (sim-only; live throws 9019 per P17-001).
        /// </summary>
        public sealed class BulkDetailEntry
        {
            [Description("Lacerte ClientNumber (the C{t}_0 identifier). Required.")]
            public string ClientNumber { get; set; } = string.Empty;

            [Description("Numeric Series identifier per Lacerte spec §2 — must match ^\\d+$ (e.g. '11'=W-2, '12'=Interest, '13'=Dividends, '14'=Dependents, '20'=Income, '30'=State, '40'=Partners). Required.")]
            public string Series { get; set; } = string.Empty;

            [Description("Numeric Code (must parse as integer; leading minus allowed). Required.")]
            public string Code { get; set; } = string.Empty;

            [Description("Dollar amount as a number (or null for non-numeric rows like dependent listings). SDK range: -999,999,999 to 999,999,999, at most 12 decimal places.")]
            public double? Amount { get; set; }

            [Description("Free-form description for the row. Required.")]
            public string Description { get; set; } = string.Empty;

            [Description("Prefix (SDK type Integer): blank or a whole number. Defaults to empty.")]
            public string Prefix { get; set; } = string.Empty;

            [Description("Suffix (SDK type Integer; the SDK notes standard fields start at 1000, suffixed fields at 1): blank or a whole number. Defaults to empty.")]
            public string Suffix { get; set; } = string.Empty;

            [Description("State abbreviation for the row's tax bucket — 2-letter postal code if non-empty (validated against ^[A-Z]{2}$). Defaults to empty. Maps to the DBF column StateAbbr internally.")]
            public string StateAbbrev { get; set; } = string.Empty;

            [Description("City abbreviation, at most 3 characters (SDK: Length=3). Defaults to empty.")]
            public string CityAbbr { get; set; } = string.Empty;

            [Description("SDK detail Source flag: must be blank (default), 'Nonsource' or 'Source' (SDK detail-file constraint). It is NOT a form tag — do not pass 'W2' / '1099INT'.")]
            public string Source { get; set; } = string.Empty;

            [Description("Taxpayer/Spouse/Joint indicator: Taxpayer, Spouse, Joint (or the aliases T, S, J), or empty (= Joint per the SDK). Stored as the SDK value Taxpayer/Spouse/Joint. Defaults to empty.")]
            public string Tsj { get; set; } = string.Empty;

            [Description("Read Only per the SDK detail-file schema — leave blank (any non-empty value is rejected with 9008).")]
            public string ImportSrc { get; set; } = string.Empty;

            [Description("Optional zero-based index to overwrite an existing row instead of appending. Sim-mode only — live mode rejects replaceIndex with 9019 LiveReplaceIndexUnsupported (the SDK documents no update-by-index path; live detail writes are append-only INSERTs through the ODBC driver).")]
            public int? ReplaceIndex { get; set; }
        }

        [McpServerTool]
        [Description("Bulk-append (or replace, per-entry via replaceIndex) detail rows across one or many clients in one MCP call. PREVIEW REQUIRED: first call with dryRun=true; if every row passes it returns previewToken (+ previewExpiresUtc, 30 minutes); show the operator the preview, get approval, then call again with the SAME entries (same order and values), same context and same continueOnError, dryRun=false and that previewToken. A real call without a valid token for exactly that batch (missing / unknown / expired / from another connection / entries, context or continueOnError changed) is refused with 9017 InvalidArgument before any row is written; a dry run with any failed row returns no token (fix or drop the row and preview again). The token stays valid for its 30 minutes, so a half-failed commit can be resubmitted with the same idempotencyKey and token. This is the canonical tool for intake workflows that seed many W-2/1099/dependent rows instead of looping lacerte_detail_write per row. Each entry mirrors the single detail-write params ({clientNumber, series, code, amount, description} required + the 7 optional row-key segments {prefix, suffix, stateAbbrev, cityAbbr, source, tsj, importSrc} defaulting to empty + optional replaceIndex). Live mode: each row is written through the Lacerte ODBC driver in a separate worker process only when the server runs with LACERTE_LIVE_DETAIL_WRITES=1; otherwise each row fails with 9202 SimulationOnly (with the default continueOnError=false the first such row stops the batch and the rest are marked skipped). Live rows get the same backup-before-first-write and read-back as lacerte_detail_write: each succeeded row carries verified (true = read back and confirmed; null = no read-back, see verificationNote) and snapshot (the client's backup file); a read-back mismatch fails that row with 9022. Runs the MANDATORY client-side row validation per entry BEFORE the session call (the SDK does not validate detail-row shape; bad rows fail with 9008 DetailWriteInvalid) and iterates the existing detail-write chokepoint (read-only rejection, dry-run undo, per-key idempotency lock, and the live 9019 replaceIndex rejection all still apply per row). Aggregates per-row outcomes into {requestedCount, processedCount, succeededCount, failedCount, skippedCount, stoppedEarly, rows[]}. Default continueOnError=false stops at the first failure and marks the remainder skipped; pass continueOnError=true to collect every row's status. When idempotencyKey is supplied it acts as a prefix — each row uses '{prefix}-{index}-{clientNumber}-{series}-{code}' (POSITIONAL: a retry must resubmit the same entries[] in the same order) so resubmitting the same batch replays already-appended rows from cache instead of doubling them (detail files have no primary key — duplicates are silent data corruption). entries.Length is capped at 500 by default (shared with lacerte_field_write_bulk; override via LACERTE_BULK_WRITE_MAX env var); larger batches throw 9020 BulkBatchTooLarge before any row runs. Honors MCP cancellation between rows: a cancelled call stops early and returns a partial envelope with stoppedEarly=true and processedCount.")]
        public Task<string> LacerteDetailWriteBulk(
            [Description("Array of per-row detail writes. Each entry: {clientNumber, series, code, amount, description, prefix?, suffix?, stateAbbrev?, cityAbbr?, source?, tsj?, importSrc?, replaceIndex?}. Empty array returns a no-op envelope with requestedCount=0. Capped at 500 entries by default (shared LACERTE_BULK_WRITE_MAX env var); exceeding the cap throws 9020 BulkBatchTooLarge before any row runs.")] BulkDetailEntry[] entries,
            [Description("If true, validate + simulate every row write then roll back without persisting. Each row returns its would-be TAPIStatus. When every row passes, the response carries previewToken + previewExpiresUtc — REQUIRED for the real (dryRun=false) call of the same batch.")] bool dryRun = false,
            [Description("If true, a failed row records its error and the loop continues to the next entry. If false (default), the loop stops at the first failure and remaining entries are marked skipped.")] bool continueOnError = false,
            [Description("STRONGLY RECOMMENDED for any write that could be retried. Pass a stable string keyed on op+batch intent; the server uses it as a per-row prefix ('{prefix}-{index}-{clientNumber}-{series}-{code}' — per-row keys are POSITIONAL, so a retry must resubmit the same entries[] in the same order) so a retry of the whole batch replays succeeded rows from cache and re-executes failed/skipped rows. Without a prefix, an MCP transport retry will re-APPEND every row — detail files have no primary key, so duplicates silently double W-2/1099/dependent amounts. Same prefix + same per-row payload: cached replay (idempotent for 24h). Same prefix + different payload: 9102 IdempotencyConflict for that row only. NOTE: dry-run results are also cached (dryRun is part of the fingerprint) — use a DIFFERENT prefix for the preview pass ('detail-<batchId>-preview') than the commit pass ('detail-<batchId>') or the commit fails with 9102. The idempotencyKey is NOT part of the preview token's binding.")] string? idempotencyKey = null,
            [Description("REQUIRED when dryRun is false (and entries is non-empty): the previewToken returned by a dry run (dryRun=true) of exactly this batch — same entries in the same order with the same values, same active context (dataPath, taxYear, module), same continueOnError, same connection. Valid for 30 minutes from the dry run and reusable within that window (e.g. to resubmit a half-failed batch with the same idempotencyKey). Missing / unknown / expired / mismatched tokens refuse the whole batch with 9017 before anything is written. Ignored on a dry run.")] string? previewToken = null,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Pre-flight: context presence, checked up front so the
                // failure carries bulk-envelope context (entryCount) instead
                // of repeating N per-row errors. WriteDetail re-checks via
                // RequireContext on every row.
                // -----------------------------------------------------------
                var ctx = _session.Context;
                if (ctx == null)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextNotSet,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "DetailWriteBulk",
                            ["entryCount"] = entries?.Length ?? 0,
                        });
                }

                // LACERTE_REQUIRE_APPROVAL=1 — refuse the whole real batch up
                // front (9023), before the preview-token check; dry runs pass.
                _session.ThrowIfApprovalRequired("DetailWriteBulk", dryRun);

                entries ??= Array.Empty<BulkDetailEntry>();

                // -----------------------------------------------------------
                // Blast-radius brake — SHARED cap with lacerte_field_write_bulk
                // (same weight class), resolved through the same internal
                // helper so the two tools can never drift on semantics
                // (default 500, LACERTE_BULK_WRITE_MAX override, fail-closed
                // 9020 before any row runs).
                // -----------------------------------------------------------
                int maxAllowed = FieldWriteBulkTools.ResolveMaxBulkEntries();
                if (entries.Length > maxAllowed)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.BulkBatchTooLarge,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "DetailWriteBulk",
                            ["entryCount"] = entries.Length,
                            ["maxAllowed"] = maxAllowed,
                            ["envOverride"] = FieldWriteBulkTools.BulkWriteMaxEnvName,
                        });
                }

                // -----------------------------------------------------------
                // Preview gate (filing-preparation decision 2). The binding
                // uses the SAME context every row is pinned to below (R7-25)
                // and the entries exactly as they will be written. A commit
                // is refused whole, before any row runs, unless it presents
                // the token a passing dry run of this exact batch issued.
                // -----------------------------------------------------------
                BulkPreviewBinding? binding = entries.Length == 0
                    ? null
                    : BuildPreviewBinding(_session.Mode, ctx, entries, continueOnError);
                DateTimeOffset? acceptedTokenExpiry = null;
                if (!dryRun && binding != null)
                {
                    var check = _session.DetailBulkPreviews.Check(previewToken, binding, SessionScope.Current);
                    if (!check.Accepted)
                    {
                        throw PreviewRefusal(check, entries.Length);
                    }
                    acceptedTokenExpiry = check.ExpiresUtc;
                }

                var rows = new List<object>(entries.Length);
                int succeededCount = 0;
                int failedCount = 0;
                int skippedCount = 0;
                bool stopped = false;
                bool stoppedEarly = false;
                int? firstFailureIndex = null;

                for (int i = 0; i < entries.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        stoppedEarly = true;
                        break;
                    }

                    var entry = entries[i];

                    // If a prior entry failed in stop-on-error mode, mark the
                    // remainder skipped without invoking WriteDetail.
                    if (stopped)
                    {
                        skippedCount++;
                        rows.Add(new
                        {
                            index = i,
                            clientNumber = entry?.ClientNumber,
                            series = entry?.Series,
                            code = entry?.Code,
                            succeeded = false,
                            skipped = true,
                            reason = "stop-on-error: a prior row failed and continueOnError is false.",
                        });
                        continue;
                    }

                    if (entry == null)
                    {
                        // Defensive guard — a null array element is a
                        // caller-input bug (9017), not a server fault.
                        failedCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        rows.Add(new
                        {
                            index = i,
                            clientNumber = (string?)null,
                            series = (string?)null,
                            code = (string?)null,
                            succeeded = false,
                            statusCode = SyntheticStatusCodes.InvalidArgument,
                            statusName = "InvalidArgument",
                            userMessage = "Bulk entry at this index was null.",
                        });
                        if (!continueOnError) stopped = true;
                        continue;
                    }

                    // Per-row idempotency key:
                    // '{prefix}-{i}-{clientNumber}-{series}-{code}'.
                    // POSITIONAL (index component) so the same logical row
                    // appearing twice in one batch (legitimate for detail
                    // files — e.g. two W-2s with the same series+code) does
                    // not alias to one key.
                    string? rowKey = string.IsNullOrEmpty(idempotencyKey)
                        ? null
                        : $"{idempotencyKey}-{i}-{entry.ClientNumber}-{entry.Series}-{entry.Code}";

                    try
                    {
                        // MANDATORY per-row validation (9008) BEFORE the
                        // session call — identical gates to DetailsTools.
                        ValidateDetailEntry(entry);

                        var row = ToDetailRow(entry);

                        var outcome = await _session.WriteDetailWithOutcome(
                            entry.ClientNumber, row, entry.ReplaceIndex,
                            dryRun, rowKey, cancellationToken,
                            // R7-25 — pin every row to the context validated above.
                            expectedContext: ctx).ConfigureAwait(false);
                        int status = outcome.Status;

                        var info = StatusCodes.GetStatusInfo(status);
                        if (status == StatusCodes.Success)
                        {
                            succeededCount++;
                            rows.Add(new
                            {
                                index = i,
                                clientNumber = entry.ClientNumber,
                                series = entry.Series,
                                code = entry.Code,
                                replaceIndex = entry.ReplaceIndex,
                                succeeded = true,
                                statusCode = status,
                                statusName = info.Name,
                                // FP-2 — same meaning as lacerte_detail_write.
                                verified = outcome.Verified,
                                verificationNote = outcome.VerificationNote,
                                snapshot = outcome.Snapshot,
                            });
                        }
                        else
                        {
                            // The single detail-write tool returns whatever
                            // TAPIStatus the session yields; in the bulk
                            // envelope a non-Success status MUST count as a
                            // failed row so succeededCount/continueOnError
                            // reflect actual outcomes.
                            failedCount++;
                            if (firstFailureIndex == null) firstFailureIndex = i;
                            rows.Add(new
                            {
                                index = i,
                                clientNumber = entry.ClientNumber,
                                series = entry.Series,
                                code = entry.Code,
                                replaceIndex = entry.ReplaceIndex,
                                succeeded = false,
                                statusCode = status,
                                statusName = info.Name,
                                userMessage = info.UserMessage,
                                remediation = info.Remediation,
                            });
                            if (!continueOnError) stopped = true;
                        }
                    }
                    catch (LacerteSDKError sdkErr)
                    {
                        failedCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        rows.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            series = entry.Series,
                            code = entry.Code,
                            succeeded = false,
                            statusCode = sdkErr.StatusCode,
                            statusName = sdkErr.StatusName,
                            severity = sdkErr.Severity.ToString().ToLowerInvariant(),
                            userMessage = sdkErr.UserMessage,
                            remediation = sdkErr.Remediation,
                            context = sdkErr.Context,
                        });
                        if (!continueOnError) stopped = true;
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation surfaced from inside the session call.
                        // Stop the batch; partial envelope carries
                        // stoppedEarly + processedCount. The in-flight row is
                        // NOT recorded — its outcome is unknown and a keyed
                        // retry will replay or re-execute it safely.
                        stoppedEarly = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        rows.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            series = entry.Series,
                            code = entry.Code,
                            succeeded = false,
                            statusCode = SyntheticStatusCodes.InternalServerError,
                            statusName = "InternalServerError",
                            userMessage = ex.Message,
                            exceptionType = ex.GetType().FullName,
                        });
                        if (!continueOnError) stopped = true;
                    }
                }

                object? firstFailure = null;
                if (firstFailureIndex.HasValue)
                {
                    firstFailure = rows[firstFailureIndex.Value];
                }

                object? recoveryHint = null;
                if (failedCount > 0)
                {
                    bool canResume = !string.IsNullOrEmpty(idempotencyKey) && !dryRun;
                    recoveryHint = new
                    {
                        canResumeViaSameBatch = canResume,
                        idempotencyKeyPrefix = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
                        perRowKeyShape = "{prefix}-{index}-{clientNumber}-{series}-{code}",
                        note = canResume
                            ? "Resubmit the same entries[] (same order — per-row keys are positional) with the same idempotencyKey and the same previewToken (still valid until previewExpiresUtc; after that, dry-run the same batch again for a new token): succeeded rows replay from cache (no duplicate appends), failed/skipped rows re-execute. Do NOT slice or reorder entries[] — that breaks the positional key alignment and re-appends rows that already landed (detail files have no primary key, so duplicates silently double amounts)."
                            : (dryRun
                                ? "Dry-run results ARE stored in the idempotency cache (dryRun is part of the payload fingerprint), so the commit pass MUST use a DIFFERENT idempotencyKey prefix than this preview pass — reusing the preview prefix fails every previewed row with 9102 IdempotencyConflict. Convention: preview with 'detail-<batchId>-preview', then commit with dryRun=false and prefix 'detail-<batchId>'. No previewToken was issued because a row failed: fix or drop the failed rows and dry-run again."
                                : "No idempotencyKey was supplied, so a resubmit would re-APPEND every row including the ones that already landed — silently doubling detail amounts. Re-run the batch with an idempotencyKey prefix to enable safe retries on future failures."),
                        replayedRowsCount = succeededCount,
                        retryRowsCount = failedCount + skippedCount,
                    };
                }

                // A dry run issues a token only when EVERY row passed (no
                // failures, nothing skipped, not cancelled).
                string? issuedToken = null;
                DateTimeOffset? issuedExpiry = null;
                string previewNote;
                if (binding == null)
                {
                    previewNote = "Empty batch: nothing to preview or commit, so no previewToken is needed.";
                }
                else if (dryRun)
                {
                    if (!stoppedEarly && succeededCount == entries.Length)
                    {
                        var issued = _session.DetailBulkPreviews.Issue(binding, SessionScope.Current);
                        issuedToken = issued.Token;
                        issuedExpiry = issued.ExpiresUtc;
                        previewNote = "Every row passed. Show this preview to the operator; once approved, call again with the SAME entries (same order and values), same context and continueOnError, dryRun=false, and this previewToken (valid until previewExpiresUtc, reusable until then for a resubmission with the same idempotencyKey). Any change needs a new dry run.";
                    }
                    else
                    {
                        previewNote = stoppedEarly
                            ? "No previewToken: the dry run was cancelled before every row was checked. Run the dry run again."
                            : $"No previewToken: {failedCount} row(s) failed and {skippedCount} were skipped in this dry run. Fix or remove the failed rows, then dry-run the corrected batch to get a token; only a fully passing preview can be committed.";
                    }
                }
                else
                {
                    previewNote = "Committed against the dry-run preview this previewToken was issued for.";
                }

                return (object)new
                {
                    dryRun,
                    continueOnError,
                    // Preview gate fields: on a dry run, the token the commit
                    // must present (null when a row failed); on a commit, the
                    // token that was accepted and when it stops being usable.
                    previewToken = dryRun ? issuedToken : (binding == null ? null : previewToken?.Trim()),
                    previewExpiresUtc = (dryRun ? issuedExpiry : acceptedTokenExpiry)?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    previewNote,
                    requestedCount = entries.Length,
                    // processedCount = rows with a recorded outcome
                    // (succeeded / failed / skipped); rows after a cancel
                    // have no entry at all (R6-20b pattern).
                    processedCount = rows.Count,
                    succeededCount,
                    failedCount,
                    skippedCount,
                    stoppedEarly,
                    firstFailureIndex,
                    firstFailure,
                    recoveryHint,
                    rows,
                };
            });
        }

        /// <summary>The detail row an entry writes (tool-level defaults applied; the session normalizes vocabulary).</summary>
        private static DetailRow ToDetailRow(BulkDetailEntry entry) => new DetailRow(
            Series: entry.Series,
            Prefix: entry.Prefix ?? "",
            Code: entry.Code,
            Suffix: entry.Suffix ?? "",
            StateAbbr: entry.StateAbbrev ?? "",
            CityAbbr: entry.CityAbbr ?? "",
            Source: entry.Source ?? "",
            TSJ: entry.Tsj ?? "",
            ImportSrc: entry.ImportSrc ?? "",
            Amount: entry.Amount,
            Description: entry.Description ?? "");

        /// <summary>
        /// The preview binding for this batch: mode, the pinned context triple,
        /// continueOnError and a SHA-256 over the canonical entries — each row
        /// as the session will write it (client number trimmed like
        /// RequireClientNumber, TSJ/Source normalized by
        /// <see cref="LacerteSessionManager.NormalizeDetailVocabulary"/>),
        /// serialized with a fixed property order and invariant-culture
        /// round-trip amounts.
        /// </summary>
        internal static BulkPreviewBinding BuildPreviewBinding(
            SessionMode mode, SessionContext ctx, IReadOnlyList<BulkDetailEntry?> entries, bool continueOnError)
        {
            byte[] canonical;
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var entry in entries)
                    {
                        if (entry == null)
                        {
                            w.WriteNullValue();
                            continue;
                        }
                        var row = LacerteSessionManager.NormalizeDetailVocabulary(ToDetailRow(entry));
                        w.WriteStartObject();
                        w.WriteString("clientNumber", entry.ClientNumber?.Trim() ?? "");
                        w.WriteString("series", row.Series ?? "");
                        w.WriteString("prefix", row.Prefix ?? "");
                        w.WriteString("code", row.Code ?? "");
                        w.WriteString("suffix", row.Suffix ?? "");
                        w.WriteString("stateAbbr", row.StateAbbr ?? "");
                        w.WriteString("cityAbbr", row.CityAbbr ?? "");
                        w.WriteString("source", row.Source ?? "");
                        w.WriteString("tsj", row.TSJ ?? "");
                        w.WriteString("importSrc", row.ImportSrc ?? "");
                        if (row.Amount.HasValue)
                        {
                            w.WriteString("amount", row.Amount.Value.ToString("R", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            w.WriteNull("amount");
                        }
                        w.WriteString("description", row.Description ?? "");
                        if (entry.ReplaceIndex.HasValue)
                        {
                            w.WriteNumber("replaceIndex", entry.ReplaceIndex.Value);
                        }
                        else
                        {
                            w.WriteNull("replaceIndex");
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                canonical = ms.ToArray();
            }

            string hash;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(canonical);
                var sb = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                hash = sb.ToString();
            }

            return new BulkPreviewBinding(
                Mode: mode.ToString(),
                DataPath: ctx.DataPath,
                Year: ctx.Year,
                Module: ctx.Module,
                EntriesHash: hash,
                EntryCount: entries.Count,
                ContinueOnError: continueOnError);
        }

        private static LacerteSDKError PreviewRefusal(BulkPreviewTokenCheck check, int entryCount)
        {
            const string howTo = "Run the same entries with dryRun:true first (same order and values, same context, same continueOnError), show the preview to the operator, then call again with dryRun:false and pass the previewToken that dry run returned. Nothing was written.";
            string problem;
            string reason;
            switch (check.Problem)
            {
                case BulkPreviewTokenProblem.Missing:
                    problem = "missing";
                    reason = "lacerte_detail_write_bulk only commits rows that were previewed, and no previewToken was passed. " + howTo;
                    break;
                case BulkPreviewTokenProblem.Expired:
                    problem = "expired";
                    reason = "The previewToken expired (tokens last 30 minutes after the dry run). " + howTo;
                    break;
                case BulkPreviewTokenProblem.WrongConnection:
                    problem = "wrongConnection";
                    reason = "The previewToken was issued to a different MCP connection; a token only works on the connection whose dry run created it. " + howTo;
                    break;
                case BulkPreviewTokenProblem.Mismatched:
                    problem = "mismatched";
                    reason = "This batch is not the one the previewToken was issued for (changed since the preview: "
                        + string.Join(", ", check.Mismatches) + "). " + howTo;
                    break;
                default:
                    problem = "unknown";
                    reason = "The previewToken is not one this server issued (mistyped, or the server restarted since the dry run). " + howTo;
                    break;
            }

            var context = new Dictionary<string, object?>
            {
                ["op"] = "DetailWriteBulk",
                ["argument"] = "previewToken",
                ["previewTokenProblem"] = problem,
                ["entryCount"] = entryCount,
                ["rowsWritten"] = 0,
                ["reason"] = reason,
            };
            if (check.Problem == BulkPreviewTokenProblem.Mismatched)
            {
                context["changedSincePreview"] = check.Mismatches;
            }
            if (check.ExpiresUtc.HasValue)
            {
                context["previewExpiresUtc"] = check.ExpiresUtc.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            }
            return new LacerteSDKError(SyntheticStatusCodes.InvalidArgument, context);
        }

        /// <summary>
        /// Per-row validation, mirroring <see cref="DetailsTools"/>'
        /// client-side gates exactly (same 9008 code, same field/value/reason
        /// context shapes): Series required + numeric, Code integer,
        /// stateAbbrev 2-letter-or-empty, TSJ in {T,S,J,""}.
        /// </summary>
        private static void ValidateDetailEntry(BulkDetailEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Series))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "series",
                        ["reason"] = "Series is required.",
                    });
            }

            if (!_rxNumericSeries.IsMatch(entry.Series))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "series",
                        ["value"] = entry.Series,
                        ["reason"] = "Series must be a numeric Lacerte screen identifier (^\\d+$) per spec §2 — e.g. '11'=W-2, '12'=Interest, '13'=Dividends, '14'=Dependents, '20'=Income, '30'=State, '40'=Partners.",
                    });
            }

            if (string.IsNullOrEmpty(entry.Code) || !_rxNumeric.IsMatch(entry.Code))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "code",
                        ["value"] = entry.Code,
                        ["reason"] = "Code must be a numeric integer (e.g. '1', '12', '-3').",
                    });
            }

            if (!string.IsNullOrEmpty(entry.StateAbbrev) && !_rxStateAbbrev.IsMatch(entry.StateAbbrev))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "stateAbbrev",
                        ["value"] = entry.StateAbbrev,
                        ["reason"] = "stateAbbrev must be empty OR a 2-letter uppercase postal code (e.g. CA, NY).",
                    });
            }

            if (!string.IsNullOrEmpty(entry.Tsj))
            {
                // R7-SRC — SDK TSJ values (aliases T/S/J accepted).
                if (LacertMCP.Server.Bridge.BridgeInputValidation.NormalizeTsj(entry.Tsj) == null)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["field"] = "tsj",
                            ["value"] = entry.Tsj,
                            ["reason"] = "TSJ must be empty OR one of: T/Taxpayer, S/Spouse, J/Joint.",
                        });
                }
            }
        }
    }
}
