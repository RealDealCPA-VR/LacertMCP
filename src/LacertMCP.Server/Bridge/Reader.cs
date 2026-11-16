using System;
using System.Collections.Generic;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// Wraps the <c>IDBFReader</c> COM surface for the rare introspection
    /// cases. 99% of reads go through ODBC (Boundary Invariant #6) — this
    /// exists for:
    /// <list type="bullet">
    ///   <item><description>Schema discovery for the field-access-map dumper (follow-up F5).</description></item>
    ///   <item><description>Fallback when the ODBC DSN is misconfigured but the COM lib loads.</description></item>
    /// </list>
    ///
    /// <para>
    /// Late binding via <c>Type.GetTypeFromProgID</c> + <c>dynamic</c> so this
    /// compiles without the SDK reference. Off-Windows or when the ProgID
    /// resolves to null this throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
    /// </para>
    ///
    /// <para>
    /// Live verification deferred to Windows + Lacerte box — see
    /// <c>REGRESSION_CHECKLIST.md</c> "Manual Live-Mode Verification".
    /// </para>
    /// </summary>
    public static class Reader
    {
        /// <summary>
        /// Wraps <c>IDBFReader.ReadFromDBFData(appId, dataPath, dbf, ref xml)</c>.
        /// Returns the raw XML payload the COM call produces; parsing into
        /// row dicts is a separate concern (see
        /// <c>scripts/dump-field-access.csx</c> from follow-up F5).
        /// </summary>
        public static string ReadFromDBFData(string appId, string dataPath, string dbf)
        {
            // P14-050 — bridge-boundary defense-in-depth. Validates appId
            // (when non-empty — Reader cold-fallback path passes empty),
            // dataPath (control chars, length), and dbf (DATA{n}{m}{YY}.DBF
            // shape) BEFORE acquiring the COM RCW. Off-Windows the
            // BridgeUnavailable throw would mask the validation error
            // otherwise. Validation stays OUTSIDE WireLog so rejected calls
            // don't pollute the log.
            BridgeInputValidation.ValidateReadFromDBFDataArgs(appId, dataPath, dbf);

            // P15-073 — F11 parity with Writer. Reader's cold-fallback path
            // is exactly where wire observability matters most; pre-P15-073
            // setting LACERTE_DEBUG_WIRE=1 produced zero records for bridge
            // reads. appId intentionally omitted from args object (matches
            // Writer.cs which treats appId as a stable session identifier
            // already known from Bridge.Register).
            return WireLog.RecordSync<string>("Bridge.ReadFromDBFData",
                new { dbf, dataPath }, () =>
            {
                // R7-SRC — documented shape (Developer Instructions, "Database
                // Read Example"): IDBFReader comes from
                // ILacerteAccess.CreateDBFReader(), and
                // ReadFromDBFData(AppID, DataPath, DBFName, ref string XML)
                // RETURNS a TAPIStatus with the XML in the ref parameter. The
                // previous 3-argument call returning the XML matched no SDK
                // overload.
                using var handle = SdkAccess.Acquire("ReadFromDBFData", "CreateDBFReader");
                int status;
                string xml = string.Empty;
                try
                {
                    dynamic r = handle.Target;
                    status = (int)r.ReadFromDBFData(appId, dataPath, dbf, ref xml);
                }
                catch (LacerteSDKError) { throw; }
                catch (Exception ex)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "ReadFromDBFData",
                            ["dbf"] = dbf,
                            ["dataPath"] = dataPath,
                            ["reason"] = ex.Message,
                        }, ex);
                }
                if (status != StatusCodes.Success)
                {
                    throw new LacerteSDKError(status,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "ReadFromDBFData",
                            ["dbf"] = dbf,
                        });
                }
                return xml ?? string.Empty;
            });
        }
    }
}
