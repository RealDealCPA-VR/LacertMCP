using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools.Internal;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P15-008: <c>lacerte_field_write_bulk</c> — single tool call that fans
    /// out N field writes through the existing
    /// <see cref="LacerteSessionManager.WriteField"/> chokepoint.
    ///
    /// <para>
    /// REQUIREMENTS §1.2 calls out bulk updates (mailing addresses, EF flags,
    /// preparer assignments) as a first-class operator scenario. Before this
    /// tool the only path was N round-trips through <c>lacerte_field_write</c>,
    /// each going through WrapToolHandler + JSON serialize + idempotency hash +
    /// access-map probe. For a 300-client mailing-address sweep that's 300
    /// round-trips with no resume primitive when one transient COM error
    /// trips client 50/300. This tool collapses those into a single MCP call
    /// and aggregates per-row outcomes into one structured envelope.
    /// </para>
    ///
    /// <para>
    /// Architectural invariant: this tool MUST go through <see cref="LacerteSessionManager.WriteField"/>
    /// for every row — NOT past it into <c>ApplySimFieldWrite</c> — so the
    /// DATA6/DATA7 read-only block (P14-054), AssertWritable (access map),
    /// dry-run undo (P14-043), per-key idempotency lock (P14-005), and WireLog
    /// recording all stay one-source-of-truth. Only the per-row error
    /// aggregation is new.
    /// </para>
    ///
    /// <para>
    /// Per-row idempotency key shape: <c>$"{idempotencyKey}-{i}-{clientNumber}-{field}"</c>
    /// when an <c>idempotencyKey</c> prefix is supplied. Suffixing with the
    /// row index plus clientNumber and field lets a single batch span
    /// multiple fields per client AND repeat the same (client, field) pair
    /// at different positions without aliasing keys (R6-20a). Per-row keys
    /// are therefore POSITIONAL — a retry must resubmit the same entries[]
    /// in the same order.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class FieldWriteBulkTools
    {
        private readonly LacerteSessionManager _session;

        /// <summary>
        /// P17-008 — default per-call cap on <c>entries.Length</c>. The bulk
        /// write tool fans out N writes through the live SDK from a single
        /// MCP call; without an upper bound a fuzzy LLM-constructed payload
        /// (e.g., "write zip=12345 for every client in NY" emitted as every
        /// client in the firm) can mutate thousands of rows before the
        /// operator can inspect. The project's own pagination layer caps
        /// every read tool at <see cref="Pagination.Pagination.MaxLimit"/>
        /// (1000); writes have strictly higher blast radius and deserve at
        /// least the same ceiling. We pick 500 (stricter than read) as a
        /// deliberate brake on un-deliberate writes — overridable via the
        /// <see cref="BulkWriteMaxEnvName"/> environment variable when an
        /// operator workflow genuinely needs a larger batch.
        /// </summary>
        public const int DefaultMaxBulkEntries = 500;

        /// <summary>
        /// P17-008 — environment-variable override for
        /// <see cref="DefaultMaxBulkEntries"/>. Positive integer values
        /// replace the default; null / empty / non-numeric / non-positive
        /// values silently fall back to the default (matches the project's
        /// never-throw-from-env-parse convention).
        /// </summary>
        public const string BulkWriteMaxEnvName = "LACERTE_BULK_WRITE_MAX";

        /// <summary>
        /// Resolves the effective cap from <see cref="BulkWriteMaxEnvName"/>,
        /// falling back to <see cref="DefaultMaxBulkEntries"/> on any parse
        /// failure or non-positive value. Read on each call so a test or
        /// operator can flip the env var mid-process without restarting.
        /// </summary>
        internal static int ResolveMaxBulkEntries()
        {
            var raw = Environment.GetEnvironmentVariable(BulkWriteMaxEnvName);
            if (string.IsNullOrEmpty(raw)) return DefaultMaxBulkEntries;
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                return parsed;
            }
            return DefaultMaxBulkEntries;
        }

        public FieldWriteBulkTools(LacerteSessionManager session)
        {
            _session = session;
        }

        /// <summary>
        /// One entry in a bulk-write batch. <c>ClientNumber</c> + <c>Field</c>
        /// + <c>Value</c> are the per-row payload; the shared <c>table</c> on
        /// the tool call applies to every entry.
        /// <para>
        /// P16-026 — renamed FieldName -> Field to match the single-write
        /// tools (lacerte_field_write + lacerte_update_return both use
        /// `field`). One canonical name across the write surface so an LLM
        /// refactoring single -> bulk doesn't silently fail every row with
        /// an empty-field error.
        /// </para>
        /// <para>
        /// P16-080 — SubClientID -> SubClientId casing aligned with .NET
        /// Framework Design Guidelines (2-letter acronym `Id` stays mixed-
        /// case on the wire to avoid the ALL-CAPS suffix anti-pattern).
        /// </para>
        /// </summary>
        public sealed class BulkWriteEntry
        {
            [Description("Lacerte ClientNumber (the C{t}_0 identifier, not the operator-chosen ClientId).")]
            public string ClientNumber { get; set; } = string.Empty;

            [Description("Field name (e.g. C1_1, C2_15, C4_G3). Must be a writable field in the access map.")]
            public string Field { get; set; } = string.Empty;

            [Description("Value to write. JSON-serializable scalar - string, number, or boolean.")]
            public object? Value { get; set; }

            [Description("Required for DATA2 / DATA4 writes. Positive integer identifying the dependent/partner/shareholder row.")]
            public int? SubClientId { get; set; }

            [Description("Required for DATA3 / DATA4 writes. 2-letter state postal code (e.g. CA, NY).")]
            public string? StateAbbrev { get; set; }
        }

        [McpServerTool]
        [Description("Bulk-update a single DBF table across many clients in one MCP call. Iterates the existing field-write chokepoint per entry (DATA6/DATA7 still rejected, access-map gate still applied, dry-run undo still honored, idempotency-lock still serialized per key) and aggregates per-row outcomes into {totalCount, successCount, errorCount, perItem[]}. Default continueOnError=false stops at the first failure; pass continueOnError=true to keep going and collect every row's status. When idempotencyKey is supplied it acts as a prefix - each row uses '{prefix}-{index}-{clientNumber}-{field}' (per-row keys are POSITIONAL: a retry must resubmit the same entries[] in the same order) so a retry of the whole batch replays cleanly even when the same (client, field) pair appears more than once. P17-008 - entries.Length is capped at 500 by default (override via LACERTE_BULK_WRITE_MAX env var); larger batches throw 9020 BulkBatchTooLarge. Pass optional expectedCount to assert entries.Length equals an operator-intended target (throws 9021 BulkBatchCountMismatch on drift) - catches fuzzy LLM payload construction before any row runs. Honors MCP cancellation between rows: a cancelled call stops early and returns a partial envelope with stoppedEarly=true and processedCount.")]
        public Task<string> LacerteFieldWriteBulk(
            [Description("DBF table number applied to every entry. 1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status are writable; 6=Display and 7=Forms are read-only per spec §2 and will be rejected with ExposedForRead.")] int table,
            [Description("Array of per-row writes. Each entry: {clientNumber, field, value, subClientId?, stateAbbrev?}. Empty array returns a no-op envelope with totalCount=0. Capped at 500 entries by default (override via LACERTE_BULK_WRITE_MAX env var); exceeding the cap throws 9020 BulkBatchTooLarge before any row runs.")] BulkWriteEntry[] entries,
            // P16-069/P16-079 — canonical wire name `taxYear` everywhere
            // (matches lacerte_set_context + lacerte_can_use_new_apis +
            // lacerte_field_access_map). Was `year` pre-P16; bumped wire
            // name to `taxYear` as part of the X_naming_xtool sweep.
            [Description("Optional - validates that the active session context's tax year matches this value before iterating. Null skips the check. Pass to catch 'I set context to TY24 but meant TY23' mistakes before the batch starts.")] int? taxYear = null,
            [Description("Optional - validates that the active session context's readOnly flag matches this value before iterating. Null skips the check.")] bool? readOnly = null,
            [Description("If true, run the full validation + access-map gate per entry then roll back without persisting. Each row returns its would-be TAPIStatus.")] bool dryRun = false,
            [Description("If true, a failed row records its error and the loop continues to the next entry. If false (default), the loop stops at the first failure and remaining entries are marked skipped (matches the canonical bulk-write prompt's stop-on-first-error posture).")] bool continueOnError = false,
            [Description("STRONGLY RECOMMENDED for any write that could be retried. Pass a stable string keyed on op+target+intent; the server uses it as a per-row prefix ('{prefix}-{index}-{clientNumber}-{field}' — per-row keys are POSITIONAL, so a retry must resubmit the same entries[] in the same order) so a retry of the whole batch replays succeeded rows from cache and re-executes failed/skipped rows. Without a prefix, an MCP transport retry will re-execute every row including the ones that already succeeded. Same prefix + same per-row payload: cached replay (idempotent for 24h). Same prefix + different payload: 9102 IdempotencyConflict for that row only. NOTE: dry-run results are also cached (dryRun is part of the fingerprint) — use a DIFFERENT prefix for the preview pass ('bulk-<batchId>-preview') than the commit pass ('bulk-<batchId>') or the commit fails with 9102.")] string? idempotencyKey = null,
            // P17-008 — optional cross-check that catches LLM-vs-operator
            // intent drift BEFORE any row runs. The operator's prompt may
            // say "write to exactly 47 NY clients" — passing expectedCount=47
            // lets the system enforce it instead of the operator scanning a
            // 200-row perItem afterwards. Null skips the check.
            [Description("Optional - validates that entries.Length equals this caller-supplied value before iterating. Null skips the check. Use to catch 'I meant 47 clients but the LLM emitted 200' drift; throws 9021 BulkBatchCountMismatch on mismatch.")] int? expectedCount = null,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Pre-flight: validate the active context up front so we
                // fail loudly BEFORE iterating instead of N-times mid-batch.
                // The per-row WriteField call also re-checks via RequireContext;
                // doing it here surfaces the failure with the bulk envelope
                // context (entryCount, table) instead of as a per-row error.
                // -----------------------------------------------------------
                var ctx = _session.Context;
                if (ctx == null)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextNotSet,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "FieldWriteBulk",
                            ["table"] = table,
                            ["entryCount"] = entries?.Length ?? 0,
                        });
                }

                if (taxYear.HasValue && taxYear.Value != ctx.Year)
                {
                    // P16-063 — distinct synthetic code 9016 ContextMismatch
                    // (NOT 9009 ContextNotSet). 9009's canonical remediation
                    // says "Call lacerte_set_context first" — actively wrong
                    // for the mismatch case (context IS set, just for a
                    // different year). 9016 ships a remediation that names
                    // the mismatch and tells the operator to either re-set
                    // context or drop the assertion.
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextMismatch,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "FieldWriteBulk",
                            ["reason"] = "Caller-supplied taxYear does not match the active session context's tax year.",
                            // P16-069/P16-079 — canonical error-context keys
                            // use `taxYear` for cross-tool consistency.
                            ["expectedTaxYear"] = taxYear.Value,
                            ["actualTaxYear"] = ctx.Year,
                        });
                }

                if (readOnly.HasValue && readOnly.Value != ctx.ReadOnly)
                {
                    // P16-063 — see note above; mismatch uses 9016, not 9009.
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextMismatch,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "FieldWriteBulk",
                            ["reason"] = "Caller-supplied readOnly flag does not match the active session context's readOnly flag.",
                            ["expectedReadOnly"] = readOnly.Value,
                            ["actualReadOnly"] = ctx.ReadOnly,
                        });
                }

                entries ??= Array.Empty<BulkWriteEntry>();

                // -----------------------------------------------------------
                // P17-008 — blast-radius brake. Fail-CLOSED at the tool
                // chokepoint when entries.Length exceeds the per-call cap so
                // a fuzzy LLM-constructed payload (e.g. "every client in NY"
                // emitted as every client in the firm) can't mutate thousands
                // of rows from a single MCP call. The project's pagination
                // layer already bounds every READ tool at MaxLimit=1000;
                // writes have strictly higher blast radius and deserve at
                // least the same ceiling. Default cap = 500 (stricter than
                // read) — override via LACERTE_BULK_WRITE_MAX when an
                // operator workflow genuinely needs a larger batch.
                // -----------------------------------------------------------
                int maxAllowed = ResolveMaxBulkEntries();
                if (entries.Length > maxAllowed)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.BulkBatchTooLarge,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "FieldWriteBulk",
                            ["entryCount"] = entries.Length,
                            ["maxAllowed"] = maxAllowed,
                            ["table"] = table,
                            ["envOverride"] = BulkWriteMaxEnvName,
                        });
                }

                // -----------------------------------------------------------
                // P17-008 — optional caller-supplied entries.Length cross-
                // check. Catches LLM-vs-operator intent drift BEFORE any
                // row runs (the operator's prompt says "write to exactly
                // 47 NY clients"; expectedCount=47 enforces the count
                // structurally instead of leaving it to a post-hoc perItem
                // scan). Symmetric with the taxYear/readOnly assertions
                // above — both use 9016-family pre-flight throws.
                // -----------------------------------------------------------
                if (expectedCount.HasValue && expectedCount.Value != entries.Length)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.BulkBatchCountMismatch,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "FieldWriteBulk",
                            ["reason"] = "Caller-supplied expectedCount does not match entries.Length.",
                            ["expected"] = expectedCount.Value,
                            ["actual"] = entries.Length,
                            ["table"] = table,
                        });
                }

                var perItem = new List<object>(entries.Length);
                int successCount = 0;
                int errorCount = 0;
                int skippedCount = 0;
                bool stopped = false;
                // P16-064 — capture the index of the FIRST non-success row so
                // the response envelope can hoist it to the top, sparing the
                // LLM a linear scan of up to N perItem entries to find where
                // the batch tripped. Set on first failure only; later failures
                // (in continueOnError mode) are still visible via perItem.
                int? firstFailureIndex = null;
                // R6-20b — MCP cancellation between rows. When the host
                // cancels mid-batch we stop BEFORE the next mutation and
                // return a clear partial-result envelope instead of either
                // throwing away the completed rows' outcomes or continuing
                // to mutate after the operator asked us to stop.
                bool stoppedEarly = false;

                for (int i = 0; i < entries.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        stoppedEarly = true;
                        break;
                    }

                    var entry = entries[i];

                    // If a prior entry failed in stop-on-error mode, mark
                    // the remainder as skipped without invoking WriteField.
                    if (stopped)
                    {
                        skippedCount++;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry?.ClientNumber,
                            // P16-026 — per-row response echoes `field`
                            // to match the single-write tool's response shape.
                            field = entry?.Field,
                            success = false,
                            skipped = true,
                            reason = "stop-on-error: a prior row failed and continueOnError is false.",
                        });
                        continue;
                    }

                    if (entry == null)
                    {
                        // Shouldn't happen with array deserialization but guard
                        // defensively so a malformed payload doesn't NRE.
                        errorCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = (string?)null,
                            field = (string?)null,
                            success = false,
                            statusCode = SyntheticStatusCodes.InternalServerError,
                            statusName = "InternalServerError",
                            userMessage = "Bulk entry at this index was null.",
                        });
                        if (!continueOnError) stopped = true;
                        continue;
                    }

                    // Per-row idempotency key: '{prefix}-{i}-{clientNumber}-{field}'.
                    // R6-20a — the index component makes keys POSITIONAL so
                    // two entries targeting the same (client, field) in one
                    // batch (e.g. write-then-correct) no longer alias to one
                    // key (which produced a 9102 or a silent cached replay
                    // for the second row). A retry must resubmit the same
                    // entries[] in the same order. Without a prefix the
                    // per-row write goes through with no idempotency -
                    // matches the single-write tool's contract when
                    // idempotencyKey is null.
                    string? rowKey = string.IsNullOrEmpty(idempotencyKey)
                        ? null
                        : $"{idempotencyKey}-{i}-{entry.ClientNumber}-{entry.Field}";

                    try
                    {
                        int status = await _session.WriteField(
                            table,
                            entry.ClientNumber,
                            entry.Field,
                            entry.Value,
                            entry.SubClientId,
                            entry.StateAbbrev,
                            dryRun,
                            rowKey,
                            cancellationToken).ConfigureAwait(false);

                        var verification = dryRun
                            ? null
                            : await FieldWriteCore.VerifyOrThrow(_session, "WriteFieldBulk", table,
                                entry.ClientNumber, entry.Field, entry.Value, entry.SubClientId, entry.StateAbbrev,
                                cancellationToken).ConfigureAwait(false);

                        var info = StatusCodes.GetStatusInfo(status);
                        successCount++;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            field = entry.Field,
                            success = true,
                            statusCode = status,
                            statusName = info.Name,
                            verified = verification?.Verified,
                            verificationNote = verification?.Note,
                        });
                    }
                    catch (LacerteSDKError sdkErr)
                    {
                        errorCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            field = entry.Field,
                            success = false,
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
                        // R6-20b — cancellation surfaced from inside the
                        // session call. Stop the batch; the partial envelope
                        // below carries stoppedEarly + processedCount. The
                        // in-flight row is NOT recorded as an error — its
                        // outcome is unknown and a keyed retry will replay
                        // or re-execute it safely.
                        stoppedEarly = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Non-LacerteSDKError exceptions still get aggregated
                        // per-row so a NullRef in one row doesn't kill the
                        // whole bulk envelope. Wrap as 9203 to match the
                        // WrapToolHandler convention (P15-035).
                        errorCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            field = entry.Field,
                            success = false,
                            statusCode = SyntheticStatusCodes.InternalServerError,
                            statusName = "InternalServerError",
                            userMessage = ex.Message,
                            exceptionType = ex.GetType().FullName,
                        });
                        if (!continueOnError) stopped = true;
                    }
                }

                // P16-064 — hoist the first failure to the envelope top so the
                // LLM doesn't have to linear-scan perItem to locate where the
                // batch tripped, AND emit a structured recoveryHint when the
                // operator/LLM can safely retry. The recovery story for bulk
                // writes is: "resubmit the SAME entries[] with the SAME
                // idempotencyKey — succeeded rows replay from the per-row
                // idempotency cache, failed/skipped rows re-execute." That
                // strategy only works when an idempotency prefix was supplied
                // AND we're not in dryRun mode (dryRun bypasses the cache).
                object? firstFailure = null;
                if (firstFailureIndex.HasValue)
                {
                    firstFailure = perItem[firstFailureIndex.Value];
                }

                object? recoveryHint = null;
                if (errorCount > 0)
                {
                    bool canResume = !string.IsNullOrEmpty(idempotencyKey) && !dryRun;
                    recoveryHint = new
                    {
                        canResumeViaSameBatch = canResume,
                        idempotencyKeyPrefix = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
                        // R6-20a — keys are positional (index component).
                        perRowKeyShape = "{prefix}-{index}-{clientNumber}-{field}",
                        note = canResume
                            ? "Resubmit the same entries[] (same order — per-row keys are positional) with the same idempotencyKey: succeeded rows replay from cache, failed/skipped rows re-execute. Do NOT slice or reorder entries[] — that breaks the positional key alignment, loses the cache replay, and loses the audit envelope."
                            : (dryRun
                                // R6-02 — post-P18, dry-run results ARE
                                // cached (dryRun is in the fingerprint), so
                                // the commit pass MUST use a different
                                // prefix or every previewed row 9102s.
                                ? "Dry-run results ARE stored in the idempotency cache (dryRun is part of the payload fingerprint), so the commit pass MUST use a DIFFERENT idempotencyKey prefix than this preview pass — reusing the preview prefix fails every previewed row with 9102 IdempotencyConflict. Convention: preview with 'bulk-<batchId>-preview', then commit with dryRun=false and prefix 'bulk-<batchId>'."
                                : "No idempotencyKey was supplied, so a resubmit would re-execute every row including the ones that already succeeded. Re-run the batch with an idempotencyKey prefix to enable safe retries on future failures."),
                        replayedRowsCount = successCount,
                        retryRowsCount = errorCount + skippedCount,
                    };
                }

                return (object)new
                {
                    table,
                    dryRun,
                    continueOnError,
                    totalCount = entries.Length,
                    successCount,
                    errorCount,
                    skippedCount,
                    // R6-20b — partial-result markers for MCP cancellation.
                    // processedCount = rows with a recorded outcome
                    // (success / error / skipped); rows after a cancel have
                    // no perItem entry at all.
                    processedCount = perItem.Count,
                    stoppedEarly,
                    // P16-064 — first-failure hoist + structured recovery hint.
                    firstFailureIndex,
                    firstFailure,
                    recoveryHint,
                    perItem,
                };
            });
        }
    }
}
