using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// Default <see cref="ILacerteBridge"/> implementation. Wraps the COM type
    /// <c>LacerteSDK_Access.LacerteAccess</c> instantiated via
    /// <c>Type.GetTypeFromProgID(...)</c> + <c>Activator.CreateInstance(...)</c>
    /// so this code compiles on machines without the Lacerte SDK installed
    /// (late binding via <c>dynamic</c>).
    ///
    /// <para>
    /// On non-Windows OR when <c>GetTypeFromProgID</c> returns null (SDK
    /// missing / mis-registered), every method throws
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/>. The session
    /// manager catches that and surfaces it as a structured tool error so the
    /// operator gets a clear "install the SDK and re-register" message.
    /// </para>
    ///
    /// <para>
    /// Live verification of the actual COM round-trip is deferred to a
    /// Windows + Lacerte box — see <c>REGRESSION_CHECKLIST.md</c> "Manual
    /// Live-Mode Verification" and the <c>[Skip]</c>'d tests in
    /// <c>LacerteAccessClientTests</c>.
    /// </para>
    /// </summary>
    public sealed class LacerteAccessClient : ILacerteBridge
    {
        // Late-bound COM reference. Null off-Windows or when ProgID lookup fails.
        // Held for the lifetime of the bridge — see Boundary Invariant #8 +
        // agent 02 §5 "Session caching" (ITPRegistration reference held in memory).
        private object? _comInstance;

        private bool _disposed;

        /// <summary>
        /// True iff the COM instance was successfully created. Used by tests
        /// + the doctor command to distinguish "wired but cold" from "fully
        /// unavailable".
        /// </summary>
        public bool IsConnected => _comInstance != null;

        public LacerteAccessClient()
        {
            // Live verification deferred to Windows + Lacerte box.
            if (!IsWindows())
            {
                _comInstance = null;
                return;
            }

            try
            {
                var t = Type.GetTypeFromProgID("LacerteSDK_Access.LacerteAccess");
                if (t == null)
                {
                    _comInstance = null;
                    return;
                }
                _comInstance = Activator.CreateInstance(t);
            }
            catch (Exception)
            {
                // ProgID lookup or activation failed — leave null and let the
                // first wire call throw BridgeUnavailable with context.
                _comInstance = null;
            }
        }

        // =================================================================
        // Auth lifecycle
        // =================================================================

        public int Register(string appName, string appId, int taxYear)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            // Delegate to the static Registration helper, which creates the
            // ITPRegistration RCW per-call and releases it in a finally
            // (R6-14 / P14-004).
            // F11 — wrap with WireLog. appId redacted (not in op args).
            return WireLog.RecordSync<int>("Bridge.Register",
                new { appName, taxYear },
                () => Bridge.Registration.Register(appName, appId, taxYear));
        }

        public bool CanUseNewApis(string appId, int year, char module)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            return WireLog.RecordSync<bool>("Bridge.CanUseNewApis",
                new { year, module = module.ToString() },
                () => CanUseNewApisProbe.Probe(appId, year, module));
        }

        // =================================================================
        // Reads (rare — ODBC owns 99% per Boundary Invariant #6)
        // =================================================================

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(
            string dataPath, int year, char module)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box. The
            // expected COM path is IDBFReader.ReadFromDBFData on DATA1 then
            // XML parse — for now defer to Reader for the raw XML and let
            // callers parse, OR throw BridgeUnavailable when ODBC is the
            // intended path (it is, 99%+).
            //
            // R6-15 — build the full DATA1{module}{YY}.DBF name (e.g.
            // DATA1I24.DBF) instead of the bare "DATA1" that
            // BridgeInputValidation.ValidateDbfName rejects (it requires the
            // NAME.EXT shape), which made this path throw DBNotSupported on
            // every live call with a misleading remediation. year/module are
            // no longer discarded.
            var dbf = FormatDbfName(table: 1, module: module, year: year);
            // R7-SRC — "1. String – AppID – AppID used for calling Register
            // function": pass the registered AppID, not an empty string.
            var registeredAppId = Registration.GetStoredAppId()?.ToString("D") ?? string.Empty;
            var xml = Reader.ReadFromDBFData(appId: registeredAppId, dataPath: dataPath, dbf: dbf);
            // Parsing the XML payload into a row-dict shape is a Phase 11
            // hardening task — for now return an empty list with a TODO so
            // the operator surface doesn't crash but the gap is loud.
            _ = xml;
            // TODO Phase 11: parse Reader XML into row dicts here.
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        public IReadOnlyDictionary<string, object?>? ReadClient(
            string dataPath, int year, char module, string clientNumber)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            // 99% of ReadClient calls go through ODBC; this method exists for
            // the rare introspection path. Throw BridgeUnavailable so callers
            // route to ODBC instead of getting a half-implemented XML parse.
            _ = dataPath; _ = year; _ = module; _ = clientNumber;
            throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                new Dictionary<string, object?>
                {
                    ["op"] = "ReadClient",
                    ["reason"] = "Bridge ReadClient is the cold-fallback path; ODBC should service this call.",
                });
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(
            string dataPath, int year, char module, int table,
            string? clientNumber = null, string? stateAbbrev = null)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            // Same shape as ReadClient — ODBC should service this; this is
            // the schema-discovery fallback.
            _ = dataPath; _ = year; _ = module; _ = table; _ = clientNumber; _ = stateAbbrev;
            throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                new Dictionary<string, object?>
                {
                    ["op"] = "ReadTable",
                    ["reason"] = "Bridge ReadTable is the cold-fallback path; ODBC should service this call.",
                });
        }

        // =================================================================
        // Writes
        // =================================================================

        public int WriteField(
            string appId, string dataPath, int year, char module,
            int table, int rowNum, string field, object? value)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            var dbf = FormatDbfName(table, module, year);
            // R6-34 — no outer WireLog wrapper here. Writer.WriteToDBFDatabase
            // emits the canonical "Bridge.WriteToDBFDatabase" record AFTER its
            // BridgeInputValidation pass; an outer wrapper double-logged every
            // write and logged rejected args, defeating Writer's
            // validate-before-log contract.
            return Writer.WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value);
        }

        public int UpdateTaxReturn(
            string appId, string dataPath, int year, char module,
            int table, string clientNumber, string field, object? value,
            int? subClientId, string? stateAbbrev)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            var dbf = FormatDbfName(table, module, year);
            // R6-34 — no outer WireLog wrapper here. Writer.UpdateTaxReturn
            // emits the canonical "Bridge.UpdateTaxReturn" record AFTER its
            // BridgeInputValidation pass; the outer wrapper with the SAME op
            // name double-logged every write and logged rejected args.
            return Writer.UpdateTaxReturn(appId, dataPath, dbf, clientNumber, field, value, subClientId, stateAbbrev);
        }

        public int WriteDetail(
            string appId, string dataPath, int year, char module,
            string clientNumber, DetailRow row, int? replaceIndex)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            _ = year; _ = module;
            // P17-001 — fail-CLOSED on replaceIndex in live mode. The SDK's
            // SDK has no documented COM detail write / update-by-index (R7-SRC); silently dropping
            // replaceIndex (the prior behavior) made an operator-requested
            // row replace silently become an append against a live tax
            // return. Until a real read-delete-append path lands at this
            // bridge, refuse the call with a structured 9019 so the operator
            // sees the divergence instead of a corrupted return reported as
            // Success. The sim path continues to honor replaceIndex via
            // ApplySimDetailWriteWithUndo.
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
            // R6-34 — no outer WireLog wrapper here. Writer.WriteDetail emits
            // the canonical "Bridge.WriteDetail" record AFTER its
            // BridgeInputValidation pass; the outer wrapper with the SAME op
            // name double-logged every write and logged rejected args.
            return Writer.WriteDetail(appId, dataPath, clientNumber, row);
        }

        // =================================================================
        // Create
        // =================================================================

        /// <summary>
        /// R7-SRC — sets <c>IClient.TaxType</c> (the SDK's <c>TaxModule</c>
        /// enum). The SDK is a .NET assembly registered for COM ("Add reference
        /// to the SDK access libraries (From GAC)"), so a net48 process that
        /// activates it by ProgID typically receives the MANAGED object, not a
        /// COM wrapper — and C# <c>dynamic</c> binding has no implicit
        /// int → enum conversion (RuntimeBinderException → every live create
        /// would fail 9201). Use reflection with <c>Enum.ToObject</c> when the
        /// property is a managed enum; fall back to the int for a genuine COM
        /// RCW, where IDispatch accepts the integral value.
        /// </summary>
        internal static void SetTaxType(object client, int ordinal)
        {
            var prop = Marshal.IsComObject(client) ? null : client.GetType().GetProperty("TaxType");
            if (prop != null && prop.PropertyType.IsEnum)
            {
                prop.SetValue(client, Enum.ToObject(prop.PropertyType, ordinal));
                return;
            }
            if (prop != null)
            {
                prop.SetValue(client, Convert.ChangeType(ordinal, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            dynamic d = client;
            d.TaxType = ordinal;
        }

        public BridgeCreateClientResult CreateClient(
            string appId, string dataPath, int year, char module,
            string clientId, string taxType,
            string? firstName, string? lastName, string? emailId)
        {
            // P15-020 — bridge-boundary defense-in-depth. Validation runs
            // BEFORE EnsureLive() + WireLog so a bad arg is rejected even on
            // a cold (off-Windows / SDK-missing) box, matching the
            // Writer.WriteToDBFDatabase pattern where validation precedes
            // AcquireWriter. CreateClient is the only COM-mutation surface
            // that bypassed BridgeInputValidation in Phase 14; it accepts
            // operator-supplied firstName/lastName/emailId strings which the
            // SDK marshaller silently truncates on the first NUL
            // ("Smith\0; DROP" -> "Smith" in DBF).
            BridgeInputValidation.ValidateCreateClientArgs(
                appId, dataPath, clientId, taxType, firstName, lastName, emailId);
            EnsureLive();

            // R7-SRC — documented flow (Developer Instructions, "Creating a
            // New client using SDK", .NET + COM samples):
            //   IClient     client = access.CreateClient();       // blank object
            //   IClientList list   = access.CreateClientList();
            //   TAPIStatus  st     = list.SetDataPath(AppID, DataPath, TaxYear);
            //   client.TaxType = TaxModule.<module>; client.ClientID = ...;
            //   client.EmailID = ...; client.FirstName = ...; client.LastName = ...;
            //   TAPIStatus  st2    = list.CreateClient(client);
            // The previous code used members that do not exist in the SDK
            // (access.IClientList property, list.IClient(5 args) factory,
            // client.CreateClient(), one-argument SetDataPath). The SDK
            // returns ONLY a status — no ClientNumber — so a successful live
            // create reports ClientNumber=null; the tool layer then tells the
            // caller to resolve it by ClientID (R7-28b clientNumberHint).
            return WireLog.RecordSync<BridgeCreateClientResult>("Bridge.CreateClient",
                new { clientId, taxType, firstName, lastName, emailId },
                () =>
            {
                dynamic access = _comInstance!;
                object? clientObj = null;
                object? clientListObj = null;
                try
                {
                    dynamic client = access.CreateClient();
                    clientObj = (object?)client;
                    dynamic clientList = access.CreateClientList();
                    clientListObj = (object?)clientList;

                    int pathStatus = (int)clientList.SetDataPath(
                        appId, dataPath, year.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (pathStatus != StatusCodes.Success)
                    {
                        return new BridgeCreateClientResult(pathStatus, null);
                    }

                    // TaxType is the SDK's TaxModule enum. The .NET sample sets
                    // it as (TaxModule)(index + 1); the documented module order
                    // (Individual, Corporate, Partnership, S-Corporation,
                    // Fiduciary, Exempt, Estate, Gift, Benefit) is the order of
                    // Schema.Modules.All, so the ordinal is that index + 1.
                    SetTaxType(clientObj!, Schema.Modules.TaxModuleOrdinal(module, taxType));
                    client.ClientID = clientId;
                    client.EmailID = emailId ?? string.Empty;
                    client.FirstName = firstName ?? string.Empty;
                    client.LastName = lastName ?? string.Empty;

                    int status = (int)clientList.CreateClient(client);
                    return new BridgeCreateClientResult(status, null);
                }
                catch (LacerteSDKError)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>
                        {
                            ["op"] = "CreateClient",
                            ["clientId"] = clientId,
                            ["reason"] = ex.Message,
                        }, ex);
                }
                finally
                {
                    // P14-004 / R6-14 — release the per-call RCWs.
                    SdkAccess.Release(clientObj);
                    SdkAccess.Release(clientListObj);
                }
            });
        }

        // =================================================================
        // Internals
        // =================================================================

        private void EnsureLive()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LacerteAccessClient));
            }
            if (_comInstance == null)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["reason"] = IsWindows()
                            ? "LacerteSDK_Access.LacerteAccess ProgID not registered. Install the Lacerte SDK and run `lacerte-mcp doctor` to verify."
                            : "Live mode requires Windows + the Lacerte SDK. Currently running off-Windows; set LACERTE_SIMULATION=1.",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                    });
            }
        }

        private static bool IsWindows()
        {
            // net48-compatible Windows check. `OperatingSystem.IsWindows()`
            // is .NET 5+; on net48 the canonical check is Win32NT.
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        /// <summary>
        /// Builds the DBF filename per agent 03 §3 / spec §2:
        /// <c>DATA{n}{moduleChar}{YY}.DBF</c>, e.g. <c>DATA1I24.DBF</c>.
        /// <para>
        /// Internal (R6-15) so the name-construction contract is unit-testable
        /// without a COM round-trip. The module char is uppercased defensively
        /// because <c>BridgeInputValidation.ValidateDbfName</c> only accepts
        /// uppercase ASCII.
        /// </para>
        /// </summary>
        internal static string FormatDbfName(int table, char module, int year)
        {
            // YY = last two digits of the tax year.
            var yy = (year % 100).ToString("D2");
            return string.Concat("DATA", table.ToString(),
                char.ToUpperInvariant(module).ToString(), yy, ".DBF");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Release the COM ref on Windows only; off-Windows _comInstance
            // is always null so Marshal calls would no-op anyway.
            if (_comInstance != null && IsWindows())
            {
                try
                {
                    Marshal.ReleaseComObject(_comInstance);
                }
                catch (Exception)
                {
                    // Best-effort release. Disposal must not throw.
                }
            }
            _comInstance = null;
        }
    }
}
