# Handoff State

## Last Session Summary

**Three consecutive deep-audit-and-execute rounds landed on 2026-05-30:**

### Round 1 — Phase 14 (117 agents, 71 tasks)
- Audit: 10-lens parallel + adversarial verify → 78 candidates / 71 confirmed
- Execute: preflight cross-cluster Modules hoist → 22 parallel cluster agents → final verify
- Cleanup: closed 3 cross-cluster deferrals (CLI verbs, BridgeInputValidation, Detail Series numeric-string)
- Net: **481 → 695 tests** (+214)

### Round 2 — Phase 15 (132 agents, 78 tasks)
- Audit: 12 new lenses (r1-quality, mcp-protocol, lifecycle, error-UX, integration-flows, observability, config-validation, test-quality, security-deep, cli-UX, deeper-spec, doc-consistency) → 86 / 78 confirmed
- Execute: preflight redaction-split (the round-1 over-redaction regression) → 21 parallel cluster agents → final verify
- Cleanup: 11 agents closed 15 cross-cluster deferrals
- Net: **695 → 1006 tests** (+311)

### Round 3 — Phase 16 (115 + 19 + 1 = 135 agents, 85 tasks) — USABILITY + PERFECTION
- Audit: 14 usability lenses (llm-tool-usability, operator-onboarding, cross-tool-consistency, output-formatting, edge-cases, discoverability, recovery-ux, first-five-min, cli-polish, docs-accuracy, naming-quality, i18n-locale, perception-feel, mcp-client-compat) + adversarial verify (strict `meaningfullyImproves` gate) → 101 / 85 confirmed
- Execute: preflight cross-tool naming + response-shape canonicalization (15 refactors touching every tool file) → 17 parallel cluster agents → final verify
- Cleanup: 1 agent closed 8 carry-forwards (4 already done by preflight, 4 trivial fixes)
- Net: **1006 → 1113 tests** (+107)

**Final state: 1113/1123 tests passing, 0 failures, 10 expected live-only skips.** Build clean (0 warnings, 0 errors). 71/71 + 78/78 + 85/85 = **234 audit tasks complete across 3 rounds**. 147 files changed since bootstrap, ~10K lines net added.

### What round 3 changed

**Wire-surface naming canonicalized (preflight P16 X_naming_xtool, 15 refactors):**
- `taxYear` everywhere (was: `year` in MetadataTools/FieldWriteBulkTools/SessionTools responses)
- `stateAbbrev` everywhere (was: `stateAbbr` in DetailsTools.LacerteDetailWrite)
- `fieldName` everywhere (was: `field` in single-write tools, `fieldName` in bulk — now uniform)
- `module` parameter typed as `string` length-1 across MetadataTools (was: `char` — now matches SessionTools)
- `clientId` / `emailId` / `subClientId` per Microsoft acronym convention (was: ALL-CAPS Id suffix in CreateClientTools)
- Collection key `rows` is canonical (legacy domain aliases like `clients`/`forms`/`statuses` kept until v0.2 for back-compat)
- SchemaReloadTools dropped its inner `success` field (WrapToolHandler owns the outer envelope)
- Error envelope now carries `origin` ("sdk" / "synthetic") + lacerte_return_codes catalog carries `originGroup`
- LacerteClientRead description corrected — returns `{clientNumber, found, client}` envelope, never null
- LacerteDetailWrite — 7 row-key string params now default to "" (W-2 happy path drops from 12 args to 5)
- Phantom `lacerte_register` MCP tool name in remediation strings replaced with the actual CLI command `lacerte-mcp register`

**Other round-3 polish:**
- SessionMode enum serialized as lowercase ("sim"/"live") everywhere — was PascalCase in session_status
- 14 README updates: stale prompts table corrected (4 prompts, not 3), reload_schema + bulk_write rows added, troubleshooting expanded, "Try it (simulation mode)" section showing the 8 seeded ClientIDs (SMIT/JONE/REJE/BROW/PEND/ACME/WIDG/PART) so operators can demo without a Lacerte install
- 12 CLI polish items: `--version`, `--no-color`, JSON output for all commands, exit-code consistency, help-text polish, subcommand aliases
- 7 error-UX items: clearer userMessage / remediation strings, code-name consistency, accurate next-step commands
- 5 write-tool polish items: parameter ordering required-first, default values for genuinely-optional row-keys
- 3 pagination edge cases: limit=0 returns zero rows (not full set), negative offset clamped, `returnedCount` field clarifies vs requested `limit`
- 6 SessionStatusSnapshot extensions: debug-log state exposed so operator hitting BridgeUnavailable knows where the log file is
- Tool descriptions stripped of internal audit IDs (P14-XXX/P15-XXX references gone from operator-visible text)
- `NamingConventionsTests.cs` (13 reflection-driven sentinels) prevents future drift on the new conventions
- DECISIONS.md gained dated 2026-05-30 entry documenting the cross-tool naming conventions established by P16 preflight

### Workflow artifacts (on disk)

- `audit-findings.json` + `audit-findings-r2.json` + `audit-findings-r3.json` — 234 audit entries with verdicts + refinedRecommendations
- `Todo.md` — Phases 14 + 15 + 16 (P14-001..071, P15-001..078, P16-001..085) all checked off (234 tasks)
- `scripts/build-phase14-addendum.js` + `scripts/build-phase15-addendum.js` + `scripts/cluster-phase15.js` + `scripts/cluster-phase16.js` — regenerators

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report 0 warnings, 0 errors.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **1113 passed, 10 skipped, 0 failed**. The 10 skips are 9 `[Fact(Skip = "Requires real Lacerte SDK")]` in `Tests/Bridge/` + 1 Windows-only Credential Manager P/Invoke in `Tests/Cli/AppIdStoreTests.cs`.
- [ ] **CLI smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` → JSON exit 0. `tools` → tool table exit 0. `doctor` → exit 2 (PARTIAL) off-Windows; exit 0 on green Windows + Lacerte box.
- [ ] **MCP server smoke:** `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` starts cleanly; kill after confirming.

## Next Task

Off-Windows codebase is **comprehensively audited (3 rounds, 36 lenses total) and hardened**. All sim-verifiable improvements have landed.

### Windows-gated work (single focused session on the operator's box)

1. **Walk `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification"** (now includes BridgeInputValidation, App.config shim, shard-count probe rows).
2. **Run `lacerte-mcp register`** to mint AppID and click through Intuit dialogs; capture actual integer values for `AccessGranted` and `NotSignedIn` (closes F1 + F7).
3. **Run `LacertMCP.FieldAccessDumper.exe --module I --table 1`** on the live box. Then `lacerte_reload_schema` instead of restarting (P15-044).
4. **Capture `tests/Parity/fixtures/live-ty24-individual.json`** recording for parity-test Live leg.
5. **Tag `v0.1.0`** after first all-green pass.
6. **Record 60-second walkthrough** for task #87 in Todo.md.

### Out of our control

- **F9** — bump `ModelContextProtocol` dependency to 1.x when it ships on NuGet (currently `0.3.0-preview.4`).

## Context Notes

### Wire-surface conventions (canonical as of round 3)

Tool parameters (always camelCase):
- `taxYear` (int) — never `year`
- `stateAbbrev` (string, "" or 2-letter postal) — never `stateAbbr`
- `fieldName` (string) — never `field`
- `module` (string length-1, "I"/"C"/etc.) — never `char`
- `clientId` / `emailId` / `subClientId` (Microsoft acronym convention)

Tool response envelope (outer always `{success:true, result:{...}}` from WrapToolHandler):
- Collection key: `rows` (legacy domain aliases kept till v0.2)
- Counts: `total`, `limit` (requested), `returnedCount` (actual)
- Pagination: `hasMore`, `nextOffset`
- Mode strings: lowercase ("sim"/"live")
- Correlation ID: `cid` (8-hex AsyncLocal, also stamped in nested WireLog records)

Error envelope (when WrapToolHandler catches LacerteSDKError):
- `{success:false, statusCode, statusName, severity, userMessage, remediation, origin, context?, cid}`
- `origin` ∈ {"sdk", "synthetic"} — disambiguates the 28 SDK codes vs 13 synthetic 9xxx codes
- `cid` echoed for log-line correlation

### Structural additions since bootstrap

```
src/LacertMCP.Server/
  Bridge/
    BridgeInputValidation.cs        (round 1 P14-050)
  Diagnostics/
    HeartbeatWriter.cs              (round 2 P15-054)
  Tools/
    FieldWriteBulkTools.cs          (round 2 P15-008)
    SchemaReloadTools.cs            (round 2 P15-044)
    Internal/FieldWriteCore.cs      (round 3 P16 — shared single+bulk write code)
  Session/
    SimulationTypes.cs              (round 3 P16 — extracted from SimulationStore)
src/LacertMCP.Cli/Commands/
  ToolsCommand.cs                   (round 1 P14-046)
  UnregisterCommand.cs              (round 1 P14-046)
src/LacertMCP.Tests/Tools/
  NamingConventionsTests.cs         (round 3 P16 — 13 reflection-driven sentinels)
src/LacertMCP.Tests/Integration/
  McpDiscoveryRoundtripTests.cs     (round 3 P16)
  FullFlow_CsvIntake.cs             (round 3 P16)
.github/workflows/                  (round 1)
.editorconfig                       (round 1)
scripts/build|test|release.ps1      (round 1)
scripts/build-phase14-addendum.js
scripts/build-phase15-addendum.js + cluster-phase15.js
scripts/cluster-phase16.js          (round 3)
audit-findings.json                 (round 1 — 71 findings)
audit-findings-r2.json              (round 2 — 78 findings)
audit-findings-r3.json              (round 3 — 85 findings)
src/LacertMCP.Server/Odbc/
  ConnectionPool.cs                 DELETED (round 1 P14-066 — dead)
```

### Operator-facing notable changes (cumulative across 3 rounds)

- **Wire redaction split** (round 2 P15-006): broad on-disk, narrow on-wire — operator-data Descriptions and `reason` fields preserved in tool responses.
- **InternalServerError 9203** (round 2 P15-035): generic exceptions no longer surface as the misleading code 0.
- **Correlation IDs** (round 2 P15-052): 8-hex `cid` on every tool call, echoed in nested WireLog lines.
- **Idempotency sliding TTL** (round 2 P15-060): configurable via `LACERTE_IDEMPOTENCY_TTL_SECONDS`; per-key SemaphoreSlim eviction.
- **CancellationToken plumbed** (round 2 P15-013): tools honor MCP cancel notifications.
- **Bulk writes** (round 2 P15-008): `lacerte_field_write_bulk` with per-row idempotency + continueOnError.
- **Detail Series filter** (round 2 P15-009): `lacerte_detail_read` accepts optional `series` int filter.
- **EF status since-date** (round 2 P15-010): `lacerte_ef_status` accepts optional `sinceUtc`.
- **Schema hot-reload** (round 2 P15-044): `lacerte_reload_schema` MCP tool.
- **TaxType canonicalization** (round 2 P15-059): synthetic aliases normalized at write boundary.
- **Heartbeat writer** (round 2 P15-054): opt-in via `LACERTE_HEARTBEAT_SECONDS`.
- **App.config + schema-shard Doctor probes** (round 2 P15-032, P15-034).
- **Cross-tool naming uniformity** (round 3 P16 preflight): `taxYear`/`stateAbbrev`/`fieldName`/`clientId` everywhere.
- **Pagination edge cases fixed** (round 3 P16-037, P16-074): limit=0 returns zero rows; `returnedCount` separated from requested `limit`.
- **Operator onboarding** (round 3): "Try it (simulation mode)" README section with the 8 seeded ClientIDs.
- **4 MCP prompts** (round 3): added `lacerte_csv_intake`, alongside `lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_field_write_bulk`.

### Gotchas (carry-forward)

- Use `$env:USERPROFILE\.dotnet\dotnet.exe` — system-wide `dotnet` is runtime-only.
- `LACERTE_DEBUG_WIRE=1` activates wire log (disk-scope redaction, file handle stays open).
- `LACERTE_DEBUG_LOG_PATH` overrides log dir; default `./logs`.
- `LACERTE_SIM_EXPIRED=1` simulates expired 24h auth.
- `LACERTE_PARITY_RECORDING=path/to/file.json` activates parity-test recording adapter.
- `LACERTE_MCP_FORCE_FILE_STORE=1` + `APPDATA=<tempdir>` isolates AppID from Credential Manager.
- `LACERTE_IDEMPOTENCY_TTL_SECONDS` configures idempotency cache TTL (default 3600).
- `LACERTE_HEARTBEAT_SECONDS` configures heartbeat writer; default 30; 0 disables.
- AppID GUID is permanent — `lacerte-mcp register --reset` OR `lacerte-mcp unregister --confirm` remints.
- MCP SDK is preview-4 — watch for breaking API changes when 1.x ships (F9).
- BridgeInputValidation fires BEFORE WireLog + AcquireWriter — bad inputs throw validation error first, not BridgeUnavailable.
- `ModuleInfo` record constructor takes 5 positional args.
- Tests use `[Collection("AccessMapMutation")]` for access-map mutation and `[Collection("Cli (serial)")]` + `IsolatedAppIdScope` for AppID isolation.
- `NamingConventionsTests` enforces the round-3 wire conventions — adding new tools that violate (e.g. `year` instead of `taxYear`, or `clientID` instead of `clientId`) will fail the suite.

### How to /pickup this session

Run `/pickup`. It will:
1. Read this HANDOFF.md.
2. Read `Todo.md`.
3. Run the "Verify Before Continuing" checks above.
4. Report status without writing any code yet.

The remaining real work is on a Windows + Lacerte + SDK box (Phase 13 tasks 87 + 88; Windows-gated follow-ups F1, F2, F4, F7).
