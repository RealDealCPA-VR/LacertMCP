# Handoff State

## Last Session Summary

**Two consecutive deep-audit-and-execute rounds landed on 2026-05-30:**

### Round 1 — Phase 14 (88+25+4 = 117 agents, 71 tasks)
- Audit: 10-lens parallel + adversarial verify → 78 candidates / 71 confirmed
- Execute: preflight cross-cluster Modules hoist (P14-020) → 22 parallel cluster agents → final verify
- Cleanup: closed 3 cross-cluster deferrals (P14-046 CLI verbs, P14-050 BridgeInputValidation, P14-055 Series numeric-string)
- Net: **481 → 695 tests** (+214 passing, 1 previously-skipped now active)

### Round 2 — Phase 15 (98+23+11 = 132 agents, 78 tasks)
- Audit: 12 new lenses (r1-quality, mcp-protocol, lifecycle, error-UX, integration-flows, observability, config-validation, test-quality, security-deep, cli-UX, deeper-spec, doc-consistency) + adversarial verify → 86 candidates / 78 confirmed
- Execute: preflight redaction-split (the round-1 over-redaction regression) → 21 parallel cluster agents → final verify (with 2 trivial test fixes by the final agent)
- Cleanup: 9 parallel agents + 1 session-bundle agent + 1 verifier closed the 15 cross-cluster deferrals
- Net: **695 → 1006 tests** (+311 passing)

**Final state: 1006/1016 tests passing, 0 failures, 10 expected live-only skips.** Build clean (0 warnings, 0 errors). 71/71 Phase-14 + 78/78 Phase-15 tasks complete. 75 files changed since the round-1 starting point, +6348 / -580 lines net.

### Key round-2 landings

- **Redaction-split fix (P15-006)** — The most critical finding: round 1's P14-018/P14-019 had over-redacted both wire and disk envelopes with one broad keyset, destroying operator-facing `reason` diagnostics and detail-row `Description` data. Split into `RedactionScope.Disk` (broad — protects against third-party log readers) vs `RedactionScope.Wire` (narrow — only true cross-context secrets: SSN/EIN/TIN/Bank/Routing/DOB/Email/Phone variants). `WrapToolHandler` uses Wire; `WireLog.WriteRecord` uses Disk. Existing tests that pinned the over-redaction were corrected.
- **Idempotency cache lifecycle (P15-060)** — Sliding-expiration model with configurable TTL via `LACERTE_IDEMPOTENCY_TTL_SECONDS`. SemaphoreSlim cleanup on eviction.
- **CancellationToken plumbing (P15-013)** — Every public chokepoint on `LacerteSessionManager` accepts `CancellationToken` (default None); MCP tool methods plumb it through; ODBC commands honor cancellation.
- **Bulk write tool (P15-008)** — New `lacerte_field_write_bulk` tool: N writes through one MCP call, per-row idempotency key shape `{prefix}-{clientNumber}-{fieldName}`, `continueOnError` flag, dryRun support, full per-row result envelope.
- **Detail Series push-down (P15-009)** — `lacerte_detail_read` now takes optional `series` filter; SqlTemplates.DetailBySeries activated.
- **EF status since-date filter (P15-010)** — `lacerte_ef_status` takes optional `sinceUtc`; SQL parameterized.
- **Schema reload tool (P15-044)** — `lacerte_reload_schema` MCP tool clears the field-access shard cache without process restart.
- **TaxType canonicalization (P15-059)** — `Modules.CanonicalizeTaxType(char, string)` used at every write site to normalize synthetic aliases before reaching the SDK.
- **Doctor probes (P15-032, P15-034)** — App.config shim validation + FieldAccessMap shard-count probe.
- **AutoReconnect observability (P15-014)** — Retries now emit `attemptNumber` + HRESULT to WireLog instead of being silent.
- **Field-write value type whitelist (P15-051)** — `NormalizeFieldWriteValue` rejects unsupported types with new `SyntheticStatusCodes.FieldWriteInvalid`.
- **Heartbeat writer (P15-054)** — Optional background service writes JSON heartbeat to `%TEMP%/lacerte-mcp-heartbeat.json` every N seconds (configurable via `LACERTE_HEARTBEAT_SECONDS`; 0 disables).
- **Result-shape logging (P15-072)** — WireLog records now include result-shape telemetry on read paths.
- **InternalServerError 9203 (P15-035)** — Round 1's catch-all `Error(0)` fallback in `WrapToolHandler` is now a proper synthetic code with actionable remediation pointing at `LACERTE_DEBUG_WIRE=1`. OdbcException routes through `OdbcErrorMapper` for SQLSTATE → TAPIStatus preservation.
- **Correlation IDs (P15-052)** — Wire envelopes now carry an 8-hex `cid` (AsyncLocal) that nested WireLog calls inherit; LLM bug reports can quote the cid to find every log line for one tool call.
- **Response envelope documentation (P15-048)** — README now has a `## Response envelope` subsection covering both success and error shapes plus the disk-vs-wire redaction split.
- **Round-2 docs sync** — README/REGRESSION_CHECKLIST/HANDOFF test counts updated to current; ARCHITECTURE.md/DECISIONS.md/.env.example aligned.

### Workflow artifacts on disk

- `audit-findings.json` (round 1, 71 findings) + `audit-findings-r2.json` (round 2, 78 findings) — full per-task records with verdicts and refinedRecommendations.
- `Todo.md` — Phase 14 (P14-001..071) + Phase 15 (P15-001..078) sections appended; all 149 tasks checked off.
- `scripts/build-phase14-addendum.js` + `scripts/build-phase15-addendum.js` + `scripts/cluster-phase15.js` — regenerators for the Todo.md addendum blocks and the cluster mapping.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report 0 warnings, 0 errors.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **1006 passed, 10 skipped, 0 failed**. The 10 skips are 9 `[Fact(Skip = "Requires real Lacerte SDK")]` in `Tests/Bridge/` + 1 Windows-only manual Credential Manager P/Invoke in `Tests/Cli/AppIdStoreTests.cs`.
- [ ] **CLI smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` → JSON payload exit 0. `tools` → tool table exit 0. `doctor` → exit 2 (PARTIAL) off-Windows; exit 0 on a green Windows + Lacerte box.
- [ ] **MCP server smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` starts cleanly; kill after confirming.

## Next Task

Off-Windows codebase is **comprehensively audited (two rounds, 22 lenses total) and hardened**. All sim-verifiable improvements have landed.

### Windows-gated work (single focused session on the operator's box)

1. **Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification"** (now includes BridgeInputValidation, App.config shim, and shard-count probe rows).
2. **Run `lacerte-mcp register`** to mint the AppID and click through Intuit dialogs; capture actual integer values for `AccessGranted` and `NotSignedIn` (closes F1 + F7).
3. **Run `LacertMCP.FieldAccessDumper.exe --module I --table 1`** on the live box to populate `Schema/FieldAccessMap/base/individual.json` via introspection. Then `lacerte_reload_schema` instead of restarting (P15-044).
4. **Capture `tests/Parity/fixtures/live-ty24-individual.json`** recording for the parity-test harness Live leg.
5. **Tag `v0.1.0`** after the first all-green pass.
6. **Record the 60-second walkthrough** for task #87 in Todo.md.

### Out of our control

- **F9** — bump `ModelContextProtocol` dependency to 1.x when it ships on NuGet (currently `0.3.0-preview.4`).

## Context Notes

### Structural additions since the bootstrap session

```
src/LacertMCP.Server/
  Bridge/
    BridgeInputValidation.cs      NEW (round 1 P14-050) — centralized bridge-boundary input checks
  Diagnostics/
    HeartbeatWriter.cs            NEW (round 2 P15-054) — hosted service writing JSON heartbeat
  Tools/
    FieldWriteBulkTools.cs        NEW (round 2 P15-008) — lacerte_field_write_bulk
    SchemaReloadTools.cs          NEW (round 2 P15-044) — lacerte_reload_schema

src/LacertMCP.Cli/Commands/
  ToolsCommand.cs                 NEW (round 1 P14-046) — lacerte-mcp tools
  UnregisterCommand.cs            NEW (round 1 P14-046) — lacerte-mcp unregister

.github/workflows/                NEW (round 1 P14-057) — CI build+test on Windows
.editorconfig                     NEW (round 1 P14-058) — MS C# defaults
scripts/
  build.ps1 / test.ps1 / release.ps1      NEW (round 1 P14-059, P14-060)
  build-phase14-addendum.js / build-phase15-addendum.js / cluster-phase15.js
                                  NEW workflow artifacts
audit-findings.json               NEW (round 1) — 71 findings
audit-findings-r2.json            NEW (round 2) — 78 findings

src/LacertMCP.Server/Odbc/
  ConnectionPool.cs               DELETED (round 1 P14-066 — dead code)
```

### Notable behavioral changes operators should know about

- **Wire redaction is now narrow** (round 2 P15-006): `reason`, `Description`, `City`, `Zip`, `FirstName/LastName` etc. now survive on the MCP wire. Logs on disk still scrub them. The only fields scrubbed everywhere are true cross-context secrets (SSN/EIN/TIN/Bank/Routing/DOB/Email/Phone variants).
- **Internal exceptions now surface as 9203** (round 2 P15-035), not the generic `Error(0)`. Set `LACERTE_DEBUG_WIRE=1` to see the underlying exception type + cid in the log file.
- **Correlation IDs**: every tool call gets an 8-hex `cid` echoed in success + error envelopes. The same cid appears in every log line for that call's nested operations.
- **Idempotency cache is now sliding-TTL** (round 2 P15-060) configurable via `LACERTE_IDEMPOTENCY_TTL_SECONDS` (default 3600).
- **Heartbeat writer is opt-in** (round 2 P15-054) via `LACERTE_HEARTBEAT_SECONDS` env var; default 30s; set 0 to disable.
- **Bulk writes** via `lacerte_field_write_bulk` — N rows through one MCP call. Default `continueOnError=false`; subsequent rows are marked skipped on first failure.

### Gotchas (carry-forward)

- Use `$env:USERPROFILE\.dotnet\dotnet.exe` — system-wide `dotnet` is runtime-only.
- `LACERTE_DEBUG_WIRE=1` activates wire log (file-handle kept open; disk-scope redaction).
- `LACERTE_DEBUG_LOG_PATH` overrides log dir.
- `LACERTE_SIM_EXPIRED=1` simulates expired 24h auth.
- `LACERTE_PARITY_RECORDING=path/to/file.json` activates parity-test recording adapter.
- `LACERTE_MCP_FORCE_FILE_STORE=1` + `APPDATA=<tempdir>` isolates AppID from Credential Manager.
- `LACERTE_IDEMPOTENCY_TTL_SECONDS` (NEW) overrides idempotency cache sliding TTL (default 3600).
- `LACERTE_HEARTBEAT_SECONDS` (NEW) configures heartbeat writer; default 30; 0 disables.
- AppID GUID is permanent — `lacerte-mcp register --reset` OR `lacerte-mcp unregister --confirm` remints.
- MCP SDK is preview-4 — watch for breaking API changes when 1.x ships (F9).
- BridgeInputValidation fires BEFORE WireLog + AcquireWriter — bad inputs throw validation error first, not BridgeUnavailable.
- `ModuleInfo` record constructor takes 5 positional args.
- Tests use `[Collection("AccessMapMutation")]` for access-map mutation and `[Collection("Cli (serial)")]` + `IsolatedAppIdScope` for AppID isolation.

### How to /pickup this session

Run `/pickup`. It will:
1. Read this HANDOFF.md.
2. Read `Todo.md`.
3. Run the "Verify Before Continuing" checks above.
4. Report status without writing any code yet.

If a verification check fails, fix it BEFORE starting any new work.

The remaining real work is on a Windows + Lacerte + SDK box (Phase 13 tasks 87 + 88; Windows-gated follow-ups F1, F2, F4, F7).
