# Plan: return preparation for 1120, 1120-S, 1065 and 1041

**Goal.** Claude can enter a corporate (1120), S-corporation (1120-S), partnership (1065) or trust (1041) return's data into Lacerte from source documents (trial balance, K-1 packages, prior-year return), with every write validated, read back and reversible. Lacerte still computes the forms; filing stays in Lacerte.

**Before this work (2026-11-18 morning).** Every module could be read and clients created, but live mode wrote only 3 DATA1 fields (1040/1065), detail rows couldn't be written, there was no field dictionary and no 1041 pre-filing check. See **Status** below for what is built now.

## Status (2026-11-18)

Built and tested in simulation, not yet verified on the Lacerte PC:

- **FP-1a** — gated range-rule fallback: a field the module's dictionary names gets its access from the SDK range rules (literal entries and overlays still win). The dictionaries ship empty, so nothing changes until mappings are recorded.
- **FP-2** — live detail-row writes over ODBC in a separate worker process, with a per-client snapshot before the first write and a read-back after each. **Off by default** (`LACERTE_LIVE_DETAIL_WRITES=1` enables them) until REGRESSION_CHECKLIST "FP-2 live items" pass.
- **FP-3 tooling** — `Schema/FieldDictionary/` (empty per module), the `lacerte_field_dictionary` tool, and `lacerte-mcp map-diff snapshot|compare|record`. Recorded entries survive `lacerte-mcp update` and installer re-runs.
- **FP-4** — the `lacerte_entity_return_entry` prompt (dictionary-gated, dry run, approval, read-back), plus pre-filing rules for 1041 (now supported) and 1120-S shareholders.
- **FP-5** — simulation sample clients for S-corps (SData: ORCH, HOLW) and trusts (FData: MAPL, BIRC).

Decisions 1 and 2 below were taken as recommended: range rules apply only to dictionary-listed fields, and detail writes take a snapshot and read back. Since built: bulk detail writes require a passing dry run's `previewToken`, and every live detail row the MCP adds is journaled so `lacerte-mcp detail-restore` can remove exactly those rows (ODBC DELETE still to be verified on the Lacerte PC).

Remaining, all on the Lacerte PC: record the mappings (FP-3), run the FP-2 live checks, optionally implement the dumper's live probe (FP-1b), and live acceptance per return type (FP-6). Rules that need verified codes (1120-S/1065 ownership totals, 1041 trust type, 1120 balance sheet) follow the mappings.

## Decisions needed before building

1. **Writable-field policy.** The SDK manual states range rules: non-global `C1_n`, `C3_n`, `C2_2+` and `C4_2+` are ReadWrite, and other ranges are ReadOnly or Hidden. The SDK also rejects non-writable fields itself (7 NotExposed / 8 ExposedForRead). Two options:
   - **Keep literal-only, fail-closed (current).** Every field must be listed before it can be written.
   - **Adopt the range rules as a fallback for every module**, with the SDK as backstop. This unlocks client, state and subclient fields at once.

   **Recommendation:** adopt the range rules, but only for fields named in the field dictionary (FP-3). That way Claude can only write fields it can explain.
2. **Detail-row write safety.** The SDK does not validate detail rows, so a bad row can corrupt a return. **Recommendation:** each session's first write to a client takes a backup of that client's detail file, every write is read back, and bulk writes require a dry run first.

## Work items

| ID | Item | Where it can be built | Depends on |
|---|---|---|---|
| FP-1 | **Writable-field coverage.** (a) Loader fallback to the SDK range rules, gated by the dictionary, per decision 1. Buildable and testable in simulation now. (b) Implement the dumper's live probe: enumerate columns with `ReadFromDBFData`, and classify each field by writing back its current value through `WriteToDBFDatabase` / `UpdateTaxReturn` (a no-op) and recording the TAPIStatus. | (a) here; (b) Lacerte PC | Decision 1 |
| FP-2 | **Live detail-row writes over ODBC.** INSERT into `[<client>-Detail]` using the existing column validation (Series/Prefix/Code/Suffix integers, Source/TSJ vocabularies, Amount range). Each write is read back with `lacerte_detail_read`, the client's detail file is backed up first, and a restore command is provided. | Code and fake-ODBC tests here; verification on the Lacerte PC | Decision 2 |
| FP-3 | **Field and code dictionary per return type**, the largest item. It maps tax concepts (e.g. "1120 gross receipts", "1065 guaranteed payments", "1041 distributable net income", K-1 boxes) to DATA1–4 columns or detail Series/Code. It ships as `Schema/FieldDictionary/<module>.json` and is exposed as a `lacerte_field_dictionary(module, search)` tool. It is built with a **map-diff** helper: snapshot a test client, enter one known value on one Lacerte input screen, snapshot again, and record what changed. This also confirms or corrects the Series IDs the simulation uses today (11 = W-2, 14 = dependents, ...), which are not verified against Lacerte. | Map-diff tool here; mapping on the Lacerte PC | none |
| FP-4 | **Return-type workflows.** MCP prompts for 1120, 1120-S, 1065 and 1041 data entry from a trial balance or K-1 package, built on the dictionary. Adds pre-filing rules for 1041 (trust, fiduciary and beneficiaries), 1120-S (shareholders and ownership summing to 100%) and the 1120 balance sheet. | Here | FP-3 |
| FP-5 | **Simulation fixtures** for S-corps and trusts (sample clients, shareholders, beneficiaries, detail rows), plus parity tests for each new write path. | Here | FP-3 for realistic codes |
| FP-6 | **Live acceptance on the Lacerte PC.** For each return type: create a test client, have Claude enter a small known return from a trial balance, and compare Lacerte's computed form lines with expected values. Then test a rollback of the detail-file backup. | Lacerte PC | FP-1 to FP-5 |

## Order

1. Decide 1 and 2.
2. Build the map-diff helper (FP-3 tooling), FP-1a, the FP-2 code and FP-5 here, all testable in simulation.
3. On the Lacerte PC, run the map-diff sessions to build the dictionaries, starting with the return type the firm files most. Verify FP-2 there, and if wanted, FP-1b.
4. Build FP-4 on top of the dictionaries, then run FP-6 for each return type.

**Not in scope:** e-filing, printing, Lacerte diagnostics, depreciation asset entry (stored outside the DBF tables; needs its own investigation) and organizers.
