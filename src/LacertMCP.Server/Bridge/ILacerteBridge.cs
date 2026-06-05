using System;
using System.Collections.Generic;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Bridge
{
    /// <summary>
    /// The C# interface the <see cref="LacerteSessionManager"/> binds to in
    /// live mode. Mirrors the wire-shape the session chokepoints already expect
    /// from the simulation store so the two backends are observationally
    /// identical at the seam (per Boundary Invariant #2 in <c>ARCHITECTURE.md</c>).
    ///
    /// <para>
    /// Each method either returns the same shape as the equivalent
    /// <c>SimulationStore</c> entry point OR returns a TAPIStatus integer
    /// (1 = Success). Failures throw <see cref="Errors.LacerteSDKError"/> with
    /// the SDK-native status code so the tool layer's error wrapping is
    /// consistent across modes.
    /// </para>
    ///
    /// <para>
    /// Implementations are NOT expected to be ODBC routers — this interface
    /// covers ONLY the COM/.NET surface that ODBC cannot reach (registration,
    /// writes, create, can-use-new-apis probing, the rare bridge-only reads).
    /// Bulk reads still go through <c>Odbc/LacerteDsnReader</c> per Boundary
    /// Invariant #6 ("ODBC is reads-only").
    /// </para>
    ///
    /// <para>
    /// Live verification of every method is deferred to a Windows + Lacerte box
    /// (see <c>REGRESSION_CHECKLIST.md</c> "Manual Live-Mode Verification").
    /// </para>
    /// </summary>
    public interface ILacerteBridge : IDisposable
    {
        // =================================================================
        // Auth lifecycle (Boundary Invariant #8: called ONCE at startup)
        // =================================================================

        /// <summary>
        /// Wraps <c>ITPRegistration.Register(appName, appId, taxYear)</c>.
        /// Returns the TPSDKStatus integer (e.g. 201 = AccessGranted, 203 =
        /// NotSignedIn). Surfaces the consent + Intuit sign-in dialogs on
        /// first call; rides the 24h cache on subsequent calls within the
        /// window.
        /// </summary>
        int Register(string appName, string appId, int taxYear);

        /// <summary>
        /// Wraps <c>ILacerteAccess.CanUseNewApis(year, module)</c>. Returns
        /// true iff the (year, module) pair supports the TY21+
        /// <c>UpdateTaxReturn</c> API. Cached by the session manager so this
        /// is called at most once per pair per process.
        /// </summary>
        bool CanUseNewApis(string appId, int year, char module);

        // =================================================================
        // Reads (rare — ODBC owns 99% of reads per Boundary Invariant #6)
        // =================================================================

        /// <summary>
        /// Bulk client list for a given module/year. Live implementations
        /// typically delegate to <c>IDBFReader</c>; this is on the bridge
        /// interface only so the session manager has a uniform fallback when
        /// ODBC is not available (e.g. an introspection / schema-dump path).
        /// Returns the same row-dict shape as the simulation store.
        /// </summary>
        IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(
            string dataPath, int year, char module);

        /// <summary>
        /// Reads one client by ClientNumber. Returns null if absent.
        /// </summary>
        IReadOnlyDictionary<string, object?>? ReadClient(
            string dataPath, int year, char module, string clientNumber);

        /// <summary>
        /// Generic table reader for DATA1..DATA7. Used only when ODBC is
        /// unavailable (schema-discovery / field-access-map dumper). Live
        /// implementations call into <see cref="Reader"/>.
        /// </summary>
        IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(
            string dataPath, int year, char module, int table,
            string? clientNumber = null, string? stateAbbrev = null);

        // =================================================================
        // Writes — TY21 branch (Boundary Invariant #5: session picks)
        // =================================================================

        /// <summary>
        /// Pre-TY21 write path. Wraps
        /// <c>IDBFWriter.WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value)</c>.
        /// Returns the TAPIStatus integer (1 = Success).
        /// </summary>
        int WriteField(
            string appId, string dataPath, int year, char module,
            int table, int rowNum, string field, object? value);

        /// <summary>
        /// TY21+ write path. Wraps
        /// <c>IDBFWriter.UpdateTaxReturn(appId, dataPath, dbf, clientNumber, field, value, subClientId, stateAbbrev)</c>.
        /// Returns the TAPIStatus integer (1 = Success).
        /// <para>P16-054/P16-080 — `subClientId` casing matches the wire form.</para>
        /// </summary>
        int UpdateTaxReturn(
            string appId, string dataPath, int year, char module,
            int table, string clientNumber, string field, object? value,
            int? subClientId, string? stateAbbrev);

        /// <summary>
        /// Per-client detail-file write. Wraps the detail-row write path on
        /// <c>IDBFWriter</c>. Returns the TAPIStatus integer (1 = Success).
        /// <para>
        /// P17-001 — <paramref name="replaceIndex"/> mirrors the sim path's
        /// replace-vs-append semantics. The underlying SDK
        /// <c>WriteToDetailFile</c> COM method only appends per spec §2; live
        /// implementations are expected to fail-closed with
        /// <see cref="SyntheticStatusCodes.LiveReplaceIndexUnsupported"/> when
        /// <paramref name="replaceIndex"/> is non-null until a real
        /// read-delete-append path lands. Threading the param through the
        /// interface (rather than dropping it at the session manager) keeps
        /// the bridge boundary honest and lets future implementations fan out
        /// when the SDK gains a true update-by-index primitive.
        /// </para>
        /// </summary>
        int WriteDetail(
            string appId, string dataPath, int year, char module,
            string clientNumber, DetailRow row, int? replaceIndex);

        // =================================================================
        // Create
        // =================================================================

        /// <summary>
        /// Wraps the <c>IClientList.SetDataPath → IClient(...) → CreateClient</c>
        /// sequence. Returns the TAPIStatus integer (1 = Success) plus the
        /// newly-assigned ClientNumber (null on failure).
        /// </summary>
        BridgeCreateClientResult CreateClient(
            string appId, string dataPath, int year, char module,
            // P16-054/P16-080 — `clientId`/`emailId` casing matches the wire.
            string clientId, string taxType,
            string? firstName, string? lastName, string? emailId);
    }

    /// <summary>
    /// Result shape from <see cref="ILacerteBridge.CreateClient"/>. Carries
    /// the TAPIStatus integer plus the newly-assigned ClientNumber (string-
    /// typed so it matches the sim path) on success; ClientNumber is null when
    /// <see cref="Status"/> is non-Success.
    /// </summary>
    public sealed class BridgeCreateClientResult
    {
        public int Status { get; }
        public string? ClientNumber { get; }

        public BridgeCreateClientResult(int status, string? clientNumber)
        {
            Status = status;
            ClientNumber = clientNumber;
        }
    }
}
