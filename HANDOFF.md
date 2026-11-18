# Handoff State

## Last Session Summary (2026-11-18, evening) — bulk preview token + detail-row restore

- `lacerte_detail_write_bulk`: a real call needs the `previewToken` from a passing dry run of the same entries/context (30 min, reusable; per-connection in HTTP mode); both modes. Prompts updated.
- Every live detail row the MCP adds is journaled (`<backup>.journal.jsonl`); `lacerte-mcp detail-restore list|plan|apply` removes exactly those rows (one copy each, DELETE + re-insert n-1, verified, refuses over-broad matches). The DELETE is a separate strictly validated worker request.
- DECISIONS 2026-11-18 entry; REGRESSION_CHECKLIST "FP-2 restore live items" (ODBC DELETE unverified).
- Tests 2189 passed, 11 skipped, 0 failed. Not pushed yet.

## Last Session Summary (2026-11-18, review) — independent review of the filing-prep code

- Three independent reviewers (detail writes; dictionary + map-diff; prompt/fixtures/docs). All confirmed findings fixed; tests 2095 passed, 11 skipped, 0 failed (two runs); build 0 warnings.
- Most important: a live detail row that may have landed (9022, or a worker failure with rowWritten yes/unknown) now PINS its idempotency key, so a retry can't append a duplicate; live detail writes are server-gated to dictionary-mapped Series/Code (9012 otherwise) and need a Suffix; map-diff record requires --expect and can't record a coincidental change; the range-rule fallback skips fields any year's overlay withdraws; the dictionary merge no longer aborts on a case-only id difference.
- Installer: -LiveDetailWrites (remembered). Release zip ships REGRESSION_CHECKLIST.md. Guide republished.
- Still to verify on the Lacerte PC: REGRESSION_CHECKLIST "FP-2 live items" and "Round-8 live items"; then record mappings and run FP-6.

## Last Session Summary (2026-11-18, later) — filing-prep build (everything that doesn't need the Lacerte PC)

- Built FP-1a, FP-2, FP-3 tooling, FP-4 and FP-5 (three worktree agents + coordinator; details in docs/plans/filing-preparation.md "Status" and Todo "Filing preparation").
- New: `lacerte_field_dictionary` tool (43 tools), `lacerte_entity_return_entry` prompt (5 prompts), `lacerte-mcp map-diff`, hidden `field-access merge-dictionary`, env `LACERTE_LIVE_DETAIL_WRITES` (default off) and `LACERTE_DETAIL_SNAPSHOT_DIR`, sim datapaths SData/FData (12 seeded clients).
- Safety: field dictionaries ship EMPTY (no invented Lacerte codes); range rules apply only to dictionary-listed fields; live detail writes are out-of-process, snapshot first, read back, and OFF until verified; recorded dictionary entries survive updates.
- Tests 2067 passed, 11 skipped, 0 failed; build 0 warnings. Nothing pushed.
- Next, on the Lacerte PC: FP-2 live items -> enable detail writes; record mappings with map-diff for the most-filed return type; FP-6 acceptance.

## Last Session Summary (2026-11-18) — return-preparation capability check

- Asked whether the MCP can prepare 1120 / 1120-S / 1065 / 1041 filings: **no, not today.** It reads every module and creates clients, but live mode writes only 3 DATA1 fields (I/P; none for C/S/F), live detail-row writes are unwired (9202), there is no field dictionary, and there is no 1041 pre-filing check.
- Found that `LacertMCP.FieldAccessDumper.exe --live` is a stub that always fails, while the README, manual, SKILL, installer, control-page health hint, 9012 remediation and operator guide all told people to run it. All corrected to the working path (`lacerte-mcp field-access set`, one field at a time). `lacerte_help(topic:"limits")` and the README "does NOT do" list now state the preparation limit. Guide republished with a by-return-type table.
- Plan: `docs/plans/filing-preparation.md`; Todo "Filing preparation" FP-0..FP-6. FP-0 needs operator decisions.
- Tests 1831 passed, 0 failed. Nothing pushed (local master is ahead of origin).

## Last Session Summary (2026-11-17) — Round-8 deep review (HTTP mode, live path, updater) + operator guide

- Reviewed everything that landed after the R7 review (4c36372..5f615ed, ~6.3k lines never reviewed) in four independent lanes, then fixed every confirmed finding (Todo "Round-8 review"; DECISIONS 2026-11-17). HTTP and updater fixes were made in isolated worktrees and merged; an independent confirmation review of the combined fixes followed.
- Notable bugs fixed: 202 replies carried `Content-Length: -1` (Node/Claude Code HTTP clients reject it); tagged Tailscale nodes counted as "owner"; GET /mcp pinned sessions forever; chunked bodies bypassed size caps; a 9022 stayed stuck behind its idempotency key; JSON booleans falsely failed verification; startup blocked on the Lacerte sign-in call; the field-map keep rule froze curated shards on every install; re-running the installer copy in the install root deleted server\.
- New: `docs/lacerte-mcp-guide.html`, an operator guide (published as a private claude.ai artifact).
- Tests: 1831 passed, 11 skipped, 0 failed (two consecutive runs); build 0 warnings. A confirmation review of the combined fixes found 11 follow-ups, all fixed (commit "R8 confirmation review fixes").
- Not verifiable here: REGRESSION_CHECKLIST "Round-8 live items" and the installer/updater live checks in Todo.

## Last Session Summary (2026-11-16, later) — `lacerte-mcp update` (self-update from GitHub releases)

- Branch `feat/self-update`, cut from `feat/live-reads-verified-writes`. New `Commands/UpdateCommand.cs` + `Update/` (ReleaseVersion, GitHubReleaseSource, ReleaseInstaller, LauncherService). README "Network mode" + CLI sections, INSTRUCTION_MANUAL section 9, `install-http.ps1` closing hints, DECISIONS and Todo updated.
- Verified on this Windows box:
  - 34 new tests pass. They include a really-running locked exe (a ping.exe copy) renamed aside, and a two-clone git fast-forward.
  - Real GitHub runs against a scratch install:
    - v0.2.0 → v0.2.1, SHA-256 verified.
    - The CLI replaced itself from inside the install.
    - An install-http-style launcher (sim mode, port 8799) was stopped, the files swapped, the launcher restarted, and `/healthz` answered.
  - `--schedule daily` / `off` registered and removed a real per-user task without admin.
- **Releases v0.2.0/v0.2.1 were built from `feat/live-reads-verified-writes`, not master.** An install only gets `update` after a release containing this branch is tagged. Until then, bootstrap it once by hand: unzip that release and re-run `install-http.ps1`.

## Last Session Summary (2026-11-16) — merged master (R7) into the HTTP branch; workstation one-line setup

- Merged `origin/master` (PR #4, R7 deep review) into `feat/live-reads-verified-writes`. Conflicts: `LacerteSessionManager.cs` (kept R7's canonical dataPath + `ThrowIfWriteFailed`, re-applied the per-device read-only `SessionScope` check in `SetContext` and `RejectIfReadOnly`), one R6 test, and these docs (both sides kept). Two branch tests updated to R7's PDF-aligned ODBC: `TaxYear=2024`, `[<client>-Detail-ClientStateInfo]`.
- New `Web/WorkstationSetup.cs`: `GET /setup.ps1` (one-liner for a workstation), `GET /bridge.ps1` (stdio-to-HTTP bridge for Claude Desktop / Codex), `GET /access-check` ({device, level}). Any identified tailnet device may fetch them; `/mcp` still enforces access. A setup run is logged (Activity → Access) and the device gets a **Ran setup** badge on the Access tab. Agents tab has an "easy way" panel with Copy buttons (execCommand fallback, since http over the tailnet isn't a secure context).
- Verified on the tower: build 0 warnings; tests **1716 passed, 11 skipped, 10 failed**: the same 10 Linux-only failures master has on its own under Mono (5 Windows-path tests listed below + R718, R720 x3, R702). HTTP sim server under Mono + bridge under pwsh 7: initialize, tools/list (42), tools/call, **reconnect after a server restart**, unreachable-server error. setup.ps1 under pwsh with fake Windows dirs: install, re-run (no duplicates, other servers / single-item arrays / Codex tables preserved), uninstall.
- **Not yet verified on Windows PowerShell 5.1 / a real workstation**: run the one-liner on a workstation, grant access, restart Claude Desktop, and call `lacerte_session_status`.

## Last Session Summary (2026-11-11) — live path wired, HTTP mode + control page

Branch `feat/live-reads-verified-writes` (not merged). Built and tested on the Linux tower: `mcr.microsoft.com/dotnet/sdk:8.0` builds the net48 solution; tests run under Mono in a local image `lacerte-test-mono` (dotnet sdk 8 + mono-complete). **1545 passed, 11 skipped, 5 failed.** The 5 failures are Linux-only Windows-path tests (`SessionToolsTests.ValidateDataPath_*`/`ValidateLogDir_*`, `ObservabilityToolsTests.ResourceUsage_EmitsCanonicalShape`); they fail identically on the untouched baseline and pass on Windows.

## Last Session Summary (2026-11-15) — Phase 21: round-7 deep code review

Goal: a deep review of the whole project, checked against source and `Lacerte_MCP_Structure_Spec.md`, with every confirmed bug or gap fixed and no regressions. The review ran in five independent lanes:

- bridge / ODBC / errors, checked against the spec
- session layer
- core tools
- bulk and aggregate tools, plus logging
- CLI, startup and build

Each finding was re-verified against source before it was fixed. Net: **1506 → 1678 tests passing, 11 skipped, 0 failed (3 consecutive full-suite runs), build 0 warnings / 0 errors.** All work is on branch `review/r7-deep-review`. The full per-finding list is in `Todo.md` Phase 21, and the policy choices are in `DECISIONS.md` (the 10 2026-11-15 entries).

### What was wrong before this session (found by running the real binary)

1. **The server never called `Register()`.** Every data tool returned 203 NotSignedIn — in simulation too — when driven by a real MCP client. Only the tests registered. Fixed: `Startup/StartupRegistration.cs`.
2. **Live reads were never wired.** Every read threw 9201 BridgeUnavailable; `_odbc` was never assigned. Fixed: `Session/LacerteSessionManager.Live.cs`.
3. **Live write failures were returned as values.** The bulk tools counted e.g. DBFNotAccessable as success. Now they throw (sim parity).
4. **Writes were never verified.** Now every field write is read back over a fresh ODBC connection; mismatch → new **9022 WriteNotVerified** (`Session/LacerteSessionManager.Verify.cs`).
5. The writable-field access map ships **empty**, so every live field write is refused (9012) until FieldAccessDumper is run on the Lacerte PC. Fail-closed is correct; the control page now says so.

### Added

- `--http` / `LACERTE_HTTP=1`: Streamable HTTP MCP at `/mcp` + control page at `/` (`Web/`). Loopback + Tailscale IP only, `tailscale whois` identity, per-device None/Read/Write (`%APPDATA%\lacerte-mcp\device-access.json`), per-connection context (`Session/SessionScope.cs`), idle reaping, activity timeline (`activity.jsonl`), `/healthz`.
- `scripts/install-http.ps1` (in the release zip): copies to `%LOCALAPPDATA%\LacertMCP`, urlacl `http://+:8767/`, firewall 8767 from 100.64/10, Startup-folder VBS restart loop running live `--http`. `-Uninstall` reverses.
- Command Center: Lacerte tile → "Open control page" (`/mcp/lacerte/control`), host from `LACERTE_MCP_HOST` in `~/command-center/.env` (not set yet).

## Verify Before Continuing

- [ ] Build: `docker run --rm -v $PWD:/src -v lacerte-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build LacertMCP.sln -c Debug -p:Platform=x64` → 0 warnings, 0 errors. (Windows: `scripts/build.ps1`.)
- [ ] Tests: same with image `lacerte-test-mono` and `dotnet test src/LacertMCP.Tests/LacertMCP.Tests.csproj -c Debug -p:Platform=x64` → only the 5 Linux-only failures above.
- [ ] HTTP smoke under Mono: start `mono .../LacertMCP.Server.exe --http` with `LACERTE_SIMULATION=1`, use `http://127.0.0.1:8767` (Mono's HttpListener rejects `Host: localhost` when both prefixes are bound — Windows http.sys does not).

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
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` → **1678 passed, 11 skipped, 0 failed**.
- [ ] **CLI smoke:** `src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` → exit 0; `tools --names-only` → **42 names**; `doctor` → exit 1 on this box (no Lacerte installed). Note: since `0776766` there is **no `net48` subfolder** under `bin/x64/<Config>/`.
- [ ] **MCP server smoke:** `src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` boots (`[startup] LacertMCP 0.1.0 mode=simulation ...`) and exits 0 on stdin EOF, including with `LACERTE_DEFAULT_TAX_YEAR=abc` (banner shows `defaultTaxYear=<ignored>`).

## Next Task — live verification on the Lacerte PC (nothing above is proven against real Lacerte yet)

1. Install: release zip → `install-http.ps1`. `doctor` should be green for SDK/ODBC/AppID.
2. `lacerte-mcp register` (first time) — record the real AccessGranted/NotSignedIn integers (F1/F7).
3. `dumper\LacertMCP.FieldAccessDumper.exe --live --year <yyyy> --out %LOCALAPPDATA%\LacertMCP\server\Schema\FieldAccessMap\base` for each year in use (without `--out` it writes next to itself and the server never sees it); restart; control page "Writable fields" goes green. Re-running install-http.ps1 keeps a built map.
4. Control page → Data folders → **Test read** on each folder. Confirms connection string, ReturnType, DATA1 columns, client-number format.
5. Read a known client with `lacerte_client_read`, `lacerte_table_read` 2/3/5/7, `lacerte_detail_read`, `lacerte_ef_status`; compare against the Lacerte UI. **Check:** DATA2/DATA4 subclient id column name (code looks for `SubClientID`/`SubClientNumber`), TY21+ per-client table names (`[CLIENT-SubClientInfo]` etc.).
6. On a TEST client only: `lacerte_field_write` a harmless field with Lacerte closed → expect `verified:true`; with the return open in Lacerte → expect a DBFNotAccessable error, not success. Confirm the fresh-connection read-back sees the COM write.
7. Capture `Tests/Parity/fixtures/live-ty24-individual.json`; merge the branch; tag.

## Context Notes

- Lacerte PC candidates on the tailnet: `vr` (100.114.92.33), `desktop-0ngsgb4` (100.124.82.41, SSH open as of 2026-11-11 but key not yet accepted), `desktop-6fhu255` (100.80.126.71). Tower key for Windows: `~/.ssh/id_ed25519_lacerte`.
- Per-connection context: every read of `LacerteSessionManager.Context` resolves through `SessionScope.Current`; under stdio it is null and the old global applies. Don't cache `Context` across awaits in new code.
- `ToolActivity` records tool name, device, outcome and for writes only clientNumber/field/table — never values.
- MCP SDK 1.4.0's `StreamableHttpServerTransport` is in ModelContextProtocol.Core (netstandard2.0); the AspNetCore package is net8+ only, hence the HttpListener host.

Everything sim-verifiable is done. Remaining work needs the operator's Windows + Lacerte box:

1. Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification", including the new "Round-7 live items".
2. R7-L1/L2/L3 are all resolved from the primary source: the Intuit SDK Developer Instructions PDF, which is now pinned by `SdkSourceAlignmentR7Tests`; see `Todo.md` "Source verification". On the live box, walk the updated "Round-7 live items" in `REGRESSION_CHECKLIST.md` as a smoke test of the source-aligned bridge.
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
