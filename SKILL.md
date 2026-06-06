---
name: lacerte-mcp
description: |
  Use this skill when interacting with Intuit Lacerte tax software via the Lacerte MCP server's tools (any tool name starting with `lacerte_`). Triggers on operator phrases involving tax-pro workflows: reading client info, updating DBF fields, creating clients, bulk field updates, reviewing EF (e-file) status, year-over-year comparisons, firm-wide dashboards, pre-filing readiness checks, name-to-ClientNumber resolution, detail-row writes, or any reference to TY (tax year), DBF tables 1-7, Lacerte modules (Individual/Corporate/Partnership/SCorp/Fiduciary/Exempt/Estate/Gift/Benefit), TAPIStatus codes, the LacerteSDK_Access COM API, or the LacerteDSII ODBC driver. Also activates when the operator references CLI commands like `lacerte-mcp register` / `lacerte-mcp login` / `lacerte-mcp doctor` / `lacerte-mcp tools`, environment variables like `LACERTE_LIVE` / `LACERTE_SIMULATION` / `LACERTE_DATA_PATH` / `LACERTE_DATA_ROOT` / `LACERTE_DEBUG_WIRE`, or the seeded simulation ClientIDs SMIT / JONE / REJE / BROW / PEND / ACME / WIDG / PART. Do NOT use for unrelated tax software (Drake, UltraTax, ProConnect, TurboTax, QuickBooks), generic tax-prep advice, or anything that doesn't go through a `lacerte_*` tool call.
---

# Lacerte MCP — operator skill

You drive Intuit Lacerte tax software through a stdio MCP server exposing ~38 tools, all named `lacerte_*`. This skill grounds you in the conventions, the catalog, the error space, and the recovery recipes so you can use the surface deterministically without trial and error.

---

## When to use

Trigger on any of these intents:

- "look up / read / find a Lacerte client" / "what's the address for X" / "show me John Smith"
- "set / change / switch context to TY24 / 2023 / Individual / Corporate"
- "update field C1_42" / "change the email" / "write to DATA1" / "field write"
- "bulk update / mass mail merge / sweep N clients" / "update zip for every NY client"
- "create a new client" / "onboard a new return" / "import a CSV / tax organizer"
- "review e-file status" / "what got rejected" / "any returns pending?"
- "pre-filing check" / "is this return ready to file"
- "year over year" / "compare TY23 vs TY24"
- "firm dashboard / Monday morning snapshot" / "how does the firm look"
- "compare clients" / "diff two returns"
- "what fields can I write to" / "field access map"
- "the call hung / timed out" / "what just happened" / "tail the wire log"
- references to `lacerte-mcp register`, `lacerte-mcp login`, `lacerte-mcp doctor`, `LACERTE_LIVE`, TAPIStatus codes, DBF table numbers 1-7, sim seed IDs SMIT/JONE/REJE/BROW/PEND/ACME/WIDG/PART

Do NOT use this skill for:

- Drake Tax, UltraTax CS, ProConnect Tax Online, TurboTax, QuickBooks (Desktop OR Online) — those have separate MCPs / no MCPs.
- Generic "how do I file taxes" / "what deductions exist" advice — that's tax knowledge, not Lacerte.
- E-filing actions (transmit, retransmit, correct an EF) — the server is read-only on EF; submission still happens in the Lacerte UI.
- PDF rendering, printing, organizer / client portal / letter template / diagnostics-detail access — out of scope.

---

## Mode awareness

The server runs in one of two modes; identical wire shape, different backend:

| Mode         | Triggered by                                      | Notes                                                                                              |
| ------------ | ------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `simulation` | default off-Windows; `LACERTE_SIMULATION=1` forces | 8 seeded clients across 4 datapaths; safe for any operation that doesn't probe live SDK semantics. |
| `live`       | `LACERTE_LIVE=1` on Windows with SDK installed    | Real ODBC reads + COM writes. Requires `lacerte-mcp register` + 24h auth ticket.                   |

Sim seed roster (verbatim ClientIDs you may see back):

| Datapath                  | Tax year | Module      | Seeded ClientIDs                               |
| ------------------------- | -------- | ----------- | ---------------------------------------------- |
| `C:\Lacerte\20Tax\IData`  | 2020     | Individual  | `SMIT` (single), `JONE` (joint, 1 dep), `REJE` (EF rejected) |
| `C:\Lacerte\24Tax\IData`  | 2024     | Individual  | `BROW` (joint, 2 deps), `PEND` (EF Transmitted) |
| `C:\Lacerte\24Tax\CData`  | 2024     | Corporate   | `ACME` (CA + NY), `WIDG` (TX)                  |
| `C:\Lacerte\24Tax\PData`  | 2024     | Partnership | `PART` (3 partners: Alice / Bob / Carol)       |

Always call `lacerte_session_status` first to confirm mode, registration, active context, and any auto-emitted `nextSteps` bootstrap hints.

---

## Mandatory first step on every new conversation that touches Lacerte

1. Call `lacerte_session_status` (zero wire I/O, never fails on the happy path).
2. Inspect the returned envelope:
   - `mode` — `"sim"` or `"live"`.
   - `isRegistered` — false in live mode means the operator must run `lacerte-mcp register` from PowerShell.
   - `context` — null means you MUST call `lacerte_set_context` BEFORE any read/write tool.
   - `nextSteps` — array of remediation strings; the server pre-emits the right next move so you don't eat a 9006/9007/9009 reactive error.
3. If `context` is null, call `lacerte_set_context(dataPath, taxYear, module)` before anything else. Many tools throw `9009 ContextNotSet` if you skip this.

Skipping step 1 is the #1 reason for unnecessary `9009 ContextNotSet` errors.

---

## Wire conventions (CANONICAL — never deviate)

These are enforced by `NamingConventionsTests` (13 reflection-driven sentinels). Drift breaks the test suite.

### Parameter naming (always camelCase)

| Use this           | NEVER use                       | Notes                                              |
| ------------------ | ------------------------------- | -------------------------------------------------- |
| `taxYear` (int)    | `year`, `Year`, `TaxYear`       | Floor 2018; ceiling `DateTime.Today.Year + 1`.     |
| `stateAbbrev`      | `stateAbbr`, `state`, `State`   | `""` or 2-letter postal code; matches `^[A-Z]{2}$`. |
| `fieldName`        | `field` (sometimes — see below) | Bulk-write entries use `Field`; single writes use `field`. |
| `field`            | `fieldName`                     | Specifically on `lacerte_field_write` / `lacerte_update_return`. |
| `module` (string)  | `char`, `Module`, integer ordinal | Length-1 string; one of `"I"/"C"/"P"/"S"/"F"/"R"/"T"/"N"/"B"`. |
| `clientId`         | `clientID`, `ClientID`          | Operator-chosen, ≤8 alphanumeric, normalized uppercase. |
| `emailId`          | `emailID`, `EmailID`            | Microsoft acronym casing (`Id`, not `ID`).         |
| `subClientId`     | `subClientID`, `SubClientID`     | Positive int (DATA2 / DATA4 row identifier).        |
| `clientNumber`     | `clientNum`, `ClientNumber`      | SDK-minted monotonic identifier (sim counter); DIFFERENT from `clientId`. |
| `dataPath`         | `path`, `Path`                  | Absolute path; rejected if relative / `..` / contains control chars. |
| `dryRun`           | `dry_run`, `dryrun`             | Boolean.                                           |
| `idempotencyKey`  | `key`, `idempotency_key`         | Optional but STRONGLY recommended on writes.       |

### Critical distinction: `clientId` vs `clientNumber`

- `clientId` — operator-chosen alphanumeric tag (e.g. `"BROW"`, `"SMIT"`); supplied by the operator when creating a client; ≤8 chars, matches `^[A-Z0-9]+$`.
- `clientNumber` — SDK-minted monotonic identifier (sim counter assigns `"1001"`, `"1002"`...); used by every read/write tool except `lacerte_client_create`.

Most tools take `clientNumber`. If the operator only gives you a name, resolve via `lacerte_client_find(name)` first.

### Collection key & pagination shape

- Canonical collection key: `rows`. Legacy domain aliases (`clients`, `forms`, `statuses`, `subclients`, `states`, `stateSubclients`, `datapaths`, `modules`, `efStatuses`, `codes`) survive until v0.2 — prefer `rows`.
- Paginated read envelope: `{ rows, total, limit, offset, hasMore, nextOffset }`. Legacy `count` reflects the slice length (gone in v0.2).
- Limit semantics: `null` = default (100); positive = clamped to `Pagination.MaxLimit` (1000); `0` = count-only (returns `rows: []` with `total: N`); negative throws.
- `taxYear` (int) is canonical on response envelopes too.

### Mode strings, correlation IDs

- Mode strings: lowercase `"sim"` / `"live"` on the wire (no PascalCase).
- Correlation ID: `cid` — 8-hex per-request id stamped by `WrapToolHandler`. Echoed in every success AND error envelope; appears in nested WireLog records on disk. Quote it in bug reports.

### Response envelopes

Success:

```json
{ "success": true, "result": { ... }, "cid": "a1b2c3d4" }
```

Error:

```json
{
  "success": false,
  "statusCode": 9012,
  "statusName": "UnknownFieldAccess",
  "origin": "synthetic",
  "severity": "error",
  "userMessage": "...",
  "remediation": "...",
  "context": { /* structured retry hints, redacted of true secrets */ },
  "cid": "a1b2c3d4"
}
```

- `origin` discriminator: `"sdk"` for SDK codes 0-204; `"synthetic"` for MCP-minted codes ≥9000.
- `severity` lowercase: `"success"` / `"warning"` / `"error"`.
- All `userMessage` and `remediation` strings are en-US only.

---

## Tool catalog (38 tools)

Format: `lacerte_<name>(args) → return shape — purpose`.

### Session & context (6 tools)

- `lacerte_session_status() → { mode, isRegistered, registeredAt, expiresAt, minutesUntilExpiry, context, appName, appId, debugWireEnabled, wireLogPath, nextSteps[] }` — snapshot the session. Call this FIRST, every conversation. Zero wire I/O.
- `lacerte_set_context(dataPath: string, taxYear: int, module: string, readOnly: bool = false) → { dataPath, taxYear, module, readOnly }` — set active triple. Validates year ≥2018 and module in `I/C/P/S/F/R/T/N/B`. `readOnly: true` locks the session against ALL writes (every write tool throws `9001` first).
- `lacerte_can_use_new_apis(taxYear: int, module: string) → { taxYear, module, canUseNewApis, apiPath }` — whether the pair supports TY21+ `UpdateTaxReturn`. Cached.
- `lacerte_list_modules() → { count, rows: [{ module, name, returnType }], modules }` — the 9-module catalog with ODBC ReturnType strings. Static; no wire I/O.
- `lacerte_list_datapaths() → { root, rootExists, count, rows: [{ path, taxYear, module, moduleName }], datapaths, hint? }` — discover `{YY}Tax\{Module}Data` under `LACERTE_DATA_ROOT` (default `C:\Lacerte`). `hint` non-null when scan returns zero rows.
- `lacerte_health() → { mode, status, heartbeatPath?, heartbeatSeconds? }` — reachability probe.

### Read (10 tools)

- `lacerte_client_list(nameFilter?: string, limit?: int, offset?: int) → { rows, total, limit, offset, hasMore, nextOffset, count, clients }` — list clients in active context. `nameFilter` is case-insensitive SUBSTRING against C1_1 AND C1_2 (Individual) or C1_1 only (entity). Prefer `lacerte_client_find` for name resolution.
- `lacerte_client_read(clientNumber: string) → { clientNumber, found, client, hint? }` — read one DATA1 row. Envelope is never null; check `found` (not the top-level result).
- `lacerte_client_find(name: string, limit?: int) → { matches: [{ clientNumber, clientId, firstName, lastName, email }], total, truncated, didYouMean? }` — resolve a name to ClientNumber(s). Call BEFORE `lacerte_client_read` / `lacerte_field_write` when the operator gave you a name instead of a ClientNumber. `didYouMean` carries up to 3 Levenshtein-closest names on a 0-match query.
- `lacerte_client_summary(clientNumber: string, includeDetail: bool = false) → { summaryVersion, clientNumber, found, context, identity, counts, states, statesTruncated, flags, efStatus, cursors }` — one-call snapshot replacing 5-7 round-trips. Identity field set is FIXED per module.
- `lacerte_export_client(clientNumber: string) → { uri: "lacerte://client/{clientNumber}", clientNumber, found, context: { dataPath, taxYear, module }, client, hint? }` — portable resource-style envelope with context echo. The context echo pins the (datapath, year, module) the row was resolved against.
- `lacerte_table_read(table: int, clientNumber?: string, stateAbbrev?: string, limit: int = 100, offset: int = 0) → { table, clientNumber, stateAbbrev, rows, total, limit, offset, hasMore, nextOffset, count }` — generic DATA1..DATA7 reader. Table 8 rejected; tables 6/7 are read-only.
- `lacerte_subclient_read(clientNumber: string, limit: int = 100, offset: int = 0) → { clientNumber, rows, total, limit, offset, hasMore, nextOffset, count, subclients }` — DATA2 dependents/partners/shareholders/beneficiaries.
- `lacerte_state_read(clientNumber: string, stateAbbrev?: string, limit: int = 100, offset: int = 0) → { clientNumber, stateAbbrev, rows, total, limit, offset, hasMore, nextOffset, count, states }` — DATA3 per-state overrides.
- `lacerte_state_subclient_read(clientNumber: string, stateAbbrev?: string, limit: int = 100, offset: int = 0) → { clientNumber, stateAbbrev, rows, total, limit, offset, hasMore, nextOffset, count, stateSubclients }` — DATA4. Multistate partnerships are the heaviest result sets in the API.
- `lacerte_status_read(clientNumber: string, limit: int = 100, offset: int = 0) → { clientNumber, rows, total, limit, offset, hasMore, nextOffset, count, statuses }` — DATA5 F4-status (read-only outside global flags).
- `lacerte_forms_read(clientNumber: string, limit: int = 100, offset: int = 0) → { clientNumber, rows, total, limit, offset, hasMore, nextOffset, count, forms }` — DATA7 form rows (read-only).
- `lacerte_detail_read(clientNumber: string, limit: int = 100, offset: int = 0, series?: int) → { clientNumber, rows, total, limit, offset, hasMore, nextOffset, count }` — per-client detail file. Optional `series` push-down (11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners).
- `lacerte_ef_status(sinceUtc?: string, limit: int = 100, offset: int = 0) → { rows: [{ EFilingId, ClientId, EFId, InstanceId, Status, StatusDate }], total, limit, offset, hasMore, nextOffset, count, efStatuses }` — EFilings JOIN EFStatuses. TY19+. ISO-8601 `sinceUtc` for incremental delta polls.

### Write (4 tools)

- `lacerte_field_write(table: int, clientNumber: string, field: string, value: any?, subClientId?: int, stateAbbrev?: string, dryRun: bool = false, idempotencyKey?: string) → { statusCode, statusName, ... }` — single field write. Tables 1-5 writable; 6/7 read-only (`ExposedForRead`). Pre-TY21 uses `WriteToDBFDatabase`; TY21+ routes through `UpdateTaxReturn` (the session manager picks).
- `lacerte_update_return(...same as lacerte_field_write...)` — alias for TY21+ `UpdateTaxReturn` developer-doc parity. Behavior identical to `lacerte_field_write`. Prefer `lacerte_field_write` in new code.
- `lacerte_field_write_bulk(table: int, entries: [{ clientNumber, field, value, subClientId?, stateAbbrev? }], taxYear?: int, readOnly?: bool, dryRun: bool = false, continueOnError: bool = false, idempotencyKey?: string, expectedCount?: int) → { table, dryRun, continueOnError, totalCount, successCount, errorCount, skippedCount, firstFailureIndex, firstFailure, recoveryHint, perItem }` — N writes per call. Capped at 500 entries (override `LACERTE_BULK_WRITE_MAX`). Per-row key shape: `{prefix}-{clientNumber}-{field}`. Pre-flight throws `9020 BulkBatchTooLarge` / `9021 BulkBatchCountMismatch` / `9016 ContextMismatch`.
- `lacerte_detail_write(clientNumber: string, series: string, code: string, amount: double?, description: string, prefix: string = "", suffix: string = "", stateAbbrev: string = "", cityAbbr: string = "", source: string = "", tsj: string = "", importSrc: string = "", replaceIndex?: int, dryRun: bool = false, idempotencyKey?: string) → { statusCode, statusName, clientNumber, replaceIndex, dryRun }` — append/replace a detail row. Mandatory client-side validation (Series `^\d+$`, Code `^-?\d+$`, stateAbbrev `^[A-Z]{2}$` if non-empty, TSJ ∈ {T,S,J,""}). The SDK does NOT validate row shape — without this tool's gates a malformed row silently corrupts the return. `replaceIndex` in live mode throws `9019 LiveReplaceIndexUnsupported`.

### Create (1 tool)

- `lacerte_client_create(clientId: string, taxType: string, firstName: string, lastName: string, emailId: string, dryRun: bool = false, idempotencyKey?: string) → { statusCode, statusName, clientNumber, clientId, dryRun }` — create new client. `clientId` ≤8 alphanumeric, normalized to uppercase. `taxType` must match the active module (e.g. `"Individual"` or `"1040"` for module I); accepted aliases canonicalize to the spec ReturnType. firstName/lastName/emailId all required per spec §4.

### Discovery & schema (4 tools)

- `lacerte_field_access_map(module: string, table: int, taxYear: int) → { module, moduleName, table, taxYear, count, fields: { [fieldCode]: "ReadOnly"|"ReadWrite"|"Hidden"|"NotExposed"|"NotSupported"|"Unknown" } }` — per-(module,table,year) field access map. Includes universal `C{t}_0 → ReadOnly`. Call BEFORE writing to confirm the field is writable.
- `lacerte_return_codes() → { count, rows: [{ code, name, severity, userMessage, remediation, origin, originGroup }], codes }` — full code catalog. `origin` ∈ {TAPIStatus, TPSDKStatus, Synthetic}; `originGroup` ∈ {sdk, synthetic} matches the error envelope's `origin`.
- `lacerte_help(topic?: string) → various by topic` — distilled README inline. Topics: `null`/`""` (full overview), `"modes"`, `"tools"`, `"env"`, `"limits"` (the killer payload — what this MCP does NOT do), `"first-call"`, `"prompts"`. Aliases: `"quickstart"`→`first-call`, `"envvars"`/`"environment"`→`env`, etc. Pure static content; safe pre-context.
- `lacerte_prompts(name?: string) → catalog rows or { found, name, description, whenToUse, body }` — list/fetch the 4 canonical workflow recipes. Use when host MCP doesn't expose Prompts UI (Cursor pre-0.47, Windsurf, generic stdio).

### Schema reload (1 tool)

- `lacerte_reload_schema() → { modulesReloaded, overlaysReloaded, schemaRoot }` — drop in-process FieldAccessMap caches so freshly written shards take effect WITHOUT restarting the server (which would invalidate the Intuit 24h auth ticket). Call after running `LacertMCP.FieldAccessDumper.exe`.

### Observability (5 tools)

- `lacerte_recent_log(cid?: string, limit: int = 50) → { entries: [{ ts, op, cid, durationMs, error? }], totalSeen, droppedSinceProcessStart, capacity, filteredByCid }` — in-process WireLog tail (~50KB ring, default 256 entries; override via `LACERTE_RECENT_LOG_CAPACITY`). Independent of `LACERTE_DEBUG_WIRE`. Filter by `cid` for forensic triage.
- `lacerte_request_stats() → { uptime, totalCalls, perOp: [{ op, count, errors, p50, p95, p99 }], idempotency: { hits, misses, conflicts }, autoReconnect: { attempts, exhausted } }` — process-lifetime aggregate counters.
- `lacerte_cache_state() → { idempotency: { count, cap, ttlHours, nearCap, oldestCreatedAt, newestCreatedAt, peakCount, persistEnabled, persistPath }, newApis: { entries: [{ taxYear, module, value }], count }, edition: { probed, sentinelFailure, edition, build, sdkVersion }, snapshotAtUtc }` — three in-process caches.
- `lacerte_resource_usage() → { pid, uptimeSeconds, workingSetMB, peakWorkingSetMB, privateMemoryMB, handleCount, threadCount, gcTotalMemoryMB, gcCollections: {gen0, gen1, gen2}, processorTimeMs, is64BitProcess, isServerGC }` — process telemetry. Survives bridge-unavailable.
- `lacerte_active_locks(limit: int = 20) → { idempotency: [{ keyHashPrefix, held, heldForSeconds }], totalLockEntries, heldCount, returnedCap, snapshotAtUtc }` — held idempotency semaphores.

### Accelerators (7 tools)

- `lacerte_firm_dashboard(yearFilter?: int, modulesFilter?: string, sinceUtc?: string) → { generatedAt, root, sinceUtc, summary: { totalClients, totalModulesScanned, totalEfRows, statusHistogram }, byContext: [{ taxYear, module, moduleName, dataPath, clientCount, efRowCount, statusHistogram, latestStatusDate, error? }] }` — Monday-morning firm snapshot. Fans across every (taxYear, module) under `LACERTE_DATA_ROOT`. Saves ~30 round-trips. Snapshots+restores active context.
- `lacerte_pre_filing_check(clientNumber: string, severity: "normal" | "strict" = "normal") → { clientNumber, readiness: "ready"|"blocked"|"warn"|"unsupported-module", passed, blockers, warnings, checkedRules, skippedRules }` — pre-flight gate. v1 ships Individual module only with 7 rules: `required-name`, `required-last-name`, `required-email`, `forms-at-least-one`, `forms-has-1040`, `dependents-have-description`, `status-rows-present`. `severity="strict"` promotes every warning to a blocker.
- `lacerte_year_over_year(clientNumber: string, fromYear: int, toYear: int, scope: "demographics"|"detailTotals"|"all" = "all", seriesIds?: int[]) → { clientNumber, fromYear, toYear, scope, seriesIds, fromDataPath, toDataPath, demographicsDiff, detailTotalsDiff, errors }` — diff a client across two tax years. Path rewrites `{YY}Tax` segment in active context's dataPath. `(toYear - fromYear) ≤ 5`. Default seriesIds: `[11, 12, 13, 20]` (Wages/Interest/Dividends/Income).
- `lacerte_diff_clients(client1Number: string, client2Number: string, includeSubclients: bool = true, includeStates: bool = true, includeDetail: bool = false, ignoreFields?: string[]) → { client1Number, client2Number, summary, dataOne: { fields: [{ field, a, b }] }, subclients, states, detail, ignoredFields }` — structured field-by-field diff. Same-context only. Returns ONLY differing rows. Built-in ignore: `clientNumber`.
- `lacerte_field_history(clientNumber: string, field: string, sinceUtc?: string, limit: int = 100) → { clientNumber, field, rows: [{ ts, cid, value, dryRun, statusCode, durationMs }], total, truncated, logPath, hint?, redactionNote }` — audit log for one (clientNumber, field) pair sourced from WireLog JSONL. Requires `LACERTE_DEBUG_WIRE=1`. `value` is `"***"` for every row by design (disk-scope redaction is unconditional). Default window: 14 days.

### Prompts catalog tool

- `lacerte_prompts(name?: string)` — already listed under Discovery. Lists or fetches the 4 canonical workflow prompts; described in next section.

---

## Prompts (4 canonical workflows)

Registered via `WithPromptsFromAssembly()`. When the MCP host exposes a Prompts UI, prefer those (`/` in Claude Desktop, `@` in Cursor). When the host doesn't, call `lacerte_prompts(name)` to get the body and follow inline.

| Prompt name              | What it walks you through                                                                                                                                       | Args                                                              |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| `lacerte_client_intake`  | Onboard a new client end-to-end: verify session → pick datapath/module/year → `lacerte_client_create` → seed DATA1 fields → seed detail rows → verify roster.   | `clientName?: string`                                             |
| `lacerte_review_ef_status` | Triage e-file status: pull EFiling+EFStatus join, group by Status, surface Rejected first, flag stale Pending/Transmitted (>72h). TY19+ only.                | `taxYear?: int`                                                   |
| `lacerte_field_write_bulk` | Bulk field write across many clients with mandatory dry-run preview (first N), idempotency keys, stop-on-first-error.                                          | `table: int = 1`, `field: string` (required), `previewCount: int = 3` |
| `lacerte_csv_intake`     | Bulk-onboard clients from a CSV / Tax-Organizer export with one-shot column→field mapping declaration, per-row idempotency keys, stop-on-first-error.            | `sourceLabel?: string`, `rowCount?: int`                          |

---

## Common multi-step recipes

### Read a client by name

1. `lacerte_session_status` (confirm context).
2. `lacerte_client_find(name: "John Smith")` → pick `clientNumber` from `matches`.
3. `lacerte_client_read(clientNumber)` OR `lacerte_client_summary(clientNumber)` for the high-level snapshot.

### Update an address (single field)

1. `lacerte_session_status` (confirm context, readOnly=false).
2. `lacerte_field_access_map(module, table: 1, taxYear)` to confirm the field is writable.
3. `lacerte_field_write(table: 1, clientNumber, field: "C1_42", value: "...", idempotencyKey: "addr-update-{clientNumber}-C1_42")`.

### Bulk-update a field across many clients

1. Build `entries: [{ clientNumber, field, value }, ...]`.
2. `lacerte_field_write_bulk(table, entries, dryRun: true, idempotencyKey: "sweep-2026-q1-zip-DRY")` to preview.
3. Inspect `perItem` + `firstFailure` + `recoveryHint`.
4. `lacerte_field_write_bulk(table, entries, dryRun: false, idempotencyKey: "sweep-2026-q1-zip-COMMIT")` to commit.

Use a **different** `idempotencyKey` for the real run than the dry-run. (P17-004 fixed the bug where the fingerprint did not include `dryRun`, but be explicit anyway — it makes the audit log unambiguous.)

### Review EF status

1. `lacerte_ef_status(sinceUtc: "2026-01-01T00:00:00Z")` (incremental delta).
2. For Rejected rows: `lacerte_client_summary(clientNumber)` to inspect.

### New-client onboarding

Invoke the `lacerte_client_intake` prompt, or manually:

1. `lacerte_session_status` → confirm `isRegistered`.
2. `lacerte_set_context(dataPath, taxYear, module)`.
3. `lacerte_client_create(clientId, taxType, firstName, lastName, emailId, idempotencyKey: "create-{clientId}")`.
4. `lacerte_field_write` for each seed field with idempotencyKey `"intake-{clientId}-{field}"` (DIFFERENT per field — never reuse).
5. `lacerte_client_read(clientNumber)` to verify.

### Debug a failed call

1. Read the `cid` from the error envelope.
2. `lacerte_recent_log(cid)` to inspect the in-process tail (always-on, independent of debug-wire).
3. If empty: ask the operator to set `LACERTE_DEBUG_WIRE=1`, restart, reproduce, then check the disk log path emitted in `lacerte_session_status.wireLogPath`.

### Discover a field code from an intent

1. `lacerte_field_access_map(module, table, taxYear)`.
2. Scan response by access value (`ReadWrite`) and field code (`C{t}_{n}`).

### Get help inline

`lacerte_help(topic)` with `topic` ∈ `null` (overview) / `"modes"` / `"tools"` / `"env"` / `"limits"` / `"first-call"` / `"prompts"`. Aliases (`"quickstart"`, `"envvars"`, `"overview"`, `"all"`) auto-resolve.

### Firm-wide snapshot

`lacerte_firm_dashboard(yearFilter: 2024)` — single call replaces ~30 round-trips. Per-tuple errors don't poison the aggregate.

### Pre-filing readiness

`lacerte_pre_filing_check(clientNumber, severity: "strict")` — Individual module only in v1. Surfaces blockers/warnings against the v1 rule catalog.

### Year-over-year diff

`lacerte_year_over_year(clientNumber, fromYear: 2023, toYear: 2024)` — uses the active context's dataPath as the path template.

---

## Idempotency — REQUIRED on writes

Every write tool accepts `idempotencyKey?: string`. STRONGLY pass one for any write that might be retried (MCP transport timeouts + LLM auto-retry are the default failure mode).

### How it works

- The server fingerprints (op, args, dryRun) via SHA-256 and stores `key → result` for 24h (configurable via `LACERTE_IDEMPOTENCY_TTL_SECONDS`).
- Persisted to `%APPDATA%/lacerte-mcp/idempotency-cache.json` (P17-004). Disable via `LACERTE_IDEMPOTENCY_PERSIST=0`.
- Same key + same payload + same `dryRun` → cached replay (idempotent for 24h).
- Same key + different payload → throws `9102 IdempotencyConflict` (the SHA-256 prefix is NOT leaked in the error context unless `LACERTE_DEBUG_WIRE=1`).
- Per-row key on `lacerte_field_write_bulk`: server auto-shapes as `{prefix}-{clientNumber}-{field}`.

### Key shape suggestions

- Single writes: `"{operation}-{clientNumber}-{fieldName}"` (e.g. `"addr-update-1001-C1_42"`).
- Bulk: pass a prefix like `"sweep-2026-q1-zip"`; the per-row key auto-derives.
- Detail writes: `"intake-{clientId}-detail-{seriesCode}"`.

### Anti-pattern

Reusing the same key across `dryRun: true` preview AND `dryRun: false` commit — fixed in P17-004 (the fingerprint now includes `dryRun`) but be explicit by using distinct keys, so the audit log is unambiguous.

---

## Dry-run — use for preview

All 4 write tools accept `dryRun: true`. Behavior:

- Runs the full validation + access-map gate + simulation of the write.
- Snapshot / apply / restore — no persisted change.
- Returns the would-be TAPIStatus.
- BYPASSES the idempotency cache (a dryRun cannot replay a real commit — distinct fingerprints).

Use to validate args before committing. Especially valuable on `lacerte_field_write_bulk` — preview the first N entries, then resubmit with `dryRun: false`.

---

## Read-only mode — defensive

`lacerte_set_context(dataPath, taxYear, module, readOnly: true)` blocks ALL writes at the session layer. Every write tool throws `9001 FieldIsReadOnly` BEFORE any side effect.

Use when the operator says "just looking" or "audit mode" or you're driving a review-only chat. Disengage by re-calling `lacerte_set_context` with `readOnly: false`.

---

## Error handling pattern

1. ALWAYS check `success` first.
2. If `success: false`, read `statusCode`, then `statusName`, then `userMessage`, then `remediation`.
3. Decide based on `statusCode`:

### SDK origin codes (origin: "sdk")

| Code | Name                      | What it means                                      | What you do                                                           |
| ---- | ------------------------- | -------------------------------------------------- | --------------------------------------------------------------------- |
| 0    | Error                     | Generic SDK error                                  | Operator: `LACERTE_DEBUG_WIRE=1` + Event Viewer.                       |
| 1    | Success                   | OK                                                 | Happy path.                                                            |
| 2    | DBFNotAccessable         | DBF file missing/locked                            | Operator: confirm `LACERTE_DATA_PATH`; close client in Lacerte UI.      |
| 4    | OutOfIndex                | Row number unknown                                 | Re-query; OR switch to TY21+ ClientNumber path.                        |
| 7    | NotExposed                | Field exists but SDK refuses                       | Remove the field from your payload.                                    |
| 8    | ExposedForRead            | Field is read-only                                 | Use a read tool, not a write tool.                                     |
| 12   | DuplicateClient          | clientId already exists                            | Pick a different `clientId`; OR `lacerte_client_read` the existing.    |
| 13   | LengthExceeded            | Value too long (often `clientId > 8`)              | Trim. clientId ≤ 8 alphanumeric.                                       |
| 14   | TaxModuleMismatch        | `taxType` doesn't match the module character       | Fix `taxType` (e.g. "Individual" for module I).                        |
| 18   | DB3Error                  | DATA3-specific DBF I/O error                       | Lacerte File → Repair Indexes (verify `context.expectedDbf`).          |
| 24   | InValidClientName        | Token failed validation                            | Read `context.reason` for the gate-specific rule.                      |
| 25   | InValidTaxYear           | Year < 2018 or not installed                       | Pass `taxYear` ≥ 2018; OR install that Lacerte module.                 |
| 27   | InValidSubClientID       | `subClientId` ≤ 0                                  | Pass positive int.                                                     |
| 28   | InValidStateAbbrev       | Missing or unrecognized 2-letter code              | Pass postal code (e.g. CA, NY).                                        |
| 200  | ConsentDenied            | Operator declined Intuit consent                   | Operator: re-run `lacerte-mcp register`, click Accept.                 |
| 203  | NotSignedIn               | 24h auth window expired                            | Operator: run `lacerte-mcp login`; fall back to `register` if needed.  |

### Synthetic codes (origin: "synthetic", codes ≥9000)

| Code | Name                          | What it means                                                    | What you do                                                              |
| ---- | ----------------------------- | ---------------------------------------------------------------- | ------------------------------------------------------------------------ |
| 9001 | FieldIsReadOnly              | Field marked non-writable                                        | Don't write it. Check `lacerte_field_access_map` for writable fields.    |
| 9002 | ClientIDTooLong              | `clientId` > 8 chars                                             | Trim.                                                                    |
| 9003 | TaxYearTooOld                | `taxYear` < 2018                                                 | Pass ≥ 2018.                                                             |
| 9004 | StateAbbrevRequired           | DATA3/4 write without `stateAbbrev`                              | Pass 2-letter code.                                                      |
| 9005 | SubClientIDRequired          | DATA2/4 write without `subClientId`                              | Pass positive int.                                                       |
| 9006 | AppNotRegistered             | No registration on file                                          | Operator: run `lacerte-mcp register` (CLI; NOT an MCP tool).             |
| 9007 | LoginExpired                  | 24h ticket lapsed (proactive)                                    | Operator: run `lacerte-mcp login`.                                       |
| 9008 | DetailWriteInvalid           | Client-side validation failed (Series/Code/Amount shape)         | Read per-field `reason`; fix and retry.                                  |
| 9009 | ContextNotSet                | No `lacerte_set_context` yet                                     | Call `lacerte_list_datapaths` → `lacerte_set_context`.                   |
| 9012 | UnknownFieldAccess           | Field absent from access map shard                               | `lacerte_field_access_map` to confirm what IS writable. Operator: dumper + `lacerte_reload_schema`. |
| 9013 | InvalidDataPath              | `dataPath` rejected by `PathValidation`                          | Pass absolute local path; OR set `LACERTE_ALLOW_UNC=1`.                  |
| 9014 | TaxYearTooNew                | `taxYear > DateTime.Today.Year + 1`                              | Pass current/prior year.                                                  |
| 9015 | FieldWriteInvalid            | `value` not a scalar (array/object rejected)                     | Pass string/number/bool/null. Use `lacerte_detail_write` for rows.       |
| 9016 | ContextMismatch              | `taxYear` / `readOnly` assertion didn't match active context     | Re-call `set_context` OR drop the assertion. Inspect `context.expected*` / `actual*`. |
| 9017 | InvalidArgument              | Per-argument parse/range/format failure                          | Read `context.reason`; do NOT change unrelated state.                    |
| 9019 | LiveReplaceIndexUnsupported  | `lacerte_detail_write` `replaceIndex` in LIVE mode               | Append the corrected row; delete the stale via future delete tool.        |
| 9020 | BulkBatchTooLarge            | `entries.Length > LACERTE_BULK_WRITE_MAX` (default 500)          | Chunk into ≤500 batches with a shared `idempotencyKey` prefix.            |
| 9021 | BulkBatchCountMismatch       | `expectedCount != entries.Length`                                | Fix one side; do NOT drop the assertion blindly.                          |
| 9101 | ReAuthRequired                | Reactive NotSignedIn (203) wrapper                               | Operator: `lacerte-mcp login`.                                            |
| 9102 | IdempotencyConflict          | Same key + different payload                                     | Pick a fresh key, OR replay the original payload.                         |
| 9103 | InvalidAppId                  | AppID failed bridge-boundary shape check                         | Operator: `lacerte-mcp unregister` + `register` OR fix `LACERTE_APPID`.   |
| 9201 | BridgeUnavailable            | COM bridge didn't start                                          | Operator: confirm Windows + SDK; set `LACERTE_DEBUG_WIRE=1`; check log.   |
| 9202 | SimulationOnly                | Live-only op called in sim mode                                  | Set `LACERTE_LIVE=1` on Windows + Lacerte; OR stub in sim store.          |
| 9203 | InternalServerError          | Unexpected exception in the server                               | Capture `cid` + `context.exceptionType`; set `LACERTE_DEBUG_WIRE=1`; reproduce. |

For SDK codes, also check `lacerte_return_codes` for the full enum.

---

## Anti-patterns

- Don't construct raw SQL — tools never expose queries; use the read tools.
- Don't infer field codes — call `lacerte_field_access_map` to discover them.
- Don't ignore `cid` — it's the operator's traceability hook into the WireLog.
- Don't bypass `idempotencyKey` on writes that might be retried. MCP transport timeouts + LLM auto-retry are the default failure mode.
- Don't request all clients in one call when limit > 1000 — paginate (`Pagination.MaxLimit` enforces 1000).
- Don't write to DATA8 — reserved; rejected.
- Don't write to tables 6 or 7 — spec marks them read-only; throws `8 ExposedForRead`.
- Don't assume `lacerte_client_read` returns null on miss — it returns `{ clientNumber, found: false, client: null, hint: "..." }`. Check `found`, not `result`.
- Don't confuse `clientId` (operator-chosen, e.g. `"BROW"`) with `clientNumber` (SDK-minted, e.g. `"1001"`). The latter is the one to pass to every read/write tool.
- Don't reuse a single `idempotencyKey` across N different field writes in a loop — use distinct keys per field. Use the bulk tool with a single prefix instead; it auto-shapes per-row keys.
- Don't restart the MCP server to pick up new FieldAccessMap shards — call `lacerte_reload_schema` instead. Restarting invalidates the Intuit 24h auth ticket.
- Don't try to e-file from this MCP — read-only via `lacerte_ef_status`. Submission/correction is done in the Lacerte UI.

---

## When in doubt

- `lacerte_help(topic?)` — README-equivalent text inline.
- `lacerte_return_codes()` — full enum of all 28+5+13 codes.
- `lacerte_field_access_map(module, table, taxYear)` — what's writable.
- `lacerte_recent_log(cid)` — recent log lines for a correlation ID (always-on ring; doesn't need `LACERTE_DEBUG_WIRE`).
- `lacerte_session_status()` — current mode / registration / context / wire-log state, with `nextSteps` bootstrap guidance.
- `lacerte_request_stats()` — process-lifetime aggregate counters when "is the server healthy?".
- `lacerte_prompts()` — the 4 canonical workflow recipes.
- The skill body sketch (this file) is the single source of truth for conventions; if you find drift, prefer this skill's guidance.

---

## Versioning + back-compat note

This is v0.1.x pre-release. Wire conventions canonicalized in P16/P17/P18 (1301 tests passing, 0 failures). Legacy domain aliases for collection keys (`clients` / `forms` / `statuses` / `subclients` / `states` / `stateSubclients` / `datapaths` / `modules` / `efStatuses` / `codes`) are kept until v0.2 but `rows` is the durable name — prefer it.

Phase 9 caveat: in v0.1.x, `lacerte-mcp register` and `lacerte-mcp login` mint/store the AppID but the Intuit consent + sign-in ceremony is a stub (exits 3 with `[partial]`). Until Phase 9 ships, live mode (`LACERTE_LIVE=1`) cannot complete the registration handshake — simulation mode works fully. If the operator's box errors at registration with exit 3, that's the expected v0.1.x state, not a failure.

`ModelContextProtocol` SDK is currently `0.3.0-preview.4`; tracked as F9 for the 1.x bump. Adding `WithResourcesFromAssembly()` is out of scope until then — `lacerte_export_client` ships as a tool with an explicit `uri` field rather than as a true MCP Resource for that reason.

If a tool definition in this skill drifts from the source code, the source code wins. Always verify by running `lacerte-mcp tools --name <tool>` if you suspect drift.
