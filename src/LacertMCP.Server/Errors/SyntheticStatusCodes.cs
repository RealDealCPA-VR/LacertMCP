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
    /// Codes 9001-9011 are ported verbatim from agent 05 §6. Higher codes are reserved
    /// per the range plan above; entries are added as the auth + bridge paths land in
    /// later phases.
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

        // ---- 9100–9199: auth lifecycle ----
        // 9101 reserved for the architecture-doc cross-reference (re-auth surfacing path).
        // 9102 idempotency-conflict per session manager design in Todo.md task #31.
        public const int ReAuthRequired = 9101;
        public const int IdempotencyConflict = 9102;

        // ---- 9200–9299: COM bridge / runtime ----
        // Renumbered from agent 05 §6 (which placed BridgeUnavailable=9010 and
        // SimulationOnly=9011 in the validation range) into the bridge range
        // per DECISIONS.md 2026-05-30. Validation-range constants 9010+9011 are
        // kept as aliases below so any in-flight code that references them still
        // resolves; remove once Stream 4 lands.
        public const int BridgeUnavailable = 9201;
        public const int SimulationOnly = 9202;

        // Back-compat aliases for the original agent 05 §6 numbering.
        // TODO: drop once all call sites use the 9200-range names.
        public const int BridgeUnavailableLegacy = 9010;
        public const int SimulationOnlyLegacy = 9011;

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

            [BridgeUnavailable] = new StatusInfo(
                "BridgeUnavailable",
                Severity.Error,
                "Live mode requested but the .NET/COM bridge process didn't start or crashed.",
                "Confirm running on Windows with the SDK installed; check LACERTE_BRIDGE_LOG for the bridge stderr."),

            [SimulationOnly] = new StatusInfo(
                "SimulationOnly",
                Severity.Error,
                "Live-only op (e.g. lacerte_client_create) called while running in simulation mode.",
                "Set LACERTE_LIVE=1 on a Windows + Lacerte machine, or stub the op in the simulation store."),

            // Back-compat aliases (same StatusInfo). See TODO above.
            [BridgeUnavailableLegacy] = new StatusInfo(
                "BridgeUnavailable",
                Severity.Error,
                "Live mode requested but the .NET/COM bridge process didn't start or crashed.",
                "Confirm running on Windows with the SDK installed; check LACERTE_BRIDGE_LOG for the bridge stderr."),

            [SimulationOnlyLegacy] = new StatusInfo(
                "SimulationOnly",
                Severity.Error,
                "Live-only op (e.g. lacerte_client_create) called while running in simulation mode.",
                "Set LACERTE_LIVE=1 on a Windows + Lacerte machine, or stub the op in the simulation store."),
        };
    }
}
