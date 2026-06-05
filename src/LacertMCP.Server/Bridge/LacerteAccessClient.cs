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
            // Delegate to the static Registration helper which holds the
            // ITPRegistration reference per agent 02 §5.
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
            var table = "DATA1";
            var xml = Reader.ReadFromDBFData(appId: string.Empty, dataPath: dataPath, dbf: table);
            // Parsing the XML payload into a row-dict shape is a Phase 11
            // hardening task — for now return an empty list with a TODO so
            // the operator surface doesn't crash but the gap is loud.
            _ = year; _ = module; _ = xml;
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
            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.WriteField",
                new { dbf, rowNum, field, value },
                () => Writer.WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value));
        }

        public int UpdateTaxReturn(
            string appId, string dataPath, int year, char module,
            int table, string clientNumber, string field, object? value,
            int? subClientId, string? stateAbbrev)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            var dbf = FormatDbfName(table, module, year);
            // F11 — wrap with WireLog.
            return WireLog.RecordSync<int>("Bridge.UpdateTaxReturn",
                new { dbf, clientNumber, field, value, subClientId, stateAbbrev },
                () => Writer.UpdateTaxReturn(appId, dataPath, dbf, clientNumber, field, value, subClientId, stateAbbrev));
        }

        public int WriteDetail(
            string appId, string dataPath, int year, char module,
            string clientNumber, DetailRow row, int? replaceIndex)
        {
            EnsureLive();
            // Live verification deferred to Windows + Lacerte box.
            _ = year; _ = module;
            // P17-001 — fail-CLOSED on replaceIndex in live mode. The SDK's
            // WriteToDetailFile only appends (spec §2); silently dropping
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
            // F11 — wrap with WireLog. row is logged verbatim; WireLog.Redact
            // strips SSN/EIN/BankAccountNumber/RoutingNumber.
            return WireLog.RecordSync<int>("Bridge.WriteDetail",
                new { clientNumber, row },
                () => Writer.WriteDetail(appId, dataPath, clientNumber, row));
        }

        // =================================================================
        // Create
        // =================================================================

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

            // F11 — wrap with WireLog.
            return WireLog.RecordSync<BridgeCreateClientResult>("Bridge.CreateClient",
                new { clientId, taxType, firstName, lastName, emailId },
                () =>
            {
                // Live verification deferred to Windows + Lacerte box.
                // The canonical sequence is:
                //   1. var list = comInstance.IClientList;
                //   2. list.SetDataPath(dataPath);
                //   3. var client = list.IClient(taxType, clientId, emailId, firstName, lastName);
                //   4. var status = (int)client.CreateClient();
                //   5. var assigned = (string)client.ClientNumber;
                // Wrapped with late binding via dynamic so this compiles without
                // the SDK ref.
                dynamic com = _comInstance!;
                _ = year; _ = module;
                try
                {
                    dynamic clientList = com.IClientList;
                    clientList.SetDataPath(dataPath);
                    dynamic client = clientList.IClient(taxType, clientId, emailId, firstName, lastName);
                    int status = (int)client.CreateClient();
                    string? assigned = status == 1 ? (string?)client.ClientNumber : null;
                    return new BridgeCreateClientResult(status, assigned);
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
        /// </summary>
        private static string FormatDbfName(int table, char module, int year)
        {
            // YY = last two digits of the tax year.
            var yy = (year % 100).ToString("D2");
            return string.Concat("DATA", table.ToString(), module.ToString(), yy, ".DBF");
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
