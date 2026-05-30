# Lacerte MCP — Build Sequence

Comprehensive ordered task list. Each item is a one-line summary; deeper context lives in `ARCHITECTURE.md`, dated `DECISIONS.md` entries, per-task `ACCEPTANCE_CRITERIA.md` rows, and the relevant research report under `docs/research/agents/`.

Order rationale: simulation correctness first → read tools → write tools → create tools → live mode hardening → delivery. This mirrors the QB MCP's proven sequencing so most work is verifiable off-Windows; the live-mode tasks are last because they require a Windows + Lacerte box.

---

## Phase 0 — Foundations (.NET solution + operating system)

Stack decision is locked: C# / net48 / `ModelContextProtocol` 1.3.0 — see `DECISIONS.md` 2026-05-30 "Language/runtime stack".

- [x] **1.** Create `LacertMCP.sln` at repo root containing `LacertMCP.Server.csproj` (net48 x64), `LacertMCP.Cli.csproj` (net48 x64), `LacertMCP.Tests.csproj` (net48 xUnit). Wire project references.
- [x] **2.** Add NuGet packages to `LacertMCP.Server`: `ModelContextProtocol` **0.3.0-preview.4** (latest available — 1.x not yet released, see DECISIONS.md 2026-05-30 "MCP SDK version pinning"), `Microsoft.Extensions.Hosting` 8.0.1, `Microsoft.Extensions.Logging.Console` 8.0.1, `System.Text.Json` 8.0.6. `System.Data.Odbc` confirmed in BCL on net48.
- [x] **3.** Author `LacertMCP.Server/App.config` with `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6"`. Set `<PlatformTarget>x64</PlatformTarget>` in the csproj.
- [x] **4.** Author `Program.cs`: `Host.CreateApplicationBuilder` → `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` → run. `LacerteSessionManager` is currently a colocated stub in `Program.cs` — Phase 3 task #25 replaces it with the real DI registration.
- [x] **5.** Phase-0 spike: `[McpServerTool] LacerteHealth()` returning `"reachable (sim)"` lives in `src/LacertMCP.Server/Tools/HealthTools.cs`. **Build verified** via `dotnet build -c Debug -p:Platform=x64` — 0 warnings, 0 errors. Live-mode HRESULT call deferred to Phase 9 task #62 (requires the COM library + Windows). Stack locked.
- [x] **6.** Operating-system files at repo root (`HANDOFF.md`, `Todo.md`, `ARCHITECTURE.md`, `DECISIONS.md`, `REQUIREMENTS.md`, `REGRESSION_CHECKLIST.md`, `ACCEPTANCE_CRITERIA.md`, `CLAUDE.md`). `.gitignore` covers `node_modules/`, `dist/`, `logs/` and similar — **needs `bin/`, `obj/`, `*.user` added** when convenient (carried as follow-up F8).

## Phase 1 — Error mapping + schema metadata (pure code, no SDK)

These are dependency-free; everything else needs them.

- [x] **7.** `Errors/StatusCodes.cs` ships the full TAPIStatus 0–28 + TPSDKStatus 200–204 table with `GetStatusInfo(code)` + `Unknown(N)` fallback. Inferred codes carry `// TODO F1: verify integer against SDK headers`.
- [x] **8.** `Errors/LacerteSDKError.cs` is an `Exception` subclass with readonly `StatusCode`, `StatusName`, `Severity`, `UserMessage`, `Remediation`, `Context`. Looks up synthetic table first, then `StatusCodes`.
- [x] **9.** `Errors/SyntheticStatusCodes.cs` enumerates 9001–9009 (validation), 9101/9102 (auth), 9201/9202 (bridge), with 9010/9011 carried as legacy aliases pending Stream 4 migration.
- [x] **10.** `Errors/WrapToolHandler.cs` — `Run(Func<Task<object>>)` returns `{success:true, result}` on success, structured `{success:false, statusCode, ...}` on `LacerteSDKError`, fallback to `Error(0)` on other `Exception` (preserves `rawError` + `exceptionType` in context).
- [x] **11.** `Schema/Modules.cs` — 9-module catalog (`I/C/P/S/F/R/T/N/B`) + `GetByChar` + `GetByReturnType` round-trips.
- [x] **12.** `Schema/Tables.cs` — DATA1..DATA8 catalog with `Reserved` flag on DATA8.
- [x] **13.** `Schema/FieldAccessMap/` — sharded JSON files exist (`base/{9 modules}.json` all `{}` for v1; `overlays/ty21-not-supported.json` as `{taxYear:2021, overrides:{}}`; `manifest.json`, `README.md`). Honest finding: the source spec only *describes* the upstream PDF's "Database Field Access" table without literal field names; v1 ships fail-closed and gets populated via Phase-9 introspection (follow-up F5).
- [x] **14.** `Schema/FieldAccessMapLoader.cs` — lazy per-module JSON loader with `ConcurrentDictionary` cache, universal `C{t}_0 → ReadOnly` rule, overlay→base→`Unknown` lookup order, `AssertWritable(...)` throws the right `LacerteSDKError` per agent 04 §7.
- [x] **15.** 53 status-code tests + 19 schema tests passing (Stream 2 + Stream 3 totals). Universal-rule, `Unknown` fallback, `force:true` escape hatch all verified.

## Phase 2 — Simulation store (no SDK; everything testable here)

- [x] **16.** `Session/SimulationStore.cs` ships the type set + triple-nested `ConcurrentDictionary` root + `Register()`/`SessionStatus()`/`ToggleExpired()` auth surface + stub method signatures for #18-21, #23 throwing `NotImplementedException("Phase 2 task #18+")`.
- [x] **17.** `Session/SimulationStore.Seed.cs` + `Session/Fixtures/SeedData.cs` populate the 8-client roster (SMIT, JONE, REJE TY20-I; BROW, PEND TY24-I; ACME, WIDG TY24-C; PART TY24-P). Detail rows currently 2-3 per client — expand to 5-15 carried as inline `// TODO`.
- [x] **18.** `SimulationStore.ProcessQuery(sql, SimulationContext)` shipped via `Session/SimulationStore.Query.cs` (compiled-regex pattern match on documented templates). TY21+ branch tries per-client tables first (`[{client}-SubClientInfo/StateClientInfo/StateSubClientInfo]`) when `year>=2021 && table∈{2,3,4}`, falls through to legacy forms. Throws on unrecognized SQL.
- [x] **19.** `SimulationStore.SimulateFieldWrite(...)` ships with full TAPIStatus sequence per agent 06 §4 — sessionExpired → year guard → required-args (DATA2 SubClientID, DATA3/4 stateAbbrev, DATA4 both) → `FieldAccessMapLoader.GetFieldAccess` → row lookup → length check → write.
- [x] **20.** `ReadDetail(client, Predicate<DetailRow>?)` + `WriteDetail(client, row, replaceIndex?)` — no validation in the store (footgun parity with live), `OutOfIndex` on bad index, `DetailCreationError` on unknown client.
- [x] **21.** `SimulateCreateClient(SimulateCreateClientArgs)` ships with all error paths: NotSignedIn → InValidTaxYear → LengthExceeded → InValidClientName → TaxModuleMismatch → DuplicateClient → mints via `Interlocked.Increment`.
- [x] **22.** `SimulationStore.Register()` + `SessionStatus()` + `ToggleExpired(bool)` shipped with `LACERTE_SIM_EXPIRED=1` env honored at construction. 12 Session tests passing.
- [x] **23.** `Snapshot() -> SimulationSnapshot` + `Restore(SimulationSnapshot)` — manual per-Map deep clone preserving `ConcurrentDictionary` identity + counters + auth latches. Restore wipes-then-rebuilds so post-snapshot cells are dropped.
- [x] **24.** 47 SimulationStore tests passing (12 Wave-1 + 35 Wave-2). Coverage: every chokepoint's TAPIStatus paths, snapshot/restore round-trip, seed presence, expired-session toggle.

## Phase 3 — Session manager (sim wired in; live stubbed)

- [x] **25.** `Session/LacerteSessionManager.cs` ships (~700 lines). Constructor reads env; mode rule = `(!sim && live && Win32NT) → Live; else Simulation`; immutable for process lifetime. Sim mode constructs `SimulationStore`; Live mode constructs but `_bridge=null` (Phase 9 wires) — all wire calls throw `BridgeUnavailable (9201)`.
- [x] **26.** `SessionContext` record + `SetContext(dataPath, year, module, readOnly=false)` + `RequireContext()` chokepoint guard throwing `9009 ContextNotSet`. Year < 2018 throws `9003 TaxYearTooOld`.
- [x] **27.** `CanUseNewApis(year, module)` caches per `(year, module)` in `ConcurrentDictionary`. Sim returns `year >= 2021`; Live throws `BridgeUnavailable` until Phase 9.
- [x] **28.** Read chokepoints shipped: `ListClients`, `ReadClient`, `ReadTable`, `ReadSubclients`, `ReadStates`, `ReadStatuses`, `ReadForms`, `ReadDetail`, `ReadEFStatus`. All `Task<T>` (sim wraps with `Task.FromResult`). Tools never see live↔sim branch.
- [x] **29.** Write chokepoints shipped: `WriteField`, `WriteDetail`, `CreateClient`. `WriteField` calls `FieldAccessMapLoader.AssertWritable` before any backend.
- [x] **30.** Read-only gate: `RejectIfReadOnly` is **synchronous** and runs FIRST on every write chokepoint (before await, before idempotency, before access map). Throws `LacerteSDKError(9001 FieldIsReadOnly)`.
- [x] **31.** Idempotency cache: `ConcurrentDictionary<string, IdempotencyCacheEntry>` keyed by `idempotencyKey`. SHA-256 fingerprint of `{op, args}` JSON. Cache hit + matching fingerprint → cached result; mismatch → `9102 IdempotencyConflict`.
- [x] **32.** Dry-run: every write chokepoint accepts `dryRun: true`. Local per-module deep-clone snapshot + execute + restore in `finally`. (TODO: delegate to `SimulationStore.Snapshot/Restore` now that task #23 has landed — flagged as carry-forward.)
- [x] **33.** `SessionStatus() -> SessionStatusSnapshot` returns `{Mode, IsRegistered, RegisteredAt, ExpiresAt, MinutesUntilExpiry, Context, AppName, AppId (masked)}`. Zero wire I/O.
- [x] **34.** 24 LacerteSessionManager tests passing. Covers every gate, every chokepoint in sim mode.

## Phase 4 — ODBC layer (live reads + sim adapter)

- [x] **35.** `Odbc/ConnectionStringBuilder.cs` ships — `Build(dataPath, returnType, taxYear)` returns `Driver={LacerteDSIIDriver};...`. Brace-wraps datapath if it contains `;`. Validates 2018-2099.
- [x] **36.** `Odbc/LacerteDsnReader.cs` ships. `Query(sql)` returns `IReadOnlyList<IReadOnlyDictionary<string,object?>>`; generic `Query<T>(sql, Func<IDataRecord, T>)` is the recommended typed path. Lazy `OdbcConnection`, reused across queries, `IDisposable`. NO `ExecuteNonQuery` (Boundary Invariant #6).
- [x] **37.** `Odbc/SqlTemplates.cs` ships — every template from agent 03 §4-§5 (DATA1-7 both forms, DATA2/3/4 TY21+ branches, EF status JOIN, per-client `[CLIENT-Detail]`). Identifier validation `^[A-Z0-9]{1,8}$` throws `InValidClientName`; state abbrev `^[A-Z]{2}$` throws `InValidStateAbbrev`.
- [x] **38.** `Odbc/OdbcErrorMapper.cs` ships — `Map(OdbcException, ctx?)` + public `MapSqlState(sqlState, message)` for testability. Preserves SQLSTATE in `context["sqlState"]`. Covers `08*`, `IM002`, `42S02`, `42S22`, `42000`, `HY*`, unknown.
- [x] **39.** ODBC layer ready for `LacerteSessionManager` wire-up — actual wiring deferred to a focused later task (sim path is functional; live wire-up needs both Stream B's reader AND a recorded ODBC fixture for parity tests).
- [x] **40.** Parity-tests **harness** is ready (F10 closed in 2026-05-30 wave-4 workflow). `BothModesData` test attribute + `ParitySessionFactory.For` / `ForRecording` adapters shipped; `RecordingBridge` (full `ILacerteBridge` impl) replays canned responses from a JSON file. Stub fixture demonstrates end-to-end. **Actual live recordings remain pending — a Windows + Lacerte operator must capture wire fixtures and drop them under `tests/Parity/fixtures/` to exercise the Live leg against real data.** Tracking under the Windows-gated work in HANDOFF.md, not as an open Todo item.

**Phase 4 totals: 78 new tests across `ConnectionStringBuilderTests`, `SqlTemplatesTests`, `OdbcErrorMapperTests`. No live ODBC connections opened by tests (env has no Lacerte install).**

## Phase 5 — Read tools

- [x] **41.** `Tools/SessionTools.cs` ships 5 tools (status, set_context, can_use_new_apis, list_modules, list_datapaths). None call the SDK.
- [x] **42.** `Tools/ClientsTools.cs` ships `lacerte_client_list` + `lacerte_client_read`.
- [x] **43.** `Tools/TablesTools.cs` ships generic `lacerte_table_read`. TY21+ branching handled by session manager.
- [x] **44.** `Tools/SubclientsTools.cs` ships `lacerte_subclient_read`.
- [x] **45.** `Tools/StatesTools.cs` ships `lacerte_state_read` + `lacerte_state_subclient_read`.
- [x] **46.** `Tools/StatusTools.cs` ships `lacerte_status_read`.
- [x] **47.** `Tools/FormsTools.cs` ships `lacerte_forms_read`.
- [x] **48.** `Tools/DetailsTools.cs` ships `lacerte_detail_read` (write also covered in #53).
- [x] **49.** `Tools/EFileTools.cs` ships `lacerte_ef_status`.
- [x] **50.** ACCEPTANCE_CRITERIA.md gained Phase 5 entries + README gained tool table. 38 read-tool tests passing.

## Phase 6 — Write tools

Writes are riskier than reads — every entry runs `AssertWritable` before touching the bridge.

- [x] **51.** `Tools/FieldWriteTools.cs` ships `lacerte_field_write` (pre-TY21 entrypoint; year-routing happens in session manager).
- [x] **52.** `Tools/UpdateReturnTools.cs` ships `lacerte_update_return` (TY21+ entrypoint; same chokepoint, year-routed). Both tools exist as distinct MCP surfaces for clarity.
- [x] **53.** `Tools/DetailsTools.cs` extends — `lacerte_detail_write` with mandatory client-side validation (Series non-empty, Code numeric `^-?\d+$`, StateAbbr `^[A-Z]{2}$` if non-empty, TSJ ∈ {T,S,J,empty}) — all throw `9008 DetailWriteInvalid` BEFORE the session call.
- [x] **54.** Every write tool routes `readOnly: true` context → 9001. Verified in `FieldWriteToolsTests`, `DetailsToolsTests`, `CreateClientToolsTests`.
- [x] **55.** Every write tool accepts `dryRun: true` and short-circuits before persisting. Cross-test access-map race fixed via `[Collection("AccessMapMutation")]` serialization.
- [x] **56.** Every write tool accepts `idempotencyKey`; same-key+same-payload replays cached result, conflict throws 9102. Verified in WriteParityTests.

## Phase 7 — Create tools

- [x] **57.** `Bridge/LacerteAccessClient.CreateClient(...)` wraps the `IClientList.SetDataPath → IClient → CreateClient` ceremony via late binding. TAPIStatus translated to `LacerteSDKError`. Live verification deferred to Windows + Lacerte box.
- [x] **58.** `Tools/CreateClientTools.cs` ships `lacerte_client_create`. Validations: ClientID required + ≤ 8 chars (`9002 ClientIDTooLong`) + `^[A-Z0-9]+$` (`24 InValidClientName`); TaxYear ≥ 2018 (`9003 TaxYearTooOld`); TaxType matches active module via `_moduleTaxTypes` whitelist (`14 TaxModuleMismatch`).
- [x] **59.** 10 `CreateClientToolsTests` cover all error paths. Stream A flagged a `TaxModuleMismatch` failure that Stream B fixed in the same wave.

## Phase 8 — Metadata helpers

- [x] **60.** `Tools/MetadataTools.cs` ships `lacerte_field_access_map(module, table, year)` — module/table/year validation + returns the loaded entries for the (module, table, year) tuple plus the universal C{t}_0 = ReadOnly rule.
- [x] **61.** `Tools/MetadataTools.cs` extends — `lacerte_return_codes` returns all 28 TAPIStatus + 5 TPSDKStatus + 11 synthetic 9xxx codes with full `{name, severity, userMessage, remediation?}` shape from `StatusCodes.GetStatusInfo`. Closes agent 05 follow-up #3.

## Phase 9 — COM bridge (live only; verifiable only on Windows + Lacerte)

- [x] **62.** `Bridge/LacerteAccessClient.cs` ships — wraps `LacerteSDK_Access.LacerteAccess` via late binding (`Type.GetTypeFromProgID` + `dynamic`). Implements `ILacerteBridge` + `IDisposable`. Off-Windows / SDK-missing → every method throws `BridgeUnavailable (9201)`. Live COM round-trip deferred to Windows + Lacerte box.
- [x] **63.** `Bridge/Registration.cs` ships — `ITPRegistration.Register(appName, appId, taxYear)` via late binding. AppID read from Windows Credential Manager (Server duplicates the read half of `LacertMCP.Cli.Storage.AppIdStore` to avoid a Server→Cli reference cycle). Boundary Invariant #8 honored — called ONCE in `LacerteSessionManager.Register()`.
- [x] **64.** `Bridge/Writer.cs` ships — `WriteToDBFDatabase(...)` (pre-TY21), `UpdateTaxReturn(...)` (TY21+), `WriteDetail(...)`. All late-bound `dynamic` so they compile without the SDK ref. Live verification deferred.
- [x] **65.** `Bridge/Reader.cs` ships — `ReadFromDBFData(appId, dataPath, dbf) -> string xml`. Used only for the rare introspection paths (field-access-map dumper). 99% of reads still go ODBC per Boundary Invariant #6. Live verification deferred.
- [x] **66.** `Bridge/CanUseNewApisProbe.cs` ships — `Probe(appId, year, module)` via late binding. Session manager caches the result per `(year, module)`; this class owns only the wire call. Live verification deferred.
- [x] **67.** Session-manager wire-up shipped (`Session/LacerteSessionManager.cs`): live-mode ctor now constructs `LacerteAccessClient`; `Register`, `CanUseNewApis`, `WriteField` (TY21+ branch chooses `UpdateTaxReturn` vs `WriteField`), `WriteDetail`, `CreateClient` all route through `_bridge` when non-null. `BridgeUnavailable` throws preserved as the null-`_bridge` fallback. Read chokepoints (`ListClients`, `ReadClient`, `ReadTable`, ...) still throw `BridgeUnavailable` in Live until ODBC wires (Phase 4 task #39).
- [x] **68.** `REGRESSION_CHECKLIST.md` gained a `## Manual Live-Mode Verification` section: 10 bridge round-trip items + 12 read tools + 5 write tools + 3 create tools + 3 auth lifecycle items + 3 sanity probes. Every item references `JOHNS` / TY24 Individual / `C:\Lacerte\24data` as the canonical test scope.

## Phase 10 — CLI (one-time + recovery + ops)

- [x] **69.** `LacertMCP.Cli/Program.cs` — `System.CommandLine`-based dispatcher. Root + 5 subcommands wired, beta4 API. Smoke-tested via `lacerte-mcp.exe --help` / `status`.
- [x] **70.** `lacerte-mcp register [--app-name X] [--tax-year Y] [--reset]` — mints GUID via `AppIdStore` (Credential Manager on Windows, file fallback off-Windows), prints with backup prompt. Live `ITPRegistration.Register()` invocation stubbed pending Phase 9 task #63 (bridge wrapper).
- [x] **71.** `lacerte-mcp login` — refuses without GUID; calls `Register()` against existing GUID. Live invocation stubbed pending Phase 9.
- [x] **72.** `lacerte-mcp status` — JSON payload: registered / appId (masked, or full via `--show-app-id`) / mode / storageBackend / storageLocation / platform. Zero wire I/O.
- [x] **73.** `lacerte-mcp field-access {set | list | discover}` — `list` reads via FieldAccessMapLoader + raw shard for completeness; `set` upserts + writes sorted; `discover` exits 2 with "requires live SDK" message (follow-up F5).
- [x] **74.** `lacerte-mcp doctor` — 7 probes: Windows version, x64 process, .NET 4.8 (registry Release ≥ 528040), ODBC DSN LacerteDSII (registry probe both hives), Lacerte SDK install dir, LACERTE_DATA_PATH env, AppID persistence. Exit 0/1/2 contract matches QB MCP `cli/doctor.ts`. Smoke-tested: doctor=1, discover=2, status=0.

## Phase 11 — Hardening + DX

- [x] **75.** Debug wire log shipped in `src/LacertMCP.Server/Logging/WireLog.cs`. `LACERTE_DEBUG_WIRE=1` activates; writes one JSON record per line (`{ts, op, args, durationMs, result|error}`) to `logs/lacerte-YYYYMMDD.log`; `LACERTE_DEBUG_LOG_PATH` overrides the dir. Redacts `SSN/EIN/BankAccountNumber/RoutingNumber` BEFORE the line hits disk via regex on the serialized JSON. Public surface: `WireLog.Record(op, args, work)` async, `WireLog.RecordSync(op, args, work)`, `WireLog.Redact(json)` for tests. Hook-up to session manager chokepoints carried as F11 (low-risk surface change).
- [x] **76.** `src/LacertMCP.Server/Resilience/AutoReconnect.cs` ships. `WithRetry<T>` + `WithRetrySync<T>` retry on whitelisted transient HRESULTs (`0x800706BA RPC_S_SERVER_UNAVAILABLE`, `0x80010108 RPC_E_DISCONNECTED`, `0x8001010A RPC_E_SERVERCALL_RETRYLATER`, `0x80010001 RPC_E_CALL_CANCELED`) with `[250, 500, 1000]ms` backoff, max 3 retries. NEVER retries `LacerteSDKError` (NotSignedIn, ConsentDenied stay terminal). Final throw carries `Data["AutoReconnect.Exhausted"]` + `Attempts` for diagnostics. Wiring into bridge call sites carried as F12.
- [x] **77.** `src/LacertMCP.Server/Pagination/Pagination.cs` ships. `Pagination.Page<T>(source, limit?, offset?)` returns `Page<T>{Rows, Total, Limit, Offset, HasMore, NextOffset}`. Clamps to `MaxLimit=1000`, defensively normalizes negative offset. Existing tools NOT yet retrofitted — carried as F13.
- [x] **78.** `src/LacertMCP.Server/Bridge/EditionDetection.cs` ships. `Probe(ILacerteBridge?)` returns `EditionInfo{LacerteEdition, LacerteBuild, SdkVersion}`. Sim path returns sentinel `"Simulated"/"0.0.0-sim"/"0.0-sim"`. Live path throws `BridgeUnavailable` with a Phase-9 TODO for the late-bound COM probe sequence. Session-manager caching + `SessionStatus` surfacing carried as F14.
- [x] **79.** `src/LacertMCP.Server/Prompts/WorkflowPrompts.cs` ships three prompts registered via SDK attributes (`[McpServerPromptType]` + `[McpServerPrompt(Name=...)]`): `lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_bulk_field_update`. Each returns a `GetPromptResult` with a single `Role.User` `TextContentBlock` referencing real `lacerte_*` tool names so the agent's tool-use loop is deterministic. Wired into `Program.cs` via `.WithPromptsFromAssembly()`.

## Phase 12 — Test coverage

- [x] **80.** `src/LacertMCP.Tests/Parity/BothModes.cs` ships — `[BothModesData]` xUnit `DataAttribute` fans out every theory across `SessionMode.Simulation` and `SessionMode.Live`. `ParitySessionFactory.For(mode)` is the centralized session-construction seam. **Both legs currently resolve to Simulation** (the factory pins `LACERTE_SIMULATION=1` so the Live leg doesn't blow up with `BridgeUnavailable` on a Windows host); when the recorded-replay live adapter lands (F10), branch on `mode` in the factory and the harness picks it up.
- [x] **81.** `src/LacertMCP.Tests/Parity/RoundTripTests.cs` ships — covers `ListClients` (+last-name filter), `ReadClient` (known + unknown), `ReadSubclients`, `ReadStates`, `ReadStatuses`, `ReadForms`, `ReadDetail`, `ReadEFStatus`, `ReadTable` (unknown table). 22 theory rows (11 cases × 2 legs).
- [x] **82.** `src/LacertMCP.Tests/Parity/WriteParityTests.cs` ships — covers `WriteField` (read-only gate 9001, no-context gate 9009, DATA2/3 access-gate-first fail-closed, universal C{t}_0 ReadOnly → ExposedForRead, unknown-field 9001, idempotency miss-cache-on-error), `WriteDetail` (read-only gate, bad replaceIndex), `CreateClient` (read-only gate, ClientID-too-long 9002, duplicate, idempotency same-key/same-payload replay, idempotency conflict 9102). 26 theory rows (13 cases × 2 legs).
- [x] **83.** `src/LacertMCP.Tests/Schema/FieldAccessMapCoverageTests.cs` ships — for each of the 9 base shards: file exists, parses as valid JSON, no duplicate keys; for each entry the loader returns the entry (Unknown only when the loader can't read the shadow-copied JSON). 29 tests passing. Loop bodies are no-ops while shards ship as `{}` per v1; populate during Phase 9 introspection.
- [x] **84.** `src/LacertMCP.Tests/Integration/FullFlowTests.cs` ships — `FullFlow_Sim_RegisterContextReadWriteCreateVerify` exercises Register → SetContext → ListClients → ReadClient → WriteField (asserts fail-closed access gate fires) → CreateClient → re-ListClients (confirms count+1 and the new ClientID) → idempotency replay returns the same ClientNumber → final SessionStatus is coherent.

## Phase 13 — Delivery

- [x] **85.** README has 4 install-path blocks (Claude Desktop / Cursor / Windsurf / generic stdio) + full env-var matrix (10 vars documented).
- [x] **86.** README has `## Lacerte SDK install` section: x64 MSI link, Intuit first-run prerequisite, `App.config` requirement, doctor command, register ceremony — 5 numbered steps.
- [ ] **87.** Acceptance demo — 60-second walkthrough recording: register, list clients, read one, write an address field, verify. **DEFERRED — requires Windows + Lacerte operator to record.**
- [ ] **88.** `v0.1.0` tag — **DEFERRED to first successful end-to-end live verification on a Windows + Lacerte box** per Phase 9 task #68 manual checklist. README front matter declares v0.1.0 status today; the git tag waits for the live green-pass.

---

## Cross-cutting follow-ups carried from research

These should be addressed during the relevant phase, not stand-alone tasks:

### Windows-gated (require Lacerte + SDK install — defer to the operator's machine)

- **F1.** Verify integer values for TAPIStatus codes 10-28 + 200-204 against the actual SDK headers (`tlbimp LacerteSDK_Interfaces.tlb` + ILSpy). Drop the `?` markers in `Errors/StatusCodes.cs`.
- **F2.** Decide on `VersionBlocked` collision strategy (TAPIStatus 11 vs TPSDKStatus 202). Both paths fire only on the live bridge.
- **F4.** Confirm Lacerte SDK 4.6 `<exe>.config` shim is honored by net48 host. If the Phase-0 spike fails on the Windows box, fall back to the hybrid design from agent 01 risk #3.
- **F7.** When the first operator finishes a real `lacerte-mcp register` on the Windows box, capture the actual integer for `AccessGranted` and `NotSignedIn` and lock the table.
- **F9.** Bump `ModelContextProtocol` dependency to 1.x when it ships on NuGet (currently `0.3.0-preview.4` is the latest published — out of our control). See DECISIONS.md 2026-05-30 "MCP SDK version pinning".

### Closed in 2026-05-30 wave-4 workflow

- [x] **F3.** Added synthetic code **9012 `UnknownFieldAccess`** in `SyntheticStatusCodes.cs`. `FieldAccessMapLoader.AssertWritable` Unknown branch now throws 9012 (was 9001). `RejectIfReadOnly` carries optional `table` + `fieldName` so the 9001 error names the specific field. Tests in `WriteParityTests` + `FullFlowTests` updated to assert 9012.
- [x] **F5.** Shipped `src/LacertMCP.FieldAccessDumper/` — a net48 x64 console project that probes (module, table, field) tuples via `FieldAccessMapLoader.AssertWritable` (sim) and `IDBFReader.ReadFromDBFData` (live, stubbed with `BridgeUnavailable` pending Windows + Lacerte box). Emits the JSON shape that goes into `Schema/FieldAccessMap/base/*.json`. Idempotent merge with `--overwrite` flag. Project added to `LacertMCP.sln`.
- [x] **F6.** `Lacerte_MCP_Structure_Spec.md` §1 corrected — ODBC line now reads "full **read** of tables 1–7 plus **read** of tax detail files" with the write-path callout pointing to COM (`WriteToDBFDatabase` / `UpdateTaxReturn` / `IClient.CreateClient`) + a footnote to `DECISIONS.md` 2026-05-30 "ODBC is reads-only".
- [x] **F8.** `.gitignore` extended — added `bin/`, `obj/`, `TestResults/`, `*.user`, `*.suo`, `.vs/`, explicit `LacertMCP.Server.exe.config.bak`, editor backup patterns (`*.swp`, `*.swo`, `*~`, `*.bak`).
- [x] **F10.** Recording-adapter scaffold shipped. `ParitySessionFactory.ForRecording(recordingPath)` constructs a live-mode session backed by `RecordingBridge` (full `ILacerteBridge` impl with canonical-JSON arg matching). Env-var hook `LACERTE_PARITY_RECORDING` activates. Hand-authored stub fixture at `tests/Parity/fixtures/example-recording.json` + one end-to-end test (`RecordingHarnessTests.CanUseNewApis_ReturnsValueFromRecording`). Race with CLI tests fixed via `[Collection("Cli (serial)")]` serialization + per-test `IsolatedAppIdScope`. **Actual live recordings still pending — needs a Windows + Lacerte box to capture (closes Phase 4 #40).**
- [x] **F11.** `WireLog.Record(...)` wired around every concrete chokepoint in `LacerteSessionManager` (Register, all Read*, Write*, CreateClient, CanUseNewApis on live), `LacerteDsnReader` Query methods, and `Bridge/LacerteAccessClient` + `Bridge/Writer` methods. Activated by `LACERTE_DEBUG_WIRE=1`. Integration test under `Tests/Logging/` confirms the logfile receives the right ops + redacted args.
- [x] **F12.** Every `_bridge.<method>(...)` call site in `LacerteSessionManager` live branches wrapped in `AutoReconnect.WithRetry(...)`. Sim path unchanged (sim doesn't throw transient COM). Test asserts a fake bridge whose first call throws `COMException(0x800706BA)` and second succeeds round-trips successfully.
- [x] **F13.** `lacerte_client_list` + `lacerte_table_read` retrofitted with `Pagination.Page<T>`. Both accept `limit + offset` and emit `{rows, total, limit, offset, hasMore, nextOffset}`. Legacy `{count, clients}` / `{count}` fields kept additive (v0.2 removal note in code). README tool table updated. 6 new pagination tests passing.
- [x] **F14.** `LacerteSessionManager._edition` field cached lazily on first call to `EnsureEditionProbed()`. `SessionStatusSnapshot` extended with nullable `EditionInfo Edition` property; populated by `SessionStatus()`. In sim mode returns the `Simulated/0.0.0-sim/0.0-sim` sentinel. Live wire-up still gated on Phase 9 bridge.

---

## Phase priorities at a glance

```
Phase 0  Foundations              ← BLOCKING for everything
Phase 1  Errors + schema           ← BLOCKING for sim store
Phase 2  Simulation store          ← BLOCKING for session manager
Phase 3  Session manager           ← BLOCKING for tools
Phase 4  ODBC layer                ← BLOCKING for live read tools
Phase 5  Read tools                ← unblocks demo
Phase 6  Write tools               ← unblocks operator workflows
Phase 7  Create tools              ← unblocks intake flow
Phase 8  Metadata helpers          ← parallel with Phase 5+
Phase 9  COM bridge                ← live mode; Windows-only verification
Phase 10 CLI                       ← parallel with Phase 9
Phase 11 Hardening                 ← polish, after Phase 9
Phase 12 Test coverage             ← cross-cutting, runs throughout
Phase 13 Delivery                  ← last
```

Each agent session should pick the next unchecked item in the lowest-numbered phase with available dependencies. When in doubt, lower-numbered phase wins.

---

## Phase 14 — Discovered improvements (2026-05-30 deep-audit fan-out)

Generated by a 10-lens audit + adversarial verification workflow (88 agents, 71/78 confirmed). Each task references an audit ID; full per-task context lives in `audit-findings.json` at repo root.
Ordering: severity (high → low), then lens.

### HIGH severity

- [ ] **P14-001** [architecture] LacerteHealth bypasses WrapToolHandler and hardcodes sim response
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/HealthTools.cs (line 27-33)
  - Why: LacerteHealth returns a raw string 'reachable (sim)' without using WrapToolHandler.Run and without asking the session manager about mode.
  - Do: In-session (sim-only) fix: change LacerteHealth to `Task<string> LacerteHealth() => WrapToolHandler.Run(() => Task.FromResult<object>(new { mode = _session.Mode.ToString().ToLowerInvariant(), status = "reachable" }));` and update the Description attribute to drop the "(sim)" literal. Update HealthToolsTests.LacerteHealth_ReturnsNonEmptyString_InSimMode to assert the JSON envelope contains `"success":true` and `"mode":"simulation"`. Leave the live HRESULT probe (new session.HealthProbe() backed b...
- [ ] **P14-002** [architecture] WriteDetail session chokepoint skips access-map AssertWritable
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 681-752)
  - Why: WriteField (line 595) correctly calls FieldAccessMapLoader.AssertWritable before the wire call, but WriteDetail does not.
  - Do: Tighten Invariant #7 in ARCHITECTURE.md to read "consulted before every per-field write (WriteField)" and add a DECISIONS.md entry titled "Detail-row + whole-row writes are outside the access-map gate" that names the two affected paths (LacerteSessionManager.WriteDetail at 681-752 and CreateClient at 758+), explains the map's M|T|F key shape can't address detail rows or whole-row creates, and points readers to the actual safety nets: DetailsTools.ValidateDetailWrite (line ~96, Series/Code/StateA...
- [ ] **P14-003** [architecture] Live-mode reads throw BridgeUnavailable; sim/live observationally divergent
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 397, 426, 457, 517, 538, 880-888)
  - Why: EnsureLive() throws SyntheticStatusCodes.BridgeUnavailable whenever Mode == Live, so every read chokepoint (ListClients, ReadClient, ReadTableCore, ReadDetail, ReadEFStatus) is unreachable in live mode and always returns from the in-memory SimulationStore in sim mode.
  - Do: Rename EnsureLive() to ThrowIfLive() (or EnsureSimPath()) and update the call sites + xmldoc to make the inverted semantics obvious. Add a one-line summary on the method: "Throws BridgeUnavailable in Live mode — every wire chokepoint guards through this until Phase 9 wires the bridge + ODBC reader. Sim path is always reachable." Do NOT attempt the actual ODBC wire-up — that is Phase 4/9 work explicitly gated on Windows + Lacerte SDK and not actionable in this sim-only dev environment. Leave the ...
- [ ] **P14-004** [correctness] COM instance leak: Writer/Reader never call Marshal.ReleaseComObject
  - File(s): src/LacertMCP.Server/Bridge/Writer.cs, src/LacertMCP.Server/Bridge/Reader.cs (line Writer.cs:40-60, 76-98, 115-143; Reader.cs:35-57, 63-110)
  - Why: Writer.AcquireWriter() and Reader.AcquireReader() Activator.CreateInstance a fresh COM object (LacerteSDK_Access.DBFWriter / .DBFReader) on EVERY wire call, and the wrapping methods (WriteToDBFDatabase, UpdateTaxReturn, WriteDetail, ReadFromDBFData) never release them.
  - Do: Apply the CanUseNewApisProbe.cs:96-103 pattern to all four COM call sites (Writer.WriteToDBFDatabase, Writer.UpdateTaxReturn, Writer.WriteDetail, Reader.ReadFromDBFData). Concretely: pull `var writer = AcquireWriter(...)` out of the try, then wrap the existing try/catch in an outer try/finally whose finally does `if (writer != null) { try { Marshal.ReleaseComObject(writer); } catch { /* best-effort */ } }`. Do NOT take the cached-instance route the audit mentions as an alternative — Writer/Reade...
- [ ] **P14-005** [correctness] Idempotency cache has TOCTOU race; duplicate side-effects possible
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1129-1156)
  - Why: TryReplayIdempotent and StoreIdempotent are unsynchronized.
  - Do: Replace the unsynchronized `TryReplayIdempotent`/`StoreIdempotent` pair with per-key serialization. Concrete approach: add `private readonly ConcurrentDictionary<string, SemaphoreSlim> _idempotencyLocks = new();` Refactor the three write paths (WriteField line 589/673, WriteDetail line 692/750, CreateClient line 769/835) to call a single helper `Task<T> ExecuteIdempotentAsync<T>(string? key, string payload, Func<Task<T>> body)` that (1) returns body() directly if key is null, (2) acquires `_idem...
- [ ] **P14-006** [docs] ARCHITECTURE.md states MCP SDK 1.3.0 but project pins 0.3.0-preview.4
  - File(s): C:/Users/VR/Projects/LacertMCP/ARCHITECTURE.md (line 10)
  - Why: ARCHITECTURE.md Stack section claims `ModelContextProtocol 1.3.0 (Microsoft + Anthropic official)`, but the csproj pins `0.3.0-preview.4` and DECISIONS.md 2026-05-30 'MCP SDK version pinning' entry explicitly overrides the original 1.3.0 claim, noting it was incorrect.
  - Do: Make two targeted markdown edits, both safe and in-session:  1. ARCHITECTURE.md line 10 — change `* **MCP SDK:** \`ModelContextProtocol\` 1.3.0 (Microsoft + Anthropic official), stdio transport.` to `* **MCP SDK:** \`ModelContextProtocol\` 0.3.0-preview.4 (Microsoft + Anthropic official preview; 1.x not yet on NuGet), stdio transport. See DECISIONS.md "MCP SDK version pinning" 2026-05-30.`  2. DECISIONS.md line 21 — append an inline correction marker to the "Language/runtime stack" entry so the ...
- [ ] **P14-007** [docs] CLAUDE.md is entirely TypeScript — describes wrong stack
  - File(s): C:/Users/VR/Projects/LacertMCP/CLAUDE.md (line 18,55,56,74-83)
  - Why: CLAUDE.md (the project operating system) describes a TypeScript / Node / Zod / ESM stack — `Transport — stdio MCP server bound in src/index.ts`, `Adding a tool means: register in src/index.ts`, `npm run build`, `TypeScript strict mode, ES2022, ESM modules`, `MCP tools registered 
  - Do: Edit CLAUDE.md to align with DECISIONS.md 2026-05-30 stack entry: (1) Line 18 — replace "stdio MCP server bound in `src/index.ts`" with "stdio MCP server bound in `src/LacertMCP.Server/Program.cs` via `McpServer` + `WithStdioServerTransport()` + `WithToolsFromAssembly()`". (2) Line 55 — replace "register in `src/index.ts`, handle the request in the simulation store, update the `instructions` blurb, and add it to the README tool table" with "add an `[McpServerTool]`-decorated method on the approp...
- [ ] **P14-008** [docs] REGRESSION_CHECKLIST.md still gates on npm/tsc, not dotnet
  - File(s): C:/Users/VR/Projects/LacertMCP/REGRESSION_CHECKLIST.md (line 7-15)
  - Why: The first three check items are `npm run build (tsc) passes with no errors`, `No new TypeScript errors in unrelated files`, `All relative imports keep their .js extension (Node16 ESM)`.
  - Do: Rewrite the top three sections of REGRESSION_CHECKLIST.md as follows. (1) Build/Compile → `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` must report `0 warnings, 0 errors`; note that system-wide `dotnet` is runtime-only — must use user-local SDK. (2) Tests → `& "$env:USERPROFILE\.dotnet\dotnet.exe" test src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` must report `481 passed, 11 skipped, 0 faile...
- [ ] **P14-009** [dx-build] No CI workflow — build/test only run locally
  - File(s): C:/Users/VR/Projects/LacertMCP/.github/workflows/ (missing) (line n/a)
  - Why: No .github/workflows/, azure-pipelines.yml, or .gitlab-ci.yml exists.
  - Do: Add `.github/workflows/ci.yml` triggered on push + PR with a **windows-latest job as the primary gate** (high value, drop-in): `actions/setup-dotnet@v4` for the SDK, `actions/cache@v4` keyed on `**/packages.lock.json` or `**/*.csproj` hash for `~/.nuget/packages`, then `dotnet restore LacertMCP.sln`, `dotnet build LacertMCP.sln -c Debug -p:Platform=x64 --no-restore`, `dotnet test src/LacertMCP.Tests/LacertMCP.Tests.csproj -c Debug -p:Platform=x64 --no-build --logger "trx;LogFileName=test-results...
- [ ] **P14-010** [dx-build] No release packaging — no way to produce a distributable artifact
  - File(s): C:/Users/VR/Projects/LacertMCP/scripts/release.ps1 (missing) (line n/a)
  - Why: The README install instructions ask the operator to point Claude Desktop / Cursor / Windsurf at `C:\path\to\LacertMCP\src\LacertMCP.Server\bin\x64\Release\LacertMCP.Server.exe` — i.e.
  - Do: Add `scripts/release.ps1` that (a) builds both projects: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build LacertMCP.sln -c Release -p:Platform=x64`, (b) stages `src/LacertMCP.Server/bin/x64/Release/` (verifying `LacertMCP.Server.exe`, `App.config`, and `Schema/FieldAccessMap/manifest.json` + `base/` + `overlays/` are all present — fail loudly if any are missing, since the Schema tree is the silent-failure trap), (c) stages `src/LacertMCP.Cli/bin/x64/Release/`, (d) emits `dist/LacertMCP-v{version}....
- [ ] **P14-011** [dx-build] Not under git — no version control state for collaboration
  - File(s): C:/Users/VR/Projects/LacertMCP/ (line n/a)
  - Why: `git -C C:/Users/VR/Projects/LacertMCP status` returns 'fatal: not a git repository.' Despite a polished .gitignore, well-formed README, CLAUDE.md describing an 'AI session lifecycle' that involves multiple contractor-like agents, a CONTRIBUTING-style operating system file, and T
  - Do: In-session, do only the local-git work (defer remote + tagging to the user / Windows session): (1) Create `.gitattributes` with `* text=auto eol=lf` and per-type Windows-CRLF overrides for `*.cs`, `*.csproj`, `*.sln`, `*.props`, `*.targets`, `*.config`, `*.cmd`, `*.bat`, `*.ps1`; mark `*.dbf`, `*.pdf`, `*.png`, `*.ico` binary. (2) `git init -b main` then `git add . && git commit -m "Initial commit: v0.1.0 scaffolding (481 sim tests passing, 11 live-only skipped)"`. (3) Do NOT add a remote or pus...
- [ ] **P14-012** [perf-resources] WireLog opens + closes log file on every record (per-call FS open)
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 211-223)
  - Why: WriteRecord calls File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8) inside the lock for every wire op.
  - Do: Replace the per-call File.AppendAllText with a cached StreamWriter keyed by full log file path (path includes UTC date, so a new path naturally appears at midnight rollover — no separate timer needed). Sketch: a static Dictionary<string, StreamWriter> guarded by _writeLock; on each WriteRecord, compute the target path, look up the writer, and if absent (new date or first call) close+remove any stale entries for a different path then open `new StreamWriter(new FileStream(path, FileMode.Append, Fi...
- [ ] **P14-013** [perf-resources] Idempotency cache has no eviction, TTL, or size cap
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 89-90, 1129-1156)
  - Why: _idempotencyCache is a ConcurrentDictionary<string, IdempotencyCacheEntry> that grows monotonically.
  - Do: Keep the ConcurrentDictionary (no new package reference). Make CreatedAt earn its keep: (1) in TryReplayIdempotent, if entry.CreatedAt < UtcNow - 24h, TryRemove it and treat as miss; (2) in StoreIdempotent, after writing, if _idempotencyCache.Count > IdempotencyCacheMaxEntries (const = 10_000), scan and TryRemove all entries older than the TTL, and if still over cap, remove the oldest N by CreatedAt. Both paths are lazy/opportunistic — no Timer, no background thread, no race-prone scrubber. Expo...
- [ ] **P14-014** [perf-resources] ListClients pulls full roster then paginates in-tool (no SQL TOP/OFFSET)
  - File(s): src/LacertMCP.Server/Tools/ClientsTools.cs (line 32-49 (tool) + src/LacertMCP.Server/Odbc/SqlTemplates.cs:56-57 (SQL))
  - Why: LacerteClientList calls _session.ListClients(nameFilter, int.MaxValue) — explicitly defeating any server-side cap — then hands the materialized list to Pagination.Page which slices in memory.
  - Do: Two-part sim-only fix, both verifiable off-Windows: (1) In ClientsTools.LacerteClientList, replace `int.MaxValue` with `offset + Math.Min(limit, Pagination.MaxLimit)` so the session materializes at most one page-worth of rows (Pagination.Page still computes correct `total` because we pass it the same bounded slice — actually no: total would be wrong. So instead, push limit/offset into LacerteSessionManager.ListClients itself: add overload `ListClients(filter, limit, offset)` that returns `(rows,...
- [ ] **P14-015** [readme-cli] Missing Prompts section — three shipped prompts undiscoverable
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 101-134)
  - Why: README has a `Tools` section but no `Prompts` section, yet `WorkflowPrompts.cs` ships three first-class MCP prompts (`lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_bulk_field_update`) registered via `WithPromptsFromAssembly()`.
  - Do: Insert a new `## Prompts` section between `## Tools` (ends line 133) and `## CLI` (line 135). Use a three-column table: `Prompt | What it walks through | Args`. Concrete rows (lifted from each method's `[Description]` + parameter signatures):  | Prompt | What it walks through | Args | | --- | --- | --- | | `lacerte_client_intake` | Onboard a new client end-to-end: verify session → pick datapath/module/year → `client_create` → seed DATA1 fields → seed detail rows → verify roster. | `clientName?: ...
- [ ] **P14-016** [readme-cli] Env var matrix missing six variables that the code actually reads
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 70-82)
  - Why: The env-var table omits several vars that production/test code consults: `LACERTE_DATA_ROOT` (SessionTools.LacerteListDatapaths uses it as the discovery root with `C:\Lacerte` fallback — and the README's `lacerte_list_datapaths` row references it on line 111), `LACERTE_MCP_FORCE_
  - Do: Two-part fix, both in-session: (A) README.md env table: add three rows — `LACERTE_DATA_ROOT` (optional, default `C:\Lacerte`, datapath discovery root used by `lacerte_list_datapaths`); `LACERTE_MCP_FORCE_FILE_STORE` (optional, `1` forces JSON file store instead of Windows Credential Manager — useful for headless/non-interactive Windows hosts and is used by the test harness); `LACERTE_PARITY_RECORDING` (testing only, path to recorded parity JSON consumed by `BothModesData` harness). (B) Fix three...
- [ ] **P14-017** [readme-cli] CLI quick-start claim `doctor exit 1 off-Windows; that's expected` is wrong
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 28)
  - Why: Quick-start says `& lacerte-mcp.exe doctor   # exit 1 off-Windows; that's expected`.
  - Do: Replace line 28 with two lines that match the actual 0/1/2 contract and call out the fresh-checkout case explicitly: `& src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor   # exit 2 off-Windows = PARTIAL (Windows-only probes skipped); exit 1 = a probe failed (e.g. fresh checkout with no AppID — run 'lacerte-mcp register' or test fixture seeds it); exit 0 = all green (Windows + Lacerte install).` Optionally also fix the `& lacerte-mcp.exe` form referenced in the finding's `description` field ...
- [ ] **P14-018** [security] WireLog redaction regex misses common PII fields (DOB, address, phone, email, names)
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 62-64)
  - Why: The redaction regex covers only SSN, EIN, BankAccountNumber, RoutingNumber.
  - Do: Three-part fix, all in WireLog.cs + WireLogTests.cs (no Windows/SDK dependency):  1) Expand the key-name regex alternation to cover Lacerte-realistic PII keys: SSN|TaxpayerSSN|SpouseSSN|DependentSSN|TIN|ITIN|PTIN|EIN|EmployerEIN|FederalID|FedEIN|BankAccountNumber|RoutingNumber|DateOfBirth|DOB|BirthDate|EmailID|Email|HomePhone|WorkPhone|CellPhone|PhoneNumber|FirstName|LastName|MiddleName|Address1|Address2|StreetAddress|City|ZipCode|Zip|Description.  2) Add a second pass that specifically targets ...
- [ ] **P14-019** [security] Tool error context dict bypasses WireLog redaction — PII echoed to MCP client
  - File(s): src/LacertMCP.Server/Errors/WrapToolHandler.cs (line 72-85)
  - Why: WrapToolHandler.SerializeError serializes the LacerteSDKError.Context dictionary straight to the MCP wire with no redaction.
  - Do: Single-file fix in src/LacertMCP.Server/Errors/WrapToolHandler.cs: apply the existing WireLog.Redact() pass to the serialized JSON in BOTH SerializeError (line 84) and the success path (line 51) — `return WireLog.Redact(JsonSerializer.Serialize(envelope, JsonOptions));`. This reuses the project's proven SSN/EIN/BankAccountNumber/RoutingNumber regex and closes the wire-vs-disk asymmetry symmetrically (success envelopes also carry ODBC-read row data with those fields). Add an additional regex hit ...
- [ ] **P14-020** [simplification] Hoist _moduleFileName + _moduleTaxTypes onto Modules so 3 copies become 1
  - File(s): src/LacertMCP.Server/Schema/Modules.cs, src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs, src/LacertMCP.Server/Tools/MetadataTools.cs, src/LacertMCP.Cli/Commands/FieldAccessCommand.cs, src/LacertMCP.Server/Session/SimulationStore.cs, src/LacertMCP.Server/Tools/CreateClientTools.cs (line FieldAccessMapLoader.cs:61-72; MetadataTools.cs:30-41; FieldAccessCommand.cs:29-40; SimulationStore.cs:265-277; CreateClientTools.cs:39-51)
  - Why: The Char->filename basename map ('I'->"individual", etc.) is duplicated verbatim in 3 places (FieldAccessMapLoader, MetadataTools, FieldAccessCommand).
  - Do: Extend Schema/Modules.cs as the single source. Add two fields to ModuleInfo: `string FileBasename` and `IReadOnlyList<string> TaxTypeAliases`. Populate them in the existing Modules.All initializer (the 9 rows become e.g. `new ModuleInfo('I', "Individual", "Individual", "individual", new[] { "Individual", "1040" })`). Add two convenience helpers — `Modules.FileBasenameFor(char) -> string?` and `Modules.AcceptsTaxType(char, string) -> bool` (case-insensitive, matching the existing semantics) — so ...
- [ ] **P14-021** [spec-compliance] EF status SQL template omits EFilingId/EFId/InstanceId that tool returns
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Odbc/SqlTemplates.cs (line 159-163)
  - Why: EFileTools.LacerteEfStatus [Description] promises `flat rows with EFilingId, ClientId, EFId, InstanceId, Status, StatusDate.` The simulation path in LacerteSessionManager.ReadEFStatus() returns exactly those 6 keys.
  - Do: Rewrite `SqlTemplates.EfStatusJoin` so the SELECT list matches the sim contract exactly: `select ci.C1_0 as ClientId, ef.Id as EFilingId, ef.EFId, ef.InstanceId, s.EFStatus as Status, s.EFStatusDate as StatusDate from [DATA1-Client Info] as ci left outer join [EFilings] as ef on ci.C1_0 = ef.ClientId left outer join [EFStatuses] as s on ef.Id = s.EFilingId`. Also fix the misleading XML doc-comment from "Latest EF status per client" to "All EF status rows joined per client (full history)" — neith...
- [ ] **P14-022** [spec-compliance] Pre-TY21 WriteToDBFDatabase row-number path uses int.Parse(clientNumber) placeholder
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 638-650)
  - Why: Spec §4 Write: `lacerte_field_write — pre-TY21: WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value).` The session manager's WriteField, when CanUseNewApis returns false (TY18-TY20), calls `_bridge.WriteField(appId, ctx.DataPath, ctx.Year, ctx.Module, table, rowNum, fie
  - Do: Add `int ResolveRowNumber(string appId, string dataPath, int year, char module, int table, string clientNumber)` to ILacerteBridge. Sim implementation enumerates the table's ConcurrentDictionary (Clients/Statuses/Displays — DATA1/5/6 are keyed by ClientNumber alone; DATA2/3/4 need extra keys but spec §4 pre-TY21 row-keyed writes only target the C{t}_0 row, so resolution is well-defined) and returns the 1-based ordinal. Live implementation issues `select ROWNUM from [DATA{t}-...] where C{t}_0 = ?...
- [ ] **P14-023** [test-gaps] AutoReconnect: 2 of 4 whitelisted HRESULTs untested; ExternalException path untested
  - File(s): src/LacertMCP.Tests/Resilience/AutoReconnectTests.cs (line 94-120)
  - Why: AutoReconnect.TransientHResults whitelists 4 HRESULTs (0x800706BA, 0x80010108, 0x8001010A, 0x80010001 — AutoReconnect.cs:43-46) but tests only exercise the first two (RpcUnavailable + RpcDisconnected, AutoReconnectTests.cs:101-113).
  - Do: Replace IsTransient_RpcUnavailable_True and IsTransient_RpcDisconnected_True with one [Theory] over all 4 whitelisted HRESULTs (0x800706BA, 0x80010108, 0x8001010A, 0x80010001) using COMException. Add: (1) [Theory] IsTransient_ExternalException_TrueForAllWhitelisted — constructs a System.Runtime.InteropServices.ExternalException with each HRESULT and asserts true (pins the AutoReconnect.cs:148-151 branch); (2) [Fact] IsTransient_BaseException_HResultFallback_True — creates new Exception("x") { HR...
- [ ] **P14-024** [test-gaps] FieldAccessMapLoader overlay precedence + 3 of 5 AssertWritable branches untested
  - File(s): src/LacertMCP.Tests/Schema/FieldAccessMapLoaderTests.cs (line 1-81)
  - Why: FieldAccessMapLoader.AssertWritable (FieldAccessMapLoader.cs:121-163) emits 5 distinct status codes — ExposedForRead, NotExposed (Hidden+NotExposed), FieldNotSupported (overlay), UnknownFieldAccess, and the defensive Error default.
  - Do: Extend the existing AccessMapTestFixture pattern (src/LacertMCP.Tests/Tools/AccessMapTestFixture.cs) — which already writes a real shard file and clears the static caches via reflection on _moduleCache/_overlayCache — to add: (1) three AssertWritable tests that seed a Hidden, NotExposed, and NotSupported entry respectively and assert StatusCodes.NotExposed(7) / NotExposed(7) / FieldNotSupported(26); (2) one overlay-precedence test that seeds the base shard with field X = ReadWrite and the ty24 o...
- [ ] **P14-025** [test-gaps] lacerte_update_return tool has zero direct tests
  - File(s): src/LacertMCP.Server/Tools/UpdateReturnTools.cs (line 31-73)
  - Why: UpdateReturnTools is a registered MCP tool (one of only two public write entrypoints) but grepping the test tree for UpdateReturnTools / LacerteUpdateReturn / update_return returns zero matches.
  - Do: Create `src/LacertMCP.Tests/Tools/UpdateReturnToolsTests.cs` by copying the structure of `FieldWriteToolsTests.cs` and substituting `UpdateReturnTools` / `LacerteUpdateReturn`. Six facts (mirroring the existing sibling exactly): (1) happy-path success — assert `success==true`, `result.statusCode==1`, AND `result.api=="UpdateTaxReturn"` (the key delta vs. FieldWriteTools, since this string is the only operator-visible signal distinguishing the two write entrypoints); (2) ReadOnly session rejectio...

### MEDIUM severity

- [ ] **P14-026** [architecture] LacerteUpdateReturn lies about the live API used (api="UpdateTaxReturn" for TY<2021)
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/UpdateReturnTools.cs (line 60-69)
  - Why: The tool response hardcodes `api = "UpdateTaxReturn"` regardless of the year-routing decision the session manager makes.
  - Do: Smallest correct fix: drop the `api` field from UpdateReturnTools' anonymous response (mirroring FieldWriteTools, which already omits it). The tool description already names "UpdateTaxReturn" as the intended entrypoint; the runtime response should not assert which wire actually ran. If a wire-truth label is desired later, add a `CodeResult { int status, bool usedNewApi }` return from session.WriteField rather than have the tool peek at session state — that keeps year-routing inside the session m...
- [ ] **P14-027** [architecture] CreateClientTools duplicates simulation-store validation table inside tool layer
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/CreateClientTools.cs (line 35-51, 132-156)
  - Why: The tool keeps its own _moduleTaxTypes dictionary 'mirroring SimulationStore's' and reaches into _session.Context.Module to decide whether the TaxType is valid.
  - Do: Add `IReadOnlyList<string> AcceptedTaxTypes` to `Schema.ModuleInfo` (or a parallel `Modules.AcceptedTaxTypesByChar` dictionary) sourced from Lacerte_MCP_Structure_Spec.md. Replace both `_moduleTaxTypes` dictionaries (CreateClientTools.cs:39-51, SimulationStore.cs:265+) with calls to `Modules.GetByChar(module)?.AcceptedTaxTypes`. Keep the early-throw structured-error path in CreateClientTools intact — the gain is one catalog, not a new session API. Do not introduce `session.ValidateCreateClientAr...
- [ ] **P14-028** [correctness] SimulationStore.WriteDetail mutates List<DetailRow> without a lock
  - File(s): src/LacertMCP.Server/Session/SimulationStore.cs (line 441-465)
  - Why: SimulationStore.WriteDetail does `list.Add(row)` and `list[replaceIndex.Value] = row` on the inner List<DetailRow> without any synchronization, even though Details is a ConcurrentDictionary<string, List<DetailRow>> shared across threads.
  - Do: Add `lock (list) { ... }` around the mutate block (lines 454-463) in SimulationStore.WriteDetail, matching the existing pattern in LacerteSessionManager.ApplySimDetailWrite. Do NOT remove the lock from ApplySimDetailWrite — contrary to the finding's framing, that method does not call WriteDetail; it goes straight to module.Details with its own logic. Both paths must hold the lock independently. While you're in the file, also lock the read side: wrap the `return list.ToList();` and the `foreach (...
- [ ] **P14-029** [correctness] Universal C{t}_0 ReadOnly rule is case-sensitive; lowercase fields bypass it
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 84)
  - Why: `if (field == "C" + table.ToString() + "_0") return FieldAccess.ReadOnly;` is an ordinal case-sensitive equality.
  - Do: Normalize the field name once at the top of `GetFieldAccess` rather than scattering `StringComparison` arguments. Concretely, after the null guard on line 81: `var normField = field.ToUpperInvariant();` and use `normField` for the universal-rule comparison and the key construction. This guarantees the universal rule and base/overlay lookups stay consistent without needing to also re-key the deserialized dictionaries (which would otherwise require a post-deserialize copy into a `Dictionary<string...
- [ ] **P14-030** [correctness] Sim CreateClient bypasses Registration, TaxType-Module, and ClientID-regex gates
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 991-1026)
  - Why: ApplySimCreateClient (the sim-mode write path for CreateClient) only checks ClientID length and duplicate ClientID; it ignores the IsRegistered latch, the TaxType↔Module match, and the ClientID character whitelist regex (^[A-Z0-9]+$).
  - Do: In ApplySimCreateClient (LacerteSessionManager.cs:991-1026), replace the direct DbfRow construction with a delegation to _sim!.SimulateCreateClient, then translate the (Status, ClientNumber) tuple back to the manager's throw-on-error contract:  ```csharp private CreateClientResult ApplySimCreateClient(SessionContext ctx, CreateClientArgs args) {     var (status, clientNumber) = _sim!.SimulateCreateClient(new SimulateCreateClientArgs(         ctx.DataPath, ctx.Year, ctx.Module,         args.Clien...
- [ ] **P14-031** [correctness] Pre-TY21 row-number lookup uses int.Parse(clientNumber) with silent -1 fallback
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 644-650)
  - Why: In the live WriteField path for pre-TY21 (CanUseNewApis returns false), the code does `int rowNum = int.TryParse(clientNumber, out var rn) ? rn : -1;` and passes -1 to `_bridge.WriteField(...)`.
  - Do: In `LacerteSessionManager.cs` pre-TY21 live branch (around line 645), replace the silent `int.TryParse(...) ? rn : -1` with a fail-closed throw: `throw new LacerteSDKError(SyntheticStatusCodes.SimulationOnly, new Dictionary<string, object?> { ["op"] = "WriteField", ["reason"] = "Pre-TY21 live writes require a ClientNumber→rowNum lookup that is not yet implemented (Todo task #51 follow-up). Use TY21+ or run in simulation mode.", ["year"] = ctx.Year, ["module"] = ctx.Module, ["clientNumber"] = cli...
- [ ] **P14-032** [correctness] TaxYearOverlay loaded per-year only; deltas don't propagate to later years
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 191-214)
  - Why: LoadOverlay reads `ty{yy}-not-supported.json` for exactly the requested year.
  - Do: The semantic is already decided (per-exact-year, full-replace, per Agent 04 §6) — don't change behavior, harden it: (1) Add a dated entry to DECISIONS.md citing Agent 04 §6: "Per-year overlay files are full-replace, not cumulative deltas. Each TY ships its own complete `tyNN-not-supported.json` listing every field dropped in that build, even if previously dropped." (2) Update the XML doc on `LoadOverlay` and the header on `Schema/FieldAccessMap/README.md` to state this explicitly; reword the REA...
- [ ] **P14-033** [docs] README env var table missing LACERTE_DATA_ROOT, LACERTE_PARITY_RECORDING, LACERTE_MCP_FORCE_FILE_STORE
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 68-82)
  - Why: The 'Environment variables (full reference)' table is labeled 'full reference' but three env vars are read by production code and not documented: `LACERTE_DATA_ROOT` (read at `Tools/SessionTools.cs:146` in `LacerteListDatapaths` — drives the discovery root for `lacerte_list_datap
  - Do: Add three rows to the README env table at README.md:68-82 and mirror to .env.example (per ARCHITECTURE.md:180): (1) `LACERTE_DATA_ROOT` — optional, default `C:\Lacerte`, root scanned by `lacerte_list_datapaths` (user-facing — list under 'optional', not 'testing only'); (2) `LACERTE_MCP_FORCE_FILE_STORE` — testing/dev only, `1` bypasses Credential Manager and uses the file-based AppID store at `%APPDATA%\LacertMCP\config.json` (auto-on off-Windows); (3) `LACERTE_PARITY_RECORDING` — testing only, ...
- [ ] **P14-034** [docs] README + ARCHITECTURE.md never mention the 3 MCP prompts that exist
  - File(s): multiple (line n/a)
  - Why: `src/LacertMCP.Server/Prompts/WorkflowPrompts.cs` exposes three `[McpServerPrompt]` entries — `lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_bulk_field_update` — registered via `WithPromptsFromAssembly()` per HANDOFF.md.
  - Do: Three concrete edits, all sim-safe: (1) README.md — add a "### Prompts (Phase X)" subsection under "## Tools" with a 3-row table: name | what it walks the agent through | params. Use the existing [Description] strings from WorkflowPrompts.cs verbatim as the descriptions. (2) ARCHITECTURE.md line 201 — delete the "MCP Prompts API for workflow bundles (`lacerte_client_intake`, `lacerte_review_efile_status`)" bullet entirely (don't move to a "recently landed" note — the deferred list should only li...
- [ ] **P14-035** [docs] ARCHITECTURE.md Bridge file list misses Writer/Reader/Registration/EditionDetection/CanUseNewApisProbe
  - File(s): C:/Users/VR/Projects/LacertMCP/ARCHITECTURE.md (line 56,58)
  - Why: The Module Boundaries table line for Bridge lists only `LacerteAccessClient.cs + helpers`.
  - Do: Edit ARCHITECTURE.md Module Boundaries table:  1. **Replace the Bridge row** (line 56) so the Path column lists each file with a one-line responsibility:    - `Bridge/ILacerteBridge.cs` — interface seam for swappable bridge (live vs null/sim)    - `Bridge/LacerteAccessClient.cs` — late-bound `LacerteSDK_Access` aggregate + lifetime owner    - `Bridge/Registration.cs` — `ITPRegistration` ceremony, called once at startup (Boundary Invariant #8)    - `Bridge/Reader.cs` — `IDBFReader` COM surface (i...
- [ ] **P14-036** [docs] ACCEPTANCE_CRITERIA.md uses 'npm run build' as the pass criterion; missing Prompts + post-pagination response shape rows
  - File(s): C:/Users/VR/Projects/LacertMCP/ACCEPTANCE_CRITERIA.md (line 17,89-93)
  - Why: The 'Strong example' rubric ends with `npm run build passes` (line 17) — wrong toolchain.
  - Do: Edit `C:/Users/VR/Projects/LacertMCP/ACCEPTANCE_CRITERIA.md`: (1) Line 17: replace `> `npm run build` passes.` with `> `dotnet build LacertMCP.sln -c Debug -p:Platform=x64` reports 0 warnings / 0 errors and `dotnet test src/LacertMCP.Tests` is green.`  (2) Line 49 (#42): replace `Returns {count, clients}` with `Returns the paginated envelope {rows, total, limit, offset, hasMore, nextOffset}; legacy {count, clients} keys retained for back-compat through v0.1.x and REMOVED in v0.2 per DECISIONS.md...
- [ ] **P14-037** [docs] DECISIONS.md missing entries for landed subsystems: WireLog redaction, AutoReconnect backoff, Pagination contract
  - File(s): C:/Users/VR/Projects/LacertMCP/DECISIONS.md (line n/a)
  - Why: Three load-bearing architectural choices landed in wave-4 (per HANDOFF.md F11/F12/F13) without a corresponding `DECISIONS.md` entry: (1) WireLog log format + the specific PII redaction list (SSN/EIN/BankAccountNumber/RoutingNumber) — there is no log-schema or redaction-policy ent
  - Do: Append three dated entries to DECISIONS.md following the existing Chosen/Why/Alternatives/Downstream format. Source the facts from the XML doc comments already in WireLog.cs, AutoReconnect.cs, and Pagination.cs (plus the inline v0.2 notes in ClientsTools.cs:35-37 and TablesTools.cs:37). Each entry should be ~15-25 lines: (1) "WireLog format + PII redaction policy" — what's redacted (SSN/EIN/BankAccountNumber/RoutingNumber), what isn't (client names, addresses, dollar amounts — these are operatio...
- [ ] **P14-038** [dx-build] No .editorconfig — formatting and style drift across files
  - File(s): C:/Users/VR/Projects/LacertMCP/.editorconfig (missing) (line n/a)
  - Why: There is no .editorconfig at the repo root and no analyzer/style packages (StyleCop, Microsoft.CodeAnalysis.NetAnalyzers, dotnet-format config).
  - Do: Add two files at repo root, skip the analyzer packages entirely. (1) Directory.Build.props with a single PropertyGroup containing TargetFramework=net48, PlatformTarget=x64, Platforms=x64, LangVersion=latest, Nullable=enable — then delete those 5 lines from each of the 4 csproj files (keep per-project overrides like OutputType, RootNamespace, AssemblyName). (2) .editorconfig with: root=true, [*] indent_style=space, insert_final_newline=true, trim_trailing_whitespace=true (exempt *.md); [*.cs] ind...
- [ ] **P14-039** [dx-build] scripts/ folder is empty — no build/test/release wrappers
  - File(s): C:/Users/VR/Projects/LacertMCP/scripts/ (line n/a)
  - Why: The scripts/ and config/ directories exist but are empty.
  - Do: Add four wrappers under scripts/ that resolve dotnet once at the top:  (1) scripts/_dotnet.ps1 — shared helper exporting `$script:Dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"` with a fallback probe to `Get-Command dotnet` if the user-local SDK is missing, and a clear error if neither has an SDK (not just runtime).  (2) scripts/build.ps1 — dot-sources _dotnet.ps1, runs `& $Dotnet build $PSScriptRoot\..\LacertMCP.sln -c Debug -p:Platform=x64 @args`.  (3) scripts/test.ps1 — same pattern against s...
- [ ] **P14-040** [dx-build] No assembly version stamping — binaries identify as 1.0.0.0 forever
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/LacertMCP.Server.csproj (line 15)
  - Why: The Server csproj sets `<GenerateAssemblyInfo>true</GenerateAssemblyInfo>` but never sets <Version>, <AssemblyVersion>, <FileVersion>, or <InformationalVersion>.
  - Do: Create C:/Users/VR/Projects/LacertMCP/Directory.Build.props with: `<Project><PropertyGroup><Version>0.1.0</Version><AssemblyVersion>0.1.0.0</AssemblyVersion><FileVersion>0.1.0.0</FileVersion><InformationalVersion>$(Version)+$(SourceRevisionId.Substring(0,7))</InformationalVersion></PropertyGroup></Project>` (guard the Substring with a Condition so it no-ops when SourceRevisionId is empty in non-git builds — otherwise sim CI breaks). Then: (1) add a `BuildVersion` static helper in LacertMCP.Serve...
- [ ] **P14-041** [dx-build] No code-coverage configuration — 481-test discipline isn't measured
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj (line 14-21)
  - Why: Tests.csproj pulls Microsoft.NET.Test.Sdk + xunit + xunit.runner.visualstudio but does not reference coverlet.collector or coverlet.msbuild.
  - Do: Minimal, in-session: add `<PackageReference Include="coverlet.collector" Version="6.0.2" />` (and ideally `coverlet.msbuild` for `/p:CollectCoverage=true` invocations) to LacertMCP.Tests.csproj, plus a checked-in `coverlet.runsettings` that excludes the FieldAccessDumper and generated bridge code, and a one-line README snippet documenting `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings`. Skip the 80% threshold, ReportGenerator wiring, and coverage badge until a CI pi...
- [ ] **P14-042** [perf-resources] BuildPayload SHA-256 + JsonSerializer runs on every write even when idempotencyKey is null
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 586-588, 691-695, 768, 1158-1175)
  - Why: WriteField, WriteDetail, and CreateClient ALL compute payload = BuildPayload(...) before calling TryReplayIdempotent, regardless of whether idempotencyKey was supplied.
  - Do: Two-part fix in LacerteSessionManager.cs: (1) In WriteField (~line 586), WriteDetail (~691), and CreateClient (~768), guard the BuildPayload call: `string payload = string.IsNullOrEmpty(idempotencyKey) ? string.Empty : BuildPayload("WriteField", ...)`. The existing TryReplayIdempotent + StoreIdempotent already no-op on null/empty key, so the empty payload is never inspected. (2) In BuildPayload (line 1158), hoist a `private static readonly JsonSerializerOptions s_payloadJsonOptions = new JsonSer...
- [ ] **P14-043** [perf-resources] SimSnapshot deep-clones full active module per dry-run write
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1054-1098)
  - Why: SimSnapshot walks Clients, SubClients, States, StateSubs, Statuses, Displays, Forms, AND Details for the active module and CloneDbfRow's every row, then SimRestore replays the entire snapshot back into the live module by Clear+repopulate.
  - Do: Replace SimSnapshot/SimRestore with per-operation undo closures returned from a small helper. Concretely: (a) WriteField — capture `(table, key, field, hadPrior, priorValue)` before `row.Fields[field] = value`; restore by setting back or removing. Validation throws (SubClientIDRequired/StateAbbrevRequired/OutOfIndex) happen before any mutation, so the dry-run wrapper should only register an undo *after* ApplySimFieldWrite has located the row and is about to write — making failed dry-runs free. (...
- [ ] **P14-044** [readme-cli] README Status line is stale (469/480 instead of 481/492)
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 5)
  - Why: The Status badge claims `v0.1.0 scaffolding — 469/480 tests passing (11 expected live-only skips)`.
  - Do: Edit README.md line 5 to read: `**Status:** v0.1.0 scaffolding — 481/492 tests passing (11 expected live-only skips), 0 failures, 0 warnings.` Keep the existing trailing prose. Skip the "CI badge" / ">= 481" suggestions — this is a single-operator personal tool with no CI, and a soft lower-bound count would itself drift; the right long-term answer (already partially in place) is that HANDOFF.md + REGRESSION_CHECKLIST.md are the source of truth and the README just mirrors the latest known-good nu...
- [ ] **P14-045** [readme-cli] CLI table omits subcommand flags, hiding key knobs
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 138-144)
  - Why: The CLI table lists commands but no flags.
  - Do: Replace the current 5-row table in README.md (lines 137-143) with a per-command subsection. Each subsection should show: the full invocation signature with options, a one-line purpose, defaults for any optional flag, and a callout for non-obvious behavior. Specifically: (1) `register` — call out `--reset` as the AppID recovery path and warn it forces a fresh Intuit consent; (2) `status` — note `--show-app-id` is the documented backup-before-reinstall path; (3) `field-access discover` — explicitl...
- [ ] **P14-046** [readme-cli] Missing high-value CLI subcommands operators will need
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/Program.cs (line 32-36)
  - Why: The CLI ships register/login/status/field-access/doctor, but several common operator verbs are absent: (1) `list-tools` / `tools` — dump the registered MCP tools + descriptions without booting the server in stdio mode (useful for client-config generation and sanity checks), (2) `
  - Do: Add two CLI verbs in-session (both implementable off-Windows, no Lacerte install required): (1) `lacerte-mcp tools [--json]` — reflect over `typeof(HealthTools).Assembly.GetTypes()`, filter to types with `[McpServerToolType]`, enumerate methods with `[McpServerTool]`, and dump `{name, description, parameters}` triples. The description text already lives in `[Description(...)]` attributes on the tool methods. Useful for client-config generation and CI smoke checks. Add ~40 lines + unit test that ...
- [ ] **P14-047** [readme-cli] No Troubleshooting / FAQ section despite hand-off complexity
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line n/a)
  - Why: The setup involves an interactive Intuit consent dialog, a 24-hour ticket expiry, a 32-bit-vs-64-bit MSI trap (called out in DECISIONS.md), a mixed-mode CLR shim (App.config gotcha mentioned in passing on line 99), and ODBC DSN configuration.
  - Do: Add Troubleshooting section to README.md with symptom-fix entries for: register no dialog (interactive shell only), 24h expiry (run lacerte-mcp login), write crash (install 64-bit MSI), shim removed (restore App.config), DSN missing (use 64-bit odbcad32). Also fix StatusCodes.cs:92 and 05-error-mapping.md:167 to use LACERTE_DATA_PATH instead of LACERTE_DATAPATH.
- [ ] **P14-048** [security] dataPath connection-string injection: '}' and other ODBC delimiters not handled
  - File(s): src/LacertMCP.Server/Odbc/ConnectionStringBuilder.cs (line 58-65)
  - Why: Build wraps dataPath in braces only if it contains ';'.
  - Do: In src/LacertMCP.Server/Odbc/ConnectionStringBuilder.cs Build(): (1) Reject upfront with ArgumentException if dataPath contains any control char (c < 0x20, including '\0', '\r', '\n', '\t') — these have no place in a filesystem path and are the highest-risk parser-confusion characters. (2) Always brace-wrap dataPath unconditionally and double any embedded '}': var safe = "{" + dataPath.Replace("}", "}}") + "}". Drop the IndexOf(';') branch entirely — the conditional bracing saves nothing and cre...
- [ ] **P14-049** [security] dataPath path-traversal / write-outside-expected-root not validated
  - File(s): src/LacertMCP.Server/Tools/SessionTools.cs (line 69-96)
  - Why: LacerteSetContext accepts any string as dataPath and forwards it to SetContext, where the only validation is IsNullOrWhiteSpace.
  - Do: Land a new PathValidation.ValidateDataPath helper in src/LacertMCP.Server/Session/ and call it from LacerteSessionManager.SetContext immediately after the existing IsNullOrWhiteSpace check. Rules: (1) Path.GetFullPath inside try/catch to canonicalize and catch malformed input; (2) reject if Path.IsPathRooted is false; (3) reject any input where the original string contains '..' as a path segment (Regex on separator-bounded '..'); (4) reject UNC (starts with '\\\\' or '//') unless LACERTE_ALLOW_U...
- [ ] **P14-050** [security] Late-bound COM dynamic dispatch invokes methods with caller-controlled arguments unchecked
  - File(s): src/LacertMCP.Server/Bridge/Writer.cs (line 80-82, 125-129)
  - Why: Writer.UpdateTaxReturn and Writer.WriteDetail pass operator-supplied field/value/series/description/etc.
  - Do: Two concrete changes, both sim-only:  1) In DetailsTools.ValidateDetailWrite, extend the signature to include the remaining strings and add a single shared `EnsureSafeText(field, value, maxLen)` helper that throws SyntheticStatusCodes.DetailWriteInvalid when: (a) length exceeds a conservative blanket cap (suggest 255 for Description, 50 for Prefix/Suffix/Source/CityAbbr/ImportSrc — matches the DBF column families common in the spec), or (b) the string contains any char in [\\x00-\\x08, \\x0B, \\...
- [ ] **P14-051** [simplification] Collapse FieldWriteTools + UpdateReturnTools into shared base/helper
  - File(s): src/LacertMCP.Server/Tools/FieldWriteTools.cs, src/LacertMCP.Server/Tools/UpdateReturnTools.cs (line FieldWriteTools.cs 41-68; UpdateReturnTools.cs 43-71)
  - Why: LacerteFieldWrite and LacerteUpdateReturn are near-identical: same parameter list, same delegation to _session.WriteField(...), same response shape (UpdateReturnTools adds api='UpdateTaxReturn').
  - Do: Extract a private static helper into a new internal class (e.g. `Tools/Internal/FieldWriteCore.cs`) exposing `static Task<string> Run(LacerteSessionManager session, int table, string clientNumber, string field, object? value, int? subClientID, string? stateAbbrev, bool dryRun, string? idempotencyKey, string? apiTag = null)`. The helper owns the WrapToolHandler.Run + WriteField call + StatusCodes lookup + response shape, conditionally adding `api = apiTag` when non-null. Each tool's [McpServerToo...
- [ ] **P14-052** [simplification] Inline EnsureLive — it's a misleading one-liner that always-throws in Live
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 880-888 (method); 397, 426, 457, 517, 538 (call sites))
  - Why: EnsureLive() is called in 5 read chokepoints (ListClients/ReadClient/ReadTable/ReadDetail/ReadEFStatus).
  - Do: Rename `LacerteSessionManager.EnsureLive()` to `RejectLiveReadsPendingOdbc()` (option (a) from the original recommendation), NOT inline it. Reasons to prefer rename over inline: (1) it removes the name collision with the legitimately-named `LacerteAccessClient.EnsureLive()` which has opposite semantics — the collision is a worse trap than the inconsistency with write paths; (2) keeping a single named method documents the Phase 4 follow-up in one place, so when ODBC wire-up lands, the deleter onl...
- [ ] **P14-053** [spec-compliance] CreateClient marks EmailID/FirstName/LastName optional vs spec required-set
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/CreateClientTools.cs (line 62-69)
  - Why: Spec §4 (Create section): `lacerte_client_create — IClientList.SetDataPath → IClient (TaxType, ClientID ≤8 chars, EmailID, FirstName, LastName) → CreateClient.` REQUIREMENTS.md §Create echoes: `Create a new client (Individual, Corporate, etc.) with the minimum required fields (Ta
  - Do: Apply Option (a) — make the three fields required end-to-end, with a focused diff:  1. `src/LacertMCP.Server/Tools/CreateClientTools.cs`:    - Drop the `= null` defaults on `firstName`, `lastName`, `emailID` and change the type from `string?` to `string`.    - Rewrite the three `[Description]` strings to remove "Optional." and explain the spec requirement (e.g. "First name (individual) OR entity-name override. Required per spec — pass an empty string only if you have explicit live-SDK evidence t...
- [ ] **P14-054** [spec-compliance] Tables 6 and 7 have no table-level write block; spec marks them Read only
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/FieldWriteTools.cs (line 42)
  - Why: Spec §2 access column for DATA6 = `Read only` and DATA7 = `Read only` (DATA5 is `Read only (non-global); some globals R/W`).
  - Do: Add a table-number gate in LacerteSessionManager.WriteField (the chokepoint — preferred over duplicating in both FieldWriteTools and UpdateReturnTools) immediately after RejectIfReadOnly and before the idempotency check. Throw `new LacerteSDKError(StatusCodes.ExposedForRead, new Dictionary<string,object?>{{"table", table}, {"reason", "DATA" + table + " is read-only per spec §2 (Display/Forms tables are not writable through the SDK)."}})` for table ∈ {6,7}. Leave DATA5 to per-field gating (spec a...
- [ ] **P14-055** [spec-compliance] Detail-file Series column typed string in record vs numeric in SQL filter
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Odbc/SqlTemplates.cs (line 180-184)
  - Why: Spec §2 says detail-file columns are `Series, Prefix, Code, Suffix, StateAbbr, CityAbbr, Source, TSJ, ImportSrc, Amount, Description` with `Series ≈ Lacerte input screen, Code ≈ input field`.
  - Do: Adopt option (a) but stage it to preserve readability: (1) Change DetailRow.Series and DetailRow.Code from string to int in SimulationStore.cs. (2) In SeedData.cs add a static `SeriesIds` lookup (`Wages=11, Int=12, Div=13, Dep=14, Inc=20, State=30`) with a comment citing the Lacerte SDK Series ID table from spec §2, then rewrite seed rows as `new DetailRow(SeriesIds.Wages, "", 1, ...)` so the seed data stays human-readable but emits the numeric IDs live SQL expects. (3) Change DetailsTools.Lacer...
- [ ] **P14-056** [test-gaps] EditionDetection live-throws branch + cached-once invariant untested
  - File(s): src/LacertMCP.Tests/Bridge/RegistrationTests.cs (line n/a)
  - Why: EditionDetection.Probe (EditionDetection.cs:38-63) has 2 branches: bridge==null returns the sim sentinel, bridge!=null throws BridgeUnavailable.
  - Do: Add src/LacertMCP.Tests/Bridge/EditionDetectionTests.cs with three tests: (1) `Probe_NullBridge_ReturnsSimSentinels` — asserts the three EditionDetection.Simulated* constants come back unchanged; (2) `Probe_LiveBridge_ThrowsBridgeUnavailable` — passes a stub ILacerteBridge (any throwing or no-op impl), asserts `ex.StatusCode == SyntheticStatusCodes.BridgeUnavailable`, `ex.Context["op"]?.ToString() == "EditionDetection.Probe"`, and `ex.Context.ContainsKey("reason")`; (3) `EditionInfo_Ctor_Rejects...
- [ ] **P14-057** [test-gaps] WireLog redaction misses arrays, nested objects, and whitespace-padded JSON
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 62-64)
  - Why: The _redactRegex only matches scalar values: "(?<key>SSN|EIN|BankAccountNumber|RoutingNumber)":("..."|number|true|false|null).
  - Do: Replace the regex in WireLog.Redact with a JsonNode-walking redactor: parse the JSON; recursively descend through JsonObject/JsonArray; when a property name (case-insensitive) is SSN/EIN/BankAccountNumber/RoutingNumber, replace the entire subtree (scalar OR object OR array) with the string "***". On parse failure, fall back to the existing regex (the safe-net for malformed JSON the existing defensive catch can produce). Also: (a) correct the XML doc on _redactRegex which currently claims "arbitr...

### LOW severity

- [ ] **P14-058** [architecture] MetadataTools calls Schema layer directly, skipping session manager
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/MetadataTools.cs (line 99, 115)
  - Why: LacerteFieldAccessMap calls FieldAccessMapLoader.GetFieldAccess directly instead of routing through the session manager.
  - Do: Drop the local _moduleFileName dictionary in MetadataTools.cs entirely. Replace lines 52-61 with `var moduleInfo = Modules.GetByChar(module); if (moduleInfo == null) throw new LacerteSDKError(StatusCodes.InValidClientName, new Dictionary<string,object?>{ ["module"] = module.ToString(), ["validModules"] = string.Join(",", Modules.All.Select(m => m.Char)) });` and replace line 123 with `moduleName = moduleInfo.Name`. This (a) removes the dup, (b) fixes the cross-tool inconsistency with SessionTool...
- [ ] **P14-059** [architecture] Inverted EnsureLive() name conflates 'no live wiring' with 'is live mode'
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 880-888)
  - Why: The helper is named EnsureLive() but its body throws BridgeUnavailable when Mode == Live.
  - Do: Rename `EnsureLive()` to `ThrowIfLiveUnwired()` and update all five call sites in one sweep. Replace the internal comment with: `// Read paths are sim-only until Phase 9 wires the bridge + ODBC. Sim falls through; live throws BridgeUnavailable.` This phrasing (a) makes the boolean direction obvious from the name alone, (b) survives the Phase 9 cut-over without another rename (the method body simply becomes a no-op or gets deleted once live reads are wired, and the call-site call becomes harmless...
- [ ] **P14-060** [architecture] Session manager's _odbc field is `object? = null` placeholder, not typed
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 60)
  - Why: The session manager declares `private readonly object? _odbc = null;` with a 'Phase 4 task #39 still pending wire-up' comment.
  - Do: Replace `private readonly object? _odbc = null;   // Phase 4 task #39 still pending wire-up.` with a typed `private LacerteDsnReader? _odbc;` field. In the Live constructor branch, leave it null (do NOT eagerly construct — the connection string isn't available until SetContext supplies dataPath, and constructing a reader here would couple session ctor to ODBC config). Add a `// Phase 4 task #39 — constructed lazily on first ODBC read with the active DSN connection string; null until then.` comme...
- [ ] **P14-061** [correctness] EnsureEditionProbed re-probes on every call when the live probe throws
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 283-295)
  - Why: The docstring promises 'one-shot edition probe ...
  - Do: Two-part fix, both sim-testable: (1) Cache the attempt, not just success — wrap the probe in try/catch inside EnsureEditionProbed and on failure assign _edition = new EditionInfo("Unavailable", "Unavailable", "Unavailable") (or add a private bool _editionProbed sentinel) so subsequent SessionStatus calls return the cached unavailable triple without re-entering Probe(). (2) Log the first failure once via WireLog.RecordSync (or a direct WireLog write) at warning level with op="EditionDetection.Pro...
- [ ] **P14-062** [dx-build] App.config carries no configurable settings — env-only configuration
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/App.config (line 1-14)
  - Why: App.config is currently 14 lines of pure runtime-shim (`useLegacyV2RuntimeActivationPolicy` + `supportedRuntime v4.0/v4.6`) — load-bearing per the comment.
  - Do: Drop `LACERTE_DSN` from README.md (line 78), ARCHITECTURE.md (line 175), and .env.example (line 20) — the production ODBC path is intentionally DSN-less (driver + datapath + module + year), confirmed by `docs/research/agents/03-odbc-dbf-access.md` §2 and the fact that no .cs file reads the var. Replace those rows with a short note: "ODBC connections are DSN-less; the `LacerteDSII` DSN is only probed by `lacerte-mcp doctor` as an install-sanity check." Optionally also make DoctorCommand's hardcod...
- [ ] **P14-063** [perf-resources] AssertWritable allocates a new map-key string on every access-map lookup
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 84, 86)
  - Why: GetFieldAccess fires on every write chokepoint (WriteField → AssertWritable → GetFieldAccess).
  - Do: Two-step fix, smallest-blast-radius first: (1) Universal-rule check on line 84: replace the concat-and-equals with an allocation-free comparison — `IsClientNumberField(field, table)` that checks `field.Length == (table < 10 ? 4 : 5)`, `field[0] == 'C'`, last two chars are `_0`, and middle chars parse to the table int (or simpler: precompute a `static readonly string[] _clientNumKeys = { "C0_0", "C1_0", ..., "C9_0" }` indexed by table for the common 0-9 range, fall back to concat for >9). (2) Com...
- [ ] **P14-064** [security] Idempotency key compared with String.Equals — timing oracle when key is sensitive
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1129-1149)
  - Why: TryReplayIdempotent looks up the key in a ConcurrentDictionary and compares PayloadFingerprint with the standard String.Equals (line 1136), which short-circuits on the first byte mismatch.
  - Do: Fix the unbounded-cache leak; skip the constant-time-compare and the new synthetic code. Concretely: (1) Add `private const int MaxIdempotencyEntries = 1024;` and `private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);` constants to LacerteSessionManager. (2) In `TryReplayIdempotent`, after `TryGetValue` returns true, check `DateTimeOffset.UtcNow - entry.CreatedAt > IdempotencyTtl`; if so, `TryRemove` and return false (treat as miss — caller re-executes, same observable behavi...
- [ ] **P14-065** [security] AppId leak via error contexts and CLI commands — partial masking inconsistent
  - File(s): src/LacertMCP.Cli/Commands/RegisterCommand.cs (line 56, 66, 72)
  - Why: SessionStatusSnapshot.AppId is correctly masked to '***' (LacerteSessionManager.cs:271), but the AppId leaks through several other channels: (1) RegisterCommand prints the full GUID to stdout (lines 56, 66, 72) — the rationale is 'save for backup' but the prompt also says it can 
  - Do: Two narrow, sim-friendly changes: (1) In LoginCommand.cs:52, replace `AppID  : {existing.Value:D}` with a masked form matching StatusCommand's Mask() helper (first 8 + ****) — login doesn't need the full GUID and runs more often than register. Extract Mask() from StatusCommand into AppIdStore (or a small Cli/Util class) so DoctorCommand line 195 and LoginCommand can share one canonical masking convention. (2) Add a unit/integration test in LacertMCP.Tests that: registers a known AppID, captures ...
- [ ] **P14-066** [simplification] Remove dead ConnectionPool helper (never referenced)
  - File(s): src/LacertMCP.Server/Odbc/ConnectionPool.cs (line 1-44 (whole file))
  - Why: ConnectionPool.GetOrCreate is defined but referenced by no production or test code (grep finds zero callers outside its own definition).
  - Do: Delete src/LacertMCP.Server/Odbc/ConnectionPool.cs. Lift its design-note header (lines 1-14, the "why no second-layer pool" rationale) verbatim into LacerteDsnReader.cs as a `<remarks>` block on the class or as a file-top banner — that's the only load-bearing content. Update HANDOFF.md line 71 to drop "ConnectionPool" from the Odbc/ inventory. When Phase 4 task #39 wires ODBC into LacerteSessionManager, the session manager should `new LacerteDsnReader(connectionString)` directly (or take it via ...
- [ ] **P14-067** [simplification] Drop no-op WireLog.ResetForTests + unused _testReset field
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 245-249)
  - Why: WireLog.ResetForTests() does '=> _testReset?.Invoke()' where _testReset is declared 'private static readonly Action? _testReset = null' and never reassigned.
  - Do: Delete lines 241-249 of src/LacertMCP.Server/Logging/WireLog.cs in one chunk: the `ResetForTests()` method, its XML doc, the half-finished `// Use reflection-free reset...` comment, and the `_testReset` field. Leave `EnableForTests`, `DisableForTests`, and `WriteForTest` untouched — those are the working test surface (confirmed by grep: zero callers reference `ResetForTests`, so no test churn). If a future test needs to reset the lazy env-resolved config, the cleanest path is to rewrap `_config`...
- [ ] **P14-068** [simplification] ConcurrentDictionary fields and _odbc are over-engineered scaffolding
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 60 (_odbc field), 1215 (Dispose), 75-91 (caches))
  - Why: private readonly object? _odbc = null; is declared, never written, never read, only disposed in Dispose().
  - Do: Delete just two lines in `src/LacertMCP.Server/Session/LacerteSessionManager.cs`: line 60 (`private readonly object? _odbc = null;   // Phase 4 task #39 still pending wire-up.`) and line 1215 (`(_odbc as IDisposable)?.Dispose();`). Leave line 1214 (`(_bridge as IDisposable)?.Dispose();`) and all ConcurrentDictionary fields (`_newApisCache`, `_idempotencyCache`) untouched — those are actively read. The "Phase 4 task #39 pending" reminder is already preserved by the docstring on line 440 of the sa...
- [ ] **P14-069** [simplification] Bridge.Registration carries dead RegisterWithStoredAppId + CredentialAccount
  - File(s): src/LacertMCP.Server/Bridge/Registration.cs (line 120-132 (RegisterWithStoredAppId), 38 (CredentialAccount const))
  - Why: RegisterWithStoredAppId is referenced only by tests (RegistrationTests.cs); production code in LacerteSessionManager.Register() inlines the GetStoredAppId() lookup directly (line 182-189).
  - Do: Delete `RegisterWithStoredAppId` (Registration.cs:109-132) and `CredentialAccount` const (line 38). Do NOT pursue option (a) — `RegisterWithStoredAppId` calls the static `Registration.Register` directly, which would bypass both the `ILacerteBridge` abstraction (used for test mocking and the sim/live split) and `AutoReconnect.WithRetry` (the F12 transient-COM retry wrapper). Inlining in `LacerteSessionManager.Register()` is correct as-is. Test cleanup: drop `RegisterWithStoredAppId_NoAppId_Throws...
- [ ] **P14-070** [simplification] Move 'TODO Stream A' comments out — Stream A tasks #18/#20/#21/#23 have all landed
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 898-900, 1032-1035)
  - Why: Two 'TODO Stream A' comments in LacerteSessionManager.cs reference work that has been completed.
  - Do: Pick option (a) — delete the two stale TODO comments and replace them with a one-line note that explains the *current* design choice (not a deferred promise). Concretely: (1) At lines 898-900, replace the TODO block with: "// Sim helpers below operate on ModuleStore directly rather than delegating to // SimulationStore.SimulateFieldWrite/SimulateCreateClient/WriteDetail. The store // methods exist (Phase 2 tasks #19-21) but layer in their own TAPIStatus guards // that the session manager has alr...
- [ ] **P14-071** [test-gaps] CLI doctor: Pass/Fail exit codes 0 and 1 are never explicitly asserted
  - File(s): src/LacertMCP.Tests/Cli/DoctorCommandTests.cs (line 16-80)
  - Why: DoctorCommandTests only asserts InRange(exit, 0, 2) for the general case and InRange(exit, 1, 2) for the off-Windows case.
  - Do: Minimal-blast-radius refactor: (1) Add `internal static int ComputeExitCode(IList&lt;ProbeResult&gt; results)` that does the hasFail/hasSkip walk + returns 0/1/2 — pure function, no I/O. (2) Keep `Run(TextWriter)` as the orchestrator but have it delegate the exit-code decision to ComputeExitCode. (3) Add three xUnit theories in DoctorCommandTests pinning exit codes against synthetic List&lt;ProbeResult&gt;: all-Pass→0, one-Fail→1, one-Skipped+no-Fail→2, plus Fail-trumps-Skip→1. (4) Optionally ma...
