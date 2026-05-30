using System.Collections.Generic;

namespace LacertMCP.Server.Errors
{
    /// <summary>
    /// Lacerte SDK status-code catalog. Ported verbatim from
    /// <c>docs/research/agents/05-error-mapping.md</c> §1 (numeric table) and §3 (severity + user
    /// messages + remediation).
    ///
    /// <para>
    /// Two SDK enums are folded into one flat namespace here:
    /// <list type="bullet">
    ///   <item><description><c>TAPIStatus</c> (general SDK calls) → codes 0–28.</description></item>
    ///   <item><description><c>TPSDKStatus</c> (registration) → codes 200–204, prefixed with
    ///     <c>Reg</c> on the collision (<c>RegVersionBlocked = 202</c>) per agent 05 §1
    ///     VersionBlocked note. See follow-up F2 in <c>Todo.md</c>.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Codes marked with <c>// TODO F1</c> have integer values inferred from SDK declaration order
    /// in the spec; they MUST be verified against the real SDK headers
    /// (<c>tlbimp LacerteSDK_Interfaces.tlb</c> + ILSpy) before release per agent 05 follow-up #1.
    /// </para>
    /// </summary>
    public static class StatusCodes
    {
        // ---- TAPIStatus (general SDK calls) — spec-literal integer values for 0..9 ----
        public const int Error = 0;
        public const int Success = 1;
        public const int DBFNotAccessable = 2; // sic: "Accessable" per spec
        public const int CodeBaseError = 3;
        public const int OutOfIndex = 4;
        public const int DBNotSupported = 5;
        public const int NotImplemented = 6;
        public const int NotExposed = 7;
        public const int ExposedForRead = 8;
        public const int ExposedForWrite = 9;

        // ---- TAPIStatus — inferred integer values; verify against SDK headers ----
        public const int ExposedForReadWrite = 10;   // TODO F1: verify integer against SDK headers
        public const int VersionBlocked = 11;        // TODO F1: verify integer against SDK headers
        public const int DuplicateClient = 12;       // TODO F1: verify integer against SDK headers
        public const int LengthExceeded = 13;        // TODO F1: verify integer against SDK headers
        public const int TaxModuleMismatch = 14;     // TODO F1: verify integer against SDK headers
        public const int DetailCreationError = 15;   // TODO F1: verify integer against SDK headers
        public const int DB1Error = 16;              // TODO F1: verify integer against SDK headers
        public const int DB2Error = 17;              // TODO F1: verify integer against SDK headers
        public const int DB3Error = 18;              // TODO F1: verify integer against SDK headers
        public const int DB4Error = 19;              // TODO F1: verify integer against SDK headers
        public const int DB5Error = 20;              // TODO F1: verify integer against SDK headers
        public const int DB6Error = 21;              // TODO F1: verify integer against SDK headers
        public const int DB7Error = 22;              // TODO F1: verify integer against SDK headers
        public const int DB8Error = 23;              // TODO F1: verify integer against SDK headers
        public const int InValidClientName = 24;     // TODO F1: verify integer against SDK headers (sic: "InValid")
        public const int InValidTaxYear = 25;        // TODO F1: verify integer against SDK headers
        public const int FieldNotSupported = 26;     // TODO F1: verify integer against SDK headers
        public const int InValidSubClientID = 27;    // TODO F1: verify integer against SDK headers
        public const int InValidStateAbbrev = 28;    // TODO F1: verify integer against SDK headers

        // ---- TPSDKStatus (registration) — namespaced 200+ to avoid TAPIStatus collision ----
        // Per agent 05 §1 follow-up F2: VersionBlocked may collide with TAPIStatus 11; registered
        // as RegVersionBlocked here. Revisit during Phase 9 when both paths first hit live.
        public const int ConsentDenied = 200;            // TODO F1: verify integer against SDK headers
        public const int AccessGranted = 201;            // TODO F1: verify integer against SDK headers
        public const int RegVersionBlocked = 202;        // TODO F1/F2: verify integer + collapse-or-keep collision strategy
        public const int NotSignedIn = 203;              // TODO F1: verify integer against SDK headers
        public const int LacerteTaxSetupRequired = 204;  // TODO F1: verify integer against SDK headers

        /// <summary>
        /// Canonical status code → <see cref="StatusInfo"/> table.
        /// Ported from agent 05 §3 (severity + userMessage + remediation columns).
        /// </summary>
        public static readonly IReadOnlyDictionary<int, StatusInfo> StatusTable = new Dictionary<int, StatusInfo>
        {
            [Error] = new StatusInfo(
                "Error",
                Severity.Error,
                "Lacerte returned a generic error with no further detail.",
                "Re-run with LACERTE_DEBUG=1 to capture the bridge call args; check Windows Event Viewer for SDK traces."),

            [Success] = new StatusInfo(
                "Success",
                Severity.Success,
                "OK.",
                null),

            [DBFNotAccessable] = new StatusInfo(
                "DBFNotAccessable",
                Severity.Error,
                "The Lacerte data file is missing, locked, or on a path this MCP can't reach.",
                "Confirm LACERTE_DATAPATH exists and the user account has read/write on it; close the file in Lacerte if open exclusively."),

            [CodeBaseError] = new StatusInfo(
                "CodeBaseError",
                Severity.Error,
                "The Lacerte DBF index is corrupt or unreadable.",
                "Open Lacerte and run File -> Repair Indexes for the affected module/year, then retry."),

            [OutOfIndex] = new StatusInfo(
                "OutOfIndex",
                Severity.Error,
                "The row number passed to a pre-TY21 write doesn't exist in this table.",
                "Re-query the table to refresh row numbers, or switch to the TY21+ UpdateTaxReturn path (keyed by ClientNumber)."),

            [DBNotSupported] = new StatusInfo(
                "DBNotSupported",
                Severity.Error,
                "This DBF table isn't supported for the requested module/year combo.",
                "Check lacerte_list_modules for which tables/modules are actually installed."),

            [NotImplemented] = new StatusInfo(
                "NotImplemented",
                Severity.Error,
                "The SDK doesn't implement this operation yet.",
                "No workaround — file with Intuit; the MCP cannot fix this."),

            [NotExposed] = new StatusInfo(
                "NotExposed",
                Severity.Error,
                "This field exists in the DBF but the SDK refuses to expose it.",
                "Don't reference this field — remove it from the request payload."),

            // Per agent 05 §3: ExposedForRead is "warning" at the table level so the access-map
            // tool doesn't surface isError on a normal access-query answer. Callers escalate
            // to error context-locally when the answer arrives from a data call.
            [ExposedForRead] = new StatusInfo(
                "ExposedForRead",
                Severity.Warning,
                "This field is read-only.",
                "Use lacerte_table_read instead of a write tool for this field."),

            [ExposedForWrite] = new StatusInfo(
                "ExposedForWrite",
                Severity.Warning,
                "This field is write-only.",
                "Don't try to read this field; it's input-only."),

            [ExposedForReadWrite] = new StatusInfo(
                "ExposedForReadWrite",
                Severity.Warning,
                "This field supports both read and write.",
                null),

            [VersionBlocked] = new StatusInfo(
                "VersionBlocked",
                Severity.Error,
                "Your installed Lacerte/SDK build is too old for this operation.",
                "Upgrade Lacerte, or set LACERTE_DEFAULT_TAX_YEAR to a year your build supports."),

            [DuplicateClient] = new StatusInfo(
                "DuplicateClient",
                Severity.Error,
                "A client with that ClientID already exists for this tax year/module.",
                "Pick a different ClientID, or call lacerte_client_read to load the existing record."),

            [LengthExceeded] = new StatusInfo(
                "LengthExceeded",
                Severity.Error,
                "A field value is longer than Lacerte allows (ClientID > 8 chars is the most common cause).",
                "Trim the value; ClientIDs must be <= 8 alphanumeric characters with no special chars."),

            [TaxModuleMismatch] = new StatusInfo(
                "TaxModuleMismatch",
                Severity.Error,
                "The connection's ReturnType doesn't match the module character of the DBF being addressed.",
                "Call lacerte_set_context again with the correct ReturnType for the module char in the DBF filename."),

            [DetailCreationError] = new StatusInfo(
                "DetailCreationError",
                Severity.Error,
                "Failed to create the per-client detail file (.IDx) for a new client.",
                "Verify disk space and write permission on the datapath; retry lacerte_client_create."),

            [DB1Error] = new StatusInfo(
                "DB1Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA1).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB2Error] = new StatusInfo(
                "DB2Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA2).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB3Error] = new StatusInfo(
                "DB3Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA3).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB4Error] = new StatusInfo(
                "DB4Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA4).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB5Error] = new StatusInfo(
                "DB5Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA5).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB6Error] = new StatusInfo(
                "DB6Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA6).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB7Error] = new StatusInfo(
                "DB7Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA7).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [DB8Error] = new StatusInfo(
                "DB8Error",
                Severity.Error,
                "A table-specific DBF error occurred (DATA8 — reserved; if this fires, treat as SDK bug).",
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year."),

            [InValidClientName] = new StatusInfo(
                "InValidClientName",
                Severity.Error,
                "The client name contains characters Lacerte rejects.",
                "Use letters, numbers, spaces, hyphen/apostrophe only; avoid < > \" ' & / \\ | ?."),

            [InValidTaxYear] = new StatusInfo(
                "InValidTaxYear",
                Severity.Error,
                "The requested tax year is < 2018 or not installed on this machine.",
                "Set LACERTE_DEFAULT_TAX_YEAR >= 2018, or install the Lacerte module for that year."),

            [FieldNotSupported] = new StatusInfo(
                "FieldNotSupported",
                Severity.Error,
                "The field name doesn't exist in this module/year's schema.",
                "Cross-check lacerte_field_access_map for the valid field list at this year/module."),

            [InValidSubClientID] = new StatusInfo(
                "InValidSubClientID",
                Severity.Error,
                "SubClientID must be > 0 for DATA2/DATA4 writes.",
                "Pass a positive integer SubClientID identifying the dependent/partner/shareholder row."),

            [InValidStateAbbrev] = new StatusInfo(
                "InValidStateAbbrev",
                Severity.Error,
                "StateAbbrev is required and must be a recognized 2-letter code for DATA3/DATA4 writes.",
                "Pass the 2-letter postal code (e.g. CA, NY); see lacerte_list_modules for supported states."),

            // ---- TPSDKStatus (registration) 200–204 ----
            [ConsentDenied] = new StatusInfo(
                "ConsentDenied",
                Severity.Error,
                "You declined consent in the Lacerte registration dialog.",
                "Re-run lacerte_register and click Accept on the consent dialog."),

            [AccessGranted] = new StatusInfo(
                "AccessGranted",
                Severity.Success,
                "Lacerte registration succeeded.",
                null),

            [RegVersionBlocked] = new StatusInfo(
                "RegVersionBlocked",
                Severity.Error,
                "Registration is blocked because Lacerte/SDK is below the minimum version.",
                "Upgrade Lacerte to the minimum supported build, then re-run lacerte_register."),

            [NotSignedIn] = new StatusInfo(
                "NotSignedIn",
                Severity.Error,
                "No active Intuit login, or the 24-hour login window has expired.",
                "Run lacerte_register (or open Lacerte and sign in) to refresh the login token."),

            [LacerteTaxSetupRequired] = new StatusInfo(
                "LacerteTaxSetupRequired",
                Severity.Error,
                "Lacerte itself isn't installed or fully initialized on this machine.",
                "Install Lacerte TY18+ and complete its first-run setup before retrying MCP registration."),
        };

        /// <summary>
        /// Returns the <see cref="StatusInfo"/> for a numeric status code. Unknown codes return
        /// an <c>Unknown(N)</c> fallback with <see cref="Severity.Error"/>, matching the TS
        /// behavior in agent 05 §2.
        /// </summary>
        public static StatusInfo GetStatusInfo(int code)
        {
            if (StatusTable.TryGetValue(code, out var info))
            {
                return info;
            }

            return new StatusInfo(
                $"Unknown({code})",
                Severity.Error,
                $"Lacerte SDK returned an unrecognized status code ({code}).",
                "Capture the SDK call + arguments and file an issue; verify against the SDK enum headers.");
        }
    }
}
