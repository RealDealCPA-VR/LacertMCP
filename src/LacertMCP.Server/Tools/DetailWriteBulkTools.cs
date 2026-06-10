using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// </summary>
    [McpServerToolType]
    public class DetailWriteBulkTools
    {
        // Validation regexes mirror DetailsTools (private there, so mirrored
        // here — the shapes ARE the contract per agent 06 §5 / P14-055).
        private static readonly Regex _rxStateAbbrev = new Regex(
            @"^[A-Z]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _rxNumeric = new Regex(
            @"^-?\d+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _rxNumericSeries = new Regex(
            @"^\d+$",
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

            [Description("Dollar amount as a number (or null for non-numeric rows like dependent listings).")]
            public double? Amount { get; set; }

            [Description("Free-form description for the row. Required.")]
            public string Description { get; set; } = string.Empty;

            [Description("Prefix string. Defaults to empty — omit unless the row's natural key needs a prefix segment.")]
            public string Prefix { get; set; } = string.Empty;

            [Description("Suffix string. Defaults to empty — omit unless the row's natural key needs a suffix segment.")]
            public string Suffix { get; set; } = string.Empty;

            [Description("State abbreviation for the row's tax bucket — 2-letter postal code if non-empty (validated against ^[A-Z]{2}$). Defaults to empty. Maps to the DBF column StateAbbr internally.")]
            public string StateAbbrev { get; set; } = string.Empty;

            [Description("City abbreviation. Defaults to empty — omit unless the row carries a city-level breakdown.")]
            public string CityAbbr { get; set; } = string.Empty;

            [Description("Source tag (e.g. 'W2', '1099INT'). Defaults to empty.")]
            public string Source { get; set; } = string.Empty;

            [Description("Taxpayer/Spouse/Joint indicator — one of T, S, J, or empty. Defaults to empty.")]
            public string Tsj { get; set; } = string.Empty;

            [Description("Import source tag. Defaults to empty — omit unless the row originated from an import workflow.")]
            public string ImportSrc { get; set; } = string.Empty;

            [Description("Optional zero-based index to overwrite an existing row instead of appending. Sim-mode only — live mode rejects replaceIndex with 9019 LiveReplaceIndexUnsupported (the SDK can only append).")]
            public int? ReplaceIndex { get; set; }
        }

        [McpServerTool]
        [Description("Bulk-append (or replace, per-entry via replaceIndex) detail rows across one or many clients in one MCP call — the canonical tool for intake workflows that seed many W-2/1099/dependent rows instead of looping lacerte_detail_write per row. Each entry mirrors the single detail-write params ({clientNumber, series, code, amount, description} required + the 7 optional row-key segments {prefix, suffix, stateAbbrev, cityAbbr, source, tsj, importSrc} defaulting to empty + optional replaceIndex). Runs the MANDATORY client-side row validation per entry BEFORE the session call (the SDK does not validate detail-row shape; bad rows fail with 9008 DetailWriteInvalid) and iterates the existing detail-write chokepoint (read-only rejection, dry-run undo, per-key idempotency lock, and the live 9019 replaceIndex rejection all still apply per row). Aggregates per-row outcomes into {requestedCount, processedCount, succeededCount, failedCount, skippedCount, stoppedEarly, rows[]}. Default continueOnError=false stops at the first failure and marks the remainder skipped; pass continueOnError=true to collect every row's status. When idempotencyKey is supplied it acts as a prefix — each row uses '{prefix}-{index}-{clientNumber}-{series}-{code}' (POSITIONAL: a retry must resubmit the same entries[] in the same order) so resubmitting the same batch replays already-appended rows from cache instead of doubling them (detail files have no primary key — duplicates are silent data corruption). entries.Length is capped at 500 by default (shared with lacerte_field_write_bulk; override via LACERTE_BULK_WRITE_MAX env var); larger batches throw 9020 BulkBatchTooLarge before any row runs. Honors MCP cancellation between rows: a cancelled call stops early and returns a partial envelope with stoppedEarly=true and processedCount.")]
        public Task<string> LacerteDetailWriteBulk(
            [Description("Array of per-row detail writes. Each entry: {clientNumber, series, code, amount, description, prefix?, suffix?, stateAbbrev?, cityAbbr?, source?, tsj?, importSrc?, replaceIndex?}. Empty array returns a no-op envelope with requestedCount=0. Capped at 500 entries by default (shared LACERTE_BULK_WRITE_MAX env var); exceeding the cap throws 9020 BulkBatchTooLarge before any row runs.")] BulkDetailEntry[] entries,
            [Description("If true, validate + simulate every row write then roll back without persisting. Each row returns its would-be TAPIStatus.")] bool dryRun = false,
            [Description("If true, a failed row records its error and the loop continues to the next entry. If false (default), the loop stops at the first failure and remaining entries are marked skipped.")] bool continueOnError = false,
            [Description("STRONGLY RECOMMENDED for any write that could be retried. Pass a stable string keyed on op+batch intent; the server uses it as a per-row prefix ('{prefix}-{index}-{clientNumber}-{series}-{code}' — per-row keys are POSITIONAL, so a retry must resubmit the same entries[] in the same order) so a retry of the whole batch replays succeeded rows from cache and re-executes failed/skipped rows. Without a prefix, an MCP transport retry will re-APPEND every row — detail files have no primary key, so duplicates silently double W-2/1099/dependent amounts. Same prefix + same per-row payload: cached replay (idempotent for 24h). Same prefix + different payload: 9102 IdempotencyConflict for that row only. NOTE: dry-run results are also cached (dryRun is part of the fingerprint) — use a DIFFERENT prefix for the preview pass ('detail-<batchId>-preview') than the commit pass ('detail-<batchId>') or the commit fails with 9102.")] string? idempotencyKey = null,
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

                        var row = new DetailRow(
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

                        int status = await _session.WriteDetail(
                            entry.ClientNumber, row, entry.ReplaceIndex,
                            dryRun, rowKey, cancellationToken).ConfigureAwait(false);

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
                            ? "Resubmit the same entries[] (same order — per-row keys are positional) with the same idempotencyKey: succeeded rows replay from cache (no duplicate appends), failed/skipped rows re-execute. Do NOT slice or reorder entries[] — that breaks the positional key alignment and re-appends rows that already landed (detail files have no primary key, so duplicates silently double amounts)."
                            : (dryRun
                                ? "Dry-run results ARE stored in the idempotency cache (dryRun is part of the payload fingerprint), so the commit pass MUST use a DIFFERENT idempotencyKey prefix than this preview pass — reusing the preview prefix fails every previewed row with 9102 IdempotencyConflict. Convention: preview with 'detail-<batchId>-preview', then commit with dryRun=false and prefix 'detail-<batchId>'."
                                : "No idempotencyKey was supplied, so a resubmit would re-APPEND every row including the ones that already landed — silently doubling detail amounts. Re-run the batch with an idempotencyKey prefix to enable safe retries on future failures."),
                        replayedRowsCount = succeededCount,
                        retryRowsCount = failedCount + skippedCount,
                    };
                }

                return (object)new
                {
                    dryRun,
                    continueOnError,
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
                if (entry.Tsj.Length != 1 || (entry.Tsj != "T" && entry.Tsj != "S" && entry.Tsj != "J"))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["field"] = "tsj",
                            ["value"] = entry.Tsj,
                            ["reason"] = "TSJ must be empty OR one of: T, S, J.",
                        });
                }
            }
        }
    }
}
