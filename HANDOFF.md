# Handoff State

## Last Session Summary

**Five deep-audit-and-execute rounds landed (2026-05-30 through 2026-06-05):**

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

### Round 4 — Phase 17 (40 + 11 = 51 agents, 17 tasks) — POST-SATURATION
- Audit: 10 post-saturation lenses (r3-quality, code-health, test-pyramid, production-readiness, ops-runbook, blast-radius, threat-model, cross-platform, build-repro, naming-post-P16) + double-strict verifier (novel + materiallyImproves) → 30 candidates / 17 confirmed
- Execute: 10 parallel cluster agents + 1 final verify (resolved cross-cluster ILacerteBridge.WriteDetail signature mismatch + 2 trivial test fixes)
- Surfaced TWO real P16 regressions: prompts weren't renamed when tool params were (`clientID`→`clientId` only on params, prompts left ALL-CAPS); same drift in error remediation strings
- Net: **1113 → 1121 tests** (+8 net; test-pyramid cleanup P17-015 also removed ~29 duplicate sim-twice parity runs, so underlying coverage improved more than the count suggests)

### Round 5 — Phase 18 (70 + 13 = 83 agents, 36 tasks) — DEEP FLAWS + ASPIRATIONAL FEATURES
- Audit: 11 lenses split 6/5 between flaw-hunting (subsystem-interactions, concurrency-under-load, end-to-end-happy-path-walk, real-LLM-traffic, unmodeled-SDK, state-corruption-races) and aspirational feature ideation (high-value-tools, MCP resources, in-MCP docs, observability tools, operator accelerators). Per-phase strict verifiers. 34 flaws → 23 confirmed; 25 features → 13 confirmed.
- Execute: preflight A_session bundle (9 interlocked LacerteSessionManager + AutoReconnect fixes) → 6 parallel fix clusters → 5 parallel feature clusters (13 new MCP tools shipped) → final verify (added [Collection] markers to serialize static-state-touching tests)
- Surfaced one critical missed bug: dryRun absent from BuildPayload fingerprint silently poisoned idempotency cache (preview-then-commit with same key returned cached Success and skipped the wire entirely). Plus AutoReconnect double-write, DbfRow.Fields non-concurrent dict, CreateClient counter rollback under contention, Delete-then-Move that can lose persisted cache, and 6 prompt content errors (DuplicateClient as status 18 vs actual 12, idempotencyKey reuse across N rows, etc.)
- 13 new operator-facing tools: lacerte_client_summary, lacerte_diff_clients, lacerte_export_client (resource-style), lacerte_help, 5 observability tools (recent_log, request_stats, cache_state, resource_usage, active_locks), 4 accelerators (firm_dashboard, pre_filing_check, year_over_year, field_history)
- Net: **1121 → 1301 tests** (+180; +12 P18 session tests + ~168 feature/observability/accelerator tests)

**Final state: 1301/1312 tests passing, 0 failures, 11 expected skips.** Build clean (0 warnings, 0 errors). 71/71 + 78/78 + 85/85 + 17/17 + 36/36 = **287 audit tasks complete across 5 rounds**. 217+ files changed since bootstrap, ~14.5K lines net added.

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

### What round 4 changed (key landings)

- **P17-005**: `WorkflowPrompts.cs` ALL-CAPS regression — 9 sites updated from `clientID`/`emailID` to `clientId`/`emailId`. New `NamingConventionsTests.PromptBodies_NoAllCapsIdSuffix_P17_005` sentinel reflects over `[McpServerPrompt]` bodies so the gap that let this drift past round 3 is closed permanently.
- **P17-006**: Error remediation strings purged of ALL-CAPS `subClientID`/`ClientID`/`emailID` — enum constant NAMES kept (wide blast radius) but every UserMessage/Remediation now uses canonical camelCase. New `RemediationStrings_NoAllCapsIdSuffix_P17_006` sentinel.
- **P17-001**: Live `WriteDetail` silently dropped `replaceIndex` — every "replace" became "append" on Windows. Threaded through `ILacerteBridge.WriteDetail` signature + `Bridge/Writer.cs`. Sim already honored it. New SyntheticStatusCode 9019 `LiveReplaceIndexUnsupported` for the temporary fail-closed gate.
- **P17-002**: Live `WriteField` bypassed table-3/4 stateAbbrev + table-2/4 subClientId discriminator gates — sim caught with 9005/9006 but live silently wrote wrong field. Mirrored gates in live branch.
- **P17-003**: Heartbeat payload now populates ExpiresAt/MinutesUntilExpiry/buildVersion (was missing despite README promise).
- **P17-004**: Idempotency cache persists to `%APPDATA%/lacerte-mcp/idempotency-cache.json` (was in-memory only; restart lost replay protection). Disable via `LACERTE_IDEMPOTENCY_PERSIST=0`.
- **P17-007**: 9102 IdempotencyConflict no longer leaks a 48-bit SHA-256 prefix (brute-force oracle on small payloads). Hash only revealed under `LACERTE_DEBUG_WIRE=1`.
- **P17-008**: `lacerte_field_write_bulk` now caps entries at 500 (configurable via `LACERTE_BULK_WRITE_MAX`). Rejects with 9008 + clear message above cap.
- **P17-009**: InformationalVersion git-SHA suffix wired through CI (`-p:SourceRevisionId=$(git rev-parse HEAD)` in `ci.yml` + `release.ps1`). Reflection test asserts presence.
- **P17-010 + P17-016**: `PathValidation.ValidateLogDir` now resolves symlinks/junctions before allowlist check (was vulnerable to junction redirect). POSIX path fails closed with a clear error.
- **P17-011**: Read-tool response row keys now camelCase on the wire (`clientNumber`, `subClientId`, `stateAbbrev`) instead of DBF-PascalCase (`ClientNumber`, `SubClientID`, `StateAbbrev`). DBF field codes like `C1_42` preserved verbatim.
- **P17-012 + P17-013**: README troubleshooting expanded — "write succeeds but I don't see the change in Lacerte UI" + "Claude Desktop config saved but no lacerte_ tools appear" with diagnostic steps.
- **P17-014**: Heartbeat file is now per-process — `%TEMP%/lacerte-mcp-heartbeat-{pid}.json` (was: clobbered when two MCP instances ran).
- **P17-015**: Parity tests no longer run sim-twice — `[BothModesData]` gates the Live leg behind `LACERTE_PARITY_RECORDING` env var or a non-example fixture under `Parity/fixtures/`. Off-Windows/CI now shows 1 honest skip (`LiveLegParityPlaceholder.LiveLeg_AwaitsRecordedReplayFixture`) instead of ~29 fake passes. Live leg auto-flips green when the operator drops a real recording.
- **P17-017**: `Bridge/Registration.GetStoredAppId` no longer drifts from `AppIdStore` on `LACERTE_MCP_FORCE_FILE_STORE` semantics — both paths share a test that round-trips.

### New documentation (2026-06-05)

- **[`INSTRUCTION_MANUAL.md`](INSTRUCTION_MANUAL.md)** (1448 lines) — comprehensive operator manual. 21 sections covering install (sim + live), every tool with parameter shapes, all 4 prompts, all 7 CLI subcommands, full env-var table, common workflows (A-G), error reference (SDK + TPSDKStatus + synthetic), wire conventions, best practices, two-modes comparison, operational notes, 9-scenario troubleshooting, architecture overview, glossary. Self-contained — a new operator can install and use every feature without reading anything else.
- **[`SKILL.md`](SKILL.md)** (442 lines) — Claude Code skill file. YAML frontmatter with positive triggers (operator phrases, sim seed IDs, CLI commands, env vars, TAPIStatus, DBF tables) + explicit negative triggers (Drake/UltraTax/ProConnect/TurboTax/QuickBooks). Body covers when-to-use, mode awareness, mandatory first-step discipline, wire conventions, the full 38-tool catalog by intent, 4 prompts, 9 multi-step recipes, idempotency rules, error tables (SDK + synthetic), 11 anti-patterns, escape hatches. Optimized for LLM consumption — read once, drive correctly thereafter.
- README.md "Reference" section now points to both as the canonical starting docs.

### Workflow artifacts (on disk)

- `audit-findings.json` + `audit-findings-r2.json` + `audit-findings-r3.json` + `audit-findings-r4.json` + `audit-findings-r5.json` — 287 audit entries with verdicts + refinedRecommendations
- `Todo.md` — Phases 14 + 15 + 16 + 17 + 18 (P14-001..071, P15-001..078, P16-001..085, P17-001..017, P18-001..036) all checked off
- `scripts/build-phase14-addendum.js` + `scripts/build-phase15-addendum.js` + `scripts/cluster-phase15.js` + `scripts/cluster-phase16.js` + `scripts/cluster-phase17.js` + `scripts/cluster-phase18.js` + `scripts/show-round4.js` + `scripts/show-round5.js` — regenerators

### New MCP tools shipped in round 5

- **`lacerte_client_summary(clientNumber)`** — one-call client snapshot fanning out client/subclients/states/statuses/forms/ef_status.
- **`lacerte_diff_clients(client1Number, client2Number)`** — structured diff: {onlyIn1, onlyIn2, valuesDiffer}.
- **`lacerte_export_client(clientNumber)`** — round-trip JSON with explicit `uri` field (resource-style; promotes to MCP Resource when SDK 1.x ships).
- **`lacerte_help(topic?)`** — README-equivalent text segmented by topic (quickstart/tools/troubleshooting/envvars/errors/overview).
- **`lacerte_recent_log(cid?, limit?)`** — in-process WireLog tail with wire-scope redaction. Source for LLM self-diagnosis.
- **`lacerte_request_stats()`** — uptime aggregate: total calls, error rate, autoReconnect attempts, p50/p95.
- **`lacerte_cache_state()`** — idempotency/edition/canUseNewApis cache sizes + TTL state.
- **`lacerte_resource_usage()`** — process memory + handle count snapshot.
- **`lacerte_active_locks()`** — live count of held/queued semaphores on idempotency map.
- **`lacerte_firm_dashboard()`** — per-(taxYear, module) client count + EF status breakdown in one call.
- **`lacerte_pre_filing_check(clientNumber)`** — missing required fields + F4 not Ready + EF rejected/pending in one call.
- **`lacerte_year_over_year(clientNumber, fromYear, toYear)`** — diff a client across tax years.
- **`lacerte_field_history(clientNumber, fieldName)`** — audit log of writes to one field via WireLog query.
- **`lacerte_prompts()`** — lists the 4 MCP prompts + returns body for hosts that do not expose Prompts UI.

## Verify Before Continuing

- [ ] **Build:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` — must report 0 warnings, 0 errors.
- [ ] **Tests:** `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` — must report **1301 passed, 11 skipped, 0 failed**. The 11 skips are 9 `[Fact(Skip = "Requires real Lacerte SDK")]` in `Tests/Bridge/` + 1 Windows-only Credential Manager P/Invoke in `Tests/Cli/AppIdStoreTests.cs` + 1 `LiveLegParityPlaceholder.LiveLeg_AwaitsRecordedReplayFixture` (flips green when operator drops a recording under `tests/Parity/fixtures/`).
  - **P17-015 parity-test counts will SHIFT:** `[BothModesData]` now gates the Live leg behind `LACERTE_PARITY_RECORDING` (env var) or a non-example fixture under `src/LacertMCP.Tests/Parity/fixtures/`. Off-Windows/CI with no fixture: ~29 Live-leg theory rows drop (was: pass-as-sim-twice), and `LiveLegParityPlaceholder.LiveLeg_AwaitsRecordedReplayFixture` adds 1 Skip with an explicit reason. Expected new totals: **~1084 passed, 11 skipped, 0 failed** (sim leg of every parity Theory still runs; Live leg surfaces as a single honest Skip instead of 29 false passes). When the operator captures `tests/Parity/fixtures/live-ty24-individual.json` (Next Task item #4), the placeholder flips to green and all 29 Live-leg rows re-emit automatically — no theory-method edits needed. See `src/LacertMCP.Tests/Parity/BothModes.cs` (P17-015) for the gate.
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
