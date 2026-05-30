# Handoff State

## Last Session Summary

Bootstrap session 2026-05-30 went from empty spec → working C# / .NET 4.8 / x64 MCP solution. Three waves of parallel implementation agents (12 agents total) landed Phases 0–13 of `Todo.md`. A fourth wave (workflow, 5 agents) closed every doable cross-cutting follow-up. **Final state: 481/492 tests passing, 0 failures, 11 expected live-only skips.** Build clean (0 warnings, 0 errors).

- **Scaffolding** — full operating-system file set (HANDOFF/Todo/ARCHITECTURE/DECISIONS/REQUIREMENTS/REGRESSION_CHECKLIST/ACCEPTANCE_CRITERIA/CLAUDE/README/.env.example/.gitignore).
- **7-agent research fan-out** under `docs/research/agents/01..07-*.md` drove every architecture decision.
- **12 dated decisions** in `DECISIONS.md` — the load-bearing one flipped the stack from TS+winax (tentative) to C# .NET 4.8 on evidence that `winax` can't drive Lacerte's .NET CCW + runtime-shim requirement.
- **Comprehensive 88-task `Todo.md`** across 13 phases. **Every sim-verifiable task checked off.** All 9 doable cross-cutting follow-ups (F3, F5, F6, F8, F10-scaffold, F11, F12, F13, F14) closed in wave-4 workflow.
- **Three waves of 4 parallel agents + one wave-4 workflow:**
  - Wave 1 — Phase 0 (.NET solution) + Phase 1 (Errors + Schema) + Phase 2 partial (sim store types + seed).
  - Wave 2 — Phase 2 chokepoints + Phase 3 (LacerteSessionManager) + Phase 4 (ODBC) + Phase 10 (CLI).
  - Wave 3 — Phase 5 (8 read tools) + Phase 6/7/8 (5 write/create/metadata tools) + Phase 9 (COM bridge scaffolding) + Phase 11/12 (hardening primitives + parity test harness).
  - Wave 4 (workflow) — Follow-ups F3 (UnknownFieldAccess 9012), F5 (FieldAccessDumper project), F6 (spec §1 fix), F8 (.gitignore), F10 (recording adapter + fixture + test), F11 (WireLog wiring), F12 (AutoReconnect wrapping), F13 (Pagination retrofit), F14 (EditionDetection caching).
- **Two cross-test races fixed in this session:**
  1. `[Collection("AccessMapMutation")]` serializes access-map-mutating tests across `Tools/FieldWriteToolsTests` and `Bridge/SessionManagerBridgeWireupTests`.
  2. `[Collection("Cli (serial)")]` on `Parity/RecordingHarnessTests` + per-test `IsolatedAppIdScope` isolates the AppID lookup from CLI tests that mint a real GUID.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report 0 warnings, 0 errors. (System-wide `dotnet` install is runtime-only on this box; use the user-local SDK at `$env:USERPROFILE\.dotnet`.)
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **481 passed, 11 skipped, 0 failed**. The 11 skips are `[Fact(Skip = "Requires real Lacerte SDK")]` across `Tests/Bridge/` + 1 Windows-only manual Credential Manager round-trip in `Tests/Cli/`.
- [ ] **CLI smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` should emit a JSON payload and exit 0. `doctor` should exit 1 on this box (no Lacerte install) — that's expected.
- [ ] **Dumper smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.FieldAccessDumper/bin/x64/Debug/LacertMCP.FieldAccessDumper.exe` should print `no fields to probe in sim; run on Windows for live introspection.` in dev env.
- [ ] **MCP server smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` should start the stdio MCP server without throwing. Kill after confirming startup.
- [ ] **Research outputs intact:** `docs/research/agents/` contains 7 reports `01-stack-decision.md` through `07-qb-mcp-patterns.md`.

## Next Task

The off-Windows build is **complete and stable**. All remaining open items require a Windows + Lacerte + SDK box:

### Windows-gated work (single focused session on the operator's box)

1. **Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification"** — 31 checkboxes spanning bridge wire round-trips (10), read tools (12), write tools (5), create tools (3), auth lifecycle (3), sanity probes (3). All against `JOHNS` / TY24 Individual / `C:\Lacerte\24data`.
2. **Run `npx lacerte-mcp register`** to mint the AppID, click through the Intuit consent + sign-in dialogs, and capture the actual integer values for `AccessGranted` and `NotSignedIn` (closes follow-ups F1 + F7).
3. **Run `LacertMCP.FieldAccessDumper.exe --module I --table 1`** on the live box to populate `Schema/FieldAccessMap/base/individual.json` via introspection (closes the v1-empty-shard data gap from F5 v2 roadmap).
4. **Capture a `tests/Parity/fixtures/live-ty24-individual.json` recording** by running the MCP against a real `JOHNS` client and dumping the wire calls (the recording-bridge format is documented in `tests/Parity/BothModes.cs` + the existing `example-recording.json` stub). Drop the file in place and the parity tests' Live leg starts exercising real wire shapes (closes #40 + F10 v2).
5. **Tag `v0.1.0`** after the first all-green pass on items 1–4 (closes #88).
6. **Record a 60-second walkthrough** demonstrating register → list clients → read one → write an address field → verify (closes #87).

### Sim-side work after Windows recordings land

Once `tests/Parity/fixtures/live-*.json` files exist:
- Drop the `[Fact(Skip = "Requires real Lacerte SDK")]` on the 11 currently-skipped Bridge tests and rerun.
- The parity-test harness's Live leg will start exercising real wire shapes (F10 v2). 
- Any divergence between sim and recorded-live becomes a `DECISIONS.md` entry + sim-side fix.

### Out of our control

- **F9** — bump `ModelContextProtocol` dependency to 1.x when it ships on NuGet (currently `0.3.0-preview.4` is the latest published).

## Context Notes

### Architecture & build
- **Stack:** C# / **.NET Framework 4.8** / x64 / `ModelContextProtocol` 0.3.0-preview.4 (1.x release pending; tracked as F9). App.config carries the load-bearing `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime v4.0 v4.6` shim.
- **Source spec:** `Lacerte_MCP_Structure_Spec.md`. §1 was corrected in wave-4 F6 — ODBC is reads-only; writes go through COM.
- **Reference codebase:** `C:/Users/VR/Projects/Quickbooks MCP Desktop/`. Patterns extracted in `docs/research/agents/07-qb-mcp-patterns.md`.
- **Boundary invariants** in ARCHITECTURE.md are non-negotiable.

### Project layout

```
LacertMCP.sln
src/
  LacertMCP.Server/         net48 x64, the MCP host binary (LacertMCP.Server.exe)
    Program.cs              Host.CreateApplicationBuilder + AddMcpServer + WithStdioServerTransport + WithToolsFromAssembly + WithPromptsFromAssembly
    App.config              <— load-bearing runtime shim
    Tools/                  16 tools across Read/Write/Create/Metadata/Health (lacerte_client_list + lacerte_table_read now paginated)
    Session/                LacerteSessionManager (+ WireLog + AutoReconnect wraps + EditionDetection cache) + SimulationStore (+ partials Seed/Query) + SessionContext/Mode/StatusSnapshot/IdempotencyCacheEntry + Fixtures/SeedData
    Odbc/                   ConnectionStringBuilder, SqlTemplates, LacerteDsnReader (WireLog wrapped), OdbcErrorMapper, ConnectionPool
    Schema/                 Modules, Tables, FieldAccess, FieldAccessMapLoader + FieldAccessMap/{base/*.json, overlays/*.json, manifest.json, README.md}
    Errors/                 StatusCodes (28 + 5 + 12 codes incl. new 9012 UnknownFieldAccess), SyntheticStatusCodes, LacerteSDKError, WrapToolHandler, Severity, StatusInfo
    Bridge/                 ILacerteBridge, LacerteAccessClient (WireLog wrapped), Registration, Writer (WireLog wrapped), Reader, CanUseNewApisProbe, EditionDetection
    Logging/                WireLog (LACERTE_DEBUG_WIRE=1 → logs/lacerte-YYYYMMDD.log; SSN/EIN/BankAccountNumber/RoutingNumber redacted; EnableForTests hook)
    Resilience/             AutoReconnect (transient HRESULT whitelist + [250,500,1000]ms backoff; live-only)
    Pagination/             Pagination.Page<T>
    Prompts/                WorkflowPrompts (3 MCP prompts: client_intake, review_ef_status, bulk_field_update)
  LacertMCP.Cli/            net48 x64, the operator CLI binary (lacerte-mcp.exe)
    Program.cs              System.CommandLine 2.0.0-beta4 root + 5 subcommands
    Commands/{Register,Login,Status,FieldAccess,Doctor}Command.cs
    Storage/AppIdStore.cs   advapi32 P/Invoke on Win; %APPDATA%/lacerte-mcp/config.json fallback
  LacertMCP.FieldAccessDumper/  net48 x64 console exe — probe (module, table, field) tuples; live introspection stubbed pending Windows + Lacerte box (F5 wave-4)
    Program.cs
  LacertMCP.Tests/          net48 x64, xUnit (481 passing / 11 skipped / 0 failed)
    Errors/ Schema/ Session/ Odbc/ Bridge/ Tools/ Cli/ Parity/ Integration/ Logging/ Resilience/ Pagination/ Prompts/
    AccessMapMutationCollection.cs    serializes access-map-mutating tests
    Parity/BothModes.cs               ParitySessionFactory + RecordingBridge + ForRecording adapter (F10)
    Parity/fixtures/example-recording.json  hand-authored stub
    Parity/RecordingHarnessTests.cs   F10 end-to-end test (isolated AppID scope, serialized with Cli)
```

### Gotchas
- **Use `$env:USERPROFILE\.dotnet\dotnet.exe`** — system-wide `dotnet` (in `C:\Program Files\dotnet`) is runtime-only.
- **Cross-test access-map race fixed** via `[Collection("AccessMapMutation")]`. If you add a new test that mutates `_moduleCache` or writes to `Schema/FieldAccessMap/base/*.json`, tag it with the same collection.
- **Cross-test AppID race fixed** via `[Collection("Cli (serial)")]` + `IsolatedAppIdScope`. If you add a new test that calls `Bridge.Registration.GetStoredAppId()` or `AppIdStore.Get/Set/Reset`, follow that pattern.
- **`LACERTE_SIM_EXPIRED=1`** simulates an expired 24h auth — useful for testing the 9101 NotSignedIn UX without waiting.
- **`LACERTE_DEBUG_WIRE=1`** activates the wire log. SSN/EIN/BankAccountNumber/RoutingNumber are redacted before the line hits disk.
- **`LACERTE_PARITY_RECORDING=path/to/file.json`** activates the parity-test recording adapter — the Live leg of `BothModesData` tests replays from the file instead of pinning to sim.
- **`LACERTE_MCP_FORCE_FILE_STORE=1`** + `APPDATA=<tempdir>` isolates `Bridge.Registration.GetStoredAppId()` from Credential Manager. Used by `IsolatedAppIdScope` in tests.
- **CLI `register`/`login` stub the live Intuit call** pending Phase 9 wire-up — AppID still persists, doctor still works. Activate the live call by setting `LACERTE_LIVE=1` and re-running on a Lacerte box.
- **MCP SDK is preview-4** — watch for breaking API changes when 1.x ships. Carried as F9.
- **AppID GUID is permanent** — `lacerte-mcp register --reset` is the only way to remint and it forces a fresh Intuit consent ceremony.

### Test landing breakdown
- Errors: 53 tests
- Schema: 19 + 29 coverage = 48 tests
- Session/SimulationStore: 47 tests
- Session/LacerteSessionManager: 24+ tests (incl. new 9012 + EditionDetection cache tests)
- Odbc: 78 tests
- Bridge: 38 + 10 skipped (live-only) = 48 tests
- Tools (Read + Write + Create + Metadata + Health): 68 + 6 pagination = 74 tests
- Cli (AppIdStore, Doctor, Status): 14 + 1 skipped (Windows-only manual CredMan) = 15
- Logging / Resilience / Pagination / Prompts: 27 + integration tests for F11/F12
- Parity (RoundTripTests + WriteParityTests + RecordingHarnessTests): 49+ rows
- Integration (FullFlowTests): 1 end-to-end sim test
- **Total: 481 passing + 11 skipped = 492 across the suite. 0 failures, 0 warnings.**

### How to /pickup this session

Run `/pickup`. It will:
1. Read this HANDOFF.md.
2. Read `Todo.md`.
3. Run the "Verify Before Continuing" checks above.
4. Report status without writing any code yet.

If a verification check fails, fix it BEFORE starting any new work — the boundary invariants are load-bearing and a broken build means follow-on work will compound the breakage.

The remaining real work is on a Windows + Lacerte + SDK box. Plan to spend a focused session there walking the 31-item live-mode checklist in `REGRESSION_CHECKLIST.md`; capture findings as `DECISIONS.md` entries (especially actual TAPIStatus integers per F1 + F7), drop a recorded-live JSON fixture under `tests/Parity/fixtures/`, run the dumper to populate the access-map shards, and tag v0.1.0 on the first all-green pass.
