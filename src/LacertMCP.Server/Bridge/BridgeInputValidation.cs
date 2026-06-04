using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// P14-050 — bridge-boundary input validation. Every <c>dynamic</c>
    /// COM dispatch in <see cref="Writer"/> / <see cref="Reader"/> takes
    /// caller-controlled strings (appId, dataPath, dbf, clientNumber, field,
    /// stateAbbrev, detail-row strings) which the SDK historically does NOT
    /// re-validate before concatenating into its DBF buffer. Higher layers
    /// (DetailsTools, FieldWriteTools, ConnectionStringBuilder, SqlTemplates)
    /// already gate the public surface — this is the defense-in-depth check at
    /// the BRIDGE boundary so a future caller that bypasses the tool layer
    /// (a script, a regression harness, a refactor) still cannot push a
    /// curly-brace / NUL / oversize string into the late-bound COM marshaller.
    ///
    /// <para>
    /// Patterns are reused from <see cref="LacertMCP.Server.Odbc.SqlTemplates"/>
    /// (<c>^[A-Z0-9]{1,8}$</c> for ClientNumber, <c>^[A-Z]{2}$</c> for
    /// stateAbbrev) and from the same control-character family
    /// <see cref="LacertMCP.Server.Odbc.ConnectionStringBuilder"/> rejects for
    /// <c>dataPath</c>. Throws <see cref="LacerteSDKError"/> BEFORE the
    /// <c>dynamic</c> invocation so the bridge never marshals a bad value.
    /// </para>
    /// </summary>
    internal static class BridgeInputValidation
    {
        // ClientNumber: 1-8 ASCII uppercase alphanumerics. Mirrors
        // Odbc/SqlTemplates.ClientIdentifierPattern verbatim. The 8-char cap
        // is per StatusCodes.LengthExceeded remediation.
        private static readonly Regex ClientNumberPattern = new Regex(
            "^[A-Z0-9]{1,8}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // State abbreviation: 2 uppercase ASCII letters. Mirrors
        // Odbc/SqlTemplates.StatePattern.
        private static readonly Regex StateAbbrevPattern = new Regex(
            "^[A-Z]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // DBF filename per spec §2 / agent 03 §3: DATA{n}{moduleChar}{YY}.DBF
        // with optional trailing module/year mix. We accept a conservative
        // ASCII shape — uppercase letters, digits, dot — to bound what reaches
        // the SDK. Empty rejected; max 16 chars (DATA + digit + char + 2 +
        // .DBF = 11 typical, 16 covers any future variant).
        private static readonly Regex DbfNamePattern = new Regex(
            "^[A-Z0-9]{1,12}\\.[A-Z0-9]{1,4}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // SDK field identifier: C{table}_{ordinal-or-Gn}. Mirrors
        // Odbc/SqlTemplates.ColumnIdentifierPattern but bridge writes also
        // accept additional ordinal widths the read path doesn't expose
        // (e.g. C5_M5 maintenance flags) so we relax to the broader
        // /^[A-Z0-9_]{2,16}$/ shape — narrow enough to reject braces / quotes
        // / semicolons, wide enough that the access-map gate is the source of
        // truth for "is this an actual field".
        private static readonly Regex FieldNamePattern = new Regex(
            "^[A-Z0-9_]{2,16}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // appId is normally a registered AppID GUID rendered 36 chars (D
        // format), but scaffolding / test paths pass arbitrary opaque strings
        // through. We do NOT enforce GUID shape here — the registration
        // chokepoint already does that and operators may legitimately pass
        // through an appId from env. Bridge-level rule: must be ASCII
        // printable, no braces / semicolons / NULs / backticks, length
        // capped to AppIdMaxLength.
        internal const int AppIdMaxLength = 128;

        // Conservative length caps — see refinedRecommendation in
        // audit-findings.json P14-050: 50 for Prefix/Suffix/Source/CityAbbr/
        // ImportSrc, 255 for Description. Other identifier lengths are
        // gated by their regex (8 / 2 / 12.4 / 16) so don't need separate
        // length checks.
        internal const int DescriptionMaxLength = 255;
        internal const int ShortStringMaxLength = 50;
        internal const int StringValueMaxLength = 255;

        // Largest dataPath we'll allow. Windows MAX_PATH=260; we keep the
        // bridge cap a hair under that because the SDK concatenates module
        // file names on top of dataPath internally.
        internal const int DataPathMaxLength = 248;

        // -----------------------------------------------------------------
        // Per-argument validators. Each throws LacerteSDKError with a
        // specific synthetic / SDK status code BEFORE the dynamic call.
        // -----------------------------------------------------------------

        /// <summary>
        /// Validates the appId argument. Empty string is permitted because
        /// some bridge call sites (notably Reader cold-fallback) pass
        /// appId="". When non-empty, appId must (a) be at most
        /// <see cref="AppIdMaxLength"/> chars, (b) contain only ASCII
        /// printable characters, and (c) not contain any of the COM/SQL
        /// unsafe chars: <c>{ } ; ` \" '</c> or control characters.
        /// </summary>
        public static void ValidateAppId(string? appId, string op)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return;
            }
            if (appId!.Length > AppIdMaxLength)
            {
                // P15-007: misclassified as AppNotRegistered (9006) until now.
                // The operator DID register; the persisted/env-supplied AppID
                // just has bad shape, so the 9006 "Run `lacerte-mcp register`"
                // remediation sent LLM clients into infinite loops.
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidAppId,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "appId",
                        ["length"] = appId.Length,
                        ["maxLength"] = AppIdMaxLength,
                        ["reason"] = $"appId length {appId.Length} exceeds {AppIdMaxLength}-char cap.",
                    });
            }
            for (int i = 0; i < appId.Length; i++)
            {
                char c = appId[i];
                bool isUnsafe = c < 0x20 || c == 0x7F
                    || c == '{' || c == '}' || c == ';' || c == '`'
                    || c == '"' || c == '\'';
                if (isUnsafe)
                {
                    // P15-007: see length-cap branch above.
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidAppId,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["field"] = "appId",
                            ["index"] = i,
                            ["charCode"] = (int)c,
                            ["reason"] = $"appId must not contain COM/SQL-unsafe chars (found 0x{((int)c):X2} at index {i}).",
                        });
                }
            }
        }

        /// <summary>
        /// Validates a dataPath argument. Rejects empty, oversize, and any
        /// string containing a control character (&lt; 0x20) — the same
        /// rejection rule used in
        /// <see cref="LacertMCP.Server.Odbc.ConnectionStringBuilder"/>
        /// to bound parser-confusion characters reaching the SDK.
        /// </summary>
        public static void ValidateDataPath(string? dataPath, string op)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidDataPath,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "dataPath",
                        ["reason"] = "dataPath is required (non-empty absolute path).",
                    });
            }
            if (dataPath!.Length > DataPathMaxLength)
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.InvalidDataPath,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "dataPath",
                        ["reason"] = $"dataPath exceeds {DataPathMaxLength}-char cap (got {dataPath.Length}).",
                    });
            }
            for (int i = 0; i < dataPath.Length; i++)
            {
                if (dataPath[i] < 0x20)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.InvalidDataPath,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["field"] = "dataPath",
                            ["reason"] = $"dataPath must not contain control characters (found 0x{((int)dataPath[i]):X2} at index {i}).",
                        });
                }
            }
        }

        /// <summary>
        /// Validates a DBF table name (e.g. <c>DATA1I24.DBF</c>). Bridge
        /// passes this directly into the SDK ProgID dispatch and any value
        /// containing braces, semicolons, or NULs could confuse SDK-internal
        /// string handling.
        /// </summary>
        public static void ValidateDbfName(string? dbf, string op)
        {
            if (string.IsNullOrWhiteSpace(dbf) || !DbfNamePattern.IsMatch(dbf!))
            {
                throw new LacerteSDKError(
                    StatusCodes.DBNotSupported,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "dbf",
                        ["value"] = dbf,
                        ["reason"] = "dbf must match the DATA{n}{module}{YY}.DBF shape (uppercase ASCII alphanumerics + single dot, <= 16 chars).",
                    });
            }
        }

        /// <summary>
        /// Validates a Lacerte ClientNumber identifier. Mirrors
        /// <see cref="LacertMCP.Server.Odbc.SqlTemplates.ValidateClient"/>
        /// at the bridge boundary so the COM dispatch never sees a value
        /// with braces / quotes / specials.
        /// </summary>
        public static void ValidateClientNumber(string? clientNumber, string op)
        {
            if (clientNumber == null || !ClientNumberPattern.IsMatch(clientNumber))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "clientNumber",
                        ["value"] = clientNumber,
                        ["expected"] = "1-8 chars matching ^[A-Z0-9]{1,8}$",
                    });
            }
        }

        /// <summary>
        /// Validates a field identifier (C{t}_N / C{t}_GN family). Bounds
        /// the alphabet so an injected ';' or '}' can never reach the COM
        /// dispatcher.
        /// </summary>
        public static void ValidateFieldName(string? field, string op)
        {
            if (field == null || !FieldNamePattern.IsMatch(field))
            {
                throw new LacerteSDKError(
                    StatusCodes.FieldNotSupported,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "field",
                        ["value"] = field,
                        ["expected"] = "2-16 chars matching ^[A-Z0-9_]{2,16}$",
                    });
            }
        }

        /// <summary>
        /// Validates a state abbreviation. Empty / null is allowed
        /// (UpdateTaxReturn substitutes <c>string.Empty</c> when caller
        /// doesn't supply); when non-empty must be 2 uppercase letters.
        /// </summary>
        public static void ValidateStateAbbrev(string? stateAbbrev, string op)
        {
            if (string.IsNullOrEmpty(stateAbbrev))
            {
                return;
            }
            if (!StateAbbrevPattern.IsMatch(stateAbbrev!))
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidStateAbbrev,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "stateAbbrev",
                        ["value"] = stateAbbrev,
                        ["expected"] = "2 uppercase ASCII letters or empty",
                    });
            }
        }

        /// <summary>
        /// Validates a value argument bound for a COM write. Caller-controlled
        /// strings are checked for control characters (NUL especially — many
        /// SDK string handlers C-string-truncate on the first NUL) and bounded
        /// to <see cref="StringValueMaxLength"/>. Numeric NaN / Infinity are
        /// rejected because the SDK marshaller silently corrupts the DBF cell
        /// otherwise (mirrors the row.Amount guard in
        /// <see cref="ValidateWriteDetailArgs"/>). JsonElement arrays/objects
        /// are rejected — bridge expects scalar values only (P15-061).
        /// </summary>
        public static void ValidateValue(object? value, string op, int maxLength = StringValueMaxLength)
        {
            if (value is string s)
            {
                EnsureSafeText(s, op, fieldName: "value", maxLength: maxLength);
                return;
            }
            // P15-061: numeric NaN/Infinity guard for the generic WriteField /
            // UpdateTaxReturn paths. ValidateWriteDetailArgs already rejects
            // these on row.Amount; mirroring the check here closes the
            // asymmetry so a caller can't silently corrupt a C{t}_n Amount
            // field via the WriteField/UpdateTaxReturn surface.
            if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "value",
                        ["value"] = d,
                        ["reason"] = "value must be a finite real number (no NaN / Infinity).",
                    });
            }
            if (value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "value",
                        ["value"] = (double)f,
                        ["reason"] = "value must be a finite real number (no NaN / Infinity).",
                    });
            }
            // P15-061: JsonElement gating. P14-050 step 2 was supposed to add
            // a CLR type whitelist in LacerteSessionManager.WriteField via
            // JsonElement.ValueKind inspection but never landed. Bridge-side
            // we at minimum reject array/object shapes so a complex JSON
            // payload doesn't reach the COM marshaller as a stringified mess.
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Array || je.ValueKind == JsonValueKind.Object)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["field"] = "value",
                            ["valueKind"] = je.ValueKind.ToString(),
                            ["reason"] = "value must be a scalar (string, number, bool, or null) — array/object payloads are not supported by the bridge.",
                        });
                }
                // Recurse for string/number scalars to apply the same checks.
                if (je.ValueKind == JsonValueKind.String)
                {
                    EnsureSafeText(je.GetString(), op, fieldName: "value", maxLength: maxLength);
                }
                else if (je.ValueKind == JsonValueKind.Number)
                {
                    if (je.TryGetDouble(out var jd) && (double.IsNaN(jd) || double.IsInfinity(jd)))
                    {
                        throw new LacerteSDKError(
                            SyntheticStatusCodes.DetailWriteInvalid,
                            new Dictionary<string, object?>
                            {
                                ["op"] = op,
                                ["field"] = "value",
                                ["value"] = jd,
                                ["reason"] = "value must be a finite real number (no NaN / Infinity).",
                            });
                    }
                }
            }
        }

        /// <summary>
        /// Validates that a string identifier looks numeric. Used at bridge
        /// boundaries that accept numeric values rendered as strings (the
        /// DetailRow.Code field is the canonical case — DetailsTools already
        /// gates this; bridge re-checks defense-in-depth).
        /// </summary>
        public static void ValidateNumericString(string? value, string op, string fieldName)
        {
            if (value == null) return; // optional/empty handled at higher layers
            if (value.Length == 0) return;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (i == 0 && c == '-');
                if (!ok)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["field"] = fieldName,
                            ["value"] = value,
                            ["reason"] = $"{fieldName} must be a numeric integer string (digits with optional leading minus).",
                        });
                }
            }
        }

        /// <summary>
        /// Validates a string field for safe text content + length cap.
        /// Rejects control characters (except <c>\t \n \r</c>) and any value
        /// longer than <paramref name="maxLength"/>.
        /// </summary>
        public static void EnsureSafeText(
            string? value, string op, string fieldName, int maxLength)
        {
            if (value == null) return;
            if (value.Length > maxLength)
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = fieldName,
                        ["length"] = value.Length,
                        ["maxLength"] = maxLength,
                        ["reason"] = $"{fieldName} length {value.Length} exceeds {maxLength}-char cap.",
                    });
            }
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                // Allow \t (0x09), \n (0x0A), \r (0x0D). Reject the rest of
                // C0 (0x00-0x1F) and DEL (0x7F). NUL especially: COM string
                // marshallers historically truncate on the first NUL — an
                // operator-supplied "OK\0; DROP TABLE" would silently turn
                // into "OK" before the DBF gets the value.
                bool isControl = (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7F;
                if (isControl)
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["field"] = fieldName,
                            ["index"] = i,
                            ["charCode"] = (int)c,
                            ["reason"] = $"{fieldName} must not contain control characters (found 0x{((int)c):X2} at index {i}).",
                        });
                }
            }
        }

        // -----------------------------------------------------------------
        // Composite validators — call one per dynamic dispatch entrypoint.
        // -----------------------------------------------------------------

        /// <summary>
        /// Pre-flight for <see cref="Writer.WriteToDBFDatabase"/>: appId,
        /// dataPath, dbf, field, value. rowNum is an int so doesn't need
        /// string validation but must be non-negative.
        /// </summary>
        public static void ValidateWriteToDBFDatabaseArgs(
            string appId, string dataPath, string dbf, int rowNum, string field, object? value)
        {
            const string op = "WriteToDBFDatabase";
            ValidateAppId(appId, op);
            ValidateDataPath(dataPath, op);
            ValidateDbfName(dbf, op);
            if (rowNum < 0)
            {
                throw new LacerteSDKError(
                    StatusCodes.OutOfIndex,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "rowNum",
                        ["value"] = rowNum,
                        ["reason"] = "rowNum must be >= 0 (Lacerte detail rows are 0-indexed).",
                    });
            }
            ValidateFieldName(field, op);
            ValidateValue(value, op);
        }

        /// <summary>
        /// Pre-flight for <see cref="Writer.UpdateTaxReturn"/>: appId,
        /// dataPath, dbf, clientNumber, field, value, subClientId, stateAbbrev.
        /// <para>P16-054/P16-080 — `subClientId` casing matches the wire form;
        /// error-context echoes the same name.</para>
        /// </summary>
        public static void ValidateUpdateTaxReturnArgs(
            string appId, string dataPath, string dbf, string clientNumber,
            string field, object? value, int? subClientId, string? stateAbbrev)
        {
            const string op = "UpdateTaxReturn";
            ValidateAppId(appId, op);
            ValidateDataPath(dataPath, op);
            ValidateDbfName(dbf, op);
            ValidateClientNumber(clientNumber, op);
            ValidateFieldName(field, op);
            ValidateValue(value, op);
            if (subClientId.HasValue && subClientId.Value < 0)
            {
                throw new LacerteSDKError(
                    StatusCodes.InValidSubClientID,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["field"] = "subClientId",
                        ["value"] = subClientId.Value,
                        ["reason"] = "subClientId must be >= 0.",
                    });
            }
            ValidateStateAbbrev(stateAbbrev, op);
        }

        /// <summary>
        /// Pre-flight for <see cref="Writer.WriteDetail"/>: appId, dataPath,
        /// clientNumber, and every string field on <see cref="DetailRow"/>.
        /// This is the only path that exposes the full detail-row alphabet
        /// (Prefix, Suffix, Source, CityAbbr, ImportSrc, Description) and
        /// matches the refinedRecommendation length caps from audit P14-050.
        /// </summary>
        public static void ValidateWriteDetailArgs(
            string appId, string dataPath, string clientNumber, DetailRow row)
        {
            const string op = "WriteDetail";
            ValidateAppId(appId, op);
            ValidateDataPath(dataPath, op);
            ValidateClientNumber(clientNumber, op);

            // Detail-row strings. Series + StateAbbr + TSJ have shape rules
            // already enforced at DetailsTools; bridge re-checks the alphabet
            // floor (no control chars / NULs) and lengths only.
            EnsureSafeText(row.Series, op, "row.Series", ShortStringMaxLength);
            EnsureSafeText(row.Prefix, op, "row.Prefix", ShortStringMaxLength);
            // Code is a numeric integer string in the v1 schema.
            ValidateNumericString(row.Code, op, "row.Code");
            EnsureSafeText(row.Suffix, op, "row.Suffix", ShortStringMaxLength);
            ValidateStateAbbrev(row.StateAbbr, op);
            EnsureSafeText(row.CityAbbr, op, "row.CityAbbr", ShortStringMaxLength);
            EnsureSafeText(row.Source, op, "row.Source", ShortStringMaxLength);
            EnsureSafeText(row.TSJ, op, "row.TSJ", 1);
            EnsureSafeText(row.ImportSrc, op, "row.ImportSrc", ShortStringMaxLength);
            EnsureSafeText(row.Description, op, "row.Description", DescriptionMaxLength);
            // Amount is double? — no string content to gate. NaN / Infinity
            // would marshal cleanly but blow up downstream; reject them.
            if (row.Amount.HasValue)
            {
                double a = row.Amount.Value;
                if (double.IsNaN(a) || double.IsInfinity(a))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["field"] = "row.Amount",
                            ["value"] = a,
                            ["reason"] = "row.Amount must be a finite real number (no NaN / Infinity).",
                        });
                }
            }
        }

        /// <summary>
        /// Pre-flight for <see cref="Reader.ReadFromDBFData"/>: appId,
        /// dataPath, dbf. Reader doesn't take field/value/clientNumber.
        /// </summary>
        public static void ValidateReadFromDBFDataArgs(string appId, string dataPath, string dbf)
        {
            const string op = "ReadFromDBFData";
            ValidateAppId(appId, op);
            ValidateDataPath(dataPath, op);
            ValidateDbfName(dbf, op);
        }

        // RFC 5321 email address local-part + domain max length cap. CreateClient
        // emailID frequently mirrors a Lacerte preparer email and we want to
        // bound what reaches the COM marshaller without rejecting legacy
        // non-RFC IDs operators may have in DATA1.
        internal const int EmailMaxLength = 254;

        /// <summary>
        /// Pre-flight for <see cref="LacerteAccessClient.CreateClient"/>:
        /// appId, dataPath, clientId, taxType, firstName, lastName, emailId.
        /// P15-020 — CreateClient is the only COM mutation path that bypassed
        /// BridgeInputValidation in Phase 14. firstName/lastName/emailId are
        /// operator-supplied free-form strings that the SDK marshaller will
        /// silently truncate on the first NUL; bridge-side EnsureSafeText
        /// rejects them BEFORE the dynamic dispatch.
        /// <para>P16-054/P16-080 — `clientId`/`emailId` casing matches the
        /// wire form; error-context echoes the same names.</para>
        /// </summary>
        public static void ValidateCreateClientArgs(
            string appId, string dataPath, string clientId, string taxType,
            string? firstName, string? lastName, string? emailId)
        {
            const string op = "CreateClient";
            ValidateAppId(appId, op);
            ValidateDataPath(dataPath, op);
            ValidateClientNumber(clientId, op);
            EnsureSafeText(taxType, op, "taxType", ShortStringMaxLength);
            EnsureSafeText(firstName, op, "firstName", ShortStringMaxLength);
            EnsureSafeText(lastName, op, "lastName", ShortStringMaxLength);
            EnsureSafeText(emailId, op, "emailId", EmailMaxLength);
        }
    }
}
