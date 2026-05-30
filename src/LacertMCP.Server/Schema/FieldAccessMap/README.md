# Field Access Map

Per-field write-safety metadata for every Lacerte module / DBF table / tax year.
Consumed by `FieldAccessMapLoader.GetFieldAccess(...)` and
`AssertWritable(...)` — see `Schema/FieldAccessMapLoader.cs` and agent
research at `docs/research/agents/04-field-access-map.md`.

## Layout

```
Schema/FieldAccessMap/
  manifest.json                       # version + module/overlay index + sources
  base/
    individual.json                   # per-module flat keyed maps
    corporate.json
    partnership.json
    scorp.json
    fiduciary.json
    exempt.json
    estate.json
    gift.json
    benefit.json
  overlays/
    ty21-not-supported.json           # one file per tax year that ships deltas
```

All files are valid JSON (validated at load time via `System.Text.Json`).
JSON files are copied to the build output as `Content` and loaded from
disk relative to the running assembly (see `FieldAccessMapLoader`).

## File shape — base/*.json

Flat map keyed by `"{module}|{table}|{field}"` (e.g. `"I|1|C1_G15"`). The
value matches `Schema/FieldAccess.cs::FieldEntry`:

```jsonc
{
  "I|1|C1_G15": {
    "access": "ReadWrite",
    "dataType": "string",
    "maxLength": 35,
    "description": "Taxpayer first name",
    "source": "spec",
    "addedAt": "2026-05-30"
  }
}
```

`access` is one of `ReadWrite | ReadOnly | Hidden | NotExposed | NotSupported`.
`Unknown` is **never** stored — it's the fallback when a field isn't in the
map. The universal rule `C{table}_0 → ReadOnly` is hard-coded in the loader
and does NOT need an entry.

## File shape — overlays/*.json

```jsonc
{
  "taxYear": 2021,
  "overrides": {
    "I|1|C1_99": { "access": "NotSupported", "source": "spec" }
  }
}
```

Overlays store the **per-year override** for that exact tax year — usually
a promotion to `NotSupported` when Intuit drops a field for that build.
Lookup order is overlay (if the year matches) → base → `Unknown`.

### Per-year, full-replace — not cumulative

Overlay files are scoped to the **exact** tax year named in the filename.
The loader reads `ty{yy}-not-supported.json` for the requested year only
and does NOT walk prior years or merge them. A `ty21-not-supported.json`
entry is **not** automatically applied when looking up the same field in
TY22, TY23, or TY24 — each TY must ship its own complete "not supported"
list, even if a field was already dropped in an earlier year.

This is per agent 04 §6 and the DECISIONS.md entry dated 2026-05-30
("Per-year access-map overlay semantic"). The trade-off is explicit:
authoring discipline (each year ships its own complete list) in exchange
for a loader that never inherits an override across a TY boundary the
author didn't intend.

## How to add a new entry

Two paths, both described in agent 04 §5:

**A. CLI helper (one-off discoveries) — preferred for spot fixes.**

```
lacerte-mcp field-access set \
  --module Individual --table 2 --field C2_45 \
  --access ReadWrite --data-type numeric --max-length 12 \
  --source manual --note "discovered while writing dependent SSN"
```

The CLI loads the right `base/<module>.json`, upserts the entry, re-sorts
the file, updates `manifest.json`, and prints a diff. Commit the diff.

**B. Introspection dump (bulk) — preferred for full population.**

A script (per agent 04 §5 path A) probes the installed SDK with no-op
writes and maps the returned `TAPIStatus` to a `FieldAccess`. Output
overwrites `base/<module>.json` and a PR is opened.

## v1 coverage status — TODO

**The shipped base files (`individual.json`, `partnership.json`, all
others) are intentionally empty `{}`.**

`Lacerte_MCP_Structure_Spec.md` §2 mentions that the source SDK PDF
contains a "Database Field Access" table for Individual/Partnership table
1 & 5 globals plus a "Not Supported Fields in TY21" list — but the
extracted spec checked into this repo does **not** include the actual
field names with stated access, only the description that such tables
exist in the upstream PDF.

Until the source SDK PDF is re-mined for the literal field rows (or the
introspection dumper is run against a live install — see agent 04 §4 v2),
the v1 base files ship empty. **The fail-closed write path** in
`FieldAccessMapLoader.AssertWritable(...)` plus the universal
`C{table}_0 → ReadOnly` rule keeps the system safe: any uncatalogued
field returns `Unknown` and write tools refuse without `force: true`.

`overlays/ty21-not-supported.json` similarly ships with `overrides: {}` —
populate it once the spec's TY21 list is extracted.

Cross-references for the introspection workflow: agent 04 §5 and Todo
follow-up F5 ("decide whether to ship `scripts/dump-field-access.csx` for
introspection in v1 or defer to v2").
