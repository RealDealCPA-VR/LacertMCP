using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Resilience;
using LacertMCP.Server.Schema;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// The chokepoint between tools and the wire. One per process (singleton in
    /// DI). Owns:
    /// <list type="bullet">
    ///   <item><description>Registration + 24h auth lifecycle (sim path: instant; live path: COM/bridge — Phase 9).</description></item>
    ///   <item><description><see cref="SessionContext"/> — datapath/year/module/readOnly.</description></item>
    ///   <item><description>TY21+ branching via <see cref="CanUseNewApis"/> (cached per <c>(year, module)</c>).</description></item>
    ///   <item><description>Read-only gate (synthetic 9001) — fires SYNCHRONOUSLY before any await on every write.</description></item>
    ///   <item><description>Idempotency cache (synthetic 9102 on conflict) — SHA-256 payload fingerprint per agent 07 §9.</description></item>
    ///   <item><description>Dry-run primitives — snapshot/execute/restore in <c>finally</c>.</description></item>
    /// </list>
    ///
    /// <para>
    /// Mode is resolved once in the constructor and immutable for process
    /// lifetime: <c>(LACERTE_SIMULATION != "1") AND (LACERTE_LIVE == "1") AND
    /// Win32NT → Live; else Simulation</c>. See <c>ARCHITECTURE.md</c>
    /// "Operating Modes" and agent 02 §5 "Session caching".
    /// </para>
    ///
    /// <para>
    /// In simulation mode the manager owns a <see cref="SimulationStore"/>; in
    /// live mode it would hold a bridge + ODBC client (Phase 9 — currently
    /// throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/> from any
    /// wire call).
    /// </para>
    /// </summary>
    public sealed class LacerteSessionManager : IDisposable
    {
        // ----- Mode + dependencies -----------------------------------------

        public SessionMode Mode { get; }

        // Sim backend. Null in live mode (Phase 9 wires live bridge + odbc).
        private readonly SimulationStore? _sim;

        // Live-mode wiring. Constructed only in Live; null in Sim. Phase 9
        // task #67: every chokepoint that has a `_bridge != null` branch
        // routes through it; `_bridge == null` falls back to the legacy
        // BridgeUnavailable throw so non-Windows hosts still get a clean
        // structured error instead of a NullReferenceException.
        private readonly ILacerteBridge? _bridge;
        private readonly object? _odbc = null;   // Phase 4 task #39 still pending wire-up.

        // ----- Auth lifecycle ----------------------------------------------

        public bool IsRegistered { get; private set; }
        public DateTimeOffset? RegisteredAt { get; private set; }
        public DateTimeOffset? ExpiresAt { get; private set; }

        private readonly string? _appName;

        // ----- Context + per-(year,module) caches --------------------------

        public SessionContext? Context { get; private set; }

        // Cached CanUseNewApis answers keyed by (year, module). Mirrors the
        // QB MCP host-info-cache pattern (lazy + per-process).
        private readonly ConcurrentDictionary<(int Year, char Module), bool> _newApisCache =
            new ConcurrentDictionary<(int, char), bool>();

        // F14 — cached EditionInfo. Probed lazily on first SessionStatus() call
        // via EnsureEditionProbed(); never re-probed for the lifetime of the
        // process. In sim mode the probe returns sentinel values; in live mode
        // the probe surfaces the (edition, build, sdkVersion) triple from the
        // bridge. Same one-shot-per-process pattern as _newApisCache.
        private EditionInfo? _edition;
        private readonly object _editionLock = new object();

        // ----- Idempotency cache (per agent 07 §9) -------------------------

        private readonly ConcurrentDictionary<string, IdempotencyCacheEntry> _idempotencyCache =
            new ConcurrentDictionary<string, IdempotencyCacheEntry>();

        // ===================================================================
        // Construction
        // ===================================================================

        /// <summary>
        /// Resolves mode from <paramref name="env"/> (default: process env). In
        /// sim mode constructs a fresh <see cref="SimulationStore"/>. In live
        /// mode leaves bridge/odbc fields null pending Phase 9 wiring; wire
        /// calls will throw <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
        /// </summary>
        public LacerteSessionManager(IDictionary<string, string?>? env = null)
        {
            var resolved = env ?? ReadProcessEnv();

            var sim = ReadEnvFlag(resolved, "LACERTE_SIMULATION");
            var live = ReadEnvFlag(resolved, "LACERTE_LIVE");
            var onWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            Mode = (!sim && live && onWindows) ? SessionMode.Live : SessionMode.Simulation;
            _appName = TryGet(resolved, "LACERTE_APP_NAME");

            if (Mode == SessionMode.Simulation)
            {
                // Sim backend honors LACERTE_SIM_EXPIRED via its own env read.
                _sim = new SimulationStore();
                _bridge = null;
            }
            else
            {
                // Phase 9 task #67 — wire up the COM bridge. The constructor
                // is intentionally cheap: it does NOT call Register() (that's
                // Boundary Invariant #8 — Register happens once, but via the
                // explicit Register() method below, not in the ctor). On
                // non-Windows OR when the SDK ProgID isn't registered, the
                // bridge holds a null COM ref and every wire call throws
                // BridgeUnavailable (9201) with a remediation message.
                //
                // Live verification deferred to Windows + Lacerte box.
                _bridge = new LacerteAccessClient();
            }
        }

        /// <summary>
        /// Test-only ctor: injects an arbitrary <see cref="ILacerteBridge"/>
        /// (a fake or a mock) and forces <see cref="SessionMode.Live"/>. Used
        /// by Phase 9 tests that need to assert the chokepoints route through
        /// the bridge interface in live mode without owning a real Lacerte
        /// install. NOT for production use.
        /// </summary>
        internal LacerteSessionManager(ILacerteBridge bridge, IDictionary<string, string?>? env = null)
        {
            if (bridge == null) throw new ArgumentNullException(nameof(bridge));
            var resolved = env ?? new Dictionary<string, string?>();
            Mode = SessionMode.Live;
            _appName = TryGet(resolved, "LACERTE_APP_NAME");
            _bridge = bridge;
            _sim = null;
        }

        // ===================================================================
        // Lifecycle
        // ===================================================================

        /// <summary>
        /// Sim: forwards to <see cref="SimulationStore.Register"/>; stamps
        /// <see cref="RegisteredAt"/> / <see cref="ExpiresAt"/> (+24h) on
        /// success. Live: throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/>
        /// until Phase 9 wires <c>ITPRegistration.Register</c>.
        /// </summary>
        public async Task<int> Register()
        {
            // F11 — wrap whole Register in WireLog so both sim + live paths
            // produce one log line. Args carry the app name + taxYear only
            // (no secrets — appId is the AppIdStore GUID, redacted-by-policy).
            int taxYearArg = Context?.Year ?? DateTimeOffset.UtcNow.Year;
            return await WireLog.Record<int>("Register",
                new { appName = _appName ?? "Lacerte MCP", taxYear = taxYearArg },
                async () =>
            {
                if (Mode == SessionMode.Live)
                {
                    // Phase 9 task #67 — bridge wire-up. Boundary Invariant #8:
                    // Register is called ONCE at process startup, never mid-tool.
                    // Live verification deferred to Windows + Lacerte box.
                    if (_bridge == null)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
                    }
                    // AppID comes from the Cli's AppIdStore (Credential Manager
                    // on Windows) — the Registration helper reads it directly.
                    var appId = Bridge.Registration.GetStoredAppId();
                    if (appId == null)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.AppNotRegistered,
                            new Dictionary<string, object?>
                            {
                                ["reason"] = "No AppID minted. Run `lacerte-mcp register` first.",
                            });
                    }
                    // Spec wants a tax year for Register; reuse Context.Year if
                    // available, otherwise fall back to the env-default-year
                    // (Phase 11 task), otherwise current calendar year as a
                    // last-resort lower bound.
                    int taxYear = Context?.Year ?? DateTimeOffset.UtcNow.Year;
                    var appName = _appName ?? "Lacerte MCP";
                    // F12 — wrap in AutoReconnect for transient COM retries.
                    int liveStatus = await AutoReconnect.WithRetry<int>(() =>
                        Task.FromResult(_bridge.Register(appName, appId.Value.ToString("D"), taxYear)))
                        .ConfigureAwait(false);
                    if (liveStatus == StatusCodes.AccessGranted)
                    {
                        IsRegistered = true;
                        RegisteredAt = DateTimeOffset.UtcNow;
                        ExpiresAt = RegisteredAt.Value.AddHours(24);
                    }
                    else
                    {
                        IsRegistered = false;
                        RegisteredAt = null;
                        ExpiresAt = null;
                    }
                    return liveStatus;
                }

                var simStatus = _sim!.Register();
                if (simStatus == "AccessGranted")
                {
                    IsRegistered = true;
                    RegisteredAt = DateTimeOffset.UtcNow;
                    ExpiresAt = RegisteredAt.Value.AddHours(24);
                    return StatusCodes.AccessGranted;
                }

                // "NotSignedIn" — leave IsRegistered false.
                IsRegistered = false;
                RegisteredAt = null;
                ExpiresAt = null;
                return StatusCodes.NotSignedIn;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Pure in-memory snapshot of auth + context state — NO wire I/O.
        /// Mirrors QB MCP's <c>qb_session_status</c> shape per agent 02 §6.
        /// </summary>
        public SessionStatusSnapshot SessionStatus()
        {
            double? minutesUntilExpiry = null;
            if (ExpiresAt.HasValue)
            {
                minutesUntilExpiry = Math.Max(0,
                    (ExpiresAt.Value - DateTimeOffset.UtcNow).TotalMinutes);
            }

            // F14 — populate the edition triple. Probe is lazy + one-shot so
            // repeat SessionStatus() calls don't re-probe. In live mode a
            // probe failure (e.g. BridgeUnavailable) is swallowed so
            // SessionStatus() itself never throws — it's a pure status read.
            EditionInfo? edition = null;
            try
            {
                edition = EnsureEditionProbed();
            }
            catch (Exception)
            {
                // Defensive: edition probe is best-effort decoration on
                // SessionStatus. If it fails we leave Edition null rather than
                // poisoning the entire snapshot.
                edition = null;
            }

            return new SessionStatusSnapshot(
                Mode: Mode,
                IsRegistered: IsRegistered,
                RegisteredAt: RegisteredAt,
                ExpiresAt: ExpiresAt,
                MinutesUntilExpiry: minutesUntilExpiry,
                Context: Context,
                AppName: _appName,
                AppId: "***",
                Edition: edition);
        }

        /// <summary>
        /// F14 — one-shot edition probe. Caches the result on the session
        /// manager so repeated SessionStatus() calls don't re-probe the bridge.
        /// Sim mode: returns the sentinel <c>Simulated/0.0.0-sim/0.0-sim</c>
        /// from <see cref="EditionDetection.Probe"/> (called with a null bridge).
        /// Live mode: passes the live bridge so the probe reads the real COM
        /// edition triple — same one-shot caching as <see cref="CanUseNewApis"/>.
        /// </summary>
        private EditionInfo EnsureEditionProbed()
        {
            var cached = _edition;
            if (cached != null) return cached;
            lock (_editionLock)
            {
                if (_edition != null) return _edition;
                // Sim path: pass null bridge → sentinel values. Live: pass
                // the bridge so the late-bound probe reads real values.
                _edition = EditionDetection.Probe(Mode == SessionMode.Live ? _bridge : null);
                return _edition;
            }
        }

        // ===================================================================
        // Context
        // ===================================================================

        /// <summary>
        /// Sets the active <see cref="SessionContext"/>. Validates
        /// <paramref name="taxYear"/> >= 2018 (synthetic 9003) and
        /// <paramref name="module"/> is one of the 9 known module characters
        /// (synthetic <c>InValidClientName</c> 24 for unknown).
        /// </summary>
        public void SetContext(string dataPath, int taxYear, char module, bool readOnly = false)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                var ctx = new Dictionary<string, object?> { ["reason"] = "dataPath is required." };
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet, ctx);
            }

            if (taxYear < 2018)
            {
                var ctx = new Dictionary<string, object?>
                {
                    ["taxYear"] = taxYear,
                    ["floor"] = 2018,
                };
                throw new LacerteSDKError(SyntheticStatusCodes.TaxYearTooOld, ctx);
            }

            if (Modules.GetByChar(module) == null)
            {
                var ctx = new Dictionary<string, object?>
                {
                    ["module"] = module.ToString(),
                    ["validModules"] = string.Join(",", Modules.All.Select(m => m.Char.ToString())),
                };
                // No dedicated "invalid module" synthetic code yet; reuse the
                // SDK's InValidClientName (24) which carries the right "the
                // SDK rejects this token" semantics. Carry the real module in
                // context so the tool layer can render a precise message.
                throw new LacerteSDKError(StatusCodes.InValidClientName, ctx);
            }

            Context = new SessionContext(dataPath, taxYear, module, readOnly);
        }

        // ===================================================================
        // Year/module branching
        // ===================================================================

        /// <summary>
        /// Returns true iff the (year, module) pair supports the TY21+
        /// <c>UpdateTaxReturn</c> APIs. Cached per pair so we don't probe
        /// twice. Sim: <c>year &gt;= 2021</c>. Live: bridge probe — Phase 9
        /// wires; throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/>
        /// until then.
        /// </summary>
        public bool CanUseNewApis(int taxYear, char module)
        {
            return _newApisCache.GetOrAdd((taxYear, module), key =>
            {
                if (Mode == SessionMode.Live)
                {
                    // Phase 9 task #66 — bridge probe.
                    // Live verification deferred to Windows + Lacerte box.
                    if (_bridge == null)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
                    }
                    var appId = Bridge.Registration.GetStoredAppId();
                    var appIdStr = appId?.ToString("D") ?? string.Empty;
                    // F11 + F12 — wire log + auto-reconnect (live only; sim is
                    // a deterministic compile-time decision).
                    return WireLog.RecordSync<bool>("CanUseNewApis",
                        new { year = key.Year, module = key.Module.ToString() },
                        () => AutoReconnect.WithRetrySync<bool>(
                            () => _bridge.CanUseNewApis(appIdStr, key.Year, key.Module)));
                }
                return key.Year >= 2021;
            });
        }

        // ===================================================================
        // Read chokepoints
        // ===================================================================

        /// <summary>
        /// Lists clients in the active module/year. The optional
        /// <paramref name="lastNameFilter"/> is case-insensitive prefix-match
        /// on the conventional C1_2 (last-name) field for individual modules;
        /// for non-I modules it filters on C1_1 (entity name).
        /// </summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListClients(
            string? lastNameFilter = null, int limit = 100)
        {
            // F11 — wrap with WireLog.Record. Args carry the filter + limit
            // (no PII — last-name filter is a search prefix, not a stored secret).
            return WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ListClients", new { lastNameFilter, limit }, () =>
            {
                var ctx = RequireContext();
                EnsureLive();
                var module = RequireSimModule(ctx);
                IEnumerable<DbfRow> rows = module.Clients.Values;

                if (!string.IsNullOrEmpty(lastNameFilter))
                {
                    var f = lastNameFilter!;
                    var nameKey = ctx.Module == 'I' ? "C1_2" : "C1_1";
                    rows = rows.Where(r =>
                        r.Fields.TryGetValue(nameKey, out var v) && v is string s &&
                        s.StartsWith(f, StringComparison.OrdinalIgnoreCase));
                }

                var result = rows
                    .Take(Math.Max(0, limit))
                    .Select(r => (IReadOnlyDictionary<string, object?>)CloneRow(r))
                    .ToList();
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result);
            });
        }

        /// <summary>Reads one client by ClientNumber from DATA1. Returns null if absent.</summary>
        public Task<IReadOnlyDictionary<string, object?>?> ReadClient(string clientNumber)
        {
            // F11 — wrap with WireLog.Record.
            return WireLog.Record<IReadOnlyDictionary<string, object?>?>(
                "ReadClient", new { clientNumber }, () =>
            {
                var ctx = RequireContext();
                EnsureLive();
                var module = RequireSimModule(ctx);
                IReadOnlyDictionary<string, object?>? result = null;
                if (module.Clients.TryGetValue(clientNumber, out var row))
                {
                    result = CloneRow(row);
                }
                return Task.FromResult(result);
            });
        }

        /// <summary>
        /// Generic DBF table reader. Routes to the right per-table map in the
        /// sim store. Live mode: throws <see cref="SyntheticStatusCodes.BridgeUnavailable"/>
        /// pending Phase 4 ODBC wiring.
        /// </summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadTable(
            int table, string? clientNumber = null, string? stateAbbrev = null)
        {
            // F11 — wrap with WireLog.Record. Forwarders below call
            // ReadTableCore directly so we don't double-log when ReadSubclients
            // etc. fan out through ReadTable.
            return WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadTable", new { table, clientNumber, stateAbbrev },
                () => ReadTableCore(table, clientNumber, stateAbbrev));
        }

        private Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadTableCore(
            int table, string? clientNumber, string? stateAbbrev)
        {
            var ctx = RequireContext();
            EnsureLive();
            var module = RequireSimModule(ctx);

            IEnumerable<DbfRow> rows;
            switch (table)
            {
                case 1: rows = module.Clients.Values; break;
                case 2: rows = module.SubClients.Values; break;
                case 3: rows = module.States.Values; break;
                case 4: rows = module.StateSubs.Values; break;
                case 5: rows = module.Statuses.Values; break;
                case 6: rows = module.Displays.Values; break;
                case 7: rows = module.Forms.Values.SelectMany(r => r); break;
                default:
                    var dctx = new Dictionary<string, object?> { ["table"] = table };
                    throw new LacerteSDKError(StatusCodes.DBNotSupported, dctx);
            }

            if (!string.IsNullOrEmpty(clientNumber))
            {
                rows = rows.Where(r => string.Equals(r.ClientNumber, clientNumber, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(stateAbbrev))
            {
                rows = rows.Where(r => string.Equals(r.StateAbbrev, stateAbbrev, StringComparison.OrdinalIgnoreCase));
            }

            var result = rows
                .Select(r => (IReadOnlyDictionary<string, object?>)CloneRow(r))
                .ToList();
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result);
        }

        /// <summary>DATA2 subclient rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadSubclients(string clientNumber)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadSubclients", new { clientNumber }, () => ReadTableCore(2, clientNumber, null));

        /// <summary>DATA3 state rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadStates(string clientNumber)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadStates", new { clientNumber }, () => ReadTableCore(3, clientNumber, null));

        /// <summary>DATA5 F4-status rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadStatuses(string clientNumber)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadStatuses", new { clientNumber }, () => ReadTableCore(5, clientNumber, null));

        /// <summary>DATA7 forms rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadForms(string clientNumber)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadForms", new { clientNumber }, () => ReadTableCore(7, clientNumber, null));

        /// <summary>Per-client detail rows from the simulated <c>[CLIENT-Detail]</c> file.</summary>
        public Task<IReadOnlyList<DetailRow>> ReadDetail(string clientNumber)
        {
            // F11 — wrap with WireLog.Record.
            return WireLog.Record<IReadOnlyList<DetailRow>>("ReadDetail", new { clientNumber }, () =>
            {
                var ctx = RequireContext();
                EnsureLive();
                var module = RequireSimModule(ctx);
                IReadOnlyList<DetailRow> result = module.Details.TryGetValue(clientNumber, out var list)
                    ? (IReadOnlyList<DetailRow>)list.ToList()
                    : Array.Empty<DetailRow>();
                return Task.FromResult(result);
            });
        }

        /// <summary>
        /// TY19+ EFilings JOIN EFStatuses for the active module/year. Returns
        /// flat row dicts so the shape matches what an ODBC join would yield
        /// in live mode.
        /// </summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadEFStatus()
        {
            // F11 — wrap with WireLog.Record. No args (uses active context).
            return WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadEFStatus", args: null, () =>
            {
                var ctx = RequireContext();
                EnsureLive();
                var module = RequireSimModule(ctx);

                var joined = from f in module.EFilings
                             join s in module.EFStatuses on f.Id equals s.EFilingId
                             select (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                             {
                                 ["EFilingId"] = f.Id,
                                 ["ClientId"] = f.ClientId,
                                 ["EFId"] = f.EFId,
                                 ["InstanceId"] = f.InstanceId,
                                 ["Status"] = s.Status,
                                 ["StatusDate"] = s.StatusDate,
                             };
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(joined.ToList());
            });
        }

        // ===================================================================
        // Write chokepoints
        // ===================================================================

        /// <summary>
        /// Writes <paramref name="value"/> to <paramref name="field"/> on
        /// <paramref name="table"/> for <paramref name="clientNumber"/>.
        ///
        /// <para>
        /// Gate order (SYNCHRONOUS up to the first <c>await</c>):
        /// <list type="number">
        ///   <item><description>Context present (<see cref="SyntheticStatusCodes.ContextNotSet"/>).</description></item>
        ///   <item><description>Read-only flag (<see cref="SyntheticStatusCodes.FieldIsReadOnly"/>).</description></item>
        ///   <item><description>Idempotency cache replay (returns cached result OR throws 9102 on conflict).</description></item>
        ///   <item><description><see cref="FieldAccessMapLoader.AssertWritable"/> — module/table/field/year aware.</description></item>
        ///   <item><description>Dry-run snapshot (if requested).</description></item>
        ///   <item><description>Wire call. Sim path forwards to the in-memory store. TY21+ branches conceptual: in sim both branches share the same writer; live splits in Phase 9.</description></item>
        ///   <item><description>Dry-run restore in <c>finally</c>.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        public async Task<int> WriteField(
            int table, string clientNumber, string field, object? value,
            int? subClientID = null, string? stateAbbrev = null,
            bool dryRun = false, string? idempotencyKey = null)
        {
            var ctx = RequireContext();
            RejectIfReadOnly("WriteField", table: table, fieldName: field);

            // Idempotency check (synchronous; before any side effect).
            var payload = BuildPayload(
                "WriteField",
                table, clientNumber, field, value, subClientID, stateAbbrev);
            if (TryReplayIdempotent(idempotencyKey, payload, out int cached))
            {
                return cached;
            }

            // Access-map gate.
            FieldAccessMapLoader.AssertWritable(ctx.Module, table, field, ctx.Year);

            // F11 — log AFTER the access-map check so a client-side-rejected
            // value (e.g. trying to write a read-only field) never hits the
            // log. Args include the value because once we're past the gate
            // it's authorized for the wire.
            int result = await WireLog.Record<int>("WriteField",
                new { table, clientNumber, field, value, subClientID, stateAbbrev, dryRun },
                async () =>
            {
                // TY21+ branching is the session manager's job (Boundary Invariant 5).
                // Live verification deferred to Windows + Lacerte box.
                var useNew = CanUseNewApis(ctx.Year, ctx.Module);

                int inner;
                if (Mode == SessionMode.Live)
                {
                    // Phase 9 task #67 — bridge wire-up. dryRun is sim-only
                    // (the bridge can't snapshot a real DBF cheaply); we honor
                    // dryRun here by short-circuiting BEFORE the wire call and
                    // returning Success without persisting.
                    if (_bridge == null)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
                    }
                    if (dryRun)
                    {
                        inner = StatusCodes.Success;
                    }
                    else
                    {
                        var appId = Bridge.Registration.GetStoredAppId()?.ToString("D") ?? string.Empty;
                        if (useNew)
                        {
                            // TY21+ → UpdateTaxReturn keyed by ClientNumber.
                            // F12 — wrap in AutoReconnect for transient COM retries.
                            inner = await AutoReconnect.WithRetry<int>(() =>
                                Task.FromResult(_bridge.UpdateTaxReturn(appId, ctx.DataPath, ctx.Year, ctx.Module,
                                    table, clientNumber, field, value, subClientID, stateAbbrev)))
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            // Pre-TY21 → WriteToDBFDatabase keyed by row number.
                            // The session manager does not track row numbers (it
                            // works in ClientNumber-space); the live caller is
                            // expected to translate ClientNumber → rowNum
                            // upstream. For now pass int.Parse(clientNumber) as
                            // the row number — the field-write tool layer (Phase
                            // 6 task #51) replaces this with the real lookup.
                            int rowNum = int.TryParse(clientNumber, out var rn) ? rn : -1;
                            // F12 — wrap in AutoReconnect.
                            inner = await AutoReconnect.WithRetry<int>(() =>
                                Task.FromResult(_bridge.WriteField(appId, ctx.DataPath, ctx.Year, ctx.Module,
                                    table, rowNum, field, value)))
                                .ConfigureAwait(false);
                        }
                    }
                }
                else if (dryRun)
                {
                    var snap = SimSnapshot();
                    try
                    {
                        inner = ApplySimFieldWrite(ctx, table, clientNumber, field, value, subClientID, stateAbbrev, useNew);
                    }
                    finally
                    {
                        SimRestore(snap);
                    }
                }
                else
                {
                    inner = ApplySimFieldWrite(ctx, table, clientNumber, field, value, subClientID, stateAbbrev, useNew);
                }
                return inner;
            }).ConfigureAwait(false);

            StoreIdempotent(idempotencyKey, payload, result);
            return result;
        }

        /// <summary>
        /// Detail-row write. No validation here — that lives in the tool layer
        /// (per agent 06: SDK doesn't validate detail-row shape either).
        /// </summary>
        public async Task<int> WriteDetail(
            string clientNumber, DetailRow row, int? replaceIndex = null,
            bool dryRun = false, string? idempotencyKey = null)
        {
            var ctx = RequireContext();
            // Detail-row writes target a whole row (Series/Code/Amount) — no
            // single "field" to name, so we surface just the operation +
            // active module/year via RejectIfReadOnly's context payload.
            RejectIfReadOnly("WriteDetail");

            var payload = BuildPayload("WriteDetail", clientNumber, row, replaceIndex);
            if (TryReplayIdempotent(idempotencyKey, payload, out int cached))
            {
                return cached;
            }

            // F11 — log AFTER read-only + idempotency gates. row is logged
            // verbatim; WireLog.Redact strips SSN/EIN/BankAccountNumber/
            // RoutingNumber before the line hits disk.
            int result = await WireLog.Record<int>("WriteDetail",
                new { clientNumber, row, replaceIndex, dryRun }, async () =>
            {
                int inner;
                if (Mode == SessionMode.Live)
                {
                    // Phase 9 task #67 — bridge wire-up. Live verification
                    // deferred to Windows + Lacerte box.
                    if (_bridge == null)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
                    }
                    if (dryRun)
                    {
                        // Detail-file writes have no SDK-level snapshot path;
                        // honor dryRun by short-circuiting before the wire call.
                        // replaceIndex isn't validated against live rows in dry
                        // mode — that fidelity is deferred to a future hardening
                        // pass.
                        _ = replaceIndex;
                        inner = StatusCodes.Success;
                    }
                    else
                    {
                        var appId = Bridge.Registration.GetStoredAppId()?.ToString("D") ?? string.Empty;
                        // replaceIndex semantics in live mode: the spec's detail
                        // write is row-ordered append only; replace is a sim
                        // convenience. For now ignore replaceIndex live-side and
                        // append unconditionally (the tool layer enforces the
                        // append-only contract in Phase 6 task #53).
                        _ = replaceIndex;
                        // F12 — wrap in AutoReconnect.
                        inner = await AutoReconnect.WithRetry<int>(() =>
                            Task.FromResult(_bridge.WriteDetail(appId, ctx.DataPath, ctx.Year, ctx.Module, clientNumber, row)))
                            .ConfigureAwait(false);
                    }
                }
                else if (dryRun)
                {
                    var snap = SimSnapshot();
                    try { inner = ApplySimDetailWrite(ctx, clientNumber, row, replaceIndex); }
                    finally { SimRestore(snap); }
                }
                else
                {
                    inner = ApplySimDetailWrite(ctx, clientNumber, row, replaceIndex);
                }
                return inner;
            }).ConfigureAwait(false);

            StoreIdempotent(idempotencyKey, payload, result);
            return result;
        }

        /// <summary>
        /// Creates a new client. Returns <see cref="CreateClientResult"/> with
        /// the assigned ClientNumber on success.
        /// </summary>
        public async Task<CreateClientResult> CreateClient(
            CreateClientArgs args, bool dryRun = false, string? idempotencyKey = null)
        {
            var ctx = RequireContext();
            // CreateClient writes a whole DATA1 row (5-field ctor on the SDK
            // side); there is no single field to surface, so the read-only
            // rejection just carries the operation + active module/year.
            RejectIfReadOnly("CreateClient");
            if (args == null) throw new ArgumentNullException(nameof(args));

            var payload = BuildPayload("CreateClient", args);
            if (TryReplayIdempotent(idempotencyKey, payload, out CreateClientResult cached))
            {
                return cached;
            }

            // Client-side validation per agent 06 §6.
            if (string.IsNullOrEmpty(args.ClientID) || args.ClientID.Length > 8)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.ClientIDTooLong,
                    new Dictionary<string, object?> { ["clientID"] = args.ClientID });
            }
            if (ctx.Year < 2018)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.TaxYearTooOld,
                    new Dictionary<string, object?> { ["taxYear"] = ctx.Year });
            }

            // F11 — log AFTER client-side validation gates (read-only +
            // ClientID length + taxYear floor). args is logged verbatim;
            // WireLog redacts secret fields before disk.
            CreateClientResult result = await WireLog.Record<CreateClientResult>(
                "CreateClient", new { args, dryRun }, async () =>
            {
                CreateClientResult inner;
                if (Mode == SessionMode.Live)
                {
                    // Phase 9 task #67 — bridge wire-up. Live verification
                    // deferred to Windows + Lacerte box.
                    if (_bridge == null)
                    {
                        throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
                    }
                    if (dryRun)
                    {
                        // CreateClient on the SDK has no dry-run primitive;
                        // honor dryRun by short-circuiting before the wire call.
                        // Return a synthetic Success without a ClientNumber so
                        // callers can distinguish dry-run from real allocation.
                        inner = new CreateClientResult(StatusCodes.Success, null);
                    }
                    else
                    {
                        var appId = Bridge.Registration.GetStoredAppId()?.ToString("D") ?? string.Empty;
                        // F12 — wrap in AutoReconnect.
                        var bridgeResult = await AutoReconnect.WithRetry<BridgeCreateClientResult>(() =>
                            Task.FromResult(_bridge.CreateClient(
                                appId, ctx.DataPath, ctx.Year, ctx.Module,
                                args.ClientID, args.TaxType,
                                args.FirstName, args.LastName, args.EmailID)))
                            .ConfigureAwait(false);
                        inner = new CreateClientResult(bridgeResult.Status, bridgeResult.ClientNumber);
                    }
                }
                else if (dryRun)
                {
                    var snap = SimSnapshot();
                    try { inner = ApplySimCreateClient(ctx, args); }
                    finally { SimRestore(snap); }
                }
                else
                {
                    inner = ApplySimCreateClient(ctx, args);
                }
                return inner;
            }).ConfigureAwait(false);

            StoreIdempotent(idempotencyKey, payload, result);
            return result;
        }

        // ===================================================================
        // Internals — guards
        // ===================================================================

        private SessionContext RequireContext()
        {
            var ctx = Context;
            if (ctx == null)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
            }
            return ctx;
        }

        // Synchronous read-only gate — fires before ANY await on writes.
        // <paramref name="fieldName"/>, when supplied by the caller, lets the
        // 9001 error message name the specific field the write was targeting
        // (follow-up F3). Active context (module/table) is included so the
        // operator sees the full coordinates of the rejected write.
        private void RejectIfReadOnly(string operation, int? table = null, string? fieldName = null)
        {
            var ctx = RequireContext();
            if (!ctx.ReadOnly) return;

            var reason = fieldName == null
                ? $"Read-only session: {operation} rejected."
                : $"Read-only session: {operation} on field '{fieldName}' rejected.";

            var data = new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["module"] = ctx.Module.ToString(),
                ["taxYear"] = ctx.Year,
                ["reason"] = reason,
            };
            if (table.HasValue) data["table"] = table.Value;
            if (fieldName != null) data["field"] = fieldName;

            throw new LacerteSDKError(SyntheticStatusCodes.FieldIsReadOnly, data);
        }

        private void EnsureLive()
        {
            // Sim path is always live (the in-memory store is always there).
            // Live path throws until Phase 9 wires the bridge + ODBC.
            if (Mode == SessionMode.Live)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
            }
        }

        // ===================================================================
        // Internals — sim adapters
        //
        // The session manager touches the sim store directly via ModuleStore
        // for now. Stream A is filling in the spec-shaped Simulate* methods on
        // SimulationStore; once those land, swap these helpers to delegate.
        // ===================================================================

        // TODO Stream A: when SimulationStore.ProcessQuery / SimulateFieldWrite /
        // SimulateCreateClient / WriteDetail land with their full TAPIStatus
        // return-path coverage, swap these helpers to delegate.

        private ModuleStore RequireSimModule(SessionContext ctx)
        {
            return _sim!.GetOrAddModule(ctx.DataPath, ctx.Year, ctx.Module);
        }

        private int ApplySimFieldWrite(
            SessionContext ctx, int table, string clientNumber, string field, object? value,
            int? subClientID, string? stateAbbrev, bool useNewApis)
        {
            // useNewApis is observable here — live mode in Phase 9 picks
            // UpdateTaxReturn vs WriteToDBFDatabase based on it. In sim both
            // paths share the same writer because the store is the canonical
            // source either way.
            _ = useNewApis;

            var module = RequireSimModule(ctx);

            DbfRow? row = null;
            switch (table)
            {
                case 1:
                    module.Clients.TryGetValue(clientNumber, out row);
                    break;
                case 2:
                    if (!subClientID.HasValue || subClientID.Value <= 0)
                        throw new LacerteSDKError(SyntheticStatusCodes.SubClientIDRequired);
                    module.SubClients.TryGetValue((clientNumber, subClientID.Value), out row);
                    break;
                case 3:
                    if (string.IsNullOrEmpty(stateAbbrev))
                        throw new LacerteSDKError(SyntheticStatusCodes.StateAbbrevRequired);
                    module.States.TryGetValue((clientNumber, stateAbbrev!), out row);
                    break;
                case 4:
                    if (!subClientID.HasValue || subClientID.Value <= 0)
                        throw new LacerteSDKError(SyntheticStatusCodes.SubClientIDRequired);
                    if (string.IsNullOrEmpty(stateAbbrev))
                        throw new LacerteSDKError(SyntheticStatusCodes.StateAbbrevRequired);
                    module.StateSubs.TryGetValue((clientNumber, subClientID.Value, stateAbbrev!), out row);
                    break;
                case 5:
                    module.Statuses.TryGetValue(clientNumber, out row);
                    break;
                case 6:
                    module.Displays.TryGetValue(clientNumber, out row);
                    break;
                default:
                    throw new LacerteSDKError(StatusCodes.DBNotSupported,
                        new Dictionary<string, object?> { ["table"] = table });
            }

            if (row == null)
            {
                throw new LacerteSDKError(StatusCodes.OutOfIndex,
                    new Dictionary<string, object?>
                    {
                        ["table"] = table,
                        ["clientNumber"] = clientNumber,
                    });
            }

            row.Fields[field] = value;
            return StatusCodes.Success;
        }

        private int ApplySimDetailWrite(SessionContext ctx, string clientNumber, DetailRow row, int? replaceIndex)
        {
            var module = RequireSimModule(ctx);
            var list = module.Details.GetOrAdd(clientNumber, _ => new List<DetailRow>());
            lock (list)
            {
                if (replaceIndex.HasValue)
                {
                    var i = replaceIndex.Value;
                    if (i < 0 || i >= list.Count)
                    {
                        throw new LacerteSDKError(StatusCodes.OutOfIndex,
                            new Dictionary<string, object?> { ["replaceIndex"] = i });
                    }
                    list[i] = row;
                }
                else
                {
                    list.Add(row);
                }
            }
            return StatusCodes.Success;
        }

        private CreateClientResult ApplySimCreateClient(SessionContext ctx, CreateClientArgs args)
        {
            var module = RequireSimModule(ctx);

            // Duplicate ClientID check — scan DATA1 for matching ClientID.
            foreach (var existing in module.Clients.Values)
            {
                if (existing.Fields.TryGetValue("ClientID", out var v) &&
                    v is string s &&
                    string.Equals(s, args.ClientID, StringComparison.OrdinalIgnoreCase))
                {
                    throw new LacerteSDKError(StatusCodes.DuplicateClient,
                        new Dictionary<string, object?> { ["clientID"] = args.ClientID });
                }
            }

            var clientNumber = System.Threading.Interlocked.Increment(ref module.NextClientNumber).ToString();
            // Interlocked.Increment returned the post-increment value; we want
            // the pre-increment slot. Compensate.
            clientNumber = (int.Parse(clientNumber) - 1).ToString();

            var row = new DbfRow(clientNumber)
            {
                Fields =
                {
                    ["C1_0"] = clientNumber,
                    ["C1_1"] = args.FirstName,
                    ["C1_2"] = args.LastName,
                    ["C1_3"] = args.EmailID,
                    ["ClientID"] = args.ClientID,
                    ["TaxType"] = args.TaxType,
                },
            };
            module.Clients[clientNumber] = row;
            return new CreateClientResult(StatusCodes.Success, clientNumber);
        }

        // ===================================================================
        // Internals — snapshot/restore for dry-run
        // ===================================================================

        // TODO Stream A: SimulationStore.Snapshot/Restore (task #23) will
        // supersede this — at which point delegate to _sim.Snapshot/Restore.
        // For now a per-call deep clone of only the active module is enough
        // for write chokepoint dry-run semantics.

        private sealed class SimDryRunSnapshot
        {
            public string DataPath = "";
            public int Year;
            public char Module;
            public List<KeyValuePair<string, DbfRow>> Clients = new List<KeyValuePair<string, DbfRow>>();
            public List<KeyValuePair<(string, int), DbfRow>> SubClients = new List<KeyValuePair<(string, int), DbfRow>>();
            public List<KeyValuePair<(string, string), DbfRow>> States = new List<KeyValuePair<(string, string), DbfRow>>();
            public List<KeyValuePair<(string, int, string), DbfRow>> StateSubs = new List<KeyValuePair<(string, int, string), DbfRow>>();
            public List<KeyValuePair<string, DbfRow>> Statuses = new List<KeyValuePair<string, DbfRow>>();
            public List<KeyValuePair<string, DbfRow>> Displays = new List<KeyValuePair<string, DbfRow>>();
            public List<KeyValuePair<string, List<DbfRow>>> Forms = new List<KeyValuePair<string, List<DbfRow>>>();
            public List<KeyValuePair<string, List<DetailRow>>> Details = new List<KeyValuePair<string, List<DetailRow>>>();
            public int NextClientNumber;
            public int NextEFilingId;
        }

        private SimDryRunSnapshot SimSnapshot()
        {
            var ctx = RequireContext();
            var module = RequireSimModule(ctx);
            var snap = new SimDryRunSnapshot
            {
                DataPath = ctx.DataPath,
                Year = ctx.Year,
                Module = ctx.Module,
                NextClientNumber = module.NextClientNumber,
                NextEFilingId = module.NextEFilingId,
            };
            foreach (var kv in module.Clients) snap.Clients.Add(new KeyValuePair<string, DbfRow>(kv.Key, CloneDbfRow(kv.Value)));
            foreach (var kv in module.SubClients) snap.SubClients.Add(new KeyValuePair<(string, int), DbfRow>(kv.Key, CloneDbfRow(kv.Value)));
            foreach (var kv in module.States) snap.States.Add(new KeyValuePair<(string, string), DbfRow>(kv.Key, CloneDbfRow(kv.Value)));
            foreach (var kv in module.StateSubs) snap.StateSubs.Add(new KeyValuePair<(string, int, string), DbfRow>(kv.Key, CloneDbfRow(kv.Value)));
            foreach (var kv in module.Statuses) snap.Statuses.Add(new KeyValuePair<string, DbfRow>(kv.Key, CloneDbfRow(kv.Value)));
            foreach (var kv in module.Displays) snap.Displays.Add(new KeyValuePair<string, DbfRow>(kv.Key, CloneDbfRow(kv.Value)));
            foreach (var kv in module.Forms) snap.Forms.Add(new KeyValuePair<string, List<DbfRow>>(kv.Key, kv.Value.Select(CloneDbfRow).ToList()));
            foreach (var kv in module.Details) snap.Details.Add(new KeyValuePair<string, List<DetailRow>>(kv.Key, kv.Value.ToList()));
            return snap;
        }

        private void SimRestore(SimDryRunSnapshot snap)
        {
            var module = _sim!.GetOrAddModule(snap.DataPath, snap.Year, snap.Module);
            module.Clients.Clear();
            foreach (var kv in snap.Clients) module.Clients[kv.Key] = kv.Value;
            module.SubClients.Clear();
            foreach (var kv in snap.SubClients) module.SubClients[kv.Key] = kv.Value;
            module.States.Clear();
            foreach (var kv in snap.States) module.States[kv.Key] = kv.Value;
            module.StateSubs.Clear();
            foreach (var kv in snap.StateSubs) module.StateSubs[kv.Key] = kv.Value;
            module.Statuses.Clear();
            foreach (var kv in snap.Statuses) module.Statuses[kv.Key] = kv.Value;
            module.Displays.Clear();
            foreach (var kv in snap.Displays) module.Displays[kv.Key] = kv.Value;
            module.Forms.Clear();
            foreach (var kv in snap.Forms) module.Forms[kv.Key] = kv.Value;
            module.Details.Clear();
            foreach (var kv in snap.Details) module.Details[kv.Key] = kv.Value;
            module.NextClientNumber = snap.NextClientNumber;
            module.NextEFilingId = snap.NextEFilingId;
        }

        private static DbfRow CloneDbfRow(DbfRow src)
        {
            var copy = new DbfRow(src.ClientNumber, src.SubClientID, src.StateAbbrev);
            foreach (var kv in src.Fields)
            {
                copy.Fields[kv.Key] = kv.Value;
            }
            return copy;
        }

        private static Dictionary<string, object?> CloneRow(DbfRow src)
        {
            var dict = new Dictionary<string, object?>(src.Fields.Count + 3)
            {
                ["ClientNumber"] = src.ClientNumber,
            };
            if (src.SubClientID.HasValue) dict["SubClientID"] = src.SubClientID.Value;
            if (!string.IsNullOrEmpty(src.StateAbbrev)) dict["StateAbbrev"] = src.StateAbbrev;
            foreach (var kv in src.Fields)
            {
                dict[kv.Key] = kv.Value;
            }
            return dict;
        }

        // ===================================================================
        // Internals — idempotency cache
        // ===================================================================

        private bool TryReplayIdempotent<T>(string? key, string payload, out T cached)
        {
            cached = default!;
            if (string.IsNullOrEmpty(key)) return false;

            if (_idempotencyCache.TryGetValue(key!, out var entry))
            {
                if (entry.PayloadFingerprint == payload)
                {
                    if (entry.Result is T match) { cached = match; return true; }
                    // Cached result type mismatch — treat as conflict.
                }
                var data = new Dictionary<string, object?>
                {
                    ["idempotencyKey"] = key,
                    ["reason"] = "Idempotency key reused with a different payload fingerprint.",
                };
                throw new LacerteSDKError(SyntheticStatusCodes.IdempotencyConflict, data);
            }
            return false;
        }

        private void StoreIdempotent(string? key, string payload, object? result)
        {
            if (string.IsNullOrEmpty(key)) return;
            _idempotencyCache[key!] = new IdempotencyCacheEntry(
                payload, result, DateTimeOffset.UtcNow);
        }

        private static string BuildPayload(string op, params object?[] args)
        {
            // Canonicalize the operation + args to a JSON blob, then SHA-256
            // it. This is deterministic for primitive args; for complex args
            // (DetailRow, CreateClientArgs) the JsonSerializer's default
            // contract is stable across calls in the same process.
            var canon = new Dictionary<string, object?>
            {
                ["op"] = op,
                ["args"] = args,
            };
            var json = JsonSerializer.Serialize(canon);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                return Convert.ToBase64String(bytes);
            }
        }

        // ===================================================================
        // Internals — env helpers
        // ===================================================================

        private static IDictionary<string, string?> ReadProcessEnv()
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var raw = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry kv in raw)
            {
                if (kv.Key is string k)
                {
                    dict[k] = kv.Value?.ToString();
                }
            }
            return dict;
        }

        private static bool ReadEnvFlag(IDictionary<string, string?> env, string name)
        {
            return TryGet(env, name) == "1";
        }

        private static string? TryGet(IDictionary<string, string?> env, string name)
        {
            return env.TryGetValue(name, out var v) ? v : null;
        }

        // ===================================================================
        // Disposal
        // ===================================================================

        public void Dispose()
        {
            // Sim store + Phase-9 bridge are owned here. SimulationStore has
            // no disposable resources today; the live bridge will when Phase 9
            // wires it.
            (_bridge as IDisposable)?.Dispose();
            (_odbc as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Args for <see cref="LacerteSessionManager.CreateClient"/>. Matches the
    /// SDK's <c>IClient(TaxType, ClientID, EmailID, FirstName, LastName)</c>
    /// constructor signature.
    /// </summary>
    public sealed record CreateClientArgs(
        string ClientID,
        string TaxType,
        string? FirstName = null,
        string? LastName = null,
        string? EmailID = null);

    /// <summary>
    /// Result of <see cref="LacerteSessionManager.CreateClient"/>. <c>Status</c>
    /// is a TAPIStatus value (1 = Success). <c>ClientNumber</c> is the
    /// sim-assigned monotonic identifier on success; null on failure.
    /// </summary>
    public sealed record CreateClientResult(int Status, string? ClientNumber);
}
