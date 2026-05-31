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
        // `lacerte-mcp field-access discover` or override with force:true.
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
                "ClientID exceeds 8 characters, or contains a disallowed character.",
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
                "DATA2 or DATA4 write attempted without subClientID.",
                "Pass a positive integer subClientID."),

            [AppNotRegistered] = new StatusInfo(
                "AppNotRegistered",
                Severity.Error,
                "Any tool other than lacerte_register was called before a successful Register() landed.",
                "Run lacerte_register once to grant consent and sign in."),

            [LoginExpired] = new StatusInfo(
                "LoginExpired",
                Severity.Error,
                "Cached session manager detects the 24h Intuit login window has lapsed.",
                "Run lacerte_register to re-authenticate (consent is one-time; only the sign-in needs refresh)."),

            [DetailWriteInvalid] = new StatusInfo(
                "DetailWriteInvalid",
                Severity.Error,
                "lacerte_detail_write failed client-side validation (Series/Code/Amount shape).",
                "Fix the failed field per the validation message; the SDK would silently corrupt the return otherwise."),

            [ContextNotSet] = new StatusInfo(
                "ContextNotSet",
                Severity.Error,
                "A read/write tool was called before lacerte_set_context provided Datapath + TaxYear + ReturnType.",
                "Call lacerte_set_context first."),

            [UnknownFieldAccess] = new StatusInfo(
                "UnknownFieldAccess",
                Severity.Error,
                "Field not in access map.",
                "Run `lacerte-mcp field-access discover` to populate the map, or pass force:true to override."),

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

            [ReAuthRequired] = new StatusInfo(
                "ReAuthRequired",
                Severity.Error,
                "The 24h Intuit auth ticket expired and was reactively detected (NotSignedIn).",
                "Run lacerte-mcp login (or lacerte_register) to refresh the auth ticket."),

            [IdempotencyConflict] = new StatusInfo(
                "IdempotencyConflict",
                Severity.Error,
                "The same idempotency key was reused with a different payload fingerprint.",
                "Pick a fresh idempotency key, or replay the original payload byte-for-byte."),

            [InvalidAppId] = new StatusInfo(
                "InvalidAppId",
                Severity.Error,
                "The supplied AppID is malformed or oversized (failed bridge-boundary shape check before COM dispatch).",
                "If the AppID came from registration storage, run `lacerte-mcp unregister` then `lacerte-mcp register` to mint a fresh GUID. If it came from a LACERTE_APPID env var, fix or unset that env var."),

            [BridgeUnavailable] = new StatusInfo(
                "BridgeUnavailable",
                Severity.Error,
                "Live mode requested but the .NET/COM bridge process didn't start or crashed.",
                "Confirm running on Windows with the SDK installed; re-run with LACERTE_DEBUG_WIRE=1 and inspect logs/lacerte-YYYYMMDD.log for the bridge stderr."),

            [SimulationOnly] = new StatusInfo(
                "SimulationOnly",
                Severity.Error,
                "Live-only op (e.g. lacerte_client_create) called while running in simulation mode.",
                "Set LACERTE_LIVE=1 on a Windows + Lacerte machine, or stub the op in the simulation store."),

            [InternalServerError] = new StatusInfo(
                "InternalServerError",
                Severity.Error,
                "The MCP server itself raised an unexpected exception — this is not a Lacerte SDK error.",
                "Capture the exceptionType from context, re-run with LACERTE_DEBUG_WIRE=1, and file an issue with the wire log timestamp."),
        };
    }
}
