# Agent 04 — Per-Field Access Map Strategy

**Scope.** Design the metadata layer that tells every write tool in the Lacerte MCP whether `C{table}_{n}` / `C{table}_G{n}` is `ReadOnly`, `ReadWrite`, `Hidden`, `NotExposed`, or `NotSupported` for a given Module x Table x TaxYear. Without this, writes can silently corrupt returns — the spec (§2, §6.6) treats this as gating data for full write coverage.

---

## 1. Data model

Access is a **5-state enum** plus an `'Unknown'` fallback so the lookup is total:

```ts
export type FieldAccess =
  | 'ReadOnly'      // C{t}_0 client number, computed fields
  | 'ReadWrite'     // safe to write
  | 'Hidden'        // exists but SDK refuses; write -> NotExposed (TAPIStatus 7)
  | 'NotExposed'    // not surfaced by SDK at all
  | 'NotSupported'  // TY21+ deprecation overlay
  | 'Unknown';      // we have not catalogued it yet
```

A **flat keyed map** beats deeply-nested objects for ~50k entries (faster JSON parse, smaller after gzip, trivial diff in PRs):

```ts
export interface FieldEntry {
  access: Exclude<FieldAccess, 'Unknown'>;
  dataType?: 'string' | 'numeric' | 'date' | 'bool';
  maxLength?: number;        // critical for string fields (LengthExceeded = TAPIStatus error)
  description?: string;      // human label from input sheet
  source: 'spec' | 'glbridge' | 'inputsheet' | 'introspection' | 'manual';
  addedAt: string;           // ISO date — for audit/diffing
}

// Key = `${module}|${table}|${field}`  e.g. "I|1|C1_G15"
export type FieldAccessMap = Record<string, FieldEntry>;

// TY-version overlay — only stores deltas (typically NotSupported)
export interface TaxYearOverlay {
  taxYear: number;            // 2021, 2022, ...
  overrides: Record<string, Pick<FieldEntry, 'access' | 'source'>>;
}
```

**Why flat-keyed, not nested?** With ~9 modules x ~8 tables x ~700 fields x ~8 years the worst case is ~400k entries. A nested `Record<Module, Record<Table, Record<Field, ...>>>` forces three property lookups per access and balloons in JSON because every leaf gets its own `{}` wrapper. A flat string-keyed map is one lookup, ~30% smaller on disk, and trivially shardable (one file per module — see §2).

---

## 2. Storage format

**Recommendation: sharded JSON, one file per module, plus a year-overlay file.**

```
src/schema/field-access-map/
  index.ts                 # loader, lazy-imports per module
  base/
    individual.json        # ~10-15k entries when fully populated
    corporate.json
    partnership.json
    scorp.json
    fiduciary.json
    exempt.json
    estate.json
    gift.json
    benefit.json
  overlays/
    ty21-not-supported.json   # spec's "Not Supported Fields in TY21" table
    ty22-not-supported.json
  manifest.json            # version, source attribution, row counts
```

Rejected alternatives:
- **TypeScript const** — fails: 50k+ entries explode tsc memory and incremental build times; also gives no runtime way to ship an update without rebuild.
- **Single monolithic JSON** — slow to parse on cold start; bad PR diffs (one module update touches the whole file).
- **SQLite** — overkill for read-mostly, append-rarely metadata; adds a native-deps headache to a Node MCP that already drags ODBC.
- **YAML** — human-friendlier but Node's `yaml` parse is ~10x slower than `JSON.parse`. Use JSON; if humans need to edit, do it through the CLI helper (§5).

Modules are loaded lazily: a session in `Individual` mode never pays the cost of parsing `corporate.json`.

---

## 3. Where the data comes from

| Source | What it gives | Public? | Effort |
|---|---|---|---|
| `Lacerte_MCP_Structure_Spec.md` (this repo) | Access for Individual + Partnership Table 1 & 5 globals; TY21 not-supported list; status enum | yes | extract by hand, one-time |
| **Lacerte Bridge SDK PDF** (`lacerte-bridge-sdk.pdf` on `digitalasset.intuit.com`) | Series + Code parameters for detail-file inputs (not C{t}_{n} fields) | yes, public download | parsable |
| **Blank input sheets** | Each input screen's Series + per-row Code; can be generated locally via `Print > Input Sheets > Suppress All > Force All Pages` per module | local-only (no public bulk download) | manual one-pass per module |
| **`show codes` toggle** | Right-click any input-screen header bar -> *Show Codes*; reveals codes inline. Useful for spot-fixing | local | per-field |
| **SDK introspection** | `IDBFReader.ReadFromDBFData` returns an XML dataset whose *schema* element names every `C{t}_{n}` field with dBase type/length. **Writes to a field always return `TAPIStatus` so we can probe access by attempting a no-op write and catching `7 NotExposed` / `8 ExposedForRead`.** | local, runs against installed SDK | scriptable — see `scripts/dump-field-access.ts` (§5) |
| GL Bridge "general developer" docs | Same Series/Code information as Bridge SDK PDF | yes | covers detail file, not DBF columns |

**Honest finding:** there is **no public Intuit document that ships a complete `C{table}_{n}` access map.** Every community thread (Intuit Accountants Lacerte SDK group) points users back at the input-sheet print-out workflow or at probing the SDK. The catalog has to be built in-house, ideally via introspection (the most reliable path — see §5).

---

## 4. Coverage roadmap

**v1 (ship now):**
1. Hard-code every entry the spec already documents:
   - Individual table 1 & 5 globals,
   - Partnership table 1 & 5 globals,
   - The TY21 not-supported list as an overlay.
2. Mark `C{t}_0` `ReadOnly` for every (module, table) — universal rule.
3. `lacerte_field_access_map` MCP tool returns `{ access: 'Unknown', source: null }` for everything else.
4. **Default behaviour for write tools: refuse `Unknown` with `RequiresExplicitAccessOverride` error.** Operator can pass `force: true` to write anyway (logged loudly).

**v2 (week after first real client write):**
- Run the introspection dumper (§5) on the live install for every Module x Year on disk; populate `base/*.json` from the result.

**v3:**
- Add per-field `dataType`, `maxLength`, `description` from input-sheet OCR or manual annotation as needed by validators (LengthExceeded is a frequent SDK error).

---

## 5. Maintenance workflow

Two paths, both backed by the same JSON files:

**A. Introspection dump (preferred, bulk).** A `scripts/dump-field-access.ts` runs in live mode against an installed Lacerte:

```ts
// pseudo
for (const module of MODULES) {
  for (const table of [1,2,3,4,5,6,7]) {
    const xml = await bridge.readFromDBFData(/* schema only */);
    for (const field of fieldsIn(xml)) {
      const probe = await bridge.writeToDBFDatabase(testRow, field, sentinelValue);
      const access = mapStatusToAccess(probe.status); // 7 -> NotExposed, 8 -> ReadOnly, 1 -> ReadWrite, ...
      upsert(map, module, table, field, { access, source: 'introspection' });
    }
    await bridge.rollbackTestRow();
  }
}
```

Then `npm run schema:fmt` re-emits the JSON files and a PR is opened.

**B. CLI helper (one-off discoveries).**

```
npx lacerte-mcp field-access set \
  --module Individual --table 2 --field C2_45 \
  --access ReadWrite --data-type numeric --max-length 12 \
  --source manual --note "discovered while writing dependent SSN"
```

The CLI:
1. Loads the right `base/<module>.json`,
2. Upserts the entry,
3. Re-sorts and rewrites the file,
4. Adds a row to `manifest.json` with operator, date, and reason,
5. Prints the diff so the operator can commit.

No silent edits — every change is a tracked file mutation, reviewable in git.

---

## 6. TY-version-aware lookup

The base map is TY-agnostic; the overlays add `NotSupported` (and rarely, demotions like `ReadWrite -> ReadOnly`) for specific tax years. Lookup applies the overlay **after** the base hit:

```ts
export function getFieldAccess(
  module: ModuleChar,
  table: number,
  field: string,
  taxYear: number
): FieldAccess {
  const key = `${module}|${table}|${field}`;

  // Universal rule first — cheap, no map hit
  if (field === `C${table}_0`) return 'ReadOnly';

  // TY overlay (NotSupported list) wins over base
  const overlay = loadOverlay(taxYear);
  const o = overlay?.overrides[key];
  if (o) return o.access;

  // Base map
  const base = loadModule(module)[key];
  if (base) return base.access;

  return 'Unknown';
}
```

Overlays are keyed by exact `taxYear`. A TY22 entry does **not** inherit from TY21 — each year's "not supported" list is shipped explicitly (per the spec, TY21 has its own list; future years will publish their own).

---

## 7. `assertWritable` validation helper

Throws a structured error mapped to the right `TAPIStatus` code (`NotExposed=7`, `ExposedForRead=8`) before the bridge call ever happens:

```ts
import { TAPIStatus } from '../errors/status-codes.js';
import { LacerteError } from '../errors/lacerte-error.js';

export function assertWritable(
  module: ModuleChar,
  table: number,
  field: string,
  taxYear: number,
  opts: { force?: boolean } = {}
): void {
  const access = getFieldAccess(module, table, field, taxYear);

  switch (access) {
    case 'ReadWrite':
      return;

    case 'ReadOnly':
      throw new LacerteError({
        code: TAPIStatus.ExposedForRead,            // 8
        message: `Field ${field} on ${module}/DATA${table} is read-only (e.g. computed or client number).`,
        field, module, table, taxYear,
      });

    case 'Hidden':
    case 'NotExposed':
      throw new LacerteError({
        code: TAPIStatus.NotExposed,                // 7
        message: `Field ${field} is not exposed for write on ${module}/DATA${table}.`,
        field, module, table, taxYear,
      });

    case 'NotSupported':
      throw new LacerteError({
        code: TAPIStatus.FieldNotSupported,
        message: `Field ${field} is not supported in TY${taxYear} (overlay).`,
        field, module, table, taxYear,
      });

    case 'Unknown':
      if (opts.force) return;
      throw new LacerteError({
        code: 'UnknownFieldAccess',                  // MCP-side code, no TAPI equivalent
        message:
          `Field ${field} on ${module}/DATA${table} is not in the access map. ` +
          `Refusing to write. Pass force:true to override, or run ` +
          `'npx lacerte-mcp field-access discover' to populate the map.`,
        field, module, table, taxYear,
      });
  }
}
```

Every write tool (`lacerte_field_write`, `lacerte_update_return`, `lacerte_detail_write`) calls `assertWritable` *before* touching the bridge — this is the spec's "client-side rejection before any SDK call" requirement (§2). The TY-overlay `NotSupported` case maps to `FieldNotSupported` rather than `NotExposed` so the operator can tell apart "your version of Lacerte dropped this" from "this never existed."

---

## Bottom line

Ship a **flat-keyed, sharded-JSON, lazily-loaded** map with a `'Unknown'` fallback and a **fail-closed** write path. The complete public catalog does not exist — **introspection against the installed SDK is the only realistic path to full coverage**, and the CLI helper plus PR workflow keep manual discoveries auditable.

## Sources

- [Lacerte Bridge Software Developer's Kit PDF](https://digitalasset.intuit.com/DOCUMENT/A4bYGYCFO/lacerte-bridge-sdk.pdf)
- [Lacerte Software Developer's Kit (SDK) overview](https://accountants.intuit.com/support/en-us/help-article/license-information/lacerte-software-developer-kit-sdk/L3pbQA7gF_US_en_US)
- [Lacerte SDK Developer Instructions PDF](https://static.developer.intuit.com/resources/Lacerte_SDK_Developer_Instructions.pdf)
- [Getting started with Lacerte SDK (Intuit Developer Portal)](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started)
- [Lacerte SDK Group — Intuit Accountants Community](https://accountants.intuit.com/community/lacerte-sdk-group/gp-p/501)
- [Finding series numbers for input screens (community thread)](https://accountants.intuit.com/community/lacerte-tax-discussions/discussion/how-do-i-find-series-numbers-for-input-screens-to-use-lacerte-gl/00/11069)
- [Common questions about input sheets in Lacerte](https://accountants.intuit.com/support/en-us/help-article/form-1040/common-questions-input-sheets-lacerte/L9ljfns9M_US_en_US)
- [Viewing Data with Lacerte SDK (community thread)](https://accountants.intuit.com/community/lacerte-sdk-group/viewing-data-with-lacerte-sdk/gpm-p/338557)
- [Understanding Lacerte data file structure and extensions](https://accountants.intuit.com/support/en-us/help-article/file-management/understanding-lacerte-data-file-structure/L0o9T6jSz_US_en_US)
