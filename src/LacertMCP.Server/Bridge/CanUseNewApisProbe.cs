using System;
using System.Collections.Generic;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// Probes <c>ILacerteAccess.CanUseNewApis(year, module)</c> via late
    /// binding. The session manager caches the result per <c>(year, module)</c>
    /// so this is invoked at most once per pair per process — this class
    /// owns the wire call only; the cache lives on the session manager.
    ///
    /// <para>
    /// Live verification deferred to Windows + Lacerte box — see
    /// <c>REGRESSION_CHECKLIST.md</c> "Manual Live-Mode Verification".
    /// </para>
    /// </summary>
    public static class CanUseNewApisProbe
    {
        /// <summary>
        /// Returns true iff the (year, module) pair supports the TY21+
        /// <c>UpdateTaxReturn</c> APIs. Off-Windows or when the ProgID
        /// resolves to null this throws
        /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
        /// </summary>
        public static bool Probe(string appId, int year, char module)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "CanUseNewApis",
                        ["reason"] = "CanUseNewApis requires Windows + the Lacerte SDK.",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                    });
            }

            object? access;
            try
            {
                var t = Type.GetTypeFromProgID("LacerteSDK_Access.LacerteAccess");
                if (t == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "CanUseNewApis",
                            ["reason"] = "LacerteSDK_Access.LacerteAccess ProgID not registered.",
                        });
                }
                access = Activator.CreateInstance(t);
                if (access == null)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "CanUseNewApis",
                            ["reason"] = "Activator.CreateInstance returned null for LacerteSDK_Access.LacerteAccess.",
                        });
                }
            }
            catch (LacerteSDKError) { throw; }
            catch (Exception ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "CanUseNewApis",
                        ["reason"] = ex.Message,
                    }, ex);
            }

            try
            {
                dynamic a = access;
                // Live verification deferred to Windows + Lacerte box.
                // Note: the spec calls CanUseNewApis(year, moduleChar). Pass
                // appId as a context tag for the wire log; the COM call shape
                // itself takes only (year, module).
                _ = appId;
                bool result = (bool)a.CanUseNewApis(year, module);
                return result;
            }
            catch (Exception ex)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = "CanUseNewApis",
                        ["year"] = year,
                        ["module"] = module.ToString(),
                        ["reason"] = ex.Message,
                    }, ex);
            }
            finally
            {
                if (access != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(access); }
                    catch (Exception) { /* best-effort */ }
                }
            }
        }
    }
}
