# Handoff State

## Last Session Summary (2026-06-11) — Phase 20: deferred-item closure (R6-D2 + F9)

Goal was "assess and confirm complete; close everything still doable on this box." Round-6 state re-verified first (build 0/0, 1492 green, CLI/server smoke), then the two non-Windows-gated deferred items were closed. Net: **1492 → 1506 tests passing, 0 warnings, 0 failures** — verified across **3 consecutive full-suite runs** plus isolated re-runs.

### What changed (read these to understand the diff)

1. **R6-D2 closed — explicit-context `*At` read overloads, ContextScanGate deleted.**
   `LacerteSessionManager` now has `ListClientsAt` / `ReadClientAt` / `ReadDetailAt` / `ReadEFStatusAt` — each takes `(dataPath, taxYear, module)` per call, resolves a read-only per-call `SessionContext`, and never touches the global `Context`. Validation is the shared `ValidateContextTuple` (extracted from `SetContext`) so error parity is exact. `lacerte_firm_dashboard` and `lacerte_year_over_year` read exclusively through them; the snapshot/set/restore shims and `Tools/Internal/ContextScanGate.cs` are **gone**. The R6-09 limitation (concurrent non-scan tool sees a scan-flipped read-only tuple → spurious 9001) is structurally impossible now. The `*At` surface is **reads-only by design** — do not add `WriteFieldAt` (see DECISIONS.md 2026-06-11).
2. **F9 closed — MCP SDK `ModelContextProtocol` 0.3.0-preview.4 → 1.4.0** (1.x finally shipped on NuGet; netstandard2.0 TFM, net48-clean). `System.Text.Json` 8.0.6 → 10.0.7 in Server + FieldAccessDumper (NU1605 floor from MCP.Core). Server-code API impact zero; test harness renames only (`IMcpClient`→`McpClient`, `McpClientFactory.CreateAsync`→`McpClient.CreateAsync`).
3. **HeartbeatWriter StartAsync override (fallout of #2).** Hosting.Abstractions 10.x schedules `BackgroundService.ExecuteAsync` asynchronously — it no longer runs synchronously to the first await inside `StartAsync`, which silently broke the "heartbeat file on disk by the time host startup returns" contract (caught by the suite). Startup sweep + immediate `TryWrite` moved into a `StartAsync` override. **Audit any future BackgroundService for the same assumption.**
4. **Pre-existing test flake fixed** in `FieldAccessMapLoaderP18AtomicityTests.ReloadCaches_AtomicSwap_NoBlendedSnapshot_VisibleToReader`: under full-suite thread-pool pressure the reloader could finish (and set `stopReader`) before the reader task ever got scheduled → zero observations → NotEmpty/Contains failures. ~1-in-5 full-suite runs on this box. Reloader now waits on a `ManualResetEventSlim` set by the reader's first observation. 0-failures across all subsequent runs.
5. **Docs synced:** README dashboard row, SKILL.md (pure-read wording, SDK 1.4.0, 1506 count, plus a latent drift fix: `summary.totalModulesScanned` → `totalTuplesScanned` per R6-33b), INSTRUCTION_MANUAL §7b/§7d + glossary, ARCHITECTURE stack line, DECISIONS.md two new 2026-06-11 entries, Todo.md Phase 20.

### New tests (14 net)

- `Tests/Session/SessionManagerAtOverloadsTests.cs` (13) — parity with active-context reads, context isolation (null stays null; `Assert.Same` reference pin), SetContext-identical validation (9003/TaxYearTooNew/24/9009-blank-path/9013-relative-path), NotSignedIn gate.
- FirmDashboard/YoY tests replaced gate assertions with `Assert.Same` context-isolation pins + a new "dashboard runs with NO active context" test.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report **0 warnings, 0 errors**.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **1506 passed, 11 skipped, 0 failed**. Skips: 9 `[Fact(Skip="Requires real Lacerte SDK")]` in Tests/Bridge/ + 1 Credential Manager P/Invoke in Tests/Cli/AppIdStoreTests.cs + 1 `LiveLegParityPlaceholder` (flips green when a recording lands under `Tests/Parity/fixtures/`).
- [ ] **CLI smoke:** `src/LacertMCP.Cli/bin/x64/Debug/net48/lacerte-mcp.exe status` → exit 0; `tools --names-only` → **42 names**, exit 0; `doctor` → exit 1 on this box (red SDK/ProgID/AppID probes — no Lacerte installed), exit 0 only on a green Windows+Lacerte box, exit 2 when only warns remain.
- [ ] **MCP server smoke:** `src/LacertMCP.Server/bin/x64/Debug/net48/LacertMCP.Server.exe` boots (`[startup] LacertMCP 0.1.0 mode=simulation ...` on stderr), exits cleanly on stdin EOF. (Verified on SDK 1.4.0 this session.)

## Next Task

**Everything sim-verifiable is done — including all previously deferred sim-doable items.** The ONLY remaining work requires the operator's Windows + Lacerte box (single focused session):

1. Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification" — includes the register/login ceremony items (AccessGranted exit 0 / ConsentDenied exit 1 / SDK-missing exit 2) and the round-6 bulk/query tool items.
2. `lacerte-mcp register` — mint AppID, click through Intuit dialogs; capture actual integers for `AccessGranted`/`NotSignedIn` (closes F1 + F7).
3. `LacertMCP.FieldAccessDumper.exe --year <yyyy>` on the live box, then `lacerte_reload_schema`.
4. Capture `Tests/Parity/fixtures/live-ty24-individual.json` recording (flips the parity placeholder + 29 live-leg rows green).
5. Tag `v0.1.0` after first all-green pass; record the 60-second walkthrough (Todo #87/#88).

Deferred (Windows-gated only now): **R6-D1** (live replaceIndex / detail delete — blocked on live SDK verification of the append-only contract). F2/F4 likewise live-box items.

NOTE: this session's changes are **uncommitted** in git (prior sessions committed at milestones — `0220bae` is the last commit). Suggest committing as "phase 20: R6-D2 *At overloads + MCP SDK 1.4.0 (F9)".

## Context Notes

### Wire-surface conventions (canonical — unchanged from round 6)

camelCase `taxYear`/`stateAbbrev`/`clientId`; write tools use `field`; collection key `rows`; lowercase mode strings; `cid` correlation; `module` = single-char string + `moduleName` friendly; paginated envelopes `total`/`limit`/`returnedCount`/`hasMore`/`nextOffset`; bulk envelopes `requestedCount`/`processedCount`/`succeededCount`/`failedCount`/`skippedCount`/`stoppedEarly`; idempotency = unkeyed SHA-256 fingerprints incl. (dataPath, taxYear, module, dryRun), Success-only caching, positional per-row bulk keys, preview/commit prefix split (`bulk-<batchId>-preview` / `bulk-<batchId>`).

NEW: `lacerte_firm_dashboard` summary key is `totalTuplesScanned` (R6-33b — SKILL.md had drifted; fixed). Dashboard requires no active context at all now.

### Gotchas (carry-forward, updated)

- Use `$env:USERPROFILE\.dotnet\dotnet.exe` — system-wide `dotnet` is runtime-only.
- CLI/server binaries live under `bin/x64/Debug/net48/`.
- **MCP SDK is 1.4.0** (F9 done). Client-side test code uses `McpClient.CreateAsync` (no `IMcpClient`/`McpClientFactory`). STJ pinned 10.0.7 — don't downgrade below MCP.Core's floor or NU1605 fails the restore (warnings-as-errors).
- **Hosting.Abstractions 10.x runs `BackgroundService.ExecuteAsync` asynchronously** — anything that must complete by end-of-host-startup belongs in a `StartAsync` override (see HeartbeatWriter).
- Reads that must not disturb the operator's context: use the session manager `*At` overloads. They are reads-only by design; writes stay on the active-context path.
- `LACERTE_IDEMPOTENCY_PERSIST` defaults ON; `=0` disables. There is NO `LACERTE_IDEMPOTENCY_TTL_SECONDS` (TTL hardcoded 24h).
- `LACERTE_DEBUG_WIRE=1` activates wire log; disk scope scrubs `sql`, name filters, and non-anchor `C{t}_N` codes. The `*At` overloads log under their own op names with the tuple in args.
- FieldHistoryToolsTests seed log files via `WriteLogLinesWithRetry` — background WireLog threads can transiently hold the day-file handle.
- **NEVER mutate `LACERTE_DATA_ROOT`/`LACERTE_DATA_PATH` process env in tests** — use the `DoctorCommand.RunAllProbes(envOverrides)` seam or join `DataRootEnvCollection`. (The `*At` overloads still run `PathValidation` against `LACERTE_DATA_ROOT` at call time — same discipline applies; `SessionManagerAtOverloadsTests` joined `DataRootEnvCollection`.)
- `NamingConventionsTests` sentinels: no `year` keys (YoY), single-char `module` values, `returnedCount` on all 9 paginated envelopes. `HelpToolsTests` parity is bidirectional (42 tools).
- Tests use `[Collection("AccessMapMutation")]` for access-map/env mutation and `DataRootEnvCollection` for SetContext path races.
- Concurrency tests that spawn reader/writer task pairs must handshake start order (see the P20-005 fix) — a `Task.Run` body may not get scheduled for many ms under full-suite load.

### How to /pickup this session

1. Read this HANDOFF.md, then `Todo.md` Phase 20, then `DECISIONS.md` 2026-06-11 entries.
2. Run the Verify Before Continuing checks.
3. The only open work is Windows+Lacerte-gated (above). If on the operator's live box, start at "Next Task" step 1.
