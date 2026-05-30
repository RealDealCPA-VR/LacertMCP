# Handoff State

## Last Session Summary

Phase-14 deep-audit + execute session on 2026-05-30. Three waves landed:

1. **Audit workflow** (88 agents) — 10-lens parallel auditor (correctness, test-gaps, architecture, security, spec-compliance, docs, simplification, perf, dx-build, readme-cli) → adversarial verifier per finding. **78 candidates → 71 confirmed** as real + actionable + useful (25 high / 32 medium / 14 low). Saved to `audit-findings.json` at repo root.
2. **Execution workflow** (25 agents) — preflight (cross-cluster Modules refactor P14-020) → 22 parallel cluster agents (each owning one file or small file-set) → final verifier.
3. **Cleanup workflow** (4 agents) — closed P14-046 (new CLI verbs `tools` + `unregister`), P14-050 (bridge input validation), P14-055 (Series numeric-string guarantee).

**Final state: 695/705 tests passing, 0 failures, 10 expected live-only skips.** Build clean (0 warnings, 0 errors). 71/71 Phase-14 tasks complete. 57+ files modified, +2705 / -845 lines, plus a fresh git repo (initialized for the session as a rollback safety net).

### What landed

- **Correctness fixes** — COM ReleaseComObject in Writer/Reader, idempotency-cache TOCTOU lock, sim CreateClient gate-parity, WriteDetail AssertWritable, EnsureEditionProbed cache-once-on-throw, int.Parse safety in pre-TY21 row lookup, case-insensitive C{t}_0 rule, TaxYearOverlay cascade, SimulationStore.WriteDetail per-bucket lock.
- **Architecture / boundary fixes** — HealthTools now wrapped + reports real mode, MetadataTools routed through session manager, EnsureLive renamed/simplified, sim/live observational divergence on reads documented, UpdateReturn no longer lies about live API used for TY<2021.
- **Security / PII** — WireLog redaction expanded (DOB, address, phone, email, name variants in addition to SSN/EIN/Bank/Routing), WrapToolHandler context-dict now redacted before leaving the boundary, ConnectionStringBuilder handles `{` and `}` in dataPath, SessionTools validates dataPath against expected root, AppId masking consistent across CLI commands, bridge input-validation layer (`Bridge/BridgeInputValidation.cs`) on every dynamic dispatch path.
- **Spec compliance** — CreateClient required-set per spec, EFStatus SQL projection columns match tool output, Tables 6/7 write block, Detail Series numeric-string invariant enforced at validator + seed.
- **Docs** — README env-var matrix completed, Prompts section added, CLI per-command subsections with all flags, troubleshooting section, stale 469/480 → 481/492 → 695/705 fixed; ARCHITECTURE.md MCP SDK version + Bridge file list corrected; CLAUDE.md rewritten from TypeScript to C#/net48; REGRESSION_CHECKLIST.md gates on dotnet not npm; ACCEPTANCE_CRITERIA.md updated for prompts + paginated shape; DECISIONS.md gained dated entries for WireLog redaction, AutoReconnect backoff, Pagination contract.
- **Simplification** — `_moduleFileName`/`_moduleTaxTypes` hoisted onto `Modules` (was duplicated in 6 files); FieldWriteTools + UpdateReturnTools share a helper; EnsureLive inlined; dead `ConnectionPool.cs` deleted; dead `RegisterWithStoredAppId` + `CredentialAccount` const removed; dead `WireLog.ResetForTests` + `_testReset` removed; stale "Stream A TODO" comments cleared.
- **Performance** — WireLog keeps file handle open across writes (was open-close per record); idempotency cache gained size cap + TTL; SHA-256 only computed when idempotencyKey present; SimSnapshot only clones touched bucket; FieldAccessMapLoader avoids per-call string allocation.
- **Tests** — AutoReconnect HRESULT coverage, FieldAccessMapLoader overlay precedence + AssertWritable branches, UpdateReturn direct tests, EditionDetection branches, WireLog redaction edge cases (arrays/nested/whitespace), Doctor exit-code assertions, BridgeInputValidation (62 new tests), new ToolsCommand + UnregisterCommand tests. **+214 net new tests** (481 → 695).
- **DX** — GitHub Actions CI (build + test on Windows), .editorconfig (MS C# defaults), scripts/build.ps1 + test.ps1 + release.ps1, assembly version stamping in all .csproj files, code-coverage via coverlet collector, git repo initialized.
- **New CLI verbs** — `lacerte-mcp tools` (lists registered MCP tools via reflection over `[McpServerTool]` attrs) and `lacerte-mcp unregister --confirm` (discoverable AppID reset; mirrors `register --reset`).

### Workflow artifacts (still on disk)

- `audit-findings.json` — full 71-finding record with per-task verdict + refinedRecommendation. Useful reference when a future change touches the same area.
- `Todo.md` — Phase 14 section appended with all 71 task IDs (P14-001..P14-071), all checked off.
- `scripts/build-phase14-addendum.js` — regeneration script for the Todo.md Phase-14 block.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report 0 warnings, 0 errors.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **695 passed, 10 skipped, 0 failed**. The 10 skips are `[Fact(Skip = "Requires real Lacerte SDK")]` across `Tests/Bridge/`.
- [ ] **CLI smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` → JSON payload exit 0. `tools` → tool table exit 0. `doctor` → exit 1 off-Windows (expected).
- [ ] **MCP server smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` starts cleanly; kill after confirming.

## Next Task

The off-Windows codebase is **fully audited and hardened**. All sim-verifiable improvements have landed. Remaining open items are the same Windows-gated ones called out before this session:

### Windows-gated work (single focused session on the operator's box)

1. **Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification"** (31 checkboxes).
2. **Run `lacerte-mcp register`** to mint the AppID and click through Intuit dialogs; capture actual integer values for `AccessGranted` and `NotSignedIn` (closes F1 + F7).
3. **Run `LacertMCP.FieldAccessDumper.exe --module I --table 1`** on the live box to populate `Schema/FieldAccessMap/base/individual.json` via introspection.
4. **Capture `tests/Parity/fixtures/live-ty24-individual.json`** recording for the parity-test harness Live leg.
5. **Tag `v0.1.0`** after the first all-green pass.
6. **Record the 60-second walkthrough** for task #87 in Todo.md.

### Out of our control

- **F9** — bump `ModelContextProtocol` dependency to 1.x when it ships on NuGet (currently `0.3.0-preview.4`).

## Context Notes

### What changed structurally in this session

- **`Schema/Modules.cs`** — `ModuleInfo` record gained `FileBasename` + `TaxTypeAliases` positional fields (was 3 args, now 5). New static accessors: `Modules.FileBasenameFor(char)`, `Modules.AcceptsTaxType(char, string?)`, `Modules.TaxTypeAliasesFor(char)`. All 5 callers updated.
- **`Bridge/BridgeInputValidation.cs`** — NEW. Centralizes input checks at the bridge boundary (control-char rejection, length caps, identifier regex). Called from Writer.WriteToDBFDatabase / UpdateTaxReturn / WriteDetail and Reader.ReadFromDBFData BEFORE WireLog + AcquireWriter/Reader.
- **`Bridge/Writer.cs` / `Bridge/Reader.cs`** — `try/finally` + `Marshal.ReleaseComObject` around every COM call site (copied from `CanUseNewApisProbe.cs:96-103` canonical pattern).
- **`Session/LacerteSessionManager.cs`** — Idempotency now uses `ExecuteWithIdempotencyLock(key, payload, body)` helper with per-key `SemaphoreSlim` serialization; cache has size cap + TTL eviction; SHA-256 fingerprint only computed when key is non-null; `EnsureLive()` inlined; `EnsureEditionProbed()` no longer re-probes when live probe throws.
- **`Logging/WireLog.cs`** — File handle kept open across writes (was open-close per record). Redaction regex expanded to cover DOB, address-line, phone, email, name-prefix variants. `ResetForTests` + `_testReset` removed.
- **`Errors/WrapToolHandler.cs`** — Context dict is now passed through `WireLog.Redact` before being included in the structured error response.
- **`Odbc/ConnectionStringBuilder.cs`** — `dataPath` validation handles `{`, `}`, `;` and control chars.
- **`Cli/Commands/ToolsCommand.cs` / `UnregisterCommand.cs`** — NEW. Wired into Program.cs alongside register/login/status/field-access/doctor.

### Project layout (delta vs prior handoff)

```
src/LacertMCP.Server/
  Bridge/
    BridgeInputValidation.cs    NEW — centralized bridge-boundary input checks
    Writer.cs                   modified — Marshal.ReleaseComObject on every call
    Reader.cs                   modified — same pattern
  Odbc/
    ConnectionPool.cs           DELETED — dead code
src/LacertMCP.Cli/Commands/
  ToolsCommand.cs               NEW — lists registered MCP tools via reflection
  UnregisterCommand.cs          NEW — discoverable AppID reset
.github/workflows/              NEW — CI build+test on Windows
.editorconfig                   NEW — MS C# defaults
scripts/
  build.ps1                     NEW
  test.ps1                      NEW
  release.ps1                   NEW
  build-phase14-addendum.js     NEW — regenerates Todo.md Phase-14 block from audit-findings.json
audit-findings.json             NEW — 71-finding record with verdicts
```

### Test landing breakdown (current)

- Errors: 53
- Schema (incl. new Modules tests, FieldAccessMapLoader branch coverage): 19 + 29 + 4 + 12 = 64
- Session/SimulationStore: 47
- Session/LacerteSessionManager (incl. new idempotency-race + EnsureEditionProbed-cache tests): ~34
- Odbc (incl. new ConnStrBuilder/SqlTemplates rows): ~95
- Bridge (incl. new BridgeInputValidationTests + RegistrationTests): 38 + 62 + ~6 + 10 skipped = ~116
- Tools (Read + Write + Create + Metadata + Health + paginated): ~90
- Cli (incl. new ToolsCommand + UnregisterCommand): ~25
- Logging / Resilience / Pagination / Prompts: ~40
- Parity / Integration: ~50
- **Total: 695 passing + 10 skipped = 705 across the suite. 0 failures, 0 warnings.**

### Gotchas (carry-forward)

- Use `$env:USERPROFILE\.dotnet\dotnet.exe` — system-wide `dotnet` is runtime-only.
- `LACERTE_DEBUG_WIRE=1` activates the wire log (now keeps file open; redacts expanded PII set).
- `LACERTE_SIM_EXPIRED=1` simulates expired 24h auth.
- `LACERTE_PARITY_RECORDING=path/to/file.json` activates parity-test recording adapter.
- `LACERTE_MCP_FORCE_FILE_STORE=1` + `APPDATA=<tempdir>` isolates Bridge.Registration.GetStoredAppId from Credential Manager.
- AppID GUID is permanent — `lacerte-mcp register --reset` OR new `lacerte-mcp unregister --confirm` remints.
- MCP SDK is preview-4 — watch for breaking API changes when 1.x ships (carried as F9).
- Idempotency cache is now per-key-locked with eviction; relying on cached-result-after-process-restart is invalid (was already true; now also true under TTL pressure).
- BridgeInputValidation fires BEFORE WireLog + AcquireWriter — off-Windows tests that previously asserted `BridgeUnavailable` for bad inputs will now see the validation error first (correctly).
- `ModuleInfo` record constructor takes 5 positional args now (was 3); any direct construction outside `Modules.All` needs `FileBasename` + `TaxTypeAliases`.

### How to /pickup this session

Run `/pickup`. It will:
1. Read this HANDOFF.md.
2. Read `Todo.md`.
3. Run the "Verify Before Continuing" checks above.
4. Report status without writing any code yet.

If a verification check fails, fix it BEFORE starting any new work.

The remaining real work is on a Windows + Lacerte + SDK box (Phase 13 tasks 87 + 88; Windows-gated follow-ups F1, F2, F4, F7).
