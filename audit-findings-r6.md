# Audit Round 6 — Findings (2026-06-10)

> **STATUS: ALL FIXED (same day).** Every finding below (R6-01..R6-42) was fixed in the round-6 execute wave, plus 4 new tools shipped (`lacerte_client_create_bulk`, `lacerte_detail_write_bulk`, `lacerte_client_query`, register/login ceremony wiring) and `lacerte_pre_filing_check` expanded to I/P/C/S. Two items deferred with rationale: live replaceIndex correction (R6-D1) and session `*At` overloads (R6-D2) — see Todo.md Phase 19. Final state: 1492 passed / 11 skipped / 0 failed, 0 warnings. See HANDOFF.md for the behavior-change summary.

Fresh sweep after the 5 prior rounds (287 tasks). Five parallel review passes: Tools layer, Session/Simulation, Bridge/ODBC, tool-coverage gaps, and server wiring/CLI/prompts. Build verified clean (0 errors, 12 xUnit1031 warnings in test code only); tests verified **1301 passed / 11 skipped / 0 failed**.

Severity legend: **P1** = data-integrity or workflow-blocking; **P2** = wrong results or operator-misleading; **P3** = polish/latent/doc drift.

---

## P1 — Critical

### R6-01. Idempotency disk persistence (P17-004) is structurally dead across real restarts
`Session\LacerteSessionManager.cs:2873-2903, 2709, 2538, 2209`
P17-007 made payload fingerprints HMAC-SHA256 with a **per-process random key** (`s_idempotencyHmacKey`). P17-004 persists fingerprints to disk and reloads them. After a real restart the key regenerates, so a byte-identical retry computes a different fingerprint → **9102 IdempotencyConflict** instead of replay. Remediation text then tells the operator to "pick a fresh key", which re-executes the write — producing the exact duplicate row the feature exists to prevent.
*Why tests pass:* `SessionManagerP17Tests.IdempotencyPersistence_OptIn_RoundTripsAcrossProcessRestart` runs two managers in the **same process**, sharing the static key.
*Fix:* persist the HMAC key alongside the cache (DPAPI-protect if desired), or use unkeyed SHA-256 for the persisted comparison and keep HMAC only for the wire-visible prefix gating.

### R6-02. Bulk-write prompt Step 5 is a guaranteed 9102 deadlock post-P18
`Prompts\WorkflowPrompts.cs:238-250`
Step 4 says dry-run with key `bulk-<batchId>`; Step 5 says reuse the **same prefix** so "previewed rows are skipped automatically." That describes the pre-P18 cache-poisoning bug as a feature. Since `dryRun` is now in the fingerprint and dry-run results ARE stored, Step 5's first row throws 9102; with the prompt-mandated `continueOnError:false` the entire real write stops at row 0. The canonical workflow can never complete as written.
*Related:* `FieldWriteBulkTools.cs:419` `recoveryHint` still claims "Dry-run mode bypasses the idempotency cache" — false.
*Fix:* prompt + hint must mandate a fresh key prefix for the commit pass.

### R6-03. Idempotency fingerprint omits session context (dataPath/taxYear/module)
`LacerteSessionManager.cs:1196-1198 (WriteField), 1355-1357 (WriteDetail), 1502-1504 (CreateClient)`
The recommended stable-key pattern (verbatim in `DetailsTools.cs:128` description) plus a `lacerte_set_context` year/module switch yields a **silent cached replay**: Success returned, wire never fires, the new-context write never lands. CreateClient: same key across modules replays module-I result; no client created in module C.
*Fix:* thread `ctx.DataPath/Year/Module` into all three `BuildPayload` calls. Same class as the P18 dryRun fix, one parameter family over.

### R6-04. Live mode caches non-Success results in the idempotency cache — failures sticky for 24h
`LacerteSessionManager.cs:1323, 1467, 1608`
`StoreIdempotent` is unconditional. Sim throws on failure so only Success is ever stored; live bridge **returns** raw status ints (e.g. `DBFNotAccessable (2)` when the return is open in the Lacerte UI). The failure gets cached under the key; the operator closes the file, retries same key + same payload → replays the cached **failure** without touching the wire. Retry can never succeed under that key.
*Fix:* gate `StoreIdempotent` on `result == Success` (and `CreateClientResult.Status == Success`).

### R6-05. `lacerte-mcp register` consent ceremony is unwired and untracked — hard blocker for first live use
`Cli\Commands\RegisterCommand.cs:114-127`
Exits 3 with "[partial] AppID minted, but the Intuit consent ceremony is not yet wired into the CLI (Phase 9 task #63)". Todo #63 is checked but only shipped server-side `Bridge/Registration.cs`; no open Todo line covers the CLI wiring. Today **no surface, MCP or CLI, can complete registration** — everything live is blocked behind it.
*Fix:* re-open as a tracked task; wire `Bridge/Registration` into the CLI command.

---

## P2 — High (wrong results / misleading)

### R6-06. `lacerte_diff_clients` detail diff: systematic false negatives
`Tools\ClientDiffTools.cs:363-379, 325-345, 250-264`
(a) `DetailRowsEqual` compares only 5 of 11 `DetailRow` fields — rows differing only in **TSJ (taxpayer vs spouse)**, Source, Prefix, Suffix, CityAbbr, or ImportSrc report "no differences", and `ShapeDetailRow` can't surface them either. (b) `ToLookup` + `.First()` makes duplicate-key multiplicity invisible (two identical W-2 rows vs one → no diff). (c) Rows with null key silently vanish from all three buckets. (d) XML doc has added/removed backwards vs code+tests.

### R6-07. `lacerte_year_over_year`: nonexistent client ⇒ confident "no changes"
`Tools\YearOverYearTools.cs:327-340`
`ReadClient` returning null records no error; client missing in BOTH years → `demographicsDiff=[], errors=[]`. A typo'd clientNumber reads as "nothing changed between 2023 and 2024". No `foundInFromYear`/`foundInToYear` flags.
Also: **`year` wire-key regression** in error rows (lines 192-197, 210-215, 238-244, 334-339, 367-372) violating the pinned `taxYear` convention — no sentinel covers this file; and self-contradicting scope validation message (says "lowercase" while requiring camelCase `detailTotals`, Ordinal match).

### R6-08. `lacerte_help` omits 9 of 38 registered tools
`Tools\HelpTools.cs:185-237`
The entire round-5 analysis + observability surface (`diff_clients`, `pre_filing_check`, `year_over_year`, `field_history`, `recent_log`, `request_stats`, `cache_state`, `resource_usage`, `active_locks`) is missing from `ToolsByCategory()`; `toolCount` reports 29. The sentinel only checks listed ⊆ registered, not the reverse.

### R6-09. FirmDashboard / YearOverYear mutate global context across awaits with no exclusion
`Tools\FirmDashboardTools.cs:152, 274-291; YearOverYearTools.cs:323`
`SetContext(..., readOnly:true)` inside an await-laced fan-out loop; any concurrent tool call reads/writes whatever tuple the dashboard last set (wrong year/module reads; concurrent `field_write` fails 9001 FieldIsReadOnly). The "observationally identical" class comment only holds single-threaded.

### R6-10. Sim `WriteDetail` auto-creates detail lists for nonexistent clients
`LacerteSessionManager.cs:1871`
`module.Details.GetOrAdd(clientNumber, ...)` on the actual tool path returns **Success** for a typo'd clientNumber (live returns DetailCreationError; `SimulationStore.WriteDetail` — the tested contract — also returns DetailCreationError but is bypassed). Also seeds a permanent orphan list that subsequent reads return. Two-mode discipline violation.

### R6-11. Multi-instance idempotency persistence clobber
`LacerteSessionManager.cs:2426-2451, 2603-2652, 2494-2516`
Fixed shared path + full-cache `File.Replace`: with Claude Desktop + Cursor both running (the scenario P17-014 explicitly handles for heartbeat), each persist erases the other instance's entries; shared `.tmp` races; startup deletes the other process's in-flight tmp. After restart, replay protection for the other instance's writes is silently gone → duplicate rows on a live return.

### R6-12. Disk-log PII gap: raw SQL with name literals bypasses redaction
`Odbc\LacerteDsnReader.cs:134; Logging\WireLog.cs:199-228, 285-298; Odbc\SqlTemplates.cs:91-97`
Key `"sql"` is in neither redaction keyset, so `... where C1_2 like 'Smith%'` lands verbatim in the on-disk debug log. `_piiFieldCodes` covers only 10 DATA1-I codes; raw-column-keyed dict rows (`C1_1`/`C1_2`, address codes, entity-module equivalents) leak identity fields the friendly keys would scrub. Latent until ODBC wiring, then automatic.

### R6-13. PreFilingCheck: unknown client throws SDK `OutOfIndex (4)` with nonsense remediation
`Tools\PreFilingCheckTools.cs:107-115`
Every sibling returns `{found:false, hint}`; this one throws an SDK-origin code whose catalog text is about pre-TY21 row writes. Also module-gate ordering (82-102) makes response shape depend on module for identical bad inputs.

### R6-14. RCW leaks: `CreateClient` and `Registration.Register` never release COM objects
`Bridge\LacerteAccessClient.cs:261-266; Bridge\Registration.cs:61-107`
Both violate the project's own P14-004 per-call `Marshal.ReleaseComObject` pattern (applied in Writer/Reader/CanUseNewApisProbe). AutoReconnect retries multiply exposure (up to 4 instantiations per register; re-register every 24h).

### R6-15. Live `ListClients` can never work as written (latent)
`Bridge\LacerteAccessClient.cs:113-114` vs `BridgeInputValidation.cs:51-53`
Passes bare `"DATA1"` to a validator requiring `NAME.EXT` shape → always throws DBNotSupported with a misleading remediation; `year`/`module` params discarded. Latent (live reads unwired) but this is the implementation the wiring phase will call.

### R6-16. Git-SHA stamp (P17-009) double-appends — broken locally and in CI
`Directory.Build.props:30-41; ci.yml:52,98; release.ps1:53`
SDK's `AddSourceRevisionToInformationalVersion` appends `.$(full-40-char-sha)` on top of the props-level `+sha7`. Verified in today's local AssemblyInfo.cs artifacts. Flows into ServerInfo.Version, heartbeat, doctor.
*Fix:* `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`.

### R6-17. `LACERTE_HEARTBEAT_SECONDS` > 2,147,483 crashes the whole server
`Diagnostics\HeartbeatWriter.cs:147-166`
`Task.Delay(TimeSpan)` throws ArgumentOutOfRangeException for >Int32.MaxValue ms; only TaskCanceledException is caught; Hosting 8.0.1 default StopHost kills the host on first loop iteration. Also: unparseable value **enables** (default 30) while Program.cs:69-71 and the class doc claim it disables.

---

## P3 — Medium/Low (selected; full agent detail retained in session transcript)

- **R6-18** `module` wire key has 3 value domains: `"I"` (SessionTools, FirmDashboard), `"individual"` (ClientSummaryTools.cs:119,216), file basename (ClientResourceTools.cs:166). Cross-tool join impossible for an LLM.
- **R6-19** `returnedCount` documented in `Pagination.cs` (P16-074) but **never emitted** by any paginated tool envelope.
- **R6-20** Bulk per-row idempotency key has no index component (`FieldWriteBulkTools.cs:322-324`) — two same-(client,field) entries in one batch alias → 9102 or silent replay. No CancellationToken on bulk write or detail write (mutations continue after MCP cancel).
- **R6-21** `lacerte_field_history`: truncation keeps OLDEST rows with no offset param (newest writes unreachable); no limit clamp; failed writes surface indistinguishable from successes (`statusCode:null`).
- **R6-22** Live dry-run divergences: CreateClient dryRun returns minted ClientNumber in sim vs null in live (LacerteSessionManager.cs:1565 vs 1594); live dryRun CreateClient skips registration gate (1559-1566) while sim throws NotSignedIn.
- **R6-23** P18-022 DBF-lock latch dead on the real tool path for field/detail writes (manager sim adapters never call `IsTableLocked`; only CreateClient honors it).
- **R6-24** `_clientWriteLocks` keyed Ordinal vs OrdinalIgnoreCase client maps (LacerteSessionManager.cs:253-254) — differently-cased clientNumbers get different locks, voiding P18 serialization. Latent in sim.
- **R6-25** `SessionStatus()` reports `IsRegistered:true` after 24h expiry until a gated call flips it.
- **R6-26** `LACERTE_IDEMPOTENCY_PERSIST` default contradicts doc-block (code: opt-in `=1`; comments at 167, 2408-2409 say default ON / disable with `=0`). HANDOFF.md absorbed the wrong version.
- **R6-27** AutoReconnect whitelists `0x80010001` labeled RPC_E_CALL_CANCELED — it's actually RPC_E_CALL_REJECTED; real CANCELED (`0x80010002`) is not retried. Resolve intent.
- **R6-28** `lacerte-mcp field-access set` accepts lowercase field names and writes a permanently dead shard row (loader looks up uppercase) — the exact failure P16-021 claimed to fix. `field-access list` universal-rule message wrong twice (`I1_0` example is Unknown, not ReadOnly).
- **R6-29** Doctor: Warn affects neither exit code nor prints remediation in human output; in-code comment claims Warn → exit 2. Warn-only box exits 0 "all probes green" with visible ⚠ rows.
- **R6-30** EnvValidator treats `LACERTE_PARITY_RECORDING` (a path) as a strict boolean → false warning on documented usage. Also misses `LACERTE_IDEMPOTENCY_PERSIST`, `LACERTE_DEBUG_LOG_FULL_RESULTS`, and validates no numeric vars.
- **R6-31** `.env.example` omits 13 env vars the code reads; heartbeat path stale (pre-P17-014, also in README:155). Note: `LACERTE_IDEMPOTENCY_TTL_SECONDS` (named in r5 HANDOFF) does not exist in code — TTL hardcoded 24h.
- **R6-32** ClientSummary: `perStateBreakdownTruncated` computed against pre-filter count; latest-status fallback picks first (not last) undateable row.
- **R6-33** FirmDashboard watermark uses `CompareOrdinal` on dates while ClientSummary parses — tools can disagree on "latest" for the same data; error-row shapes differ (`{year,code,reason}` vs `{statusCode,statusName,reason}`).
- **R6-34** WireLog double-logs every write (outer Bridge.* wrapper + inner Writer record) and the outer wrapper logs rejected args, defeating Writer's validate-before-log contract (`LacerteAccessClient.cs:169-218`).
- **R6-35** Heartbeat files for dead PIDs never cleaned up (no delete on shutdown).
- **R6-36** `LacerteDsnReader.EnsureOpen` mishandles `ConnectionState.Broken` (needs Close() first); OdbcErrorMapper routes all `HY*` (incl. timeouts HYT00/HYT01, cancel HY008) to CodeBaseError "repair indexes" + needless connection recycle.
- **R6-37** `SqlTemplates.Data1TopWhereName` doesn't escape LIKE wildcards `%`/`_` (no injection, but sim/live filter divergence).
- **R6-38** `lacerte-mcp tools` lists `cancellationToken?: CancellationToken` params the MCP wire schema excludes.
- **R6-39** FieldAccess discover hint points at dumper flags the dumper rejects; dumper exits 0 on unknown args; dumper re-implements `Modules.FileBasenameFor` despite the dedup claim.
- **R6-40** Bulk prompt Step 2 omits required `taxYear` param of `lacerte_field_access_map`; `lacerte_review_ef_status` prompt ignores pagination (>100 filings → wrong bucket counts) and its `taxYear` arg can't be honored by row filtering.
- **R6-41** DECISIONS.md entry cited by three code comments (AutoReconnect append-only retry policy) does not exist.
- **R6-42** HANDOFF.md claims "0 warnings" — build shows 12 xUnit1031 warnings in `LacerteSessionManagerTests.cs` (blocking task ops in tests).

---

## Doc drift

- **README.md tool table is 15 tools behind code** (lists 24 of 39; missing every Phase 14-18 tool). Violates CLAUDE.md's add-a-row rule.
- **SKILL.md** entries match code exactly (all 39) but counts are wrong: header "38 tools" (actual 39), "Read (10)" lists 13, "Accelerators (7)" lists 5.
- No stale entries (nothing documented that doesn't exist).

## Missing tools (gap analysis vs spec + REQUIREMENTS)

Spec §4 coverage is essentially complete. Ranked recommendations:

1. **Finish `lacerte-mcp register` consent wiring** (R6-05) — blocks all live use.
2. **`lacerte_client_create_bulk`** — REQUIREMENTS #3 ("bootstrap clients from CSV") has only a prompt looping single creates; needs the bulk dry-run/idempotency/continueOnError pattern.
3. **`lacerte_client_query`** — filtered cross-client search ("all clients missing email"); today bulk-target selection pages full rosters through the context window.
4. **`lacerte_detail_write_bulk`** — intake seeds many W-2/interest/dependent rows; single-row detail_write is the slowest loop in the intake prompt.
5. **Live detail-row correction** — close `9019 LiveReplaceIndexUnsupported` (or add `lacerte_detail_delete`); without it a live operator can append a wrong amount but never fix it.

Honorable mention: expand `lacerte_pre_filing_check` beyond module I (solo pros file 1065/1120S).

Confirmed non-gaps (SDK has no API): e-file transmission, client deletion, return locking, PDF/print, appointments, preparer tables. DATA8 correctly rejected.

## Verified-clean areas

Pagination helper, AutoReconnect structure (no sync-over-async), PathValidation (P17-010/016), SQL identifier gating (no injection), WrapToolHandler cid/redaction, WireLog rotation/ring, schema shards + loader (all 11 valid; atomic swap correct), CredMan/file-store parity (P17-017), ConnectionStringBuilder, prompt tool-name/param-casing references (DuplicateClient now correctly 12), CLI snake_case + exit codes, ci.yml structure (except R6-16), Program.cs startup + tool auto-scan (all 24 tool classes resolvable).

## Round-5 fix verification

dryRun-in-fingerprint: landed (but see R6-03). AutoReconnect double-write: plumbing landed, DECISIONS entry missing (R6-41). DbfRow.Fields concurrent: landed. CreateClient counter CAS: landed, correct. File.Replace + orphan recovery: landed (narrow crash windows noted). P17-004 persistence: landed but opt-in-off and broken across real restarts (R6-01).
