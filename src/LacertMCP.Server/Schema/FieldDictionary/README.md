# Field Dictionary

FP-3 of `docs/plans/filing-preparation.md`. Maps tax concepts ("1120 gross receipts", "1065 guaranteed payments", K-1 boxes) to where Lacerte stores them: a DATA1–DATA4 column or a detail-file Series/Code. Claude reads it through the `lacerte_field_dictionary(module, search?, taxYear?)` tool.

**Every file ships with zero entries.** Nothing has been verified against a real Lacerte install yet. An entry is added only after the mapping has been observed with `lacerte-mcp map-diff` (below). Never add an entry from memory, a forum post or a guess: a wrong field or code makes Claude write the wrong cell on a real return.

## Files

One file per module, named by `Modules.FileBasenameFor`: `individual`, `corporate`, `partnership`, `scorp`, `fiduciary`, `exempt`, `estate`, `gift`, `benefit` (`.json`). The csproj copies them to `bin\...\Schema\FieldDictionary`, next to `Schema\FieldAccessMap`, and `FieldDictionaryLoader` reads them from there.

```jsonc
{
  "module": "C",
  "entries": [
    {
      "id": "1120.gross_receipts",          // stable; letters, digits, . _ -
      "concept": "Gross receipts or sales",
      "form": "1120",
      "line": "1a",                         // optional
      "kind": "field",                      // "field" | "detail"
      "table": 1,                           // kind=field: 1..4
      "field": "C1_37",                     // kind=field: C{table}_{n} or C{table}_G{n}
      "needsSubclient": false,              // must be true exactly for DATA2 / DATA4
      "needsState": false,                  // must be true exactly for DATA3 / DATA4
      "dataType": "amount",                 // amount | text | date | bool | integer | percent
      "notes": "optional",
      "taxYears": [2024],                   // optional; omitted = every year
      "verifiedOn": "TY2024 on LACERTE-PC, 2026-11-20",
      "source": "map-diff"                  // map-diff | manual
    }
  ]
}
```

(The example values are illustrative only — they are not a verified mapping.)

A `kind: "detail"` entry has `series` and `code` (integers) and optional `prefix` / `suffix` instead of `table` / `field`. For detail entries, `needsState` is true when the observed row carried a state.

The loader is strict. Any of the following rejects the **whole file**, which then loads as empty (fail-closed) and `lacerte_field_dictionary` reports a `loadError`:

- an unknown property, `kind`, `dataType` or `source`;
- a missing required property;
- a `module` that doesn't match the file;
- duplicate ids;
- two entries pointing at the same target for overlapping tax years.

Code and tests can validate a file with `FieldDictionaryLoader.ValidateFile`, which uses the loader's own parse path.

## What a dictionary entry changes

A dictionary entry is not just documentation. It also gates the access-map fallback (FP-1a). When a field has no literal entry in `Schema/FieldAccessMap` (base or year overlay), and the module's dictionary lists that exact table and field for the tax year, `FieldAccessMapLoader` classifies it by the SDK manual's range rules:

- `C1_n` and `C3_n` are ReadWrite.
- `C2_n` and `C4_n` with n ≥ 2 are ReadWrite, while `C2_1` and `C4_1` are ReadOnly.
- `C5`–`C7` non-global fields are ReadOnly.
- Unlisted `C1_G*`, `C2`–`C4_G2+`, `C6_G*` and `C7_G*` fields are Hidden. Unlisted `C5_G*` fields are ReadWrite.

The provenance shows as `accessSource: "sdk-range-rule"` in a refusal's context.

Literal entries and year overlays always win. For example, an overlay `NotSupported` still blocks the field. A field that is not in the dictionary stays Unknown (9012), as before.

## Building it: `lacerte-mcp map-diff`

Run this on the Lacerte PC against a **test client**, never a real taxpayer.

1. Take a "before" snapshot:

   ```
   lacerte-mcp map-diff snapshot --data-path C:\Lacerte\24tax\CData --year 2024 --module C --client TEST1 --out before.json
   ```

2. In Lacerte, open the test client and enter **ONE known, distinctive value on ONE input screen**, such as `123457` in gross receipts. Avoid round numbers and zero. Then close the client so Lacerte saves it.
3. Take an "after" snapshot with the same command and `--out after.json`.
4. Check what changed:

   ```
   lacerte-mcp map-diff compare --before before.json --after after.json
   ```

5. Record the mapping:

   ```
   lacerte-mcp map-diff record --before before.json --after after.json --id 1120.gross_receipts --concept "Gross receipts or sales" --form 1120 --line 1a --expect 123457
   ```

`record` refuses unless the diff contains exactly one of these:

- one changed field cell with a non-blank new value; or
- one added or changed detail row.

When it refuses, it lists everything that changed. Re-take the snapshots with only one value entered. If Lacerte updates a bookkeeping column on every edit, leave it out with `--ignore <column>`.

Other rules:

- **Unreadable tables.** `record` also refuses when a table could not be read in either snapshot. A change there would be invisible, so a coincidental change elsewhere could be recorded instead. If that table really doesn't exist for the client, override with `--allow-read-errors`, which requires `--expect`.
- **`--expect` (recommended).** Proves the recorded cell or detail row holds the value you typed.
- **Tax years.** By default an entry covers only the snapshots' tax year. Recording the same mapping for another year widens `taxYears`. If the id is already mapped to a different target, `record` refuses: Lacerte may have moved the field that year, so use a year-specific id. `--all-years` records the mapping for every year, and `--replace` overwrites a wrong mapping.
- **Detail rows.** The observed `prefix` and `suffix` are recorded as seen. Per the SDK, Suffix starts at 1000 for standard fields. Prefix may number repeated entries (for example, the second W-2), so note that in `--notes` if you know it.
- **Where it writes.** Like `field-access set`, it writes to the tree the MCP server reads: `..\server\Schema\FieldDictionary` in the release layout, or the server's build output in a dev checkout. `--schema-dir` overrides this. Call `lacerte_reload_schema` in a running server to pick up the change; it reloads the dictionary too.
- **Making it permanent.** That copy is overwritten by a rebuild and by `lacerte-mcp update`. Copy the entry into this folder in the source tree and commit it.

**Table form.** `--table-form auto` on `snapshot` uses the TY21+ per-client tables from TY2021 on, and the global pre-TY21 tables before that. The server asks the SDK's `CanUseNewApis` instead, so pass `ty21` or `pre-ty21` if the two disagree.

Snapshot files contain the client's return data. Delete them when you're done.
