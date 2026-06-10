using System.Collections.Generic;

namespace LacertMCP.Server.Errors
{
    /// <summary>
    /// MCP-injected status codes — emitted <b>before</b> any SDK call lands. These cover
    /// pre-flight validation, auth-lifecycle gates, and bridge runtime failures.
    ///
    /// <para>
    /// Range layout per <c>DECISIONS.md</c> 2026-05-30 "Synthetic statusCode range":
    /// <list type="bullet">
    ///   <item><description><b>9001–9099</b> — validation / preflight gates (port from agent 05 §6 9001–9009).</description></item>
    ///   <item><description><b>9100–9199</b> — auth lifecycle (registration / 24h expiry / GUID missing).</description></item>
    ///   <item><description><b>9200–9299</b> — COM bridge / runtime failures.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Codes 9001-9009 are ported verbatim from agent 05 §6. Bridge codes 9201-9203
    /// live in the 9200 range per DECISIONS.md 2026-05-30; higher codes are reserved
    /// per the range plan above and added as auth + bridge paths land in later phases.
    /// </para>
    /// </summary>
    public static class SyntheticStatusCodes
    {
        // ---- 9001–9099: validation / preflight gates ----
        public const int FieldIsReadOnly = 9001;
        public const int ClientIDTooLong = 9002;
        public const int TaxYearTooOld = 9003;
        public const int StateAbbrevRequired = 9004;
        public const int SubClientIDRequired = 9005;
        public const int AppNotRegistered = 9006;
        public const int LoginExpired = 9007;
        public const int DetailWriteInvalid = 9008;
        public const int ContextNotSet = 9009;

        // 9012 — field not in access map (follow-up F3). Distinct from 9001:
        // 9001 means "ReadOnly/Hidden/NotExposed/ReadOnly-session" — the field
        // is known and is intentionally non-writable. 9012 means "we have no
        // entry for this field" — the operator can populate the map with
        // `LacertMCP.FieldAccessDumper.exe` then call the `lacerte_reload_schema`
        // MCP tool, or call `lacerte_field_access_map` to see what IS writable.
        // P16-001/P16-016: previous remediation pointed at the deprecated
        // `lacerte-mcp field-access discover` CLI verb (now exits 3) and at a
        // `force:true` parameter that is not exposed on any MCP tool — both
        // dead-ends. The new text routes to working surfaces only.
        public const int UnknownFieldAccess = 9012;

        // 9013 — dataPath failed PathValidation in SetContext. Distinct from
        // 9009 (ContextNotSet — missing entirely): the operator supplied a
        // dataPath that is relative, UNC (without the env escape hatch),
        // contains '..' traversal, contains control characters, or escapes the
        // LACERTE_DATA_ROOT allowlist when that env var is set. Carried
        // context includes {dataPath, reason} so the tool layer can render the
        // specific rejection.
        public const int InvalidDataPath = 9013;

        // 9014 — taxYear exceeds the installed Lacerte coverage ceiling
        // (DateTime.Today.Year + 1). Symmetric with 9003 TaxYearTooOld (the
        // floor side). Coordination slot for P15-077 (B_session): SetContext +
        // SimulateCreateClient + MetadataTools + ConnectionStringBuilder all
        // throw this so the wire shape stays consistent across enforcement
        // points. Context includes {taxYear, ceiling}.
        public const int TaxYearTooNew = 9014;

        // 9015 — FieldWriteTools.value passed through a non-scalar CLR or
        // JSON shape (Array / Object JsonElement, arbitrary CLR object, …).
        // Distinct from 9008 DetailWriteInvalid (which gates the row-level
        // Series/Code/Amount shape on detail writes). P15-051 closes the
        // step-2 half of P14-050: BridgeInputValidation only branches on
        // string-typed values, so a JsonElement(Array) or arbitrary object
        // bypasses both bridge and session validation. The session-level
        // NormalizeFieldWriteValue helper runs BEFORE BuildPayload so the
        // normalized form is what gets fingerprinted for idempotency
        // (otherwise int 42 vs JsonElement(42) would hash differently and
        // silently break replay). Context includes {field, clrType, reason}.
        public const int FieldWriteInvalid = 9015;

        // 9016 — caller-supplied assertion (year or readOnly) does NOT
        // match the active session context. Distinct from 9009 ContextNotSet
        // (which means context is missing entirely): context IS set, just
        // for a different year/readOnly value than the caller expected.
        // P16-063: lacerte_field_write_bulk's preflight previously reused
        // 9009 for the mismatch case, but 9009's canonical remediation
        // ("Call lacerte_set_context first") actively misleads the operator
        // when context is already set — they call session_status, see a
        // non-null context, and get stuck. 9016 has a remediation that
        // names the mismatch and tells the operator to either fix the
        // context OR drop the mismatching assertion from the tool call.
        // Context includes {reason, expectedTaxYear/actualTaxYear} or
        // {reason, expectedReadOnly/actualReadOnly}.
        public const int ContextMismatch = 9016;

        // 9017 — a tool-layer argument failed validation. Distinct from 9013
        // InvalidDataPath (strictly about the SetContext dataPath surface),
        // 9008 DetailWriteInvalid (Series/Code/Amount shape on detail rows),
        // and 9015 FieldWriteInvalid (non-scalar `value` on lacerte_field_write).
        // 9017 covers any other per-argument parse/range/format failure
        // (e.g. sinceUtc ISO-8601 parse, statusFilter enum set). The context
        // dict carries {argument, value, reason} so the tool layer can render
        // the specific rejection without the LLM mis-rerouting to a different
        // tool. P16-044/P16-048: previously EFileTools borrowed 9013 for a
        // sinceUtc parse failure, which made the wire envelope suggest a path
        // problem (and the LLM would rerun lacerte_set_context instead of
        // fixing the date string). New tools doing per-argument validation
        // should reuse 9017 with the {argument, value, reason} shape rather
        // than reaching for whatever code is handy.
        public const int InvalidArgument = 9017;

        // 9019 — lacerte_detail_write replaceIndex is supplied in LIVE mode.
        // P17-001: the live SDK's WriteToDetailFile COM method only appends
        // (spec §2); there is no documented update-by-index path. Prior
        // behavior silently dropped replaceIndex on the floor (two
        // `_ = replaceIndex;` discards in LacerteSessionManager.WriteDetail's
        // live branch), so an operator-requested row REPLACE became a live
        // APPEND while the tool returned statusCode=Success — the worst
        // possible two-mode-discipline violation: data divergence with a
        // green light. Fail-CLOSED at the session chokepoint before the
        // wire call so sim (which honors replaceIndex via
        // ApplySimDetailWriteWithUndo) and live throw the same structured
        // error rather than diverging silently. Remediation: read the
        // existing rows via lacerte_detail_read, append the corrected row,
        // and delete the stale row via the (future) lacerte_detail_delete
        // tool. Context carries {op, clientNumber, replaceIndex}.
        public const int LiveReplaceIndexUnsupported = 9019;

        // 9020 — a bulk tool's entries.Length exceeds its per-call cap
        // (field/detail bulk: default 500 via LACERTE_BULK_WRITE_MAX;
        // create bulk: default 100 via LACERTE_BULK_CREATE_MAX). Bulk
        // tools fan out N writes through the live SDK from a single MCP
        // call; without an upper bound a fuzzy LLM-constructed payload can
        // mutate thousands of rows with no per-row inspection. The project's
        // own pagination layer (Pagination.MaxLimit=1000) bounds every read
        // tool — writes have strictly higher blast radius and deserve at
        // least the same ceiling. P17-008. Context carries {entryCount,
        // maxAllowed, table}.
        public const int BulkBatchTooLarge = 9020;

        // 9021 — lacerte_field_write_bulk caller-supplied expectedCount does
        // NOT equal entries.Length. Symmetric with 9016 ContextMismatch (a
        // pre-flight cross-check that catches LLM-vs-operator intent drift
        // before any row runs). The operator's prompt says "write to exactly
        // 47 NY clients" — passing expectedCount=47 lets the system enforce
        // the count instead of the operator scanning a 200-row perItem
        // afterwards. P17-008. Context carries {expected, actual, table}.
        public const int BulkBatchCountMismatch = 9021;

        // ---- 9100–9199: auth lifecycle ----
        // 9101 reserved for the architecture-doc cross-reference (re-auth surfacing path).
        // 9102 idempotency-conflict per session manager design in Todo.md task #31.
        public const int ReAuthRequired = 9101;
        public const int IdempotencyConflict = 9102;

        // 9103 — appId failed bridge-boundary shape check (length cap, COM-unsafe
        // chars, or control codes) BEFORE the dynamic dispatch. Distinct from
        // 9006 AppNotRegistered (which means "Register() never ran"). P15-007:
        // bridge ValidateAppId previously raised 9006 for malformed appIds,
        // sending LLM clients into "rerun register" loops that don't fix
        // anything — a fresh code lets the remediation point at unregister/
        // re-register OR at the LACERTE_APPID env override.
        public const int InvalidAppId = 9103;

        // ---- 9200–9299: COM bridge / runtime ----
        // 9201/9202 are the canonical bridge-range codes (DECISIONS.md 2026-05-30 "Synthetic statusCode range").
        //
        // P15-035 — 9203 InternalServerError covers in-server faults (NullRef,
        // InvalidOperationException, JsonException, FileNotFoundException, ...)
        // that bubble out of a tool handler. Distinct from 9201 (the bridge
        // process itself failed to start) and from StatusCodes.Error=0 (which
        // means the SDK explicitly returned code 0). The split prevents the
        // wrapper from misattributing a server-side bug as an SDK trace and
        // pointing the operator at Windows Event Viewer for a stack that
        // doesn't exist there.
        public const int BridgeUnavailable = 9201;
        public const int SimulationOnly = 9202;
        public const int InternalServerError = 9203;

        /// <summary>
        /// Synthetic code → <see cref="StatusInfo"/> table. Mirrors agent 05 §6 columns
        /// (Emitted When + Remediation), with severity defaulting to <see cref="Severity.Error"/>
        /// (these are always failure terminals at the tool layer).
        /// </summary>
        public static readonly IReadOnlyDictionary<int, StatusInfo> StatusTable = new Dictionary<int, StatusInfo>
        {
            [FieldIsReadOnly] = new StatusInfo(
                "FieldIsReadOnly",
                Severity.Error,
                "Caller tried to write a field marked ReadOnly/Hidden/NotExposed in the access map.",
                "Remove the field from the write payload, or use the corresponding read tool."),

            [ClientIDTooLong] = new StatusInfo(
                "ClientIDTooLong",
                Severity.Error,
                "clientId exceeds 8 characters, or contains a disallowed character.",
                "Trim to <= 8 alphanumeric chars; no special chars."),

            [TaxYearTooOld] = new StatusInfo(
                "TaxYearTooOld",
                Severity.Error,
                "Tax year is below the SDK floor (2018).",
                "Pass a TaxYear >= 2018."),

            [StateAbbrevRequired] = new StatusInfo(
                "StateAbbrevRequired",
                Severity.Error,
                "DATA3 or DATA4 write attempted without stateAbbrev.",
                "Pass a 2-letter stateAbbrev (e.g. CA)."),

            [SubClientIDRequired] = new StatusInfo(
                "SubClientIDRequired",
                Severity.Error,
                "DATA2 or DATA4 write attempted without subClientId.",
                "Pass a positive integer subClientId."),

            [AppNotRegistered] = new StatusInfo(
                "AppNotRegistered",
                Severity.Error,
                "A read/write tool was called before the operator ran the `lacerte-mcp register` CLI command. Registration cannot be performed through MCP — it requires interactive consent against the local Lacerte install.",
                "Ask the operator to run the shell command `lacerte-mcp register` once on the Lacerte box (this is a CLI command, not an MCP tool) to open the Intuit consent dialog, then restart the MCP server in your host (Claude Desktop / Cursor / Windsurf)."),

            [LoginExpired] = new StatusInfo(
                "LoginExpired",
                Severity.Error,
                "The 24h Intuit auth ticket lapsed; the one-time consent is still valid — only the sign-in needs to refresh.",
                "Ask the operator to run the shell command `lacerte-mcp login` on the Lacerte box (this is a CLI command, not an MCP tool) to refresh the auth ticket, then restart the MCP server. Use `lacerte-mcp register` only if `login` reports the AppID was never registered."),

            [DetailWriteInvalid] = new StatusInfo(
                "DetailWriteInvalid",
                Severity.Error,
                "lacerte_detail_write failed client-side validation (Series/Code/Amount shape).",
                "Fix the failed field per the validation message; the SDK would silently corrupt the return otherwise."),

            [ContextNotSet] = new StatusInfo(
                "ContextNotSet",
                Severity.Error,
                "A read/write tool was called before lacerte_set_context provided Datapath + TaxYear + ReturnType.",
                "First call `lacerte_list_datapaths` to discover available (path, year, module) triples, then `lacerte_list_modules` for the module catalog (I/C/P/S/F/R/T/N/B), then `lacerte_set_context(dataPath, taxYear, module)` with the chosen triple. In simulation mode the canonical seeded paths are `C:\\Lacerte\\20Tax\\IData` (TY20 individual), `C:\\Lacerte\\24Tax\\IData` (TY24 individual), `C:\\Lacerte\\24Tax\\CData` (TY24 corp), and `C:\\Lacerte\\24Tax\\PData` (TY24 partnership)."),

            [UnknownFieldAccess] = new StatusInfo(
                "UnknownFieldAccess",
                Severity.Error,
                "Field not in access map — the per-module shard has no entry for this (module, table, field). The v1 base shards ship empty for many modules.",
                "To recover in-session: call `lacerte_field_access_map(module, table, taxYear)` to see what IS writable for this slot — if the field is genuinely supported but uncataloged, pick a mapped field. To permanently populate the map: run `LacertMCP.FieldAccessDumper.exe --module <I|C|P|S|F|R|T|N|B> --table <1..7>` on a Windows + live-Lacerte box, then call the `lacerte_reload_schema` MCP tool so the new shards take effect without restarting (restart would invalidate the 24h Intuit auth ticket)."),

            [InvalidDataPath] = new StatusInfo(
                "InvalidDataPath",
                Severity.Error,
                "lacerte_set_context rejected the dataPath — relative path, '..' traversal, control characters, UNC without LACERTE_ALLOW_UNC=1, or outside LACERTE_DATA_ROOT.",
                "Pass an absolute local path under the Lacerte install root (e.g. C:\\Lacerte\\24Tax\\IData). Set LACERTE_ALLOW_UNC=1 to permit \\\\server\\share paths."),

            [TaxYearTooNew] = new StatusInfo(
                "TaxYearTooNew",
                Severity.Error,
                "Tax year exceeds installed Lacerte coverage (above DateTime.Today.Year + 1 — the Lacerte SDK has no data for unreleased years).",
                "Pass a TaxYear at or before the current calendar year (DateTime.Today.Year). The +1 buffer tolerates early-year planning for the next TY."),

            [FieldWriteInvalid] = new StatusInfo(
                "FieldWriteInvalid",
                Severity.Error,
                "lacerte_field_write 'value' was not a JSON-serializable scalar (string, number, bool, or null). Arrays, objects, and arbitrary CLR types are rejected at the session chokepoint before any wire call.",
                "Pass a scalar value — a string, number, bool, or null. Use lacerte_detail_write for multi-field row payloads."),

            [ContextMismatch] = new StatusInfo(
                "ContextMismatch",
                Severity.Error,
                "The active session context does not match a caller-supplied assertion (taxYear or readOnly).",
                "Re-call lacerte_set_context with the correct taxYear/readOnly, OR remove the mismatching assertion from your tool call. Do NOT just re-call set_context with the same values — that's the value you asserted against. Inspect context.expectedTaxYear/actualTaxYear (or expectedReadOnly/actualReadOnly) to see exactly what diverged."),

            [InvalidArgument] = new StatusInfo(
                "InvalidArgument",
                Severity.Error,
                "A tool argument failed validation. See context.reason for the specific failure.",
                "Re-call the tool with the argument value corrected per the reason field; do NOT change unrelated state (context, registration, etc.). This is a caller-input bug — not a Lacerte / data-path / context problem."),

            [LiveReplaceIndexUnsupported] = new StatusInfo(
                "LiveReplaceIndexUnsupported",
                Severity.Error,
                "lacerte_detail_write received a replaceIndex while running in LIVE mode. The Lacerte SDK's WriteToDetailFile COM method only appends (spec §2); there is no documented update-by-index path. Sim mode honors replaceIndex for parity testing; the live path fails closed rather than silently appending and reporting Success.",
                "To correct a detail row in live mode: (1) call lacerte_detail_read to enumerate existing rows; (2) call lacerte_detail_write WITHOUT replaceIndex to append the corrected row; (3) delete the stale row via lacerte_detail_delete (forthcoming). Until that delete tool ships, surface the divergence to the operator rather than mutating live tax-return data with a stale row left in place."),

            [BulkBatchTooLarge] = new StatusInfo(
                "BulkBatchTooLarge",
                Severity.Error,
                "The bulk tool received more entries than the per-call cap allows. See context.op for the originating tool, and context.entryCount / context.maxAllowed for the specific numbers.",
                "Split the batch into chunks of <= maxAllowed entries and submit sequentially with a shared idempotencyKey prefix — succeeded rows replay from cache on resubmit, so chunked submission stays safe under retry. Override the cap via the env var in context.envOverride (LACERTE_BULK_WRITE_MAX for field/detail bulk, default 500; LACERTE_BULK_CREATE_MAX for client create bulk, default 100) if your operator workflow genuinely needs larger batches; do NOT loop the call client-side without an idempotencyKey."),

            [BulkBatchCountMismatch] = new StatusInfo(
                "BulkBatchCountMismatch",
                Severity.Error,
                "lacerte_field_write_bulk caller-supplied expectedCount does not match entries.Length. The expectedCount cross-check exists to catch LLM-vs-operator intent drift BEFORE any row runs.",
                "Inspect context.expected and context.actual to see which side drifted. If the operator's intended target count is correct, fix the entries[] payload to match; if the entries[] payload is correct, drop or update the expectedCount assertion. Do NOT just re-call with expectedCount = entries.Length — that defeats the cross-check."),

            [ReAuthRequired] = new StatusInfo(
                "ReAuthRequired",
                Severity.Error,
                "The 24h Intuit auth ticket expired and was reactively detected (NotSignedIn).",
                "Ask the operator to run the shell command `lacerte-mcp login` on the Lacerte box (this is a CLI command, not an MCP tool) to refresh the auth ticket, then restart the MCP server. Fall back to `lacerte-mcp register` only if `login` reports the AppID is unknown."),

            [IdempotencyConflict] = new StatusInfo(
                "IdempotencyConflict",
                Severity.Error,
                "The same idempotency key was reused with a different payload fingerprint.",
                "Pick a fresh idempotency key, or replay the original payload byte-for-byte."),

            [InvalidAppId] = new StatusInfo(
                "InvalidAppId",
                Severity.Error,
                "The supplied AppID is malformed or oversized (failed bridge-boundary shape check before COM dispatch).",
                "Ask the operator to mint a fresh AppID: if the AppID came from registration storage, run the shell commands `lacerte-mcp unregister` then `lacerte-mcp register` (CLI, not MCP tools); if it came from a LACERTE_APPID env var, fix or unset that env var."),

            [BridgeUnavailable] = new StatusInfo(
                "BridgeUnavailable",
                Severity.Error,
                "Live mode requested but the .NET/COM bridge process didn't start or crashed.",
                "Confirm running on Windows with the SDK installed. If LACERTE_DEBUG_WIRE=1 is already enabled, grep for the `cid` value on this error envelope inside the wire log (default `./logs/lacerte-YYYYMMDD.log` relative to the MCP server's working directory; override via LACERTE_DEBUG_LOG_PATH) — call `lacerte_session_status` to see the resolved log path. If LACERTE_DEBUG_WIRE is not enabled, set it in your MCP host config (Claude Desktop / Cursor / Windsurf), restart the host, and reproduce — the flag must be on BEFORE the failure to capture the bridge stderr."),

            [SimulationOnly] = new StatusInfo(
                "SimulationOnly",
                Severity.Error,
                "Live-only op (e.g. lacerte_client_create) called while running in simulation mode.",
                "Set LACERTE_LIVE=1 on a Windows + Lacerte machine, or stub the op in the simulation store."),

            [InternalServerError] = new StatusInfo(
                "InternalServerError",
                Severity.Error,
                "The MCP server itself raised an unexpected exception — this is not a Lacerte SDK error.",
                "Capture `exceptionType` from context. If LACERTE_DEBUG_WIRE=1 is enabled, grep for the `cid` value on this error envelope inside the wire log (default `./logs/lacerte-YYYYMMDD.log` relative to the MCP server's working directory; override via LACERTE_DEBUG_LOG_PATH) — call `lacerte_session_status` to see the resolved log path. Otherwise set LACERTE_DEBUG_WIRE=1 in your MCP host config, restart, reproduce, and file an issue with the resulting log block. The flag must be on BEFORE the failure to capture the exception with its arg payload."),
        };
    }
}
