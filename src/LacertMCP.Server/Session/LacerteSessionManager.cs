using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Odbc;
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

        // Phase 4 task #39 — constructed lazily on first ODBC read with the
        // active DSN connection string; null until then. Typed (not object?)
        // so a future contributor can't accidentally route around the proper
        // reader. See ARCHITECTURE.md "Module Boundaries" for the read path.
#pragma warning disable CS0649 // assigned in Phase 4 wire-up; null sentinel for now
        private LacerteDsnReader? _odbc;
#pragma warning restore CS0649

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

        // Per-key semaphores to serialize concurrent same-key writes so the
        // TryReplay → execute → StoreIdempotent triple is atomic per key.
        // Without this, two concurrent identical writes both miss the cache
        // and both execute, defeating the idempotency contract.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _idempotencyLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        // P14-013/P14-064 — bound the cache so a long-running session can't
        // leak idempotency entries forever. TTL matches the 24h Intuit auth
        // window (entries created before re-auth are semantically stale).
        // Eviction is opportunistic — checked on TryReplay (expired entries
        // treated as miss + removed) and StoreIdempotent (over-cap → prune
        // expired entries + drop oldest by CreatedAt).
        private const int IdempotencyCacheMaxEntries = 10_000;
        private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

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
                Edition: edition,
                // P14-040: echo the assembly InformationalVersion so operators get
                // a copy-pasteable build stamp via lacerte_session_status.
                BuildVersion: Diagnostics.BuildInfo.Version);
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
                // P14-061 — cache the attempt (success OR failure) so the
                // docstring's "never re-probed for the lifetime of the
                // process" contract holds even when Probe() throws (which is
                // the steady state today — Phase 9 hasn't wired the live
                // probe). A failed probe is recorded as the "Unavailable"
                // sentinel so subsequent SessionStatus calls return cheaply
                // instead of re-entering Probe.
                try
                {
                    _edition = EditionDetection.Probe(Mode == SessionMode.Live ? _bridge : null);
                }
                catch (Exception ex)
                {
                    // Log the first (and only) probe failure via WireLog so
                    // operators see why Edition stays "Unavailable" instead
                    // of silent null. RecordSync swallows any logging
                    // exception internally; wrap in try/catch defensively in
                    // case the underlying writer throws synchronously.
                    try
                    {
                        WireLog.RecordSync<object?>("EditionDetection.Probe",
                            new { mode = Mode.ToString() },
                            () => throw ex);
                    }
                    catch { /* logging is best-effort; never poison the probe */ }
                    _edition = new EditionInfo("Unavailable", "Unavailable", "Unavailable");
                }
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

            // P14-049 — defensive path-string validation. Rejects relative
            // paths, '..' traversal, control characters, UNC (without the
            // LACERTE_ALLOW_UNC escape hatch), and paths outside
            // LACERTE_DATA_ROOT when that env var is set. Throws
            // LacerteSDKError(9013 InvalidDataPath) with {dataPath, reason}
            // — preserves the structured-error invariant so the tool layer
            // surfaces a precise rejection.
            PathValidation.ValidateDataPath(dataPath);

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
                ThrowIfLiveUnwired();
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
                ThrowIfLiveUnwired();
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
            ThrowIfLiveUnwired();
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
                ThrowIfLiveUnwired();
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
                ThrowIfLiveUnwired();
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

            // Spec §2 table-access invariant: DATA6 (Display) and DATA7
            // (Forms) are read-only at the TABLE level. We gate here in the
            // chokepoint so the rule holds independent of access-map state
            // (today the base maps are empty, which means today's fail-closed
            // behaviour already rejects table 6/7 writes; the moment the map
            // gains a writable DATA6/DATA7 entry, this gate keeps the spec
            // invariant load-bearing). See P14-054.
            if (table == 6 || table == 7)
            {
                throw new LacerteSDKError(StatusCodes.ExposedForRead,
                    new Dictionary<string, object?>
                    {
                        ["table"] = table,
                        ["reason"] = "DATA" + table + " is read-only per spec §2 (Display/Forms tables are not writable through the SDK).",
                    });
            }

            // P14-005 — per-key serialization. Concurrent same-key calls wait
            // here so TryReplay → execute → StoreIdempotent runs atomically.
            // Null/empty key: skip locking (no idempotency contract to honor).
            return await ExecuteWithIdempotencyLock<int>(idempotencyKey, async () =>
            {
                // P14-042 — only compute the payload fingerprint when there's
                // an idempotency key. JSON serialize + SHA-256 + base64 is
                // wasted work on the common no-key write path.
                string payload = string.IsNullOrEmpty(idempotencyKey)
                    ? string.Empty
                    : BuildPayload("WriteField", table, clientNumber, field, value, subClientID, stateAbbrev);

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
                                // P14-022/P14-031 — Pre-TY21 → WriteToDBFDatabase keyed
                                // by ROW number, not ClientNumber. The session manager
                                // works in ClientNumber-space; translating
                                // ClientNumber → row index requires an ODBC lookup
                                // (Phase 4 task #39 + follow-up). Until that lookup
                                // lands the only safe behavior is to refuse the
                                // write with a structured error rather than silently
                                // fall through with rowNum=-1 (which the live SDK
                                // will reject with OutOfIndex at best or, worse,
                                // overwrite a sentinel row). Use SimulationOnly
                                // (9202) since the operator's remediation is the
                                // same: stick to TY21+ for now or run in sim.
                                throw new LacerteSDKError(SyntheticStatusCodes.SimulationOnly,
                                    new Dictionary<string, object?>
                                    {
                                        ["op"] = "WriteField",
                                        ["reason"] = "Pre-TY21 live writes require a ClientNumber → row-number lookup that is not yet implemented (Todo task #51 follow-up). Use TY21+ or run in simulation mode.",
                                        ["year"] = ctx.Year,
                                        ["module"] = ctx.Module.ToString(),
                                        ["clientNumber"] = clientNumber,
                                    });
                            }
                        }
                    }
                    else if (dryRun)
                    {
                        // P14-043 — per-operation undo: locate the row first
                        // (validation throws happen here), then capture the
                        // single (table, key, field, prior) tuple, then write,
                        // then restore in finally. Cost: O(1) per dry-run
                        // write vs O(active-module) for the old whole-module
                        // snapshot.
                        Action? undo = null;
                        try
                        {
                            inner = ApplySimFieldWriteWithUndo(ctx, table, clientNumber, field, value, subClientID, stateAbbrev, useNew, out undo);
                        }
                        finally
                        {
                            undo?.Invoke();
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
            }).ConfigureAwait(false);
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
            // P14-002 — no AssertWritable here; see DECISIONS.md "Detail-row +
            // whole-row writes are outside the access-map gate". The map's
            // (module|table|field) key shape can't address detail rows.
            // Safety net: DetailsTools.ValidateDetailWrite handles row-shape
            // validation before this method is invoked.
            RejectIfReadOnly("WriteDetail");

            return await ExecuteWithIdempotencyLock<int>(idempotencyKey, async () =>
            {
                // P14-042 — only compute payload when an idempotency key exists.
                string payload = string.IsNullOrEmpty(idempotencyKey)
                    ? string.Empty
                    : BuildPayload("WriteDetail", clientNumber, row, replaceIndex);

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
                            _ = replaceIndex;
                            inner = StatusCodes.Success;
                        }
                        else
                        {
                            var appId = Bridge.Registration.GetStoredAppId()?.ToString("D") ?? string.Empty;
                            _ = replaceIndex;
                            // F12 — wrap in AutoReconnect.
                            inner = await AutoReconnect.WithRetry<int>(() =>
                                Task.FromResult(_bridge.WriteDetail(appId, ctx.DataPath, ctx.Year, ctx.Module, clientNumber, row)))
                                .ConfigureAwait(false);
                        }
                    }
                    else if (dryRun)
                    {
                        // P14-043 — per-operation undo. Capture (clientNumber,
                        // index, priorRow) inside the lock, mutate, restore on
                        // exit. O(1) vs old whole-module snapshot.
                        Action? undo = null;
                        try
                        {
                            inner = ApplySimDetailWriteWithUndo(ctx, clientNumber, row, replaceIndex, out undo);
                        }
                        finally
                        {
                            undo?.Invoke();
                        }
                    }
                    else
                    {
                        inner = ApplySimDetailWrite(ctx, clientNumber, row, replaceIndex);
                    }
                    return inner;
                }).ConfigureAwait(false);

                StoreIdempotent(idempotencyKey, payload, result);
                return result;
            }).ConfigureAwait(false);
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
            // P14-002 — no AssertWritable here; see DECISIONS.md "Detail-row +
            // whole-row writes are outside the access-map gate". CreateClient
            // writes an entire row whose fields aren't enumerable until after
            // the SDK assigns the row; client-side safety is ClientID length
            // (here) + SimulateCreateClient's full TAPIStatus gate chain.
            RejectIfReadOnly("CreateClient");
            if (args == null) throw new ArgumentNullException(nameof(args));

            return await ExecuteWithIdempotencyLock<CreateClientResult>(idempotencyKey, async () =>
            {
                // P14-042 — only compute payload when an idempotency key exists.
                string payload = string.IsNullOrEmpty(idempotencyKey)
                    ? string.Empty
                    : BuildPayload("CreateClient", args);

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
                        // P14-043 — per-operation undo. CreateClient mints a
                        // ClientNumber + adds one Clients entry; undo decrements
                        // NextClientNumber + removes the entry.
                        Action? undo = null;
                        try
                        {
                            inner = ApplySimCreateClientWithUndo(ctx, args, out undo);
                        }
                        finally
                        {
                            undo?.Invoke();
                        }
                    }
                    else
                    {
                        inner = ApplySimCreateClient(ctx, args);
                    }
                    return inner;
                }).ConfigureAwait(false);

                StoreIdempotent(idempotencyKey, payload, result);
                return result;
            }).ConfigureAwait(false);
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

        /// <summary>
        /// P14-003/P14-052/P14-059 — Read paths are sim-only until Phase 9
        /// wires the bridge + ODBC. Sim falls through; live throws
        /// BridgeUnavailable. Name is intentionally inverted-as-explicit so
        /// the call sites read truthfully ("throw if we're in live mode and
        /// the wire isn't there yet"). When Phase 4 task #39 lands and ODBC
        /// reads are wired, this helper plus its five call sites can be
        /// deleted in one sweep.
        ///
        /// <para>NOTE: <see cref="LacerteAccessClient.EnsureLive"/> exists in
        /// the bridge layer with OPPOSITE semantics (throws when COM is not
        /// available). The rename here resolves the name collision.</para>
        /// </summary>
        private void ThrowIfLiveUnwired()
        {
            if (Mode == SessionMode.Live)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable);
            }
        }

        // ===================================================================
        // Internals — sim adapters
        //
        // Sim helpers below operate on ModuleStore directly rather than
        // delegating to SimulationStore.SimulateFieldWrite / WriteDetail.
        // The store methods exist (Phase 2 tasks #19-21) but layer in their
        // own TAPIStatus guards (e.g. NotSignedIn checks) that this manager
        // either runs at a different gate-order point or doesn't need;
        // double-checking would change observable error ordering. Keep the
        // direct path for field/detail writes.
        //
        // CreateClient IS delegated to SimulationStore.SimulateCreateClient
        // (P14-030) because the spec-faithful gate chain there (IsRegistered
        // → year → length → regex → TaxType↔Module → duplicate) was being
        // bypassed when callers reached the session manager directly, making
        // sim and live observationally divergent.
        // ===================================================================

        private ModuleStore RequireSimModule(SessionContext ctx)
        {
            return _sim!.GetOrAddModule(ctx.DataPath, ctx.Year, ctx.Module);
        }

        private int ApplySimFieldWrite(
            SessionContext ctx, int table, string clientNumber, string field, object? value,
            int? subClientID, string? stateAbbrev, bool useNewApis)
        {
            Action? _;
            return ApplySimFieldWriteWithUndo(ctx, table, clientNumber, field, value,
                subClientID, stateAbbrev, useNewApis, out _, registerUndo: false);
        }

        // P14-043 — overload that captures a per-operation undo closure so
        // dry-run can roll back in O(1) instead of cloning the whole module.
        // When registerUndo is false, undo is set to null and the call is
        // a normal (non-dry-run) write.
        private int ApplySimFieldWriteWithUndo(
            SessionContext ctx, int table, string clientNumber, string field, object? value,
            int? subClientID, string? stateAbbrev, bool useNewApis, out Action? undo)
        {
            return ApplySimFieldWriteWithUndo(ctx, table, clientNumber, field, value,
                subClientID, stateAbbrev, useNewApis, out undo, registerUndo: true);
        }

        private int ApplySimFieldWriteWithUndo(
            SessionContext ctx, int table, string clientNumber, string field, object? value,
            int? subClientID, string? stateAbbrev, bool useNewApis,
            out Action? undo, bool registerUndo)
        {
            // useNewApis is observable here — live mode in Phase 9 picks
            // UpdateTaxReturn vs WriteToDBFDatabase based on it. In sim both
            // paths share the same writer because the store is the canonical
            // source either way.
            _ = useNewApis;

            undo = null;
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

            // Capture the prior value (if any) BEFORE mutating so dry-run can
            // restore it exactly. A missing entry undoes by Remove; an existing
            // entry undoes by re-assigning the prior boxed value.
            if (registerUndo)
            {
                var capturedRow = row;
                var capturedField = field;
                var hadPrior = row.Fields.TryGetValue(field, out var priorValue);
                undo = () =>
                {
                    if (hadPrior) capturedRow.Fields[capturedField] = priorValue;
                    else capturedRow.Fields.Remove(capturedField);
                };
            }

            row.Fields[field] = value;
            return StatusCodes.Success;
        }

        private int ApplySimDetailWrite(SessionContext ctx, string clientNumber, DetailRow row, int? replaceIndex)
        {
            Action? _;
            return ApplySimDetailWriteWithUndo(ctx, clientNumber, row, replaceIndex, out _, registerUndo: false);
        }

        // P14-043 — overload that captures an O(1) per-op undo for dry-run.
        private int ApplySimDetailWriteWithUndo(
            SessionContext ctx, string clientNumber, DetailRow row, int? replaceIndex, out Action? undo)
        {
            return ApplySimDetailWriteWithUndo(ctx, clientNumber, row, replaceIndex, out undo, registerUndo: true);
        }

        private int ApplySimDetailWriteWithUndo(
            SessionContext ctx, string clientNumber, DetailRow row, int? replaceIndex,
            out Action? undo, bool registerUndo)
        {
            undo = null;
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
                    if (registerUndo)
                    {
                        var capturedList = list;
                        var capturedIndex = i;
                        var prior = list[i];
                        undo = () =>
                        {
                            lock (capturedList) { capturedList[capturedIndex] = prior; }
                        };
                    }
                    list[i] = row;
                }
                else
                {
                    if (registerUndo)
                    {
                        var capturedList = list;
                        undo = () =>
                        {
                            lock (capturedList)
                            {
                                if (capturedList.Count > 0)
                                    capturedList.RemoveAt(capturedList.Count - 1);
                            }
                        };
                    }
                    list.Add(row);
                }
            }
            return StatusCodes.Success;
        }

        // P14-030 — delegate to SimulationStore.SimulateCreateClient so the
        // spec-faithful gate order (NotSignedIn → year → length → regex →
        // TaxType↔Module → duplicate) runs uniformly in sim. Translates the
        // (status, clientNumber) tuple back to the manager's throw-on-error
        // contract.
        private CreateClientResult ApplySimCreateClient(SessionContext ctx, CreateClientArgs args)
        {
            Action? _;
            return ApplySimCreateClientWithUndo(ctx, args, out _, registerUndo: false);
        }

        private CreateClientResult ApplySimCreateClientWithUndo(
            SessionContext ctx, CreateClientArgs args, out Action? undo)
        {
            return ApplySimCreateClientWithUndo(ctx, args, out undo, registerUndo: true);
        }

        private CreateClientResult ApplySimCreateClientWithUndo(
            SessionContext ctx, CreateClientArgs args, out Action? undo, bool registerUndo)
        {
            undo = null;
            var simArgs = new SimulateCreateClientArgs(
                ctx.DataPath, ctx.Year, ctx.Module,
                args.ClientID, args.TaxType,
                args.FirstName, args.LastName, args.EmailID);

            // Capture pre-state for undo BEFORE calling SimulateCreateClient so
            // a successful create can be rolled back atomically for dry-run.
            var module = registerUndo ? RequireSimModule(ctx) : null;
            var priorNext = module?.NextClientNumber ?? 0;

            var (status, clientNumber) = _sim!.SimulateCreateClient(simArgs);
            if (status != StatusCodes.Success)
            {
                throw new LacerteSDKError(status, new Dictionary<string, object?>
                {
                    ["clientID"] = args.ClientID,
                    ["taxType"] = args.TaxType,
                    ["module"] = ctx.Module.ToString(),
                });
            }

            if (registerUndo && module != null && clientNumber != null)
            {
                var capturedModule = module;
                var capturedClient = clientNumber;
                var capturedPriorNext = priorNext;
                undo = () =>
                {
                    capturedModule.Clients.TryRemove(capturedClient, out _);
                    capturedModule.Details.TryRemove(capturedClient, out _);
                    Interlocked.Exchange(ref capturedModule.NextClientNumber, capturedPriorNext);
                };
            }

            return new CreateClientResult(status, clientNumber);
        }

        // ===================================================================
        // Internals — row-shape clone for read paths
        //
        // P14-043/P14-070 — The old whole-module SimSnapshot/SimRestore pair
        // is gone. Dry-run write chokepoints use per-operation undo closures
        // returned by ApplySim*WithUndo helpers above. The change drops dry-run
        // cost from O(active-module) to O(1) and resolves the stale "TODO
        // Stream A" pointing at SimulationStore.Snapshot/Restore (task #23
        // completed; the broader Snapshot/Restore primitive is intentionally
        // not used here because it clones every module, not just the active
        // one, and the dry-run contract only needs single-op rollback).
        // ===================================================================

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

        // P14-005 — wraps the (TryReplay → execute body → StoreIdempotent)
        // triple in a per-key SemaphoreSlim so concurrent same-key writes are
        // serialized. Null/empty key bypasses locking entirely (no idempotency
        // contract). The semaphore stays in _idempotencyLocks for the life of
        // the session — cheap to leak (one SemaphoreSlim per unique key) and
        // removing introduces a separate race.
        private async Task<T> ExecuteWithIdempotencyLock<T>(string? key, Func<Task<T>> body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (string.IsNullOrEmpty(key))
            {
                return await body().ConfigureAwait(false);
            }
            var sem = _idempotencyLocks.GetOrAdd(key!, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        private bool TryReplayIdempotent<T>(string? key, string payload, out T cached)
        {
            cached = default!;
            if (string.IsNullOrEmpty(key)) return false;

            if (_idempotencyCache.TryGetValue(key!, out var entry))
            {
                // P14-013/P14-064 — expired entries: treat as miss + evict.
                if (DateTimeOffset.UtcNow - entry.CreatedAt > IdempotencyTtl)
                {
                    _idempotencyCache.TryRemove(key!, out _);
                    return false;
                }
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

            // P14-013/P14-064 — opportunistic eviction when over cap. Drop
            // expired entries first; if still over cap, drop the single
            // oldest entry by CreatedAt. Single LINQ scan over ~10k entries
            // is fine for the cold path.
            if (_idempotencyCache.Count > IdempotencyCacheMaxEntries)
            {
                var cutoff = DateTimeOffset.UtcNow - IdempotencyTtl;
                foreach (var kv in _idempotencyCache)
                {
                    if (kv.Value.CreatedAt < cutoff)
                    {
                        _idempotencyCache.TryRemove(kv.Key, out _);
                    }
                }
                if (_idempotencyCache.Count > IdempotencyCacheMaxEntries)
                {
                    var oldestKey = _idempotencyCache
                        .OrderBy(kv => kv.Value.CreatedAt)
                        .Select(kv => kv.Key)
                        .FirstOrDefault();
                    if (oldestKey != null)
                    {
                        _idempotencyCache.TryRemove(oldestKey, out _);
                    }
                }
            }
        }

        // P14-042 — single cached JsonSerializerOptions to avoid the default-
        // options allocation per call. Once the no-key path is short-circuited
        // upstream, BuildPayload only runs for explicit idempotency-key
        // writes, but the options cache is essentially free.
        private static readonly JsonSerializerOptions s_payloadJsonOptions = new JsonSerializerOptions();

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
            var json = JsonSerializer.Serialize(canon, s_payloadJsonOptions);
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
            // P14-060 — _odbc is typed LacerteDsnReader? now; direct dispose
            // is cleaner than the as-IDisposable cast that hid the type.
            _odbc?.Dispose();
            // Dispose all idempotency-lock semaphores.
            foreach (var sem in _idempotencyLocks.Values) sem.Dispose();
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
