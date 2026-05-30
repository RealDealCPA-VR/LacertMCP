using System;
using System.Collections.Generic;
using LacertMCP.Server.Errors;

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
        /// Wraps <c>IDBFReader.ReadFromDBFData(appId, dataPath, dbf)</c>.
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
            // otherwise.
            BridgeInputValidation.ValidateReadFromDBFDataArgs(appId, dataPath, dbf);

            var reader = AcquireReader("ReadFromDBFData");
            try
            {
                try
                {
                    dynamic r = reader;
                    // Live verification deferred to Windows + Lacerte box.
                    var xml = (string)r.ReadFromDBFData(appId, dataPath, dbf);
                    return xml ?? string.Empty;
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
            }
            finally
            {
                // P14-004 — release RCW per-call to avoid COM leak. Canonical
                // pattern from CanUseNewApisProbe.cs:96-103.
                if (reader != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(reader); }
                    catch (Exception) { /* best-effort */ }
                }
            }
        }

        // =================================================================
        // Internals
        // =================================================================

        private static object AcquireReader(string op)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["reason"] = "IDBFReader requires Windows + the Lacerte SDK.",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                    });
            }

            try
            {
                var t = Type.GetTypeFromProgID("LacerteSDK_Access.DBFReader");
                if (t == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = "LacerteSDK_Access.DBFReader ProgID not registered. Install the Lacerte SDK.",
                        });
                }
                var instance = Activator.CreateInstance(t);
                if (instance == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = op,
                            ["reason"] = "Activator.CreateInstance returned null for LacerteSDK_Access.DBFReader.",
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
