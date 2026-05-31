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
    /// REQUIREMENTS Â§1.2 calls out bulk updates (mailing addresses, EF flags,
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
    /// for every row â€” NOT past it into <c>ApplySimFieldWrite</c> â€” so the
    /// DATA6/DATA7 read-only block (P14-054), AssertWritable (access map),
    /// dry-run undo (P14-043), per-key idempotency lock (P14-005), and WireLog
    /// recording all stay one-source-of-truth. Only the per-row error
    /// aggregation is new.
    /// </para>
    ///
    /// <para>
    /// Per-row idempotency key shape: <c>$"{idempotencyKey}-{clientNumber}-{fieldName}"</c>
    /// when an <c>idempotencyKey</c> prefix is supplied. Suffixing with both
    /// clientNumber and fieldName lets a single batch span multiple fields
    /// per client without aliasing keys.
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
        /// One entry in a bulk-write batch. <c>ClientNumber</c> + <c>FieldName</c>
        /// + <c>Value</c> are the per-row payload; the shared <c>table</c> on
        /// the tool call applies to every entry.
        /// </summary>
        public sealed class BulkWriteEntry
        {
            [Description("Lacerte ClientNumber (the C{t}_0 identifier, not the operator-chosen ClientID).")]
            public string ClientNumber { get; set; } = string.Empty;

            [Description("Field name (e.g. C1_1, C2_15, C4_G3). Must be a writable field in the access map.")]
            public string FieldName { get; set; } = string.Empty;

            [Description("Value to write. JSON-serializable scalar â€” string, number, or boolean.")]
            public object? Value { get; set; }

            [Description("Required for DATA2 / DATA4 writes. Positive integer identifying the dependent/partner/shareholder row.")]
            public int? SubClientID { get; set; }

            [Description("Required for DATA3 / DATA4 writes. 2-letter state postal code (e.g. CA, NY).")]
            public string? StateAbbrev { get; set; }
        }

        [McpServerTool]
        [Description("Bulk-update a single DBF table across many clients in one MCP call. Iterates the existing field-write chokepoint per entry (DATA6/DATA7 still rejected, access-map gate still applied, dry-run undo still honored, idempotency-lock still serialized per key) and aggregates per-row outcomes into {totalCount, successCount, errorCount, perItem[]}. Default continueOnError=false stops at the first failure (mirrors the existing lacerte_bulk_field_update prompt); pass continueOnError=true to keep going and collect every row's status. When idempotencyKey is supplied it acts as a prefix â€” each row uses '{prefix}-{clientNumber}-{fieldName}' so a retry of the whole batch replays cleanly without aliasing across fields.")]
        public Task<string> LacerteFieldWriteBulk(
            [Description("DBF table number applied to every entry. 1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status are writable; 6=Display and 7=Forms are read-only per spec Â§2 and will be rejected with ExposedForRead.")] int table,
            [Description("Array of per-row writes. Each entry: {clientNumber, fieldName, value, subClientID?, stateAbbrev?}. Empty array returns a no-op envelope with totalCount=0.")] BulkWriteEntry[] entries,
            [Description("Optional â€” validates that the active session context's tax year matches this value before iterating. Null skips the check. Pass to catch 'I set context to TY24 but meant TY23' mistakes before the batch starts.")] int? year = null,
            [Description("Optional â€” validates that the active session context's readOnly flag matches this value before iterating. Null skips the check.")] bool? readOnly = null,
            [Description("If true, run the full validation + access-map gate per entry then roll back without persisting. Each row returns its would-be TAPIStatus.")] bool dryRun = false,
            [Description("If true, a failed row records its error and the loop continues to the next entry. If false (default), the loop stops at the first failure and remaining entries are marked skipped. Matches the existing lacerte_bulk_field_update prompt's stop-on-first-error posture by default.")] bool continueOnError = false,
            [Description("Optional idempotency key prefix. Per-row key is '{prefix}-{clientNumber}-{fieldName}'. Same prefix + same per-row payload returns the cached result for that row; same prefix + different payload throws 9102 IdempotencyConflict for that row only.")] string? idempotencyKey = null)
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

                if (year.HasValue && year.Value != ctx.Year)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextNotSet,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "FieldWriteBulk",
                            ["reason"] = "Caller-supplied year does not match the active session context's tax year.",
                            ["expectedYear"] = year.Value,
                            ["actualYear"] = ctx.Year,
                        });
                }

                if (readOnly.HasValue && readOnly.Value != ctx.ReadOnly)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextNotSet,
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
                bool stopped = false;

                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];

                    // If a prior entry failed in stop-on-error mode, mark
                    // the remainder as skipped without invoking WriteField.
                    if (stopped)
                    {
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry?.ClientNumber,
                            fieldName = entry?.FieldName,
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
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = (string?)null,
                            fieldName = (string?)null,
                            success = false,
                            statusCode = SyntheticStatusCodes.InternalServerError,
                            statusName = "InternalServerError",
                            userMessage = "Bulk entry at this index was null.",
                        });
                        if (!continueOnError) stopped = true;
                        continue;
                    }

                    // Per-row idempotency key: '{prefix}-{clientNumber}-{fieldName}'.
                    // Without a prefix the per-row write goes through with no
                    // idempotency â€” matches the single-write tool's contract
                    // when idempotencyKey is null.
                    string? rowKey = string.IsNullOrEmpty(idempotencyKey)
                        ? null
                        : $"{idempotencyKey}-{entry.ClientNumber}-{entry.FieldName}";

                    try
                    {
                        int status = await _session.WriteField(
                            table,
                            entry.ClientNumber,
                            entry.FieldName,
                            entry.Value,
                            entry.SubClientID,
                            entry.StateAbbrev,
                            dryRun,
                            rowKey).ConfigureAwait(false);

                        var info = StatusCodes.GetStatusInfo(status);
                        successCount++;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            fieldName = entry.FieldName,
                            success = true,
                            statusCode = status,
                            statusName = info.Name,
                        });
                    }
                    catch (LacerteSDKError sdkErr)
                    {
                        errorCount++;
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            fieldName = entry.FieldName,
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
                        perItem.Add(new
                        {
                            index = i,
                            clientNumber = entry.ClientNumber,
                            fieldName = entry.FieldName,
                            success = false,
                            statusCode = SyntheticStatusCodes.InternalServerError,
                            statusName = "InternalServerError",
                            userMessage = ex.Message,
                            exceptionType = ex.GetType().FullName,
                        });
                        if (!continueOnError) stopped = true;
                    }
                }

                return (object)new
                {
                    table,
                    dryRun,
                    continueOnError,
                    totalCount = entries.Length,
                    successCount,
                    errorCount,
                    perItem,
                };
            });
        }
    }
}
