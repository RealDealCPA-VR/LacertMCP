# Handoff State

## Last Session Summary (2026-11-15) — Phase 21: round-7 deep code review

Goal: a deep review of the whole project, checked against source and `Lacerte_MCP_Structure_Spec.md`, with every confirmed bug or gap fixed and no regressions. The review ran in five independent lanes:

- bridge / ODBC / errors, checked against the spec
- session layer
- core tools
- bulk and aggregate tools, plus logging
- CLI, startup and build

Each finding was re-verified against source before it was fixed. Net: **1506 → 1623 tests passing, 11 skipped, 0 failed (3 consecutive full-suite runs), build 0 warnings / 0 errors.** All work is on branch `review/r7-deep-review`. The full per-finding list is in `Todo.md` Phase 21, and the policy choices are in `DECISIONS.md` (seven 2026-11-15 entries).

### What changed (read these to understand the diff)

1. **Live/sim parity on writes.**
   - A live write the SDK rejects now raises a structured error. Previously it came back as `success:true, statusCode:2`.
   - Bridge content rules (length caps, control characters, NaN, non-scalar) now run in both modes, with corrected codes: 9015 for field writes, 24 for create-client text.
   - A live dry-run now fails wherever the commit would.
2. **PII.**
   - The diff and year-over-year tools no longer leak SSN/DOB/email/bank values through `a`/`b`/`fromValue`/`toValue`.
   - `lacerte_field_access_map` no longer masks the access labels of PII field codes.
3. **Idempotency.**
   - The backend mode is part of the fingerprint, and simulation uses its own cache file (`idempotency-cache.sim.json`).
   - The persist loop can no longer drop an entry; the cache is capped at load and at persist.
   - Eviction no longer disposes a semaphore that is in use.
4. **Concurrency.** Detail reads take a locked snapshot. Simulated client creates are serialized. The client list is ordered and never shows a dry-run client. Bulk rows are pinned to the batch's context (9016).
5. **Validation.**
   - `dataPath` is canonicalized; drive-relative and drive-less paths are rejected.
   - A blank `clientNumber` on single-client reads returns 9017; it used to return every client.
   - `clientNumber` is trimmed and `stateAbbrev` upper-cased on writes.
   - Regexes now use `\z` and `[0-9]`.
   - Bulk create rejects a batch containing the same clientId twice.
6. **Tool contracts.**
   - `ArgumentException` maps to 9017, and cancellation propagates.
   - `sinceUtc` uses the exact timestamp.
   - `client_find("John Smith")` works.
   - `set_context` falls back to `LACERTE_DEFAULT_TAX_YEAR`. Its `taxYear` parameter is now optional and comes after `module`.
   - An invalid default tax year is ignored instead of crashing startup.
   - `can_use_new_apis` validates year and module, and `field_access_map` enumerates the real catalog.
7. **Resilience.** `AutoReconnect` now looks inside the bridge's 9201 wrapper, so the retry policy actually runs in live mode. A failed `ClientNumber` read after a successful create is now best-effort.
8. **CLI / build.**
   - The CLI and dumper have the legacy-runtime `App.config` shim; doctor checks both config files.
   - The shard writers target the server's schema tree.
   - The doctor shard probe now warns when every shard is empty.
   - The Windows version is read correctly.
   - `unregister` can remove a corrupt AppID.
   - `--app-name` honors `LACERTE_APP_NAME`.
   - `release.ps1` stamps the tag version into the binaries.
   - The `build.ps1` / `test.ps1` `--` argument forwarding works.
9. **Logging.** Invariant-culture timestamps and file names; the in-memory ring stays bounded when full results are on.
10. **Guard test.** New `ToolsR7Tests.R734`: every `lacerte_*` reference in server source must resolve to a real tool or prompt. It caught two stale references the reviewers missed.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` → **0 warnings, 0 errors**.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` → **1623 passed, 11 skipped, 0 failed**.
- [ ] **CLI smoke:** `src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` → exit 0; `tools --names-only` → **42 names**; `doctor` → exit 1 on this box (no Lacerte installed). Note: since `0776766` there is **no `net48` subfolder** under `bin/x64/<Config>/`.
- [ ] **MCP server smoke:** `src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` boots (`[startup] LacertMCP 0.1.0 mode=simulation ...`) and exits 0 on stdin EOF, including with `LACERTE_DEFAULT_TAX_YEAR=abc` (banner shows `defaultTaxYear=<ignored>`).

## Next Task

Everything sim-verifiable is done. Remaining work needs the operator's Windows + Lacerte box:

1. Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification", including the new "Round-7 live items".
2. Close the live-gated verification items `Todo.md` R7-L1 (`C3_G1` vs `C3_1` state column), R7-L2 (null detail Amount), and R7-L3 (`IClient.ClientNumber`).
3. Run `lacerte-mcp register` and capture the real `AccessGranted` / `NotSignedIn` integers (F1 + F7).
4. Run the FieldAccessDumper `--live --year <yyyy>`, then `lacerte_reload_schema`.
5. Capture the parity fixture `Tests/Parity/fixtures/live-ty24-individual.json`.
6. Tag `v0.1.0` after the first all-green pass.

Deferred (Windows-gated, unchanged): **R6-D1** (live replaceIndex / detail delete), F2/F4.

## Context Notes

### Wire-surface conventions (canonical — unchanged except where noted)

These are unchanged:

- camelCase `taxYear`/`stateAbbrev`/`clientId`; write tools use `field`; the collection key is `rows`.
- Mode strings are lowercase; `cid` is the correlation id.
- `module` is a single-char string plus a friendly `moduleName`.
- Paginated envelopes carry `total`/`limit`/`returnedCount`/`hasMore`/`nextOffset`.
- Bulk envelopes carry `requestedCount`/`processedCount`/`succeededCount`/`failedCount`/`skippedCount`/`stoppedEarly`.

New this round:

- Idempotency fingerprints are SHA-256 over `{op, mode, args}`.
- `ArgumentException` surfaces as 9017 with `{argument, reason[, value]}`.
- ODBC errors may carry `context.hint`.

### Gotchas (carry-forward, updated)

- Use `$env:USERPROFILE\.dotnet\dotnet.exe`; the system-wide `dotnet` is runtime-only.
- Binaries live under `bin/x64/<Config>/` (flattened; there is no `net48` folder). Stale pre-flatten copies may still sit in `bin/x64/Debug/net48/`, so don't run those.
- **MCP SDK 1.4.0**; STJ is pinned at 10.0.7. Hosting.Abstractions 10.x runs `BackgroundService.ExecuteAsync` asynchronously.
- **Lock order in `LacerteSessionManager`:** `_simGate` (ReaderWriterLockSlim: exclusive for sim creates + dry-runs, shared for sim reads) → per-client write lock → `lock(list)`. Never take them in the reverse order, and never hold one across an `await` (the gate is thread-affine).
- The session normalizes `clientNumber` (trim) and `stateAbbrev` (trim + upper-case) **before** the idempotency fingerprint and the dispatch. Tests and tools should not rely on raw spellings.
- Live write paths now **throw** on non-Success. Callers still check `status == Success` defensively, but that branch is effectively sim/legacy-only.
- `*At` overloads are reads-only by design; writes stay on the active context, now optionally pinned via `expectedContext`.
- `LACERTE_IDEMPOTENCY_PERSIST` defaults ON (`=0` disables). The TTL is a hardcoded 24h, and the cap of 10k applies on disk too.
- **NEVER mutate `LACERTE_DATA_ROOT`/`LACERTE_DATA_PATH` process env in tests.** Use the `DoctorCommand.RunAllProbes(envOverrides)` seam or join `DataRootEnvCollection`.
- Test collections are serialized (`xunit.runner.json` → `parallelizeTestCollections:false`) because static state is shared (WireLogRing, StatsRegistry, the FieldAccessMapLoader snapshot).
- Concurrency tests must handshake their start order (see P20-005 and the R7 tests that use `ManualResetEventSlim`).

### How to /pickup this session

1. Read this HANDOFF.md, then `Todo.md` Phase 21, then the `DECISIONS.md` 2026-11-15 entries.
2. Run the Verify Before Continuing checks.
3. The only open work is Windows+Lacerte-gated (see Next Task).
