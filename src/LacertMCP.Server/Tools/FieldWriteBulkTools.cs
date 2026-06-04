using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
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
    /// Per-row idempotency key shape: <c>$"{idempotencyKey}-{clientNumber}-{field}"</c>
    /// when an <c>idempotencyKey</c> prefix is supplied. Suffixing with both
    /// clientNumber and field lets a single batch span multiple fields per
    /// client without aliasing keys.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class FieldWriteBulkTools
    {
        private readonly LacerteSessionManager _session;

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
        [Description("Bulk-update a single DBF table across many clients in one MCP call. Iterates the existing field-write chokepoint per entry (DATA6/DATA7 still rejected, access-map gate still applied, dry-run undo still honored, idempotency-lock still serialized per key) and aggregates per-row outcomes into {totalCount, successCount, errorCount, perItem[]}. Default continueOnError=false stops at the first failure; pass continueOnError=true to keep going and collect every row's status. When idempotencyKey is supplied it acts as a prefix - each row uses '{prefix}-{clientNumber}-{field}' so a retry of the whole batch replays cleanly without aliasing across fields.")]
        public Task<string> LacerteFieldWriteBulk(
            [Description("DBF table number applied to every entry. 1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status are writable; 6=Display and 7=Forms are read-only per spec §2 and will be rejected with ExposedForRead.")] int table,
            [Description("Array of per-row writes. Each entry: {clientNumber, field, value, subClientId?, stateAbbrev?}. Empty array returns a no-op envelope with totalCount=0.")] BulkWriteEntry[] entries,
            // P16-069/P16-079 — canonical wire name `taxYear` everywhere
            // (matches lacerte_set_context + lacerte_can_use_new_apis +
            // lacerte_field_access_map). Was `year` pre-P16; bumped wire
            // name to `taxYear` as part of the X_naming_xtool sweep.
            [Description("Optional - validates that the active session context's tax year matches this value before iterating. Null skips the check. Pass to catch 'I set context to TY24 but meant TY23' mistakes before the batch starts.")] int? taxYear = null,
            [Description("Optional - validates that the active session context's readOnly flag matches this value before iterating. Null skips the check.")] bool? readOnly = null,
            [Description("If true, run the full validation + access-map gate per entry then roll back without persisting. Each row returns its would-be TAPIStatus.")] bool dryRun = false,
            [Description("If true, a failed row records its error and the loop continues to the next entry. If false (default), the loop stops at the first failure and remaining entries are marked skipped (matches the canonical bulk-write prompt's stop-on-first-error posture).")] bool continueOnError = false,
            [Description("Optional idempotency key prefix. Per-row key is '{prefix}-{clientNumber}-{field}'. Same prefix + same per-row payload returns the cached result for that row; same prefix + different payload throws 9102 IdempotencyConflict for that row only.")] string? idempotencyKey = null)
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

                for (int i = 0; i < entries.Length; i++)
                {
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

                    // Per-row idempotency key: '{prefix}-{clientNumber}-{field}'.
                    // Without a prefix the per-row write goes through with no
                    // idempotency - matches the single-write tool's contract
                    // when idempotencyKey is null.
                    string? rowKey = string.IsNullOrEmpty(idempotencyKey)
                        ? null
                        : $"{idempotencyKey}-{entry.ClientNumber}-{entry.Field}";

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
                            rowKey).ConfigureAwait(false);

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
                        perRowKeyShape = "{prefix}-{clientNumber}-{field}",
                        note = canResume
                            ? "Resubmit the same entries[] with the same idempotencyKey: succeeded rows replay from cache, failed/skipped rows re-execute. Do NOT slice entries[] — that loses the cache replay and the audit envelope."
                            : (dryRun
                                ? "Dry-run mode bypasses the idempotency cache, so there is no resume primitive. Re-run with dryRun=false (and an idempotencyKey) to enable safe retries."
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
