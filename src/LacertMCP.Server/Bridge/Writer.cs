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
            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.WriteToDBFDatabase",
                new { dbf, rowNum, field, value }, () =>
            {
                var writer = AcquireWriter("WriteToDBFDatabase");
                try
                {
                    dynamic w = writer;
                    int status = (int)w.WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value);
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
            });
        }

        /// <summary>
        /// TY21+ write path. Wraps
        /// <c>IDBFWriter.UpdateTaxReturn(appId, dataPath, dbf, clientNumber, field, value, subClientID, stateAbbrev)</c>.
        /// Returns the TAPIStatus integer (1 = Success).
        /// </summary>
        public static int UpdateTaxReturn(
            string appId, string dataPath, string dbf, string clientNumber,
            string field, object? value, int? subClientID, string? stateAbbrev)
        {
            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.UpdateTaxReturn",
                new { dbf, clientNumber, field, value, subClientID, stateAbbrev }, () =>
            {
                var writer = AcquireWriter("UpdateTaxReturn");
                try
                {
                    dynamic w = writer;
                    int status = (int)w.UpdateTaxReturn(
                        appId, dataPath, dbf, clientNumber, field, value,
                        subClientID ?? 0, stateAbbrev ?? string.Empty);
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
            });
        }

        /// <summary>
        /// Detail-file write. Wraps the detail-row write on the writer COM
        /// type. Live verification deferred — the exact COM method shape is
        /// inferred from spec §2 (no validation, row-ordered append).
        /// Returns the TAPIStatus integer (1 = Success).
        /// </summary>
        public static int WriteDetail(
            string appId, string dataPath, string clientNumber, DetailRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.WriteDetail",
                new { clientNumber, row }, () =>
            {
                var writer = AcquireWriter("WriteDetail");
                try
                {
                    dynamic w = writer;
                    // Live verification deferred to Windows + Lacerte box.
                    // Best-guess COM method shape is WriteToDetailFile(appId,
                    // dataPath, clientNumber, series, prefix, code, suffix,
                    // stateAbbr, cityAbbr, source, tsj, importSrc, amount,
                    // description) — confirm against the SDK headers during the
                    // live verification session.
                    int status = (int)w.WriteToDetailFile(
                        appId, dataPath, clientNumber,
                        row.Series, row.Prefix, row.Code, row.Suffix,
                        row.StateAbbr, row.CityAbbr, row.Source, row.TSJ,
                        row.ImportSrc, row.Amount ?? 0.0, row.Description);
                    return status;
                }
                catch (LacerteSDKError) { throw; }
                catch (Exception ex)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "WriteDetail",
                            ["clientNumber"] = clientNumber,
                            ["reason"] = ex.Message,
                        }, ex);
                }
            });
        }

        // =================================================================
        // Internals
        // =================================================================

        private static object AcquireWriter(string op)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["reason"] = "IDBFWriter requires Windows + the Lacerte SDK.",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                    });
            }

            try
            {
                var t = Type.GetTypeFromProgID("LacerteSDK_Access.DBFWriter");
                if (t == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = "LacerteSDK_Access.DBFWriter ProgID not registered. Install the Lacerte SDK.",
                        });
                }
                var instance = Activator.CreateInstance(t);
                if (instance == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = "Activator.CreateInstance returned null for LacerteSDK_Access.DBFWriter.",
                        });
                }
                return instance;
            }
            catch (LacerteSDKError) { throw; }
            catch (Exception ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["reason"] = ex.Message,
                    }, ex);
            }
        }
    }
}
