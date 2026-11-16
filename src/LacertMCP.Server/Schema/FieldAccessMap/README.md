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
  --module I --table 2 --field C2_45 \
  --access ReadWrite --data-type numeric --max-length 12 \
  --source manual --note "discovered while writing dependent SSN"
```

`--module` is the single module character (`I`/`C`/`P`/`S`/`F`/`R`/`T`/`N`/`B`).
The CLI loads the right `base/<module>.json`, upserts the entry, re-sorts
the file by key, writes it back, and prints the resolved schema directory
plus a one-line before/after diff of the entry. It does **not** touch
`manifest.json` (the loader never reads the manifest; it only lists the
shipped shard names). It refuses to write if the existing shard is not
valid JSON rather than replacing it.

Where it writes — the schema directory is resolved in this order:

1. `--schema-dir <path>` (a `FieldAccessMap` root, or an install folder
   containing `Schema/FieldAccessMap`);
2. `..\server\Schema\FieldAccessMap` relative to `lacerte-mcp.exe`, if it
   exists — the release-zip layout, where this is the tree
   `LacertMCP.Server.exe` actually reads;
3. `Schema\FieldAccessMap` next to `lacerte-mcp.exe` (the dev bin layout,
   where CLI and server share one output folder).

That is a **build-output / install** copy, not the source tree: an edit
made there is overwritten by the next build or release unzip. To make it
permanent, copy the entry into
`src/LacertMCP.Server/Schema/FieldAccessMap/base/<module>.json` and commit
it (or point `--schema-dir` at that source folder). A running MCP server
caches shards — call the `lacerte_reload_schema` tool to pick up a change
without a restart.

**B. Introspection dump (bulk) — preferred for full population.**

`LacertMCP.FieldAccessDumper.exe [--overwrite] [--live] [--year <yyyy>] [--out <dir>]`
probes fields and maps the returned status to a `FieldAccess`, merging the
results into `base/<module>.json` (existing entries are preserved unless
`--overwrite`). Default output: a sibling `..\server\Schema\FieldAccessMap\base`
when present (release layout), else this source tree's `base/` when run
from a checkout, else `Schema/FieldAccessMap/base` next to the exe. Today
`--live` is a stub (throws BridgeUnavailable) and sim mode has no fields to
enumerate, so the dumper writes nothing yet.

## Coverage status (R7, 2026-11-15)

The source SDK PDF (Intuit *Lacerte SDK Developer Instructions*, https://static.developer.intuit.com/resources/Lacerte_SDK_Developer_Instructions.pdf) was re-mined. Its literal rows now ship with `source: "sdk-pdf"`:

- **`individual.json` / `partnership.json`.** The documented DATA1 global fields:
  - `C1_G4`, `C1_G12` and `C1_G13` are ReadWrite. The PDF also lists `C1_G38` as ReadWrite, but its "Not Supported Fields in TY21" list removes it from TY21. Overlays are exact-year and no TY22+ list exists, so a ReadWrite base entry would fail **open** for TY22+. It is therefore omitted and stays fail-closed (9012); catalogue it per year with `field-access set` if needed.
  - The other listed `C1_G*` fields are ReadOnly.
  - The listed DATA5 globals (`C5_G5`, `C5_G48`, `C5_G49`, `C5_G52`–`C5_G54`, `C5_G56`–`C5_G61`) are Hidden.
- **Scope.** The global-field rows above ship for Individual and Partnership only, the scope the PDF's TY21 lists and the original spec mining attribute them to. Other modules stay fail-closed for those fields.
- **All nine modules.** The structural DATA2–DATA4 rows: `C2_1` and `C4_1` (subclient number), `C3_G1` and `C4_G1` (state abbreviation), and `C2_G1`, all ReadOnly.
- **`overlays/ty21-not-supported.json`.** The PDF's "Not Supported Fields in TY21" lists for Individual and Partnership. These include `C5_1`–`C5_67`, expanded to literal keys. Per DECISIONS 2026-05-30, an overlay applies to its **exact** year only.

The PDF also states **range rules**, which are *not* encoded as literal keys:

- `C{1,3}_1+` and `C{2,4}_2+` ("remaining non-global fields") are ReadWrite.
- `C5_1+`, `C6_1+` and `C7_1+` are ReadOnly.
- Unlisted `C1_G+` / `C2_G2+` / `C3_G2+` / `C4_G2+` / `C6_G+` / `C7_G+` are Hidden.
- Unlisted `C5_G+` are ReadWrite.

Per the 2026-05-30 design decision, any field without a literal entry stays **fail-closed**: `Unknown` gives 9012, and writing it needs `force: true` or a `field-access set`. The SDK itself still enforces the range rules server-side, returning 7 NotExposed or 8 ExposedForRead. The universal `C{table}_0 → ReadOnly` rule is unchanged.

Cross-references for the introspection workflow: agent 04 §5 and Todo
follow-up F5 ("decide whether to ship `scripts/dump-field-access.csx` for
introspection in v1 or defer to v2").
