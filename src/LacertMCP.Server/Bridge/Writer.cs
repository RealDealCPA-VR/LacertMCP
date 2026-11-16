using System;
using System.Collections.Generic;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// Wraps the <c>IDBFWriter</c> COM surface. Three public entry points:
    /// pre-TY21 <see cref="WriteToDBFDatabase"/>, TY21+ <see cref="UpdateTaxReturn"/>,
    /// and detail-file <see cref="WriteDetail"/>.
    ///
    /// <para>
    /// Late binding via <c>Type.GetTypeFromProgID</c> + <c>dynamic</c> so this
    /// compiles without the SDK reference. Off-Windows or when the ProgID
    /// resolves to null this throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
    /// </para>
    ///
    /// <para>
    /// Live verification deferred to Windows + Lacerte box — see
    /// <c>REGRESSION_CHECKLIST.md</c> "Manual Live-Mode Verification" and the
    /// <c>[Skip]</c>'d tests in <c>WriterTests</c>.
    /// </para>
    /// </summary>
    public static class Writer
    {
        /// <summary>
        /// Pre-TY21 write path. Wraps
        /// <c>IDBFWriter.WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value)</c>.
        /// Returns the TAPIStatus integer (1 = Success).
        /// </summary>
        public static int WriteToDBFDatabase(
            string appId, string dataPath, string dbf, int rowNum, string field, object? value)
        {
            // P14-050 — bridge-boundary defense-in-depth. Validation runs
            // BEFORE WireLog + AcquireWriter so a bad arg is rejected before
            // we log the args OR try to acquire the COM RCW. Off-Windows the
            // BridgeUnavailable throw would mask the validation error
            // otherwise.
            BridgeInputValidation.ValidateWriteToDBFDatabaseArgs(
                appId, dataPath, dbf, rowNum, field, value);

            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.WriteToDBFDatabase",
                new { dbf, rowNum, field, value }, () =>
            {
                // R7-SRC — IDBFWriter comes from ILacerteAccess.CreateDBFWriter().
                using var handle = SdkAccess.Acquire("WriteToDBFDatabase", "CreateDBFWriter");
                var writer = handle.Target;
                try
                {
                    try
                    {
                        dynamic w = writer;
                        // R7-SRC — documented signature (Developer Instructions,
                        // "Parameters for WriteToDBFDatabase"): AppID, DataPath,
                        // DBFName, int RowNum, String Fieldname, String Value,
                        // ref String newValue. The previous 6-argument call
                        // matched no overload and passed a non-string value.
                        string newValue = string.Empty;
                        int status = (int)w.WriteToDBFDatabase(
                            appId, dataPath, dbf, rowNum, field,
                            SdkAccess.ToSdkValueString(value), ref newValue);
                        return status;
                    }
                    catch (LacerteSDKError) { throw; }
                    catch (Exception ex)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                            new Dictionary<string, object?>
                            {
                                ["op"] = "WriteToDBFDatabase",
                                ["dbf"] = dbf,
                                ["rowNum"] = rowNum,
                                ["field"] = field,
                                ["reason"] = ex.Message,
                            }, ex);
                    }
                }
                finally
                {
                    // P14-004 — release RCW per-call to avoid COM leak. Canonical
                    // pattern from CanUseNewApisProbe.cs:96-103.
                    // R7-SRC — released by SdkAccess.Handle (LacerteAccess + IDBFWriter).
                }
            });
        }

        /// <summary>
        /// TY21+ write path. Wraps
        /// <c>IDBFWriter.UpdateTaxReturn(appId, dataPath, dbf, clientNumber, field, value, subClientId, stateAbbrev)</c>.
        /// Returns the TAPIStatus integer (1 = Success).
        /// <para>P16-054/P16-080 — `subClientId` casing matches the wire form.</para>
        /// </summary>
        public static int UpdateTaxReturn(
            string appId, string dataPath, string dbf, string clientNumber,
            string field, object? value, int? subClientId, string? stateAbbrev)
        {
            // P14-050 — bridge-boundary defense-in-depth. See WriteToDBFDatabase.
            BridgeInputValidation.ValidateUpdateTaxReturnArgs(
                appId, dataPath, dbf, clientNumber, field, value, subClientId, stateAbbrev);

            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.UpdateTaxReturn",
                new { dbf, clientNumber, field, value, subClientId, stateAbbrev }, () =>
            {
                // R7-SRC — IDBFWriter comes from ILacerteAccess.CreateDBFWriter().
                using var handle = SdkAccess.Acquire("UpdateTaxReturn", "CreateDBFWriter");
                var writer = handle.Target;
                try
                {
                    try
                    {
                        dynamic w = writer;
                        // R7-SRC — "Parameters for UpdateTaxReturn": Value is a
                        // String; SubClientID Integer (0 when not DATA2/DATA4);
                        // StateAbbrev String ("" when not DATA3/DATA4).
                        int status = (int)w.UpdateTaxReturn(
                            appId, dataPath, dbf, clientNumber, field,
                            SdkAccess.ToSdkValueString(value),
                            subClientId ?? 0, stateAbbrev ?? string.Empty);
                        return status;
                    }
                    catch (LacerteSDKError) { throw; }
                    catch (Exception ex)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                            new Dictionary<string, object?>
                            {
                                ["op"] = "UpdateTaxReturn",
                                ["dbf"] = dbf,
                                ["clientNumber"] = clientNumber,
                                ["field"] = field,
                                ["reason"] = ex.Message,
                            }, ex);
                    }
                }
                finally
                {
                    // P14-004 — release RCW per-call to avoid COM leak. Canonical
                    // pattern from CanUseNewApisProbe.cs:96-103.
                    // R7-SRC — released by SdkAccess.Handle (LacerteAccess + IDBFWriter).
                }
            });
        }

        /// <summary>
        /// Detail-file write. Wraps the detail-row write on the writer COM
        /// type. Live verification deferred — the exact COM method shape is
        /// inferred from spec §2 (no validation, row-ordered append).
        /// Returns the TAPIStatus integer (1 = Success).
        /// <para>
        /// P17-001 — overload that accepts an optional <paramref name="replaceIndex"/>
        /// so the bridge boundary mirrors the sim path's replace-vs-append
        /// surface. The SDK has no documented update-by-index COM call;
        /// when <paramref name="replaceIndex"/> is non-null this throws
        /// <see cref="SyntheticStatusCodes.LiveReplaceIndexUnsupported"/>
        /// (9019) BEFORE the WireLog wrap so a corrupted-but-Success
        /// replay can never reach the operator.
        /// </para>
        /// </summary>
        public static int WriteDetail(
            string appId, string dataPath, string clientNumber, DetailRow row,
            int? replaceIndex = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));

            // P17-001 — fail-CLOSED on replaceIndex in live mode. The SDK's
            // SDK documents no COM detail write / update-by-index; silently dropping replaceIndex
            // (the prior behavior of LacerteSessionManager's live branch)
            // converted operator-intended row REPLACE into row APPEND while
            // returning Success — the textbook two-mode-discipline violation.
            // Raise BEFORE BridgeInputValidation so the operator sees a
            // single targeted error rather than a generic input-shape one.
            if (replaceIndex.HasValue)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.LiveReplaceIndexUnsupported,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "WriteDetail",
                        ["clientNumber"] = clientNumber,
                        ["replaceIndex"] = replaceIndex.Value,
                    });
            }

            // P14-050 — bridge-boundary defense-in-depth. Validates every
            // string on DetailRow (Series/Prefix/Code/Suffix/StateAbbr/
            // CityAbbr/Source/TSJ/ImportSrc/Description) for length caps
            // and control-char content (esp. NUL — COM marshallers truncate
            // strings at the first NUL, so "OK\0; DROP" would silently
            // become "OK" in the DBF).
            BridgeInputValidation.ValidateWriteDetailArgs(
                appId, dataPath, clientNumber, row);

            // R7-SRC — fail CLOSED. The SDK Developer Instructions document
            // detail-file reads/writes only through the ODBC driver ("Working
            // with Detail Files": each client's detail file is its own ODBC
            // table; the ODBC driver provides CRUD on "the tables and detail
            // files"). The .NET/COM library exposes no detail-write method —
            // the previous late-bound WriteToDetailFile(...) call was a guess
            // that matches no SDK member and would always fail at runtime.
            // The live ODBC write path is not wired yet (same as live ODBC
            // reads, ThrowIfLiveUnwired), so surface that precisely.
            throw LiveDetailWriteRefusal(clientNumber);
        }

        /// <summary>
        /// R7-SRC — the single 9202 refusal for live detail-row writes, shared
        /// by the commit (here) and the session's live dry-run preview so the
        /// two can never diverge.
        /// </summary>
        internal static LacerteSDKError LiveDetailWriteRefusal(string clientNumber)
            => new LacerteSDKError(SyntheticStatusCodes.SimulationOnly,
                new Dictionary<string, object?>
                {
                    ["op"] = "WriteDetail",
                    ["clientNumber"] = clientNumber,
                    ["reason"] = "Live detail-row writes go through the Lacerte ODBC driver (the SDK's COM library has no detail-write method); the ODBC write path is not wired yet. Run in simulation mode, or enter the row in Lacerte.",
                });
    }
}
