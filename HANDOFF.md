# Handoff State

## Last Session Summary (2026-06-10) — Round 6: fresh audit + full fix cycle + tool completion

After five prior rounds claimed saturation, a fresh 5-lens sweep (tools layer, session/simulation, bridge/ODBC, coverage gaps, wiring/CLI/prompts) confirmed **42 new findings** including 5 criticals — all recorded in [`audit-findings-r6.md`](audit-findings-r6.md) and **all fixed the same day**, plus 4 new tools and a module expansion. Net: **1301 → 1492 tests passing, 0 warnings, 0 failures.**

### The 5 criticals (all fixed — read these to understand what changed)

1. **R6-01** — Idempotency disk persistence was structurally dead: fingerprints were HMAC'd with a per-process random key, so every real restart turned byte-identical replays into 9102 conflicts. Fingerprints are now **unkeyed SHA-256** (stable across processes); a true cross-restart test (fingerprint hand-computed outside the manager) now pins replay-after-restart.
2. **R6-02** — The canonical bulk-write prompt instructed reusing the SAME idempotency prefix for dry-run preview and commit, which post-P18 guarantees 9102 at row 0. Convention now: preview = `bulk-<batchId>-preview`, commit = `bulk-<batchId>` — enforced in prompt text, recoveryHint, and SKILL/manual docs.
3. **R6-03** — Idempotency fingerprints omitted session context; same key after a `lacerte_set_context` switch silently replayed the old write. `ctx.DataPath/Year/Module` now participate in all 3 `BuildPayload` sites.
4. **R6-04** — Live mode cached non-Success TAPIStatus results, making transient failures sticky for 24h. `StoreIdempotent` is now Success-gated at all 3 sites.
5. **R6-05** — `lacerte-mcp register`/`login` consent ceremony was never wired (exited 3 `[partial]` referencing closed task #63). Both now drive `Bridge/Registration.Register` via an injectable seam. **Exit 3 is retired**: 0 = AccessGranted (or off-Windows `[skipped]`), 1 = denied/other status/error, 2 = PARTIAL (SDK unavailable, AppID persisted, points at doctor).

### Other behavior changes a future session must know

- **`LACERTE_IDEMPOTENCY_PERSIST` is now DEFAULT ON** (disable with `=0`). Persistence is multi-instance safe: per-PID tmp files, merge-on-write (in-memory wins on collision), orphan-tmp cleanup only for own-PID/legacy/>1h-old.
- **Doctor Warn now exits 2 (PARTIAL)**, prints Warn remediation in human output. On this box (Windows, no Lacerte SDK) doctor exits 1 (FAIL) on the red SDK/ProgID/AppID probes — expected.
- **Bulk per-row idempotency keys are positional**: `{prefix}-{index}-{clientNumber}-{field}` (and analogues for the new bulk tools). Repeated client+field pairs in one batch no longer alias.
- **Sim/live dryRun CreateClient parity**: both return `clientNumber: null` on dryRun; CreateClient chokepoint now has `RequireRegistered()`.
- **`module` wire key is single-char everywhere** (`"I"`); friendly name moved to `moduleName`. All 9 paginated envelopes emit `returnedCount`. `year_over_year` gained `foundInFromYear`/`foundInToYear` and uses `taxYear` keys (sentinel-tested).
- **WireLog disk scope** now scrubs `sql`/`lastNameFilter`/`nameFilter` and every raw `C{t}_N` column code except the `C{t}_0` anchor. Wire scope unchanged.
- **firm_dashboard / year_over_year** serialize their context-mutating scans via `Tools/Internal/ContextScanGate` — the real fix (session-manager `*At` read overloads) is deferred (R6-D2 in Todo.md Phase 19).
- **HeartbeatWriter**: unparseable env disables (was: defaulted 30), interval clamped to 86400 (was: >2147483 crashed the host), per-PID file deleted on graceful stop, stale siblings >24h swept at startup.
- **Live `WriteDetail`/`CreateClient` retry policy** is now documented in DECISIONS.md (2026-06-10 entry) — the entry the code comments had been citing without it existing.

### New tools (42 total now; HelpTools catalog + bidirectional parity test enforce the count)

- **`lacerte_client_create_bulk`** — bulk client create, cap 100 via `LACERTE_BULK_CREATE_MAX`, dryRun/continueOnError/positional per-row keys (REQUIREMENTS #3 CSV-intake bootstrap).
- **`lacerte_detail_write_bulk`** — bulk detail rows, shares `LACERTE_BULK_WRITE_MAX` (500), per-row 9008 validation, replay-safe against duplicate appends.
- **`lacerte_client_query`** — server-side filtered search over DATA1-7 (`eq/ne/contains/startsWith/empty/notEmpty`), standard pagination; answers "all clients missing email" without paging rosters through the LLM context.
- **`lacerte_pre_filing_check`** expanded from Individual-only to **I/P/C/S** with per-rule `skipped` reporting (partners-present rule for 1065; entity-name vs first/last-name rules per module).

### Docs

README tool table (42 rows), SKILL.md (catalog + counts + idempotency/anti-pattern sections), INSTRUCTION_MANUAL.md (new 7b/7c/7d subsections, register/login/doctor exit codes, §16 idempotency rewrite, env table), REGRESSION_CHECKLIST.md (ceremony + round-6 tool items) — all synced to code this session. `.env.example` now carries all read env vars including `LACERTE_BULK_CREATE_MAX`.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report **0 warnings, 0 errors**.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **1492 passed, 11 skipped, 0 failed**. Skips: 9 `[Fact(Skip="Requires real Lacerte SDK")]` in Tests/Bridge/ + 1 Credential Manager P/Invoke in Tests/Cli/AppIdStoreTests.cs + 1 `LiveLegParityPlaceholder` (flips green when a recording lands under `Tests/Parity/fixtures/`).
- [ ] **CLI smoke:** `src/LacertMCP.Cli/bin/x64/Debug/net48/lacerte-mcp.exe status` → exit 0; `tools --names-only` → **42 names**, exit 0; `doctor` → exit 1 on this box (red SDK/ProgID/AppID probes — no Lacerte installed), exit 0 only on a green Windows+Lacerte box, exit 2 when only warns remain.
- [ ] **MCP server smoke:** `src/LacertMCP.Server/bin/x64/Debug/net48/LacertMCP.Server.exe` boots (`[startup] LacertMCP 0.1.0 mode=simulation ...` on stderr), exits cleanly on stdin EOF.

## Next Task

All sim-verifiable work is done. The remaining real work is Windows+Lacerte-gated (single focused session on the operator's box):

1. Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification" — now includes the register/login ceremony items (AccessGranted exit 0 / ConsentDenied exit 1 / SDK-missing exit 2) and the round-6 bulk/query tool items.
2. `lacerte-mcp register` — mint AppID, click through Intuit dialogs; capture actual integers for `AccessGranted`/`NotSignedIn` (closes F1 + F7).
3. `LacertMCP.FieldAccessDumper.exe --year <yyyy>` on the live box, then `lacerte_reload_schema`.
4. Capture `Tests/Parity/fixtures/live-ty24-individual.json` recording (flips the parity placeholder + 29 live-leg rows green).
5. Tag `v0.1.0` after first all-green pass; record the 60-second walkthrough (Todo #87/#88).

Deferred items: **R6-D1** (live replaceIndex / detail delete — blocked on live SDK verification), **R6-D2** (session `*At` overloads to retire ContextScanGate), **F9** (MCP SDK 1.x NuGet bump when it ships).

## Context Notes

### Wire-surface conventions (canonical as of round 6)

Everything from round 3 still holds (camelCase `taxYear`/`stateAbbrev`/`clientId`; write tools use `field`; collection key `rows`; lowercase mode strings; `cid` correlation), plus:
- `module` = single-char string everywhere; `moduleName` = friendly name.
- Paginated envelopes: `total`, `limit`, `returnedCount`, `hasMore`, `nextOffset`.
- Bulk envelopes: `requestedCount`/`processedCount`/`succeededCount`/`failedCount`/`skippedCount`/`stoppedEarly`, per-row `succeeded`.
- Idempotency: SHA-256 fingerprints including (dataPath, taxYear, module, dryRun); Success-only caching; positional per-row bulk keys; preview/commit prefix split.

### Gotchas (carry-forward, updated)

- Use `$env:USERPROFILE\.dotnet\dotnet.exe` — system-wide `dotnet` is runtime-only.
- CLI/server binaries live under `bin/x64/Debug/net48/` (the HANDOFF used to omit `net48`).
- `LACERTE_IDEMPOTENCY_PERSIST` defaults ON now; `=0` disables. There is NO `LACERTE_IDEMPOTENCY_TTL_SECONDS` (TTL is hardcoded 24h — the round-5 handoff invented that var).
- `LACERTE_DEBUG_WIRE=1` activates wire log; disk scope now also scrubs `sql`, name filters, and non-anchor `C{t}_N` codes.
- FieldHistoryToolsTests seed log files via `WriteLogLinesWithRetry` — background WireLog threads can transiently hold the day-file handle; if you add tests there, use the helper.
- **NEVER mutate `LACERTE_DATA_ROOT`/`LACERTE_DATA_PATH` process env in tests** — SetContext in concurrently-running collections reads them at call time and your mutation fails THEIR tests (this was an intermittent 1-in-4 suite flake). Use the `DoctorCommand.RunAllProbes(envOverrides)` seam (R6 flake fix) or join `DataRootEnvCollection`.
- `NamingConventionsTests` sentinels now also cover: no `year` keys (YoY), single-char `module` values, `returnedCount` presence on all 9 paginated envelopes. New tools that violate these fail the suite.
- `HelpToolsTests` parity is bidirectional — registering a new `[McpServerTool]` without adding it to `HelpTools.ToolsByCategory()` fails the suite.
- Tests use `[Collection("AccessMapMutation")]` for access-map/env mutation and `DataRootEnvCollection` for SetContext path races; bulk-tool tests that touch `LACERTE_BULK_WRITE_MAX` join `AccessMapMutationCollection`.
- MCP SDK is still `0.3.0-preview.4` (F9).

### How to /pickup this session

1. Read this HANDOFF.md, then `Todo.md` Phase 19, then `audit-findings-r6.md` for the full finding-by-finding record.
2. Run the Verify Before Continuing checks.
3. The only open work is Windows+Lacerte-gated (above) or deferred (R6-D1/D2/F9).
