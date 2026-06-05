using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;

namespace LacertMCP.Server.Session
{
    // -----------------------------------------------------------------------------
    // Type aliases (conceptual — kept as plain types in C#):
    //   DataPath     => string,   e.g. "C:\\Lacerte\\24Tax\\IData"
    //   TaxYear      => int,      2018..2025
    //   ModuleChar   => char,     'I','C','P','S','F','R','T','N','B'
    //   ClientNumber => string,   sim-assigned monotonic
    //   ClientID     => string,   operator-chosen, <=8 chars
    //   FieldName    => string,   e.g. "C1_0", "C2_15", "C4_G3"
    //
    // Sim-canonical datapath form: "C:\\Lacerte\\{YY}Tax\\{Module}Data"
    // (e.g. TY24 Individual => "C:\\Lacerte\\24Tax\\IData"). Used by Seed() so
    // tests have stable strings to assert against.
    // -----------------------------------------------------------------------------

    /// <summary>
    /// One row in DATA1..DATA7. Column set is wide and module-specific, so the
    /// field bag is a dictionary keyed by C{table}_{n} strings. The shortcut
    /// properties materialize the conventional keys ([CLIENT-Client Info]'s
    /// ClientNumber, DATA2/4's SubClientID, DATA3/4's StateAbbrev) so handlers
    /// don't need to know the per-table 0/1/2 positions.
    /// </summary>
    public sealed record DbfRow(
        string ClientNumber,
        int? SubClientID = null,
        string? StateAbbrev = null)
    {
        /// <summary>
        /// Wide field bag — keys like "C1_0", "C2_15", "C4_G3".
        /// <para>
        /// P18-001: ConcurrentDictionary (not plain Dictionary) so that LLM-driven
        /// tool batching can write distinct fields on the same client concurrently
        /// without corrupting the bucket array. The per-key idempotency semaphore
        /// (P14-005) only serializes same-key calls; distinct-key concurrent writes
        /// were previously unguarded. Sibling rationale of P14-028's per-list lock
        /// for DetailRow lists — do NOT "simplify" this back to Dictionary. The
        /// dictionary-initializer indexer syntax used by Seed + SimulateCreateClient
        /// works against ConcurrentDictionary's set indexer (upsert semantics).
        /// </para>
        /// </summary>
        public ConcurrentDictionary<string, object?> Fields { get; init; } = new ConcurrentDictionary<string, object?>();
    }

    /// <summary>
    /// One row in a per-client detail file (`{CLIENT}.{Mod}{YY}`). The
    /// underlying file format is row-ordered with no primary key, so reads
    /// filter in-memory and writes append (or replace by index).
    /// </summary>
    public sealed record DetailRow(
        string Series,
        string Prefix,
        string Code,
        string Suffix,
        string StateAbbr,
        string CityAbbr,
        string Source,
        string TSJ,            // "T" | "S" | "J" | ""
        string ImportSrc,
        double? Amount,
        string Description);

    /// <summary>An e-filing record — TY19+ only.</summary>
    public sealed record EFiling(
        int Id,
        string ClientId,       // ClientNumber
        string EFId,
        string InstanceId);

    /// <summary>An e-filing status record — TY19+ only.</summary>
    public sealed record EFStatus(
        int EFilingId,
        string Status,         // e.g. "Transmitted", "Accepted", "Rejected"
        string StatusDate);    // ISO date string

    /// <summary>
    /// All the DBF tables + detail files + EF tables for one
    /// <c>(datapath, year, module)</c> cell. Maps are
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> so concurrent tools are safe.
    /// </summary>
    public sealed class ModuleStore
    {
        // DATA1 — Client Info, keyed by ClientNumber.
        public ConcurrentDictionary<string, DbfRow> Clients { get; } =
            new ConcurrentDictionary<string, DbfRow>(StringComparer.OrdinalIgnoreCase);

        // DATA2 — SubClient Info, keyed by (ClientNumber, SubClientID).
        public ConcurrentDictionary<(string Client, int SubId), DbfRow> SubClients { get; } =
            new ConcurrentDictionary<(string, int), DbfRow>();

        // DATA3 — State Info, keyed by (ClientNumber, StateAbbrev).
        public ConcurrentDictionary<(string Client, string State), DbfRow> States { get; } =
            new ConcurrentDictionary<(string, string), DbfRow>();

        // DATA4 — State SubClient Info, keyed by (ClientNumber, SubClientID, StateAbbrev).
        public ConcurrentDictionary<(string Client, int SubId, string State), DbfRow> StateSubs { get; } =
            new ConcurrentDictionary<(string, int, string), DbfRow>();

        // DATA5 — F4 Status, keyed by ClientNumber.
        public ConcurrentDictionary<string, DbfRow> Statuses { get; } =
            new ConcurrentDictionary<string, DbfRow>(StringComparer.OrdinalIgnoreCase);

        // DATA6 — Display / read-only, keyed by ClientNumber.
        public ConcurrentDictionary<string, DbfRow> Displays { get; } =
            new ConcurrentDictionary<string, DbfRow>(StringComparer.OrdinalIgnoreCase);

        // DATA7 — Forms (many rows per client).
        public ConcurrentDictionary<string, List<DbfRow>> Forms { get; } =
            new ConcurrentDictionary<string, List<DbfRow>>(StringComparer.OrdinalIgnoreCase);

        // Per-client detail rows.
        public ConcurrentDictionary<string, List<DetailRow>> Details { get; } =
            new ConcurrentDictionary<string, List<DetailRow>>(StringComparer.OrdinalIgnoreCase);

        // EF tables (TY19+ only — kept empty pre-TY19).
        public List<EFiling> EFilings { get; } = new List<EFiling>();
        public List<EFStatus> EFStatuses { get; } = new List<EFStatus>();

        // Sim-only monotonic counters.
        public int NextClientNumber = 1001;
        public int NextEFilingId = 1;
    }

    /// <summary>
    /// In-memory replacement for the Lacerte SDK used in dev / tests. Mirrors
    /// the live path resolver <c>(datapath, year, module) → DBF set</c> exactly,
    /// so the same triple can be handed to either backend.
    ///
    /// Ported from <c>docs/research/agents/06-simulation-mode.md</c>.
    ///
    /// THIS SLICE covers Phase 2 tasks #16, #17, and the auth toggle portion of
    /// #22. The chokepoint methods (#18-23) — ProcessQuery, SimulateFieldWrite,
    /// SimulateCreateClient, ReadDetail, WriteDetail, Snapshot, Restore — are
    /// stubbed with NotImplementedException and land in a later session.
    /// </summary>
    public sealed partial class SimulationStore
    {
        // Root: datapath -> year -> module -> ModuleStore. All three levels
        // ConcurrentDictionary so tools running in parallel don't race on
        // first-touch of a new cell.
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ConcurrentDictionary<char, ModuleStore>>> _root =
            new ConcurrentDictionary<string, ConcurrentDictionary<int, ConcurrentDictionary<char, ModuleStore>>>(StringComparer.OrdinalIgnoreCase);

        // Latch flipped by LACERTE_SIM_EXPIRED=1 (read once at construction)
        // OR by the ToggleExpired(bool) test helper. Stored as int to use
        // Interlocked.* for cross-thread visibility.
        private int _sessionExpired;

        // Whether Register() has been called this process.
        private int _registered;

        // P18-022 — per-(year, module, table) lock latch. Used by the ToggleLock
        // test helper to simulate the live-mode case where the operator has the
        // same return open in Lacerte's UI, holding the DBF file with read-share/
        // write-deny per FoxPro/xBase semantics. The live SDK surfaces this as
        // TAPIStatus 2 DBFNotAccessable on WriteToDBFDatabase / UpdateTaxReturn.
        // Closed by a HashSet behind a gate object — low contention (flipped only
        // from tests, read once per SimulateFieldWrite / SimulateCreateClient /
        // WriteDetail).
        private readonly object _lockedTablesGate = new object();
        private readonly HashSet<(int year, char module, int table)> _lockedTables
            = new HashSet<(int year, char module, int table)>();

        /// <summary>
        /// Default ctor: reads the <c>LACERTE_SIM_EXPIRED</c> env var, then
        /// calls <see cref="Seed"/> to populate the 8-client fixture.
        /// </summary>
        public SimulationStore()
            : this(Environment.GetEnvironmentVariable("LACERTE_SIM_EXPIRED") == "1")
        {
        }

        /// <summary>
        /// Test ctor: explicit <paramref name="sessionExpired"/> flag bypasses the env var.
        /// </summary>
        public SimulationStore(bool sessionExpired)
        {
            _sessionExpired = sessionExpired ? 1 : 0;
            Seed();
        }

        // -------------------------------------------------------------------
        // Auth toggle (Phase 2 task #22, partial — the LacerteSessionManager
        // integration arrives later).
        // -------------------------------------------------------------------

        /// <summary>
        /// Sim equivalent of <c>ITPRegistration.Register</c>: flips the
        /// registered latch and returns "AccessGranted" — unless the session
        /// is in the simulated-expired state, in which case "NotSignedIn".
        /// Returns a string for now; Stream 2 (Errors) will introduce a
        /// TAPIStatus enum that this method will switch to.
        /// </summary>
        public string Register()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _sessionExpired, 0, 0) == 1)
                return "NotSignedIn";
            System.Threading.Interlocked.Exchange(ref _registered, 1);
            return "AccessGranted";
        }

        /// <summary>
        /// Sim equivalent of an SDK session-status probe. Returns
        /// "AccessGranted" iff <see cref="Register"/> has succeeded and the
        /// session has not been toggled expired; otherwise "NotSignedIn".
        /// </summary>
        public string SessionStatus()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _sessionExpired, 0, 0) == 1)
                return "NotSignedIn";
            if (System.Threading.Interlocked.CompareExchange(ref _registered, 0, 0) == 0)
                return "NotSignedIn";
            return "AccessGranted";
        }

        /// <summary>
        /// Test helper: flip the simulated-expired latch on or off. Mirrors
        /// the runtime effect of setting <c>LACERTE_SIM_EXPIRED=1</c>. Not
        /// part of the public tool surface.
        /// </summary>
        public void ToggleExpired(bool expired)
        {
            System.Threading.Interlocked.Exchange(ref _sessionExpired, expired ? 1 : 0);
        }

        /// <summary>
        /// P18-022 — Test helper: flip the simulated DBF-lock latch for a
        /// <c>(year, module, table)</c> triple. Mirrors the live-mode case
        /// where the operator has the same return open in Lacerte's UI; the
        /// underlying DATA{table}{module}{yy}.DBF is held with read-share /
        /// write-deny (FoxPro / xBase semantics), so the live SDK returns
        /// TAPIStatus 2 <see cref="StatusCodes.DBFNotAccessable"/> on
        /// WriteToDBFDatabase / UpdateTaxReturn. When locked, the sim's
        /// SimulateFieldWrite / WriteDetail / SimulateCreateClient return
        /// the same status code so the agent's recovery path (close in UI,
        /// retry) is rehearsable off-Windows. Not part of the public tool
        /// surface — mirrors <see cref="ToggleExpired"/>.
        /// </summary>
        public void ToggleLock(int year, char module, int table, bool locked)
        {
            lock (_lockedTablesGate)
            {
                if (locked) _lockedTables.Add((year, module, table));
                else _lockedTables.Remove((year, module, table));
            }
        }

        // P18-022 — Returns true if the given (year, module, table) was flipped
        // locked by ToggleLock. Called before LookupRow in SimulateFieldWrite,
        // before the WriteDetail mutation, and against table 1 in
        // SimulateCreateClient (which always writes DATA1 implicitly).
        private bool IsTableLocked(int year, char module, int table)
        {
            lock (_lockedTablesGate) return _lockedTables.Contains((year, module, table));
        }

        // -------------------------------------------------------------------
        // Cell access — used by Seed() and (later) the chokepoint methods.
        // -------------------------------------------------------------------

        /// <summary>
        /// Get-or-create the <see cref="ModuleStore"/> for a given cell. The
        /// triple-nested <see cref="ConcurrentDictionary{TKey,TValue}"/> means
        /// concurrent first-touches race safely.
        /// </summary>
        internal ModuleStore GetOrAddModule(string dataPath, int year, char module)
        {
            var byYear = _root.GetOrAdd(
                dataPath,
                _ => new ConcurrentDictionary<int, ConcurrentDictionary<char, ModuleStore>>());
            var byMod = byYear.GetOrAdd(
                year,
                _ => new ConcurrentDictionary<char, ModuleStore>());
            return byMod.GetOrAdd(module, _ => new ModuleStore());
        }

        /// <summary>
        /// Get the <see cref="ModuleStore"/> for a given cell, or <c>null</c>
        /// if the cell was never seeded / touched. Used by tests.
        /// </summary>
        public ModuleStore? TryGetModule(string dataPath, int year, char module)
        {
            if (!_root.TryGetValue(dataPath, out var byYear)) return null;
            if (!byYear.TryGetValue(year, out var byMod)) return null;
            return byMod.TryGetValue(module, out var store) ? store : null;
        }

        // -------------------------------------------------------------------
        // Chokepoint methods (Phase 2 tasks #18-23). ProcessQuery lives in
        // SimulationStore.Query.cs to keep this file readable.
        // -------------------------------------------------------------------

        // Mapping from table number → DBnError TAPIStatus per agent 06 §4.
        // Used for the "row lookup failed" branch in SimulateFieldWrite.
        private static readonly int[] _dbErrorByTable =
        {
            StatusCodes.Error,        // index 0 — unused
            StatusCodes.DB1Error,     // 1
            StatusCodes.DB2Error,     // 2
            StatusCodes.DB3Error,     // 3
            StatusCodes.DB4Error,     // 4
            StatusCodes.DB5Error,     // 5
            StatusCodes.DB6Error,     // 6
            StatusCodes.DB7Error,     // 7
        };

        // Per-module accepted TaxType strings for SimulateCreateClient now
        // live on Schema.Modules.TaxTypeAliases (single source of truth —
        // formerly duplicated between this file and CreateClientTools).
        // Callers below go through Modules.AcceptsTaxType which preserves
        // the case-insensitive accept semantics.

        // ClientID validation per spec: <=8 alphanumeric, no special chars.
        private static readonly Regex _rxClientId = new Regex(
            @"^[A-Z0-9]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Sim implementation of pre-TY21 <c>WriteToDBFDatabase</c> + TY21+
        /// <c>UpdateTaxReturn</c>. Returns an integer TAPIStatus from
        /// <see cref="StatusCodes"/>; observationally indistinguishable from
        /// what the live bridge would emit. Per agent 06 §4, check order is:
        /// sessionExpired → year guard → table-specific required arg →
        /// access-map → row lookup → length check → set value.
        /// </summary>
        public int SimulateFieldWrite(
            char module,
            int table,
            string clientNumber,
            string field,
            object? value,
            int year,
            string dataPath,
            int? subClientID = null,
            string? stateAbbrev = null)
        {
            // 1. sessionExpired latch
            if (System.Threading.Interlocked.CompareExchange(ref _sessionExpired, 0, 0) == 1)
                return StatusCodes.NotSignedIn;

            // 1b. spec §2: DATA6 (Display) / DATA7 (Forms) are read-only at
            // the table level — return ExposedForRead directly so the wire
            // path stays observationally identical with the session-manager
            // chokepoint gate (P14-054). Throw-via-LacerteSDKError lives at
            // the session manager so this stays a pure status return here.
            if (table == 6 || table == 7) return StatusCodes.ExposedForRead;

            // 2. year guard (spec floor is TY18)
            if (year < 2018) return StatusCodes.InValidTaxYear;

            // 3. table-specific required args
            if (table == 2 && (!subClientID.HasValue || subClientID.Value <= 0))
                return StatusCodes.InValidSubClientID;
            if ((table == 3 || table == 4) && string.IsNullOrEmpty(stateAbbrev))
                return StatusCodes.InValidStateAbbrev;
            if (table == 4 && (!subClientID.HasValue || subClientID.Value <= 0))
                return StatusCodes.InValidSubClientID;

            // 3b. P18-022 — lock check. Operator has the return open in Lacerte's
            // UI, so the DBF is held with read-share/write-deny per FoxPro/xBase
            // semantics; the live SDK surfaces this as TAPIStatus 2 DBFNotAccessable
            // on WriteToDBFDatabase / UpdateTaxReturn. Fires before the access map
            // because a locked file means the SDK cannot even read row metadata,
            // matching the live ordering. Flipped from tests via ToggleLock.
            if (IsTableLocked(year, module, table)) return StatusCodes.DBFNotAccessable;

            // 4. access map
            var access = FieldAccessMapLoader.GetFieldAccess(module, table, field, year);
            switch (access)
            {
                case FieldAccess.NotExposed:
                    return StatusCodes.NotExposed;
                case FieldAccess.Hidden:
                    return StatusCodes.FieldNotSupported;
                case FieldAccess.ReadOnly:
                    return StatusCodes.ExposedForRead;
                case FieldAccess.NotSupported:
                    return StatusCodes.FieldNotSupported;
                case FieldAccess.Unknown:
                    // Per agent 06 §4: Unknown → FieldNotSupported in the
                    // sim path (the session manager's AssertWritable handles
                    // the synthetic 9001 with force-override flow earlier).
                    return StatusCodes.FieldNotSupported;
                case FieldAccess.ReadWrite:
                    break;
            }

            // 5. row lookup
            var store = TryGetModule(dataPath, year, module);
            if (store == null) return ErrorForTable(table);

            DbfRow? row = LookupRow(store, table, clientNumber, subClientID, stateAbbrev);
            if (row == null) return ErrorForTable(table);

            // 6. length check (only meaningful for string-valued writes)
            if (value is string s && ExceedsMax(field, s)) return StatusCodes.LengthExceeded;

            // 7. set value
            row.Fields[field] = value;
            return StatusCodes.Success;
        }

        private static int ErrorForTable(int table)
        {
            if (table >= 1 && table <= 7) return _dbErrorByTable[table];
            return StatusCodes.Error;
        }

        private static DbfRow? LookupRow(
            ModuleStore store, int table, string clientNumber, int? subId, string? state)
        {
            switch (table)
            {
                case 1:
                    return store.Clients.TryGetValue(clientNumber, out var r1) ? r1 : null;
                case 2:
                    return store.SubClients.TryGetValue((clientNumber, subId ?? 0), out var r2) ? r2 : null;
                case 3:
                    return store.States.TryGetValue((clientNumber, state ?? ""), out var r3) ? r3 : null;
                case 4:
                    return store.StateSubs.TryGetValue((clientNumber, subId ?? 0, state ?? ""), out var r4) ? r4 : null;
                case 5:
                    return store.Statuses.TryGetValue(clientNumber, out var r5) ? r5 : null;
                case 6:
                    return store.Displays.TryGetValue(clientNumber, out var r6) ? r6 : null;
                case 7:
                    // DATA7 is many-rows-per-client; field writes target the
                    // first row in the sim (live path is keyed by row index
                    // pre-TY21, ClientNumber TY21+ — but field writes to
                    // DATA7 are rare; this matches the doc's no-op example).
                    return store.Forms.TryGetValue(clientNumber, out var list) && list.Count > 0
                        ? list[0]
                        : null;
                default:
                    return null;
            }
        }

        // Length cap for known-narrow columns. Most DBF columns are 50+ chars
        // so we only enforce the documented narrow ones — anything else falls
        // through. Mirrors live truncation per agent 06 §4 "exceedsMax".
        private static bool ExceedsMax(string field, string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            // ClientID is the single hot spot in the spec — 8-char cap.
            if (string.Equals(field, "ClientID", StringComparison.OrdinalIgnoreCase) && value.Length > 8)
                return true;
            // No other documented narrow columns at v1; conservative on purpose.
            return false;
        }

        // -------------------------------------------------------------------
        // Detail-file methods (Phase 2 task #20).
        // -------------------------------------------------------------------

        /// <summary>
        /// Sim implementation of per-client detail-file reads. Reads filter
        /// in-memory; the underlying file format is row-ordered with no
        /// primary key (per agent 06 §5). When <paramref name="filter"/> is
        /// non-null, only rows for which the predicate returns true are
        /// returned. Predicate form chosen so callers can compose arbitrary
        /// Series/Code/StateAbbr filters without bloating the API surface.
        /// </summary>
        public IReadOnlyList<DetailRow> ReadDetail(
            string dataPath, int year, char module, string clientNumber, Predicate<DetailRow>? filter = null)
        {
            var store = TryGetModule(dataPath, year, module);
            if (store == null) return Array.Empty<DetailRow>();
            if (!store.Details.TryGetValue(clientNumber, out var list)) return Array.Empty<DetailRow>();

            // Per-list lock for read/write parity with WriteDetail and
            // LacerteSessionManager.ApplySimDetailWrite — the inner List<T> is
            // not thread-safe even though the outer Details map is. Without
            // this, a concurrent writer can mutate the backing array mid-
            // enumeration and throw InvalidOperationException.
            lock (list)
            {
                if (filter == null) return list.ToList();

                var matched = new List<DetailRow>();
                foreach (var r in list) if (filter(r)) matched.Add(r);
                return matched;
            }
        }

        /// <summary>
        /// Sim implementation of per-client detail-file writes. No validation
        /// — load-bearing footgun parity with the live SDK per agent 06 §5.
        /// When <paramref name="replaceIndex"/> is supplied, the row at that
        /// index is overwritten; otherwise the row is appended. Returns
        /// <see cref="StatusCodes.NotSignedIn"/> when the session is expired,
        /// <see cref="StatusCodes.DetailCreationError"/> when the client has
        /// no detail file (the live SDK can create one — but the sim only
        /// auto-creates on the create-client path so missing detail =
        /// DetailCreationError), <see cref="StatusCodes.OutOfIndex"/> when
        /// the supplied index is out of range, and <see cref="StatusCodes.Success"/>
        /// otherwise.
        /// </summary>
        public int WriteDetail(
            string dataPath, int year, char module, string clientNumber,
            DetailRow row, int? replaceIndex = null)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _sessionExpired, 0, 0) == 1)
                return StatusCodes.NotSignedIn;
            if (row == null) throw new ArgumentNullException(nameof(row));

            // P18-022 — Operator has the per-client detail file ({CLIENT}.{Mod}{YY})
            // held open in Lacerte's UI. The live SDK reports the same TAPIStatus 2
            // DBFNotAccessable on detail writes. Detail files are not numbered DATA1-7,
            // so the convention is table=0 for the detail-file lock latch (mirroring
            // ToggleLock's signature without overloading the DATA{n} space).
            if (IsTableLocked(year, module, 0)) return StatusCodes.DBFNotAccessable;

            var store = TryGetModule(dataPath, year, module);
            if (store == null) return StatusCodes.DetailCreationError;
            if (!store.Details.TryGetValue(clientNumber, out var list))
                return StatusCodes.DetailCreationError;

            // Per-list lock: the Details map is concurrent but the inner
            // List<DetailRow> is not. Mirror LacerteSessionManager.ApplySimDetailWrite,
            // which already takes lock(list) before mutating. Both paths must
            // hold the lock independently (they are parallel, not nested —
            // ApplySimDetailWrite goes straight to module.Details with its own
            // logic rather than calling WriteDetail).
            lock (list)
            {
                if (replaceIndex.HasValue)
                {
                    if (replaceIndex.Value < 0 || replaceIndex.Value >= list.Count)
                        return StatusCodes.OutOfIndex;
                    list[replaceIndex.Value] = row;
                }
                else
                {
                    list.Add(row);
                }
            }
            return StatusCodes.Success;
        }

        // -------------------------------------------------------------------
        // Create-client (Phase 2 task #21).
        // -------------------------------------------------------------------

        /// <summary>
        /// Sim implementation of <c>IClientList.SetDataPath → IClient(...) → CreateClient</c>.
        /// Per agent 06 §6: check order is NotSignedIn → InValidTaxYear →
        /// LengthExceeded (ClientID > 8) → InValidClientName (regex) →
        /// TaxModuleMismatch → DuplicateClient → assign next ClientNumber.
        /// On success returns the freshly-minted <c>ClientNumber</c> (also
        /// stored as <c>C1_0</c> on the new DATA1 row).
        /// </summary>
        public (int Status, string? ClientNumber) SimulateCreateClient(SimulateCreateClientArgs args)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));

            // P16-038 — uppercase-normalize ClientId at sim entry so direct
            // sim callers (integration tests, anything that bypasses the
            // CreateClientTools layer) match the bridge's case-sensitive
            // ClientNumberPattern (BridgeInputValidation.cs:36). The tool
            // layer already normalizes; this keeps the sim/live observational
            // equivalence even when sim is invoked without going through
            // the tool first. Two-mode discipline (CLAUDE.md §"Two-mode
            // discipline") requires sim and live to agree on the happy path.
            if (!string.IsNullOrEmpty(args.ClientId))
                args = args with { ClientId = args.ClientId.ToUpperInvariant() };

            // 1. NotSignedIn — sim equivalent of the live registration latch.
            if (System.Threading.Interlocked.CompareExchange(ref _sessionExpired, 0, 0) == 1)
                return (StatusCodes.NotSignedIn, null);
            if (System.Threading.Interlocked.CompareExchange(ref _registered, 0, 0) == 0)
                return (StatusCodes.NotSignedIn, null);

            // 2. Year guard.
            if (args.Year < 2018) return (StatusCodes.InValidTaxYear, null);

            // 3. ClientId length cap.
            if (string.IsNullOrEmpty(args.ClientId) || args.ClientId.Length > 8)
                return (StatusCodes.LengthExceeded, null);

            // 4. ClientId character whitelist.
            if (!_rxClientId.IsMatch(args.ClientId))
                return (StatusCodes.InValidClientName, null);

            // 5. TaxType ↔ Module match.
            if (!ModuleAcceptsTaxType(args.Module, args.TaxType))
                return (StatusCodes.TaxModuleMismatch, null);

            // 5b. P18-022 — Operator has DATA1 open in Lacerte's UI; the live
            // SDK can't write a new client row in that state and returns
            // TAPIStatus 2 DBFNotAccessable. CreateClient writes to DATA1 by
            // definition, so the lock check is hardcoded to table 1.
            if (IsTableLocked(args.Year, args.Module, 1))
                return (StatusCodes.DBFNotAccessable, null);

            // 6. Duplicate-ClientId check (case-insensitive). The legacy
            // sim store keeps "ClientID" as the persisted dict key because
            // existing tests + seed data rely on that exact column name —
            // changing it would ripple into SimulationStore.Query / seed
            // fixtures with no wire-visible benefit (it's an internal sim
            // bookkeeping field, never serialized through the MCP envelope).
            var store = GetOrAddModule(args.DataPath, args.Year, args.Module);
            foreach (var existing in store.Clients.Values)
            {
                if (existing.Fields.TryGetValue("ClientID", out var idObj)
                    && idObj is string id
                    && string.Equals(id, args.ClientId, StringComparison.OrdinalIgnoreCase))
                {
                    return (StatusCodes.DuplicateClient, null);
                }
            }

            // 7. Mint next ClientNumber + seed a minimal DATA1 row.
            // Interlocked so concurrent creates don't collide on the counter.
            var cn = System.Threading.Interlocked.Increment(ref store.NextClientNumber) - 1;
            var clientNumber = cn.ToString();
            var row = new DbfRow(clientNumber)
            {
                Fields =
                {
                    ["C1_0"] = clientNumber,
                    ["C1_1"] = args.FirstName ?? "",
                    ["C1_2"] = args.LastName ?? "",
                    ["C1_3"] = args.EmailId ?? "",
                    ["ClientID"] = args.ClientId,
                },
            };
            store.Clients[clientNumber] = row;
            // Seed an empty detail file so subsequent WriteDetail doesn't
            // collide with DetailCreationError on the happy path.
            store.Details[clientNumber] = new List<DetailRow>();
            return (StatusCodes.Success, clientNumber);
        }

        private static bool ModuleAcceptsTaxType(char module, string? taxType)
        {
            return Modules.AcceptsTaxType(module, taxType);
        }

        // -------------------------------------------------------------------
        // Snapshot / Restore (Phase 2 task #23) — supports dry-run rollback.
        // -------------------------------------------------------------------

        /// <summary>
        /// Deep-clone the entire store + auth latches. The returned
        /// <see cref="SimulationSnapshot"/> is fully detached: subsequent
        /// mutations to the store don't bleed into the snapshot, and the
        /// snapshot can be handed to <see cref="Restore"/> any number of
        /// times. Manual per-Map clone (not JSON round-trip) so the deep
        /// copy is type-safe + ConcurrentDictionary identity is preserved.
        /// </summary>
        public SimulationSnapshot Snapshot()
        {
            var snap = new SimulationSnapshot
            {
                Registered = System.Threading.Interlocked.CompareExchange(ref _registered, 0, 0) == 1,
                SessionExpired = System.Threading.Interlocked.CompareExchange(ref _sessionExpired, 0, 0) == 1,
            };

            // P18-022 — Copy the lock-latch set under the gate so concurrent
            // ToggleLock callers don't see a torn enumeration.
            lock (_lockedTablesGate)
            {
                snap.LockedTables = new HashSet<(int year, char module, int table)>(_lockedTables);
            }

            foreach (var dpKv in _root)
            {
                var dpDict = new Dictionary<int, Dictionary<char, ModuleStore>>();
                foreach (var yrKv in dpKv.Value)
                {
                    var yrDict = new Dictionary<char, ModuleStore>();
                    foreach (var modKv in yrKv.Value)
                    {
                        yrDict[modKv.Key] = CloneModuleStore(modKv.Value);
                    }
                    dpDict[yrKv.Key] = yrDict;
                }
                snap.Root[dpKv.Key] = dpDict;
            }

            return snap;
        }

        /// <summary>
        /// Atomically restore the store from a <see cref="Snapshot"/>.
        /// Replaces every per-cell <see cref="ModuleStore"/>, the auth
        /// latches, and (transitively) the monotonic counters. Cells not
        /// present in the snapshot are dropped from the live root —
        /// otherwise a snapshot → CreateClient → Restore round-trip would
        /// leave the new cell behind.
        /// </summary>
        public void Restore(SimulationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            // Wipe the root then re-add each cell from the snapshot. The
            // ConcurrentDictionary type doesn't expose Replace-Atomic for
            // the whole tree so the two-step is the cleanest path; sim is
            // single-process by design so the brief gap is acceptable.
            _root.Clear();
            foreach (var dpKv in snapshot.Root)
            {
                var byYear = new ConcurrentDictionary<int, ConcurrentDictionary<char, ModuleStore>>();
                foreach (var yrKv in dpKv.Value)
                {
                    var byMod = new ConcurrentDictionary<char, ModuleStore>();
                    foreach (var modKv in yrKv.Value)
                    {
                        byMod[modKv.Key] = CloneModuleStore(modKv.Value);
                    }
                    byYear[yrKv.Key] = byMod;
                }
                _root[dpKv.Key] = byYear;
            }

            System.Threading.Interlocked.Exchange(ref _registered, snapshot.Registered ? 1 : 0);
            System.Threading.Interlocked.Exchange(ref _sessionExpired, snapshot.SessionExpired ? 1 : 0);

            // P18-022 — Restore the lock-latch set wholesale under the gate.
            // Null-tolerant for older snapshots that predate the field.
            lock (_lockedTablesGate)
            {
                _lockedTables.Clear();
                if (snapshot.LockedTables != null)
                {
                    foreach (var triple in snapshot.LockedTables) _lockedTables.Add(triple);
                }
            }
        }

        private static ModuleStore CloneModuleStore(ModuleStore src)
        {
            var dst = new ModuleStore
            {
                NextClientNumber = src.NextClientNumber,
                NextEFilingId = src.NextEFilingId,
            };

            foreach (var kv in src.Clients) dst.Clients[kv.Key] = CloneRow(kv.Value);
            foreach (var kv in src.SubClients) dst.SubClients[kv.Key] = CloneRow(kv.Value);
            foreach (var kv in src.States) dst.States[kv.Key] = CloneRow(kv.Value);
            foreach (var kv in src.StateSubs) dst.StateSubs[kv.Key] = CloneRow(kv.Value);
            foreach (var kv in src.Statuses) dst.Statuses[kv.Key] = CloneRow(kv.Value);
            foreach (var kv in src.Displays) dst.Displays[kv.Key] = CloneRow(kv.Value);
            foreach (var kv in src.Forms)
            {
                var list = new List<DbfRow>(kv.Value.Count);
                foreach (var r in kv.Value) list.Add(CloneRow(r));
                dst.Forms[kv.Key] = list;
            }
            foreach (var kv in src.Details)
            {
                // DetailRow is an immutable record — shallow-copy the list
                // is sufficient because the records can't mutate underneath us.
                dst.Details[kv.Key] = new List<DetailRow>(kv.Value);
            }
            // EFiling / EFStatus are immutable records too.
            foreach (var ef in src.EFilings) dst.EFilings.Add(ef);
            foreach (var es in src.EFStatuses) dst.EFStatuses.Add(es);

            return dst;
        }

        private static DbfRow CloneRow(DbfRow src)
        {
            var dst = new DbfRow(src.ClientNumber, src.SubClientID, src.StateAbbrev);
            foreach (var kv in src.Fields) dst.Fields[kv.Key] = kv.Value;
            return dst;
        }
    }
}
