using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // P16-013 — single source-of-truth for the default AppName so the
        // server fallback can't drift from the CLI default (which is
        // hard-coded to the same literal in Cli/Commands/RegisterCommand.cs
        // and Cli/Commands/LoginCommand.cs). Three-way drift between the
        // server fallback ("Lacerte MCP"), CLI default ("Operator Lacerte
        // MCP"), and the README env-table default ("LacerteMCP") used to
        // leave operators with opaque NotSignedIn failures when the env var
        // was unset; the persisted Intuit consent record is tied to the
        // AppName, so the SDK identity tuple must agree across all three.
        internal const string DefaultAppName = "Operator Lacerte MCP";

        // P16-039 — read-path stateAbbrev filter shape gate. Mirrors the
        // write-side regex in DetailsTools._rxStateAbbrev so the same
        // operator-friendly rejection fires whether the input came in via
        // lacerte_detail_write or via the read tools (lacerte_table_read,
        // lacerte_state_read, lacerte_state_subclient_read). Without this
        // the read path used to silently match nothing on lowercase /
        // trailing-whitespace / full-state-name input and return
        // {count: 0} which is indistinguishable from "no rows match" —
        // a real footgun the LLM cannot self-correct from.
        private static readonly Regex _rxStateAbbr = new Regex(
            @"^[A-Z]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string? _appName;

        // P15-004 — LACERTE_DEFAULT_TAX_YEAR parsed once at construction.
        // Preferred over DateTimeOffset.UtcNow.Year at every fallback site so
        // a Lacerte install pinned to TY24 doesn't silently file 2026 returns
        // once the calendar rolls. Validated >= 2018 + <= year ceiling in
        // the ctor (fail-loud on bad config).
        private readonly int? _defaultTaxYear;

        /// <summary>
        /// P15-004 — exposed for SessionStatus + the SessionTools layer so
        /// `lacerte_set_context` can use it as the fallback when `taxYear`
        /// is omitted. Null when LACERTE_DEFAULT_TAX_YEAR is unset.
        /// </summary>
        public int? DefaultTaxYear => _defaultTaxYear;

        // P15-030 — populated when mode resolution suppressed an operator's
        // preference (both LACERTE_LIVE=1 + LACERTE_SIMULATION=1, OR
        // LACERTE_LIVE=1 off-Windows). Null when the resolution was
        // unambiguous. Surfaced via SessionStatus + the doctor mode probe.
        private readonly string? _modeResolutionReason;

        /// <summary>P15-030 — see <see cref="_modeResolutionReason"/>.</summary>
        public string? ModeResolutionReason => _modeResolutionReason;

        // P15-077 — testable injection point for the current-calendar-year
        // ceiling so unit tests can pin the upper bound without flapping in
        // future calendar years. Production uses DateTime.Today.Year.
        internal Func<int> TodayYearProvider { get; set; } = () => DateTime.Today.Year;

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
        // P15-062 — internal const so tests can pin the cap via the same
        // symbol production uses, no reflection needed.
        internal const int IdempotencyCacheMaxEntries = 10_000;
        internal static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

        // P15-041 — batch-evict roughly 10% of the cap once we cross the
        // line, so the O(n log n) OrderBy sort cost is paid once every
        // ~1024 writes instead of on every write at steady-state-at-cap.
        private const int IdempotencyEvictionBatchSize = 1024;

        // P15-041 — single-flight gate so concurrent writers at the cap
        // don't each independently sort + evict. 0 = idle, 1 = an eviction
        // pass is in progress. Losers of the CompareExchange race simply
        // skip the eviction; if still over cap next time they get a turn.
        private int _evictingIdempotency;

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

            // P15-030 — capture a human-readable reason if the operator's
            // env preferred Live but mode resolution overrode it. Surfaces
            // through SessionStatus and the doctor "Mode resolution" probe
            // so a silent fallback to Sim no longer hides behind canned
            // "ListClients returned seed data".
            if (live && sim)
            {
                _modeResolutionReason =
                    "both LACERTE_LIVE=1 and LACERTE_SIMULATION=1 set — LACERTE_SIMULATION won";
            }
            else if (live && !onWindows)
            {
                _modeResolutionReason =
                    "LACERTE_LIVE=1 requested but not on Windows — fell back to Simulation";
            }
            else
            {
                _modeResolutionReason = null;
            }

            // P15-030 — emit ONE WireLog line at construction for the
            // operator-friendly forensic trail. WireLog.RecordSync is a
            // near-zero-cost passthrough when LACERTE_DEBUG_WIRE is unset.
            if (_modeResolutionReason != null)
            {
                try
                {
                    WireLog.RecordSync<bool>("ModeResolution",
                        new
                        {
                            resolved = Mode.ToString(),
                            liveRequested = live,
                            simRequested = sim,
                            onWindows,
                            reason = _modeResolutionReason,
                        },
                        () => true);
                }
                catch { /* logging is best-effort; never poison construction */ }
            }

            _appName = TryGet(resolved, "LACERTE_APP_NAME");

            // P15-004 — parse LACERTE_DEFAULT_TAX_YEAR once at startup.
            // Fail-loud on bad input (per Boundary Invariant: config errors
            // surface at construction, not at first tool call). Floor 2018
            // matches the SDK floor at TaxYearTooOld. Unset → null →
            // calendar-year fallback at every site that needed UtcNow.Year
            // before. The README + .env.example have advertised this env var
            // since Phase-1 docs; before P15-004 the code never read it.
            var rawYear = TryGet(resolved, "LACERTE_DEFAULT_TAX_YEAR");
            if (!string.IsNullOrEmpty(rawYear))
            {
                if (!int.TryParse(rawYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                    || y < 2018)
                {
                    throw new InvalidOperationException(
                        $"LACERTE_DEFAULT_TAX_YEAR='{rawYear}' invalid — must be an integer >= 2018.");
                }
                _defaultTaxYear = y;
            }

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
        /// <remarks>
        /// P15-042 / P15-070 — a successful Register() (AccessGranted)
        /// invalidates the per-process derived caches (<see cref="_edition"/>
        /// and <see cref="_newApisCache"/>) so that re-authenticating after
        /// the 24h ticket expiry — or after the operator's Lacerte service
        /// hiccups mid-session — naturally refreshes the SessionStatus
        /// edition triple and the TY21+ branch decisions. Without this,
        /// re-Register was a no-op for those caches and the operator's only
        /// path to refresh edition state was process restart.
        /// </remarks>
        public async Task<int> Register(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // F11 — wrap whole Register in WireLog so both sim + live paths
            // produce one log line. Args carry the app name + taxYear only
            // (no secrets — appId is the AppIdStore GUID, redacted-by-policy).
            // P15-004 — taxYear fallback prefers LACERTE_DEFAULT_TAX_YEAR
            // over the calendar year (which would silently file a 2026
            // return on a TY24 install).
            int taxYearArg = Context?.Year ?? _defaultTaxYear ?? DateTimeOffset.UtcNow.Year;
            return await WireLog.Record<int>("Register",
                new { appName = _appName ?? DefaultAppName, taxYear = taxYearArg },
                async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    // available, otherwise fall back to LACERTE_DEFAULT_TAX_YEAR
                    // (P15-004), otherwise the current calendar year as a
                    // last-resort lower bound.
                    int taxYear = Context?.Year ?? _defaultTaxYear ?? DateTimeOffset.UtcNow.Year;
                    var appName = _appName ?? DefaultAppName;
                    // F12 — wrap in AutoReconnect for transient COM retries.
                    int liveStatus = await AutoReconnect.WithRetry<int>(() =>
                        Task.FromResult(_bridge.Register(appName, appId.Value.ToString("D"), taxYear)))
                        .ConfigureAwait(false);
                    if (liveStatus == StatusCodes.AccessGranted)
                    {
                        IsRegistered = true;
                        RegisteredAt = DateTimeOffset.UtcNow;
                        ExpiresAt = RegisteredAt.Value.AddHours(24);
                        InvalidateDerivedCachesOnRegister();
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
                    InvalidateDerivedCachesOnRegister();
                    return StatusCodes.AccessGranted;
                }

                // "NotSignedIn" — leave IsRegistered false.
                IsRegistered = false;
                RegisteredAt = null;
                ExpiresAt = null;
                return StatusCodes.NotSignedIn;
            }).ConfigureAwait(false);
        }

        // P15-042 / P15-070 — clear the per-process probe caches on a
        // successful Register so re-auth refreshes the EditionInfo triple
        // and the (year, module) → useNewApis table. Without this, a probe
        // that failed on a transient COM hiccup or a Lacerte version-bump
        // mid-session would lie forever in SessionStatus.
        //
        // _edition: REQUIRED. P14-061 caches a failed probe as the
        // "Unavailable" sentinel for process lifetime; clearing here gives
        // re-Register meaning beyond auth-ticket refresh.
        //
        // _newApisCache: defensive — the (year, module) → bool answer is a
        // property of the installed Lacerte build, not the auth ticket.
        // Clearing it costs at most ~9 entries (one per module) and protects
        // against a Lacerte upgrade mid-process changing TY21+ support.
        private void InvalidateDerivedCachesOnRegister()
        {
            lock (_editionLock) { _edition = null; }
            _newApisCache.Clear();
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
                BuildVersion: Diagnostics.BuildInfo.Version,
                // P15-030: surface the mode-resolution reason when an operator
                // env preference was overridden (both flags set OR live-off-Windows).
                // Null when resolution was unambiguous — the snapshot stays compact.
                ModeResolutionReason: _modeResolutionReason,
                // P15-004: surface the parsed LACERTE_DEFAULT_TAX_YEAR so the
                // operator can confirm the env var was actually picked up.
                DefaultTaxYear: _defaultTaxYear,
                // P16-066: surface the LACERTE_DEBUG_WIRE state + resolved log
                // path so the 9201/9203 remediation ("inspect logs/...") is
                // actually executable — the operator can call session_status,
                // see enabled=false + the would-be path, set the env var,
                // restart, reproduce, and grep at the exact path.
                DebugWireEnabled: Logging.WireLog.IsEnabled,
                WireLogPath: Logging.WireLog.ResolvedOrWouldBeLogPath());
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

            // P15-077 — symmetric upper-bound. The SDK has no data for a
            // year not yet released; a typo passing year=2098 would
            // otherwise create a phantom in-memory cell in sim or surface
            // as a confusing live error after a wire roundtrip. +1 buffer
            // tolerates early-year planning for next TY (the operator may
            // legitimately set context for TY25 in Dec-2024).
            int ceiling = TodayYearProvider() + 1;
            if (taxYear > ceiling)
            {
                var ctx = new Dictionary<string, object?>
                {
                    ["taxYear"] = taxYear,
                    ["ceiling"] = ceiling,
                };
                throw new LacerteSDKError(SyntheticStatusCodes.TaxYearTooNew, ctx);
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
            string? lastNameFilter = null, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // F11 — wrap with WireLog.Record. Args carry the filter + limit
            // (no PII — last-name filter is a search prefix, not a stored secret).
            return WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ListClients", new { lastNameFilter, limit }, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ctx = RequireContext();
                RequireRegistered();
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
        public Task<IReadOnlyDictionary<string, object?>?> ReadClient(
            string clientNumber, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // F11 — wrap with WireLog.Record.
            return WireLog.Record<IReadOnlyDictionary<string, object?>?>(
                "ReadClient", new { clientNumber }, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ctx = RequireContext();
                RequireRegistered();
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
            int table, string? clientNumber = null, string? stateAbbrev = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // F11 — wrap with WireLog.Record. Forwarders below call
            // ReadTableCore directly so we don't double-log when ReadSubclients
            // etc. fan out through ReadTable.
            return WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadTable", new { table, clientNumber, stateAbbrev },
                () => ReadTableCore(table, clientNumber, stateAbbrev, cancellationToken));
        }

        private Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadTableCore(
            int table, string? clientNumber, string? stateAbbrev,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = RequireContext();
            RequireRegistered();
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

            // P16-039 — trim clientNumber so a trailing-whitespace typo
            // ('1001 ') doesn't silently match zero rows. Free-form ID
            // (no shape regex enforced at this layer), so trim-only.
            if (!string.IsNullOrEmpty(clientNumber))
            {
                var clientTrimmed = clientNumber!.Trim();
                rows = rows.Where(r => string.Equals(r.ClientNumber, clientTrimmed, StringComparison.OrdinalIgnoreCase));
            }
            // P16-039 — trim + shape-validate stateAbbrev so the read path
            // matches the write path (DetailsTools.cs:214-225). Lowercase
            // and trailing whitespace are forgiven (trim + OrdinalIgnoreCase
            // below); full state names like "California" or "Calif" are
            // rejected with a structured error the LLM can self-correct
            // from in one turn, instead of silently returning {count: 0}.
            // Reuses DetailWriteInvalid (closest existing synthetic code;
            // a dedicated ReadFilterInvalid would be cleaner but lives in
            // the Errors cluster).
            if (!string.IsNullOrEmpty(stateAbbrev))
            {
                var trimmed = stateAbbrev!.Trim();
                if (trimmed.Length == 0 || !_rxStateAbbr.IsMatch(trimmed.ToUpperInvariant()))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["field"] = "stateAbbrev",
                            ["value"] = stateAbbrev,
                            ["reason"] = "stateAbbrev must be a 2-letter US/territory postal code (e.g. CA, NY). For a full state name use the postal abbreviation: 'California' -> 'CA'.",
                        });
                }
                rows = rows.Where(r => string.Equals(r.StateAbbrev, trimmed, StringComparison.OrdinalIgnoreCase));
            }

            var result = rows
                .Select(r => (IReadOnlyDictionary<string, object?>)CloneRow(r))
                .ToList();
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result);
        }

        /// <summary>DATA2 subclient rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadSubclients(
            string clientNumber, CancellationToken cancellationToken = default)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadSubclients", new { clientNumber }, () => ReadTableCore(2, clientNumber, null, cancellationToken));

        /// <summary>DATA3 state rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadStates(
            string clientNumber, CancellationToken cancellationToken = default)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadStates", new { clientNumber }, () => ReadTableCore(3, clientNumber, null, cancellationToken));

        /// <summary>DATA5 F4-status rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadStatuses(
            string clientNumber, CancellationToken cancellationToken = default)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadStatuses", new { clientNumber }, () => ReadTableCore(5, clientNumber, null, cancellationToken));

        /// <summary>DATA7 forms rows for a given client.</summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadForms(
            string clientNumber, CancellationToken cancellationToken = default)
            => WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadForms", new { clientNumber }, () => ReadTableCore(7, clientNumber, null, cancellationToken));

        /// <summary>
        /// Per-client detail rows from the simulated <c>[CLIENT-Detail]</c> file.
        /// </summary>
        /// <remarks>
        /// Legacy single-arg overload — unfiltered, kept for back-compat.
        /// Forwards to <see cref="ReadDetail(string, int?, CancellationToken)"/>.
        /// </remarks>
        public Task<IReadOnlyList<DetailRow>> ReadDetail(
            string clientNumber, CancellationToken cancellationToken = default)
            => ReadDetail(clientNumber, seriesFilter: null, cancellationToken);

        /// <summary>
        /// P15-009 — per-client detail rows with optional Series push-down.
        /// When <paramref name="seriesFilter"/> is non-null, in live mode the
        /// session manager will dispatch <see cref="SqlTemplates.DetailBySeries"/>
        /// (which emits an unquoted integer literal — safe by P14-055 numeric-
        /// string invariant). In sim mode the in-memory <c>List&lt;DetailRow&gt;</c>
        /// is filtered by <c>int.Parse(r.Series) == seriesFilter</c>; int.Parse
        /// is safe because <c>DetailsTools.ValidateDetailWrite</c> enforces
        /// <c>^\\d+$</c> on every detail-row write AND the seed fixture uses
        /// numeric Series strings ("11" = W-2, "12" = Interest, "13" =
        /// Dividends, "14" = Dependents, "20" = Income, "30" = State,
        /// "40" = Partners). Closes the dead-push-down gap the prior code
        /// only exercised in <see cref="LacertMCP.Server.Odbc.SqlTemplates"/>
        /// unit tests.
        /// </summary>
        public Task<IReadOnlyList<DetailRow>> ReadDetail(
            string clientNumber,
            int? seriesFilter,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // F11 — wrap with WireLog.Record. Args carry seriesFilter so the
            // operator can grep the log for "Series=11" pulls.
            return WireLog.Record<IReadOnlyList<DetailRow>>(
                "ReadDetail", new { clientNumber, seriesFilter }, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ctx = RequireContext();
                RequireRegistered();
                ThrowIfLiveUnwired();
                var module = RequireSimModule(ctx);
                if (!module.Details.TryGetValue(clientNumber, out var list))
                {
                    return Task.FromResult<IReadOnlyList<DetailRow>>(Array.Empty<DetailRow>());
                }
                IEnumerable<DetailRow> rows = list;
                if (seriesFilter.HasValue)
                {
                    int wanted = seriesFilter.Value;
                    // Safe int.Parse per the numeric-string invariant enforced
                    // by DetailsTools.ValidateDetailWrite (P14-055). A row whose
                    // Series somehow violates the invariant (e.g. seeded by a
                    // test that bypassed the validator) is excluded rather than
                    // throwing — the filter is a "rows that match" semantic.
                    rows = rows.Where(r => int.TryParse(r.Series, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var s) && s == wanted);
                }
                IReadOnlyList<DetailRow> result = rows.ToList();
                return Task.FromResult(result);
            });
        }

        /// <summary>
        /// DATA1 LEFT OUTER JOIN EFilings LEFT OUTER JOIN EFStatuses for the
        /// active module/year. Returns flat row dicts so the shape matches
        /// what an ODBC LEFT-OUTER-JOIN would yield in live mode.
        ///
        /// <para>
        /// P15-056 — the previous sim implementation used a LINQ INNER join
        /// from EFilings, dropping (a) clients with no eFiling and
        /// (b) eFilings with no status. <see cref="SqlTemplates.EfStatusJoin"/>
        /// has been LEFT OUTER from Clients since P14-053; matching the sim
        /// path here closes the parity divergence the moment Phase 9 wires
        /// live reads (otherwise sim returns N rows, live returns N+K).
        /// </para>
        /// </summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadEFStatus(
            CancellationToken cancellationToken = default)
            => ReadEFStatus(sinceUtc: null, cancellationToken);

        /// <summary>
        /// P15-010 — EF status join with optional since-date filter for the
        /// "watch e-file status across the firm" poll workflow
        /// (REQUIREMENTS §1.4). When <paramref name="sinceUtc"/> is non-null
        /// the join excludes rows whose <c>StatusDate</c> is strictly older
        /// than the threshold; client-without-eFiling rows are also dropped
        /// in this branch (they have null StatusDate and the workflow only
        /// wants rows that changed since the threshold). When null the
        /// classic full LEFT OUTER JOIN runs and client-without-eFiling rows
        /// surface with null EFiling/Status columns per P15-056.
        ///
        /// <para>
        /// Live mode dispatches <see cref="SqlTemplates.EfStatusJoinSince"/>
        /// (filtered) or <see cref="SqlTemplates.EfStatusJoin"/> (unfiltered).
        /// </para>
        /// </summary>
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadEFStatus(
            DateTime? sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // F11 — wrap with WireLog.Record. sinceUtc carried in args so the
            // operator's poll cadence is visible in the log.
            return WireLog.Record<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                "ReadEFStatus",
                new { sinceUtc = sinceUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ctx = RequireContext();
                RequireRegistered();
                ThrowIfLiveUnwired();
                var module = RequireSimModule(ctx);

                // LEFT OUTER from Clients so clients without EFilings still
                // appear (with null EFiling/Status columns); LEFT OUTER from
                // EFilings so an EFiling with no EFStatus row also appears
                // (with null Status/StatusDate). Mirrors SqlTemplates.EfStatusJoin.
                var joined = from c in module.Clients.Values
                             from f in module.EFilings
                                 .Where(x => string.Equals(x.ClientId, c.ClientNumber, StringComparison.OrdinalIgnoreCase))
                                 .DefaultIfEmpty()
                             from s in module.EFStatuses
                                 .Where(x => f != null && x.EFilingId == f.Id)
                                 .DefaultIfEmpty()
                             select new
                             {
                                 Row = (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                                 {
                                     ["ClientId"] = c.Fields.TryGetValue("C1_0", out var c10) ? c10 : c.ClientNumber,
                                     ["EFilingId"] = f?.Id,
                                     ["EFId"] = f?.EFId,
                                     ["InstanceId"] = f?.InstanceId,
                                     ["Status"] = s?.Status,
                                     ["StatusDate"] = s?.StatusDate,
                                 },
                                 StatusDate = s?.StatusDate,
                             };
                IEnumerable<IReadOnlyDictionary<string, object?>> result;
                if (sinceUtc.HasValue)
                {
                    var since = sinceUtc.Value.Date;
                    result = joined
                        .Where(x => x.StatusDate != null
                                    && DateTime.TryParse(x.StatusDate, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                        out var dt)
                                    && dt.Date >= since)
                        .Select(x => x.Row);
                }
                else
                {
                    result = joined.Select(x => x.Row);
                }
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.ToList());
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
            // P16-054/P16-080 — canonical C# casing (subClientId, not
            // subClientID) — matches the wire form.
            int? subClientId = null, string? stateAbbrev = null,
            bool dryRun = false, string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = RequireContext();
            RequireRegistered();
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

            // P15-051 — type-whitelist the value BEFORE BuildPayload so the
            // normalized form is what gets fingerprinted (otherwise int 42 vs
            // JsonElement(42) hash differently and silently break replay).
            // Rejects non-scalar shapes (Array / Object JsonElement, arbitrary
            // CLR objects) with FieldWriteInvalid (9015) before any wire
            // marshalling could silently corrupt a numeric DBF cell.
            value = NormalizeFieldWriteValue(value, field);

            // P14-005 — per-key serialization. Concurrent same-key calls wait
            // here so TryReplay → execute → StoreIdempotent runs atomically.
            // Null/empty key: skip locking (no idempotency contract to honor).
            return await ExecuteWithIdempotencyLock<int>(idempotencyKey, async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // P14-042 — only compute the payload fingerprint when there's
                // an idempotency key. JSON serialize + SHA-256 + base64 is
                // wasted work on the common no-key write path.
                string payload = string.IsNullOrEmpty(idempotencyKey)
                    ? string.Empty
                    : BuildPayload("WriteField", table, clientNumber, field, value, subClientId, stateAbbrev);

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
                    new { table, clientNumber, field, value, subClientId, stateAbbrev, dryRun },
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
                                        table, clientNumber, field, value, subClientId, stateAbbrev)))
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
                            inner = ApplySimFieldWriteWithUndo(ctx, table, clientNumber, field, value, subClientId, stateAbbrev, useNew, out undo);
                        }
                        finally
                        {
                            undo?.Invoke();
                        }
                    }
                    else
                    {
                        inner = ApplySimFieldWrite(ctx, table, clientNumber, field, value, subClientId, stateAbbrev, useNew);
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
            bool dryRun = false, string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = RequireContext();
            RequireRegistered();
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
                cancellationToken.ThrowIfCancellationRequested();
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
            CreateClientArgs args, bool dryRun = false, string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                // P14-042 — only compute payload when an idempotency key exists.
                string payload = string.IsNullOrEmpty(idempotencyKey)
                    ? string.Empty
                    : BuildPayload("CreateClient", args);

                if (TryReplayIdempotent(idempotencyKey, payload, out CreateClientResult cached))
                {
                    return cached;
                }

                // Client-side validation per agent 06 §6.
                if (string.IsNullOrEmpty(args.ClientId) || args.ClientId.Length > 8)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.ClientIDTooLong,
                        new Dictionary<string, object?> { ["clientId"] = args.ClientId });
                }
                if (ctx.Year < 2018)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.TaxYearTooOld,
                        new Dictionary<string, object?> { ["taxYear"] = ctx.Year });
                }

                // P15-059 — defense-in-depth canonicalization. The tool layer
                // (CreateClientTools) already canonicalizes operator-supplied
                // aliases ("1040" → "Individual", "1120S" → "SCorporate", ...)
                // before constructing CreateClientArgs, but a direct caller of
                // _session.CreateClient that bypassed the tool would otherwise
                // re-introduce sim/live divergence. The live SDK's
                // IClient(TaxType, ...) ctor rejects form-number variants with
                // TaxModuleMismatch (14); sim accepts them via AcceptsTaxType.
                // Canonicalize here so the value that flows into both
                // ApplySimCreateClient and _bridge.CreateClient is always the
                // spec-mandated ReturnType. When CanonicalizeTaxType returns
                // null (unknown module OR alias not in the module's set), we
                // leave args.TaxType unchanged so the downstream sim
                // TaxModuleMismatch gate fires with the same payload it does
                // today (preserves existing test expectations).
                var canonicalTaxType = Modules.CanonicalizeTaxType(ctx.Module, args.TaxType);
                if (canonicalTaxType != null && !string.Equals(canonicalTaxType, args.TaxType, StringComparison.Ordinal))
                {
                    args = args with { TaxType = canonicalTaxType };
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
                                    args.ClientId, args.TaxType,
                                    args.FirstName, args.LastName, args.EmailId)))
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
            // P15-043 — proactive 24h auth expiry probe. Without this gate
            // a tool call after the +24h ticket window hits the SDK and
            // gets back the raw integer 203 NotSignedIn with no remediation
            // hint. LoginExpired (9007) carries the operator-friendly
            // "Run `lacerte-mcp register` to re-authenticate" string. The check
            // is free (one DateTimeOffset comparison) and turns the
            // cliff-edge 24h failure into a clean structured error.
            ThrowIfAuthExpired();
            return ctx;
        }

        // P15-050 — manager-side IsRegistered gate. CLAUDE.md "Simulation
        // and live must be observationally identical for the happy path"
        // requires this: without it, sim accepts WriteField/ReadClient/etc
        // before any Register() call (the SDK would reject with NotSignedIn
        // 203). Sim's IsRegistered latch is set by Register() AccessGranted
        // (lines 244, 228), so a fresh manager that never called Register
        // surfaces the same NotSignedIn the SDK would emit.
        //
        // Call this AFTER RequireContext at every read/write chokepoint
        // (except SessionStatus — which is the operator's escape hatch
        // and must NOT gate on registration).
        private void RequireRegistered()
        {
            if (!IsRegistered)
            {
                throw new LacerteSDKError(StatusCodes.NotSignedIn,
                    new Dictionary<string, object?>
                    {
                        ["reason"] = "Register() must succeed (AccessGranted) before this operation. " +
                                     "In live mode the SDK would reject with NotSignedIn 203; the sim path " +
                                     "matches that behavior so flows are observationally identical (CLAUDE.md " +
                                     "two-mode discipline).",
                    });
            }
        }

        // P15-043 — synchronous probe. Throws LoginExpired (9007) when
        // ExpiresAt has lapsed; never fires before Register() (ExpiresAt
        // is null until AccessGranted, and those pre-register failures
        // surface via RequireContext / RequireRegistered separately).
        // Side-effect: flips IsRegistered to false so SessionStatus is
        // honest about the now-expired state.
        private void ThrowIfAuthExpired()
        {
            if (ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value)
            {
                IsRegistered = false;
                throw new LacerteSDKError(SyntheticStatusCodes.LoginExpired,
                    new Dictionary<string, object?>
                    {
                        ["expiredAt"] = ExpiresAt.Value.ToString("O"),
                        ["minutesPastExpiry"] = (DateTimeOffset.UtcNow - ExpiresAt.Value).TotalMinutes,
                    });
            }
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
                args.ClientId, args.TaxType,
                args.FirstName, args.LastName, args.EmailId);

            // Capture pre-state for undo BEFORE calling SimulateCreateClient so
            // a successful create can be rolled back atomically for dry-run.
            var module = registerUndo ? RequireSimModule(ctx) : null;
            var priorNext = module?.NextClientNumber ?? 0;

            var (status, clientNumber) = _sim!.SimulateCreateClient(simArgs);
            if (status != StatusCodes.Success)
            {
                throw new LacerteSDKError(status, new Dictionary<string, object?>
                {
                    ["clientId"] = args.ClientId,
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
        // Per-call deep clone of only the active module — cheaper than
        // SimulationStore.Snapshot/Restore (which clones every module) and
        // sufficient because dry-run writes only touch the active context's
        // module. See P14-043/P14-070 for the prior whole-module snapshot
        // removal.
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
        // contract).
        //
        // P15-012 / P15-055 — lock entries are opportunistically evicted
        // alongside cache entries by EvictIdempotencyEntry, so the lock map
        // is bounded by IdempotencyCacheMaxEntries (10k) — same invariant
        // as the cache. A concurrent waiter may briefly hold a since-evicted
        // semaphore — worst case is one extra execution of the body
        // (observationally equivalent to a cache miss); the atomic-pair
        // TryRemove in EvictIdempotencyEntry keeps a new GetOrAdd from
        // having its semaphore destroyed.
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

        // P15-012 / P15-055 — atomic remove + dispose of the lock entry
        // alongside the cache entry, so the lock map stays a strict subset
        // of the cache map. Uses the ConcurrentDictionary KeyValuePair
        // overload of TryRemove (atomic compare-and-remove) so a concurrent
        // GetOrAdd that swapped in a new semaphore between TryGetValue and
        // TryRemove does NOT get its semaphore destroyed.
        //
        // Skip dispose if CurrentCount != 1 (held or waiters present): the
        // next eviction cycle will catch it once the holder releases. This
        // bounds the worst-case lock-map size at IdempotencyCacheMaxEntries +
        // (currently held entries) — practically the same as the cache cap.
        private void EvictIdempotencyEntry(string key)
        {
            _idempotencyCache.TryRemove(key, out _);
            if (_idempotencyLocks.TryGetValue(key, out var sem) && sem.CurrentCount == 1)
            {
                var pair = new KeyValuePair<string, SemaphoreSlim>(key, sem);
                // ConcurrentDictionary's KeyValuePair overload is atomic:
                // removes only if the current value still equals the snapshot.
                if (((ICollection<KeyValuePair<string, SemaphoreSlim>>)_idempotencyLocks).Remove(pair))
                {
                    sem.Dispose();
                }
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
                    // P15-016 — log the expiry so an operator can attribute
                    // a silent re-execute to TTL expiry rather than first
                    // time. PII-safe: the key is operator-supplied.
                    try
                    {
                        var ageMs = (DateTimeOffset.UtcNow - entry.CreatedAt).TotalMilliseconds;
                        WireLog.RecordSync<bool>("Idempotency.expired",
                            new { key, ageMs },
                            () => true);
                    }
                    catch { /* logging is best-effort */ }
                    EvictIdempotencyEntry(key!);
                    return false;
                }
                if (entry.PayloadFingerprint == payload)
                {
                    if (entry.Result is T match)
                    {
                        // P15-016 — log the cache hit so the operator can
                        // tell a successful tool call apart from one that
                        // actually hit the wire. Without this, a cached
                        // replay produces NO log line at all (the outer
                        // WireLog.Record wrapper sits BELOW the cache check),
                        // which is the opposite of what an audit log needs.
                        try
                        {
                            var ageMs = (DateTimeOffset.UtcNow - entry.CreatedAt).TotalMilliseconds;
                            WireLog.RecordSync<bool>("Idempotency.hit",
                                new { key, ageMs },
                                () => true);
                        }
                        catch { /* logging is best-effort */ }
                        cached = match;
                        return true;
                    }
                    // P15-074 — fingerprint MATCHES but cached return type
                    // does not (e.g., key reused across operations where the
                    // first cached a CreateClientResult and the second
                    // requests int). The legacy throw used the "different
                    // payload fingerprint" reason, which was factually wrong
                    // for this branch and sent diagnosticians chasing a
                    // canonicalization bug that wasn't there. Split into a
                    // dedicated branch with the correct reason + the
                    // cached + requested type names in the context dict.
                    var typeData = new Dictionary<string, object?>
                    {
                        ["idempotencyKey"] = key,
                        ["reason"] = "Idempotency key reused across operations with different return types.",
                        ["cachedType"] = entry.Result?.GetType().FullName ?? "null",
                        ["requestedType"] = typeof(T).FullName,
                        ["existingEntryCreatedAt"] = entry.CreatedAt.ToString("O"),
                        ["existingEntryAgeMs"] = (DateTimeOffset.UtcNow - entry.CreatedAt).TotalMilliseconds,
                    };
                    throw new LacerteSDKError(SyntheticStatusCodes.IdempotencyConflict, typeData);
                }
                // P15-016 — attach the existing entry's CreatedAt to the
                // 9102 context dict so the operator sees BOTH timestamps
                // when triaging a key collision; P15-074 — fingerprint-
                // mismatch branch carries cachedFingerprintPrefix +
                // requestedFingerprintPrefix (first 8 chars only, full
                // base64 is noise).
                var cachedFp = entry.PayloadFingerprint ?? string.Empty;
                var requestedFp = payload ?? string.Empty;
                var data = new Dictionary<string, object?>
                {
                    ["idempotencyKey"] = key,
                    ["reason"] = "Idempotency key reused with a different payload fingerprint.",
                    ["existingEntryCreatedAt"] = entry.CreatedAt.ToString("O"),
                    ["existingEntryAgeMs"] = (DateTimeOffset.UtcNow - entry.CreatedAt).TotalMilliseconds,
                    ["cachedFingerprintPrefix"] = cachedFp.Substring(0, Math.Min(8, cachedFp.Length)),
                    ["requestedFingerprintPrefix"] = requestedFp.Substring(0, Math.Min(8, requestedFp.Length)),
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
            // expired entries first; if still over cap, batch-drop the
            // oldest ~10% so the OrderBy sort cost is paid once every
            // IdempotencyEvictionBatchSize writes instead of every write
            // at steady-state-at-cap.
            //
            // P15-041 — gate the eviction work behind a single-flight
            // Interlocked.CompareExchange so concurrent writers crossing
            // the cap don't each independently sort the whole dictionary.
            // Losers just skip eviction; if still over cap on the next
            // write they get another turn.
            //
            // P15-012/P15-055 — every cache removal here goes through
            // EvictIdempotencyEntry, which also disposes the matching
            // _idempotencyLocks entry. Keeps the lock map bounded by
            // the same cap.
            if (_idempotencyCache.Count > IdempotencyCacheMaxEntries
                && Interlocked.CompareExchange(ref _evictingIdempotency, 1, 0) == 0)
            {
                try
                {
                    var cutoff = DateTimeOffset.UtcNow - IdempotencyTtl;
                    int expiredDropped = 0;
                    foreach (var kv in _idempotencyCache)
                    {
                        if (kv.Value.CreatedAt < cutoff)
                        {
                            EvictIdempotencyEntry(kv.Key);
                            expiredDropped++;
                        }
                    }
                    string? oldestDroppedKey = null;
                    string? oldestDroppedCreatedAt = null;
                    if (_idempotencyCache.Count > IdempotencyCacheMaxEntries)
                    {
                        var toDrop = _idempotencyCache
                            .OrderBy(kv => kv.Value.CreatedAt)
                            .Take(IdempotencyEvictionBatchSize)
                            .Select(kv => new { kv.Key, kv.Value.CreatedAt })
                            .ToArray();
                        if (toDrop.Length > 0)
                        {
                            oldestDroppedKey = toDrop[0].Key;
                            oldestDroppedCreatedAt = toDrop[0].CreatedAt.ToString("O");
                            foreach (var item in toDrop)
                            {
                                EvictIdempotencyEntry(item.Key);
                            }
                        }
                    }
                    // P15-016 — emit one summary line per eviction pass so
                    // an evicted-then-replayed key that silently becomes a
                    // double-write is post-hoc traceable.
                    try
                    {
                        WireLog.RecordSync<bool>("Idempotency.evicted",
                            new
                            {
                                cacheSize = _idempotencyCache.Count,
                                expiredDropped,
                                oldestDroppedKey,
                                oldestDroppedCreatedAt,
                            },
                            () => true);
                    }
                    catch { /* logging is best-effort */ }
                }
                finally
                {
                    Volatile.Write(ref _evictingIdempotency, 0);
                }
            }
        }

        // P15-050 / P15-043 — internal test seam. Some bridge-fake tests
        // (live-mode + a recording / fake ILacerteBridge) bypass the real
        // Register() path because it would call Bridge.Registration.
        // GetStoredAppId() and throw AppNotRegistered on a CI box with no
        // credential store entry. The seam pretends Register() AccessGranted
        // happened so the new RequireRegistered + ThrowIfAuthExpired gates
        // pass without round-tripping the COM call. Also lets the P15-043
        // expiry test pin ExpiresAt in the past without re-running Register.
        internal void SetRegisteredForTest(bool value)
        {
            IsRegistered = value;
            RegisteredAt = value ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            ExpiresAt = value ? DateTimeOffset.UtcNow.AddHours(24) : (DateTimeOffset?)null;
        }
        internal void SetExpiresAtForTest(DateTimeOffset? expiresAt) => ExpiresAt = expiresAt;

        // P15-062 — internal test seam. Lets tests pin the eviction +
        // expiry behavior via a real entry without round-tripping every
        // write through the chokepoint (which would make the cap test
        // O(10k+) actual write operations). Production never calls this.
        internal int IdempotencyCacheCount => _idempotencyCache.Count;
        internal int IdempotencyLockCount => _idempotencyLocks.Count;
        internal bool TryGetIdempotencyEntryForTest(string key, out IdempotencyCacheEntry entry)
            => _idempotencyCache.TryGetValue(key, out entry!);
        internal void ForceExpireIdempotencyEntryForTest(string key)
        {
            if (_idempotencyCache.TryGetValue(key, out var e))
            {
                _idempotencyCache[key] = new IdempotencyCacheEntry(
                    e.PayloadFingerprint, e.Result,
                    DateTimeOffset.UtcNow - IdempotencyTtl - TimeSpan.FromSeconds(1));
            }
        }
        internal void SeedIdempotencyEntryForTest(string key, DateTimeOffset createdAt)
        {
            _idempotencyCache[key] = new IdempotencyCacheEntry(
                PayloadFingerprint: "test", Result: 0, CreatedAt: createdAt);
        }

        // P15-051 — type-whitelist for FieldWriteTools.value. The bridge
        // layer's BridgeInputValidation.ValidateValue only branches on
        // string-typed values (with a self-aware comment "type whitelisting
        // is the session-manager's job"); a JsonElement(Array) or arbitrary
        // CLR object reaches the wire unchecked and either marshals as a
        // junk DBF value (silent corruption) or surfaces as a generic COM
        // HRESULT (loses the precise reason). The session-level normalizer
        // runs BEFORE BuildPayload so int 42 and JsonElement(42) produce
        // the SAME fingerprint — idempotency replay stays stable across
        // JSON-binding pathways. Throws FieldWriteInvalid (9015) for any
        // non-scalar shape with {field, clrType, reason} context.
        //
        // CONTRACT (audit-finding P15-051 refined recommendation):
        // - null → passes through as null (writable fields may legitimately
        //   clear a value).
        // - JsonElement → only Null/String/True/False/Number kinds accepted;
        //   Number coerced to long > decimal > double in that priority so
        //   the in-sim CLR type closely matches what a JSON-bound caller
        //   would observe at the tool surface.
        // - CLR types: string, bool, integer family (sbyte..ulong), float,
        //   double, decimal, DateTime, DateTimeOffset are whitelisted.
        // - Everything else → FieldWriteInvalid with clrType.GetType().FullName.
        internal static object? NormalizeFieldWriteValue(object? value, string field)
        {
            if (value is null) return null;
            if (value is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.Null:
                        return null;
                    case JsonValueKind.String:
                        return je.GetString();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Number:
                        if (je.TryGetInt64(out var l)) return l;
                        if (je.TryGetDecimal(out var d)) return d;
                        return je.GetDouble();
                    default:
                        throw new LacerteSDKError(
                            SyntheticStatusCodes.FieldWriteInvalid,
                            new Dictionary<string, object?>
                            {
                                ["field"] = field,
                                ["clrType"] = $"JsonElement({je.ValueKind})",
                                ["reason"] = "value must be a JSON scalar (string/number/bool/null); arrays and objects are not accepted.",
                            });
                }
            }
            switch (value)
            {
                case string:
                case bool:
                case sbyte:
                case byte:
                case short:
                case ushort:
                case int:
                case uint:
                case long:
                case ulong:
                case float:
                case double:
                case decimal:
                case DateTime:
                case DateTimeOffset:
                    return value;
                default:
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.FieldWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["field"] = field,
                            ["clrType"] = value.GetType().FullName ?? "<unknown>",
                            ["reason"] = "value must be a JSON-serializable scalar (string, number, bool, DateTime, or null).",
                        });
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
    /// SDK's <c>IClient(TaxType, ClientId, EmailId, FirstName, LastName)</c>
    /// constructor signature.
    /// <para>
    /// P16-054/P16-080 — record fields renamed from ClientID/EmailID to
    /// ClientId/EmailId so the C# casing matches the canonical wire form
    /// (camelCase clientId/emailId) one-to-one. No SQL/DBF column mapping
    /// crosses this seam — purely an in-process record.
    /// </para>
    /// </summary>
    public sealed record CreateClientArgs(
        string ClientId,
        string TaxType,
        string? FirstName = null,
        string? LastName = null,
        string? EmailId = null);

    /// <summary>
    /// Result of <see cref="LacerteSessionManager.CreateClient"/>. <c>Status</c>
    /// is a TAPIStatus value (1 = Success). <c>ClientNumber</c> is the
    /// sim-assigned monotonic identifier on success; null on failure.
    /// </summary>
    public sealed record CreateClientResult(int Status, string? ClientNumber);
}
