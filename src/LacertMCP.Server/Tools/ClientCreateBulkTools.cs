using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// R6 gap #2: <c>lacerte_client_create_bulk</c> — single tool call that
    /// fans out N client creations through the existing
    /// <see cref="LacerteSessionManager.CreateClient"/> chokepoint.
    ///
    /// <para>
    /// REQUIREMENTS.md #3 ("bootstrap clients from CSV") previously had only
    /// a workflow prompt looping <c>lacerte_client_create</c> one row at a
    /// time — N round-trips with no resume primitive when a transient COM
    /// error trips row 50/300. This tool collapses the CSV-intake bootstrap
    /// into one MCP call and aggregates per-row outcomes into one structured
    /// envelope, mirroring the <see cref="FieldWriteBulkTools"/> bulk pattern
    /// (cap, dryRun, continueOnError, positional per-row idempotency keys,
    /// cancellation between rows, stoppedEarly/processedCount markers).
    /// </para>
    ///
    /// <para>
    /// Architectural invariant: every row MUST go through
    /// <see cref="LacerteSessionManager.CreateClient"/> — never past it into
    /// the sim store or bridge — so the registration gate (R6-22), read-only
    /// rejection, dry-run undo (P14-043), Success-only idempotency caching
    /// (R6-04), and WireLog recording stay one-source-of-truth. The per-row
    /// validation below mirrors <see cref="CreateClientTools"/> exactly
    /// (same codes, same normalization, same TaxType canonicalization) so a
    /// row that would fail the single-create tool fails identically here.
    /// </para>
    ///
    /// <para>
    /// Per-row idempotency key shape: <c>$"{idempotencyKey}-{i}-{clientId}"</c>
    /// (clientId in its uppercase-normalized form). Keys are POSITIONAL — a
    /// retry must resubmit the same entries[] in the same order (R6-20a
    /// convention inherited from lacerte_field_write_bulk).
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class ClientCreateBulkTools
    {
        /// <summary>
        /// Default per-call cap on <c>entries.Length</c>. Creates are heavier
        /// than field writes (whole DATA1 row + ClientNumber mint + detail
        /// file allocation per row), so the default cap is stricter than
        /// <see cref="FieldWriteBulkTools.DefaultMaxBulkEntries"/> (500):
        /// 100 rows per call. Overridable via <see cref="BulkCreateMaxEnvName"/>
        /// when a genuine CSV-intake batch needs more.
        /// </summary>
        public const int DefaultMaxBulkCreateEntries = 100;

        /// <summary>
        /// Environment-variable override for
        /// <see cref="DefaultMaxBulkCreateEntries"/>. Positive integer values
        /// replace the default; null / empty / non-numeric / non-positive
        /// values silently fall back (matches the project's
        /// never-throw-from-env-parse convention and the
        /// LACERTE_BULK_WRITE_MAX semantics on the field/detail bulk tools).
        /// </summary>
        public const string BulkCreateMaxEnvName = "LACERTE_BULK_CREATE_MAX";

        /// <summary>
        /// Resolves the effective cap from <see cref="BulkCreateMaxEnvName"/>,
        /// falling back to <see cref="DefaultMaxBulkCreateEntries"/> on any
        /// parse failure or non-positive value. Read on each call so a test
        /// or operator can flip the env var mid-process without restarting.
        /// </summary>
        internal static int ResolveMaxBulkCreateEntries()
        {
            var raw = Environment.GetEnvironmentVariable(BulkCreateMaxEnvName);
            if (string.IsNullOrEmpty(raw)) return DefaultMaxBulkCreateEntries;
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                return parsed;
            }
            return DefaultMaxBulkCreateEntries;
        }

        // Same clientId shape gate as CreateClientTools (private there, so
        // mirrored here — the regex IS the contract per spec §4).
        private static readonly Regex _rxClientId = new Regex(
            @"^[A-Z0-9]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly LacerteSessionManager _session;

        public ClientCreateBulkTools(LacerteSessionManager session)
        {
            _session = session;
        }

        /// <summary>
        /// One entry in a bulk-create batch. Mirrors the single
        /// <c>lacerte_client_create</c> argument list exactly (clientId,
        /// taxType, firstName, lastName, emailId — all required) so an LLM
        /// refactoring single -&gt; bulk maps each tool call to one entry
        /// with no renames.
        /// </summary>
        public sealed class BulkCreateEntry
        {
            [Description("Operator-chosen ClientId. <= 8 chars, alphanumeric only. Lowercase is accepted and normalized to uppercase before storage, matching lacerte_client_create.")]
            public string ClientId { get; set; } = string.Empty;

            [Description("TaxType string passed to the SDK's IClient constructor. Must match the active module (e.g. 'Individual' / '1040' for module I). Colloquial aliases canonicalize exactly as on lacerte_client_create.")]
            public string TaxType { get; set; } = string.Empty;

            [Description("First name (individual) OR entity-name override. Required per Lacerte spec §4.")]
            public string FirstName { get; set; } = string.Empty;

            [Description("Last name (individual). Required per Lacerte spec §4.")]
            public string LastName { get; set; } = string.Empty;

            [Description("EmailId. Required per Lacerte spec §4.")]
            public string EmailId { get; set; } = string.Empty;
        }

        [McpServerTool]
        [Description("Bulk-create N clients in the active datapath/taxYear/module from one MCP call — the canonical tool for REQUIREMENTS.md #3 (bootstrap clients from a CSV roster) instead of looping lacerte_client_create per row. Each entry mirrors the single-create args ({clientId, taxType, firstName, lastName, emailId} — all required) and runs the identical validation + TaxType canonicalization before hitting the session chokepoint (registration gate, read-only rejection, dry-run undo, and idempotency cache all still apply per row). Aggregates per-row outcomes into {requestedCount, processedCount, succeededCount, failedCount, skippedCount, stoppedEarly, rows[]}. Default continueOnError=false stops at the first failure and marks the remainder skipped; pass continueOnError=true to collect every row's status (e.g. to surface ALL duplicate clientIds in one pass). dryRun=true validates and simulates every row then rolls back: dry-run rows report wouldCreate=true with clientNumber=null (a dry run cannot mint a real ClientNumber — both sim and live agree per R6-22). When idempotencyKey is supplied it acts as a prefix — each row uses '{prefix}-{index}-{clientId}' (POSITIONAL: a retry must resubmit the same entries[] in the same order) so resubmitting the same batch replays already-created rows from cache with their original clientNumbers instead of minting duplicates. entries.Length is capped at 100 by default (creates are heavier than field writes; override via LACERTE_BULK_CREATE_MAX env var); larger batches throw 9020 BulkBatchTooLarge before any row runs. Honors MCP cancellation between rows: a cancelled call stops early and returns a partial envelope with stoppedEarly=true and processedCount.")]
        public Task<string> LacerteClientCreateBulk(
            [Description("Array of per-row creates. Each entry: {clientId, taxType, firstName, lastName, emailId} — all five required, mirroring lacerte_client_create. Empty array returns a no-op envelope with requestedCount=0. Capped at 100 entries by default (override via LACERTE_BULK_CREATE_MAX env var); exceeding the cap throws 9020 BulkBatchTooLarge before any row runs.")] BulkCreateEntry[] entries,
            [Description("If true, validate + simulate every create then roll back without persisting. Each dry-run row reports wouldCreate=true and clientNumber=null — a dry run cannot mint a real ClientNumber, so never treat a dry-run row as having allocated one.")] bool dryRun = false,
            [Description("If true, a failed row records its error and the loop continues to the next entry. If false (default), the loop stops at the first failure and remaining entries are marked skipped.")] bool continueOnError = false,
            [Description("STRONGLY RECOMMENDED for any write that could be retried. Pass a stable string keyed on op+batch intent; the server uses it as a per-row prefix ('{prefix}-{index}-{clientId}' — per-row keys are POSITIONAL, so a retry must resubmit the same entries[] in the same order) so a retry of the whole batch replays already-created rows from cache (same clientNumbers, no duplicate clients) and re-executes failed/skipped rows. Without a prefix, an MCP transport retry will re-attempt every create — already-created rows then fail with DuplicateClient. Same prefix + same per-row payload: cached replay (idempotent for 24h). Same prefix + different payload: 9102 IdempotencyConflict for that row only. NOTE: dry-run results are also cached (dryRun is part of the fingerprint) — use a DIFFERENT prefix for the preview pass ('create-<batchId>-preview') than the commit pass ('create-<batchId>') or the commit fails with 9102.")] string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Pre-flight: validate the active context up front so we fail
                // loudly BEFORE iterating instead of N-times mid-batch. The
                // per-row CreateClient call re-checks via RequireContext;
                // doing it here surfaces the failure with bulk-envelope
                // context (entryCount) instead of as a per-row error.
                // -----------------------------------------------------------
                var ctx = _session.Context;
                if (ctx == null)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.ContextNotSet,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "ClientCreateBulk",
                            ["entryCount"] = entries?.Length ?? 0,
                        });
                }

                // Context-level year guard, mirroring CreateClientTools'
                // defensive check (SetContext already enforces >= 2018; this
                // keeps the failure surface identical to single-create).
                if (ctx.Year < 2018)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?>
                        {
                            ["taxYear"] = ctx.Year,
                            ["floor"] = 2018,
                        });
                }

                entries ??= Array.Empty<BulkCreateEntry>();

                // -----------------------------------------------------------
                // Blast-radius brake (P17-008 pattern). Fail-CLOSED at the
                // tool chokepoint when entries.Length exceeds the per-call
                // cap. Creates mint ClientNumbers and allocate detail files —
                // heavier than field writes — so the default cap (100) is
                // stricter than LACERTE_BULK_WRITE_MAX's 500. Same 9020
                // rejection shape as lacerte_field_write_bulk; context
                // carries the create-specific env override name.
                // -----------------------------------------------------------
                int maxAllowed = ResolveMaxBulkCreateEntries();
                if (entries.Length > maxAllowed)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.BulkBatchTooLarge,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "ClientCreateBulk",
                            ["entryCount"] = entries.Length,
                            ["maxAllowed"] = maxAllowed,
                            ["envOverride"] = BulkCreateMaxEnvName,
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
                    // remainder skipped without invoking CreateClient.
                    if (stopped)
                    {
                        skippedCount++;
                        rows.Add(new
                        {
                            index = i,
                            clientId = entry?.ClientId?.ToUpperInvariant(),
                            succeeded = false,
                            skipped = true,
                            reason = "stop-on-error: a prior row failed and continueOnError is false.",
                        });
                        continue;
                    }

                    if (entry == null)
                    {
                        // Defensive guard against a malformed payload — a null
                        // array element is a caller-input bug (9017), not a
                        // server fault.
                        failedCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        rows.Add(new
                        {
                            index = i,
                            clientId = (string?)null,
                            succeeded = false,
                            statusCode = SyntheticStatusCodes.InvalidArgument,
                            statusName = "InvalidArgument",
                            userMessage = "Bulk entry at this index was null.",
                        });
                        if (!continueOnError) stopped = true;
                        continue;
                    }

                    string normalizedClientId = (entry.ClientId ?? string.Empty).ToUpperInvariant();

                    // Per-row idempotency key: '{prefix}-{i}-{clientId}'.
                    // POSITIONAL (index component) — a retry must resubmit
                    // the same entries[] in the same order. Built from the
                    // NORMALIZED clientId so 'abc1' and 'ABC1' retries hash
                    // to the same key, matching the value the session stores.
                    string? rowKey = string.IsNullOrEmpty(idempotencyKey)
                        ? null
                        : $"{idempotencyKey}-{i}-{normalizedClientId}";

                    try
                    {
                        // Per-row validation — identical gates, codes, and
                        // context shapes to CreateClientTools so single and
                        // bulk fail observationally identically.
                        var canonicalTaxType = ValidateEntry(entry, normalizedClientId);

                        var args = new CreateClientArgs(
                            ClientId: normalizedClientId,
                            TaxType: canonicalTaxType,
                            FirstName: entry.FirstName,
                            LastName: entry.LastName,
                            EmailId: entry.EmailId);

                        var result = await _session.CreateClient(
                            args, dryRun, rowKey, cancellationToken).ConfigureAwait(false);

                        // Translate non-Success into a structured per-row
                        // error so a row never reads succeeded=true with a
                        // failure TAPIStatus (mirrors the single tool, which
                        // throws on non-Success).
                        if (result.Status != StatusCodes.Success)
                        {
                            throw new LacerteSDKError(
                                result.Status,
                                new Dictionary<string, object?>
                                {
                                    ["clientId"] = normalizedClientId,
                                    ["taxType"] = canonicalTaxType,
                                });
                        }

                        var info = StatusCodes.GetStatusInfo(result.Status);
                        succeededCount++;
                        rows.Add(new
                        {
                            index = i,
                            clientId = normalizedClientId,
                            succeeded = true,
                            statusCode = result.Status,
                            statusName = info.Name,
                            // R6-22 — dry-run create returns a null
                            // ClientNumber in BOTH modes (a dry run cannot
                            // mint). Surface wouldCreate=true instead of a
                            // number so the LLM never chases a phantom id.
                            clientNumber = result.ClientNumber,
                            wouldCreate = dryRun,
                        });
                    }
                    catch (LacerteSDKError sdkErr)
                    {
                        failedCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        rows.Add(new
                        {
                            index = i,
                            clientId = normalizedClientId,
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
                        // Stop the batch; the partial envelope carries
                        // stoppedEarly + processedCount. The in-flight row is
                        // NOT recorded — its outcome is unknown and a keyed
                        // retry will replay or re-execute it safely.
                        stoppedEarly = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Non-LacerteSDKError exceptions aggregate per-row so
                        // one bad row doesn't kill the whole bulk envelope.
                        failedCount++;
                        if (firstFailureIndex == null) firstFailureIndex = i;
                        rows.Add(new
                        {
                            index = i,
                            clientId = normalizedClientId,
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
                        perRowKeyShape = "{prefix}-{index}-{clientId}",
                        note = canResume
                            ? "Resubmit the same entries[] (same order — per-row keys are positional) with the same idempotencyKey: already-created rows replay from cache with their original clientNumbers, failed/skipped rows re-execute. Do NOT slice or reorder entries[] — that breaks the positional key alignment and re-attempts creates that already landed (DuplicateClient per row)."
                            : (dryRun
                                ? "Dry-run results ARE stored in the idempotency cache (dryRun is part of the payload fingerprint), so the commit pass MUST use a DIFFERENT idempotencyKey prefix than this preview pass — reusing the preview prefix fails every previewed row with 9102 IdempotencyConflict. Convention: preview with 'create-<batchId>-preview', then commit with dryRun=false and prefix 'create-<batchId>'."
                                : "No idempotencyKey was supplied, so a resubmit would re-attempt every create — already-created rows then fail with DuplicateClient. Re-run the batch with an idempotencyKey prefix to enable safe retries on future failures."),
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
        /// Per-row validation, mirroring <see cref="CreateClientTools"/>
        /// gate-for-gate (same codes, same context shapes, same order):
        /// clientId required/length/charset, taxType required, firstName/
        /// lastName/emailId required, module accept-gate + canonicalization.
        /// Returns the canonical TaxType to pass to the session. Throws
        /// <see cref="LacerteSDKError"/> on the first failed gate.
        /// </summary>
        private string ValidateEntry(BulkCreateEntry entry, string normalizedClientId)
        {
            if (string.IsNullOrEmpty(entry.ClientId))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.ClientIDTooLong,
                    new Dictionary<string, object?>
                    {
                        ["clientId"] = entry.ClientId,
                        ["reason"] = "LengthExceeded - clientId is required and must be 1-8 alphanumeric chars.",
                    });
            }

            if (normalizedClientId.Length > 8)
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.ClientIDTooLong,
                    new Dictionary<string, object?>
                    {
                        ["clientId"] = normalizedClientId,
                        ["length"] = normalizedClientId.Length,
                        ["limit"] = 8,
                        ["reason"] = "LengthExceeded - clientId exceeds the 8-character limit.",
                    });
            }

            if (!_rxClientId.IsMatch(normalizedClientId))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["clientId"] = normalizedClientId,
                        ["reason"] = "clientId must match ^[A-Z0-9]+$ - letters and digits only.",
                    });
            }

            if (string.IsNullOrWhiteSpace(entry.TaxType))
            {
                throw new LacerteSDKError(
                    StatusCodes.TaxModuleMismatch,
                    new Dictionary<string, object?>
                    {
                        ["taxType"] = entry.TaxType,
                        ["reason"] = "TaxType is required (e.g. 'Individual', '1040').",
                    });
            }

            if (string.IsNullOrWhiteSpace(entry.FirstName))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "firstName",
                        ["reason"] = "FirstName is required per spec §4 / REQUIREMENTS §Create.",
                    });
            }

            if (string.IsNullOrWhiteSpace(entry.LastName))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "lastName",
                        ["reason"] = "LastName is required per spec §4 / REQUIREMENTS §Create.",
                    });
            }

            if (string.IsNullOrWhiteSpace(entry.EmailId))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "emailId",
                        ["reason"] = "EmailId is required per spec §4 / REQUIREMENTS §Create.",
                    });
            }

            // Module accept-gate + alias canonicalization (P15-059 pattern,
            // identical to CreateClientTools): the live SDK accepts only the
            // 9 spec ReturnType strings, so "1040" must canonicalize to
            // "Individual" before the session call.
            string canonicalTaxType = entry.TaxType;
            var sessionCtx = _session.Context;
            if (sessionCtx != null)
            {
                var moduleChar = sessionCtx.Module;
                var accepted = Modules.TaxTypeAliasesFor(moduleChar);
                if (accepted != null && !Modules.AcceptsTaxType(moduleChar, entry.TaxType))
                {
                    throw new LacerteSDKError(
                        StatusCodes.TaxModuleMismatch,
                        new Dictionary<string, object?>
                        {
                            ["taxType"] = entry.TaxType,
                            ["module"] = moduleChar.ToString(),
                            ["acceptedTaxTypes"] = accepted,
                            ["reason"] = $"TaxType '{entry.TaxType}' is not valid for module '{moduleChar}'.",
                        });
                }
                canonicalTaxType = Modules.CanonicalizeTaxType(moduleChar, entry.TaxType) ?? entry.TaxType;
            }

            return canonicalTaxType;
        }
    }
}
