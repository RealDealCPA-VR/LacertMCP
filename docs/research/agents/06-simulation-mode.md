# Lacerte MCP — Simulation Mode Design

This document specifies the off-Windows / `LACERTE_SIMULATION=1` mode that lets development happen without a Lacerte install. It mirrors the QuickBooks Desktop MCP single-`SimulationStore` pattern (one in-memory `Map`-of-`Map`s, seeded on construction, mode chosen once in the session-manager constructor), adapted to Lacerte's wider data shape: 9 modules × 8 tables × N tax years × per-client detail files, plus the TY21+ schema shift and EF status pair.

---

## 1. Storage shape

The store is keyed by the same triple Lacerte itself uses to locate a return: **datapath → tax year → module**. Inside each module we hold the eight DBF tables plus per-client detail files and (TY19+) the EF tables.

```ts
// src/session/simulation-store.ts

export type DataPath     = string;          // e.g. "C:\\Lacerte\\21Tax\\IData"
export type TaxYear      = number;          // 2018..2025
export type ModuleChar   = "I"|"C"|"P"|"S"|"F"|"R"|"T"|"N"|"B";
export type ClientNumber = string;          // C{n}_0 — assigned by sim, monotonic
export type ClientID     = string;          // operator-chosen, ≤8 chars
export type FieldName    = `C${number}_${number}` | `C${number}_G${number}`;

// One row in DATA1..DATA7. Field bag is C{table}_{n}; we don't normalize —
// the column set is wide and module-specific.
export interface DbfRow {
  [field: string]: string | number | null;
  // Conventional keys we always materialize so handlers don't need to know
  // the per-table 0/1/2 positions:
  ClientNumber: ClientNumber;   // mirrors C{table}_0 — ReadOnly in live
  SubClientID?: number;         // DATA2 / DATA4 only
  StateAbbrev?: string;         // DATA3 / DATA4 only
}

export interface DetailRow {
  Series:    string;
  Prefix:    string;
  Code:      string;
  Suffix:    string;
  StateAbbr: string;
  CityAbbr:  string;
  Source:    string;
  TSJ:       "T"|"S"|"J"|"";
  ImportSrc: string;
  Amount:    number | null;
  Description: string;
}

export interface EFiling     { Id: number; ClientId: ClientNumber; EFId: string; InstanceId: string; }
export interface EFStatus    { EFilingId: number; EFStatus: string; EFStatusDate: string; }

export interface ModuleStore {
  // DATA1..DATA7 keyed by ClientNumber (DATA1) or composite key for 2/3/4:
  clients:    Map<ClientNumber, DbfRow>;                            // DATA1
  subclients: Map<`${ClientNumber}|${number}`, DbfRow>;             // DATA2 — key: client|subId
  states:     Map<`${ClientNumber}|${string}`, DbfRow>;             // DATA3 — key: client|state
  stateSubs:  Map<`${ClientNumber}|${number}|${string}`, DbfRow>;   // DATA4
  statuses:   Map<ClientNumber, DbfRow>;                            // DATA5 (F4)
  displays:   Map<ClientNumber, DbfRow>;                            // DATA6 (read-only)
  forms:      Map<ClientNumber, DbfRow[]>;                          // DATA7 — many rows / client
  details:    Map<ClientNumber, DetailRow[]>;                       // {CLIENT}.{Mod}{YY} files
  efilings:   EFiling[];                                            // TY19+ only
  efStatuses: EFStatus[];                                           // TY19+ only
  nextClientNumber: number;                                         // monotonic, sim only
  nextEFilingId:    number;
}

type YearMap   = Map<TaxYear,    Map<ModuleChar, ModuleStore>>;
type RootStore = Map<DataPath,   YearMap>;

export class SimulationStore {
  private root: RootStore = new Map();
  private sessionExpired = false;          // toggled via LACERTE_SIM_EXPIRED
  private registered     = false;          // toggled by lacerte_register
  constructor() { this.seed(); }
}
```

The triple-nested `Map` exactly mirrors the live path resolver — `(datapath, year, module) → DBF set` — so a tool that worked out the right context once can hand the same triple to either backend.

---

## 2. Seed data

Hand-authored TS const in `src/session/fixtures/seed.ts` (compiled in, zero file I/O on construction — same as QB MCP). Large rows live in `tests/fixtures/*.json` and are imported by the seed module; that keeps the seed code skim-readable while the rows stay greppable.

Recommended starter set, spanning the pre-/post-TY21 boundary so schema-shift code is always exercised:

| Datapath  | Year | Module | Clients                                                                 |
|-----------|------|--------|-------------------------------------------------------------------------|
| IData     | 2020 | I      | `SMIT` (single), `JONE` (joint, 1 dependent), `REJE` (EF rejected)      |
| IData     | 2024 | I      | `BROW` (joint, 2 dependents), `PEND` (EF in-progress, status `Transmitted`) |
| CData     | 2024 | C      | `ACME` (1120, 2 states CA/NY), `WIDG` (1120, single state)              |
| PData     | 2024 | P      | `PART` (1065, 3 partners → DATA2 subclient rows)                        |

That is 8 clients total — small enough to author by hand, wide enough that every read path (DATA1..7 + details + EF), every write path (pre/post-TY21 field write, detail write), and every error path (`DuplicateClient` on `SMIT`, `EFStatus=Rejected` on `REJE`) has a target. Detail files are 5-15 rows per client across a few common Series.

---

## 3. Schema-shift simulation (TY21+)

**The store keeps one canonical shape.** Rows are always `DbfRow` keyed by the composite keys above; we do not pre-build the old `select * from [DATA2-Subclient Info] where C2_0 = 'TEST'` flat-table form. The simulated SQL/UpdateTaxReturn entry points branch on `(year >= 2021)` before they touch the store:

```ts
processSqlQuery(sql: string, ctx: Context): DbfRow[] {
  const useNew = ctx.year >= 2021 && /^[2-4]$/.test(parseTable(sql));
  return useNew
    ? this.queryNewForm(sql, ctx)   // [CLIENT-SubClientInfo] style
    : this.queryOldForm(sql, ctx);  // [DATA2-Subclient Info] where C2_0 = 'CLIENT'
}
```

Both branches read from the same `subclients`/`states`/`stateSubs` Maps. Keeping one canonical shape avoids two write paths and matches how the live ODBC driver actually works — only the *query form* changed in TY21, not the underlying DBF.

---

## 4. Field-write simulation

The access map (`ReadOnly | ReadWrite | Hidden | NotExposed`) lives in `src/schema/field-access.ts` and is consulted by *both* live and sim before any mutation. The sim's write entry point is:

```ts
simulateFieldWrite(
  module:       ModuleChar,
  table:        1|2|3|4|5,
  clientNumber: ClientNumber,
  field:        FieldName,
  value:        string | number,
  year:         TaxYear,
  opts: { subClientID?: number; stateAbbrev?: string } = {},
): TAPIStatus {
  if (this.sessionExpired)            return "NotSignedIn";
  if (year < 2018)                    return "InValidTaxYear";
  if (table === 2 && !opts.subClientID)            return "InValidSubClientID";
  if ((table === 3 || table === 4) && !opts.stateAbbrev) return "InValidStateAbbrev";

  const access = fieldAccess(module, table, field, year);
  if (access === "NotExposed")   return "NotExposed";
  if (access === "Hidden")       return "FieldNotSupported";
  if (access === "ReadOnly")     return "ExposedForRead";
  if (access === "Unknown")      return "FieldNotSupported";

  const store = this.getModule(ctx.dataPath, year, module);
  const row   = lookupRow(store, table, clientNumber, opts);
  if (!row)                      return ["DB1Error","DB2Error","DB3Error","DB4Error","DB5Error"][table-1];

  // honor LengthExceeded for known-narrow columns (sim mirrors live truncation)
  if (typeof value === "string" && exceedsMax(field, value)) return "LengthExceeded";

  row[field] = value;
  return "Success";
}
```

Every return path here is a TAPIStatus the live SDK actually emits — the tool layer must not be able to distinguish modes by string-matching errors.

---

## 5. Detail-file simulation

Detail rows are keyed by `ClientNumber` only — the underlying file format is row-ordered with no primary key, so the sim mirrors that as `DetailRow[]`. Reads filter in-memory; writes append (or replace by index when an index is supplied). **No validation.** Validation lives in the tool layer; the sim must reproduce live's footgun:

```ts
readDetail(client: ClientNumber, filter: Partial<DetailRow>): DetailRow[] {
  const rows = this.module(ctx).details.get(client) ?? [];
  return rows.filter(r =>
    Object.entries(filter).every(([k, v]) => v == null || r[k as keyof DetailRow] === v)
  );
}

writeDetail(client: ClientNumber, row: DetailRow, replaceIndex?: number): TAPIStatus {
  if (this.sessionExpired) return "NotSignedIn";
  const list = this.module(ctx).details.get(client);
  if (!list) return "DetailCreationError";
  if (replaceIndex != null) list[replaceIndex] = row;
  else                      list.push(row);
  return "Success";   // no series/code/state validation — same as live
}
```

The `qb_detail_*` tool layer is where Series/Code lookups, Amount parsing, and state-abbreviation checks belong.

---

## 6. Create-client simulation

Mirrors `IClientList.SetDataPath` → `IClient` (TaxType, ClientID, EmailID, FirstName, LastName) → `CreateClient`. Returns the new `ClientNumber` or a TAPIStatus.

```ts
simulateCreateClient(args: {
  dataPath:  DataPath; year: TaxYear; module: ModuleChar;
  clientID:  ClientID; firstName?: string; lastName?: string;
  emailID?:  string;   taxType:  string;
}): { status: TAPIStatus; clientNumber?: ClientNumber } {
  if (!this.registered)             return { status: "NotSignedIn" };
  if (args.year < 2018)             return { status: "InValidTaxYear" };
  if (args.clientID.length > 8)     return { status: "LengthExceeded" };
  if (/[^A-Z0-9]/i.test(args.clientID)) return { status: "InValidClientName" };
  if (!moduleAcceptsTaxType(args.module, args.taxType))
                                    return { status: "TaxModuleMismatch" };

  const store = this.getOrCreateModule(args.dataPath, args.year, args.module);
  if ([...store.clients.values()].some(c => c.ClientID === args.clientID))
    return { status: "DuplicateClient" };

  const cn = String(store.nextClientNumber++);
  store.clients.set(cn, {
    ClientNumber: cn, ClientID: args.clientID,
    C1_0: cn, C1_1: args.firstName ?? "", C1_2: args.lastName ?? "",
    C1_3: args.emailID ?? "",
    /* …seed remaining ReadOnly globals to plausible defaults… */
  });
  return { status: "Success", clientNumber: cn };
}
```

---

## 7. Auth simulation

`lacerte_register` in sim flips `this.registered = true` and returns `AccessGranted` synchronously — no consent dialog, no Intuit window. The "logged in forever" default keeps dev frictionless. The 24-hour expiry UX is testable by setting `LACERTE_SIM_EXPIRED=1`:

```ts
constructor() {
  this.sessionExpired = process.env.LACERTE_SIM_EXPIRED === "1";
  this.seed();
}
register(): TAPIStatus {
  if (this.sessionExpired) return "NotSignedIn";
  this.registered = true;
  return "AccessGranted";
}
sessionStatus(): TAPIStatus {
  if (this.sessionExpired)  return "NotSignedIn";
  if (!this.registered)     return "NotSignedIn";
  return "AccessGranted";
}
```

---

## 8. Mode-switch detection

One check in the session manager constructor, immutable for process lifetime — same shape as QB's `simulation-store.ts`:

```ts
// src/session/manager.ts
export class LacerteSessionManager {
  readonly mode: "live" | "simulation";
  private sim?: SimulationStore;
  private bridge?: LacerteBridge;

  constructor(env = process.env) {
    const forceSim   = env.LACERTE_SIMULATION === "1";
    const forceLive  = env.LACERTE_LIVE === "1";
    const onWindows  = process.platform === "win32";
    this.mode = (!forceSim && forceLive && onWindows) ? "live" : "simulation";
    if (this.mode === "simulation") this.sim    = new SimulationStore();
    else                            this.bridge = new LacerteBridge();
  }
}
```

The rule: **live mode requires Windows AND `LACERTE_LIVE=1` AND `LACERTE_SIMULATION !== "1"`.** Everything else is sim. No env re-reads, no per-call mode flips.

---

## 9. Two-mode parity rule

Adapted from QB MCP `ARCHITECTURE.md` Boundary Invariant #2:

> **Simulation and live must be observationally identical.** A tool can't tell which mode it's running in by reading the response — same TAPIStatus values, same row shape, same column names, same TY21+ branching, same access-map rejections. When you change a live behavior, change the simulation in the same commit (or stub it) so the two stay in sync.

This is enforced by parity tests (§10) and by the rule that every tool calls `session.<verb>(...)` — never the bridge or the store directly.

---

## 10. Parity tests

A single test harness runs every behavioral test twice — once against `new SimulationStore()` directly, once against a recorded-fixture live adapter (until a real Windows runner exists, the "live" leg is a recording-replayer seeded from the SDK doc's documented responses). Sketch:

```ts
// tests/parity/harness.ts
type Backend = { name: "sim" | "live"; session: LacerteSessionManager };

export function bothModes(name: string, body: (b: Backend) => Promise<void>) {
  const sim  = new LacerteSessionManager({ LACERTE_SIMULATION: "1" });
  const live = new LacerteSessionManager({ LACERTE_LIVE: "1" }); // recorder in CI
  describe(name, () => {
    test("sim",  () => body({ name: "sim",  session: sim  }));
    test("live", () => body({ name: "live", session: live }));
  });
}

bothModes("field write rejects ReadOnly", async ({ session }) => {
  const r = await session.fieldWrite({ module: "I", table: 1,
    clientNumber: "1001", field: "C1_0", value: "X", year: 2024 });
  expect(r.status).toBe("ExposedForRead");
});
```

The harness asserts response equality across modes; any divergence fails CI. This is the operational enforcement of the parity rule in §9.

---

## File this shapes

`C:/Users/VR/Projects/LacertMCP/src/session/simulation-store.ts` — the types, the seed, the eight request handlers (`processSqlQuery`, `simulateFieldWrite`, `simulateUpdateReturn`, `readDetail`, `writeDetail`, `simulateCreateClient`, `register`, `sessionStatus`), and the `LACERTE_SIM_EXPIRED` toggle. Plus `src/session/fixtures/seed.ts` for the hand-authored seed and `tests/fixtures/*.json` for the larger row bodies.
