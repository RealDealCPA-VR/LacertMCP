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

### Deferred to v0.2 (planned breaking-change window)

- [ ] **F15.** Remove legacy pagination envelope keys at v0.2: drop `{count, clients}` from `lacerte_client_list` (`ClientsTools.cs:46-49`) and `{count}` from `lacerte_table_read` (`TablesTools.cs:50-52`). These were retained as additive back-compat when F13 landed pagination — see `DECISIONS.md` 2026-05-30 "Pagination envelope" for the contract. README tool table and any test assertions that still read the legacy keys must be updated in the same change.

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

- [x] **P14-001** [architecture] LacerteHealth bypasses WrapToolHandler and hardcodes sim response
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/HealthTools.cs (line 27-33)
  - Why: LacerteHealth returns a raw string 'reachable (sim)' without using WrapToolHandler.Run and without asking the session manager about mode.
  - Do: In-session (sim-only) fix: change LacerteHealth to `Task<string> LacerteHealth() => WrapToolHandler.Run(() => Task.FromResult<object>(new { mode = _session.Mode.ToString().ToLowerInvariant(), status = "reachable" }));` and update the Description attribute to drop the "(sim)" literal. Update HealthToolsTests.LacerteHealth_ReturnsNonEmptyString_InSimMode to assert the JSON envelope contains `"success":true` and `"mode":"simulation"`. Leave the live HRESULT probe (new session.HealthProbe() backed b...
- [x] **P14-002** [architecture] WriteDetail session chokepoint skips access-map AssertWritable
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 681-752)
  - Why: WriteField (line 595) correctly calls FieldAccessMapLoader.AssertWritable before the wire call, but WriteDetail does not.
  - Do: Tighten Invariant #7 in ARCHITECTURE.md to read "consulted before every per-field write (WriteField)" and add a DECISIONS.md entry titled "Detail-row + whole-row writes are outside the access-map gate" that names the two affected paths (LacerteSessionManager.WriteDetail at 681-752 and CreateClient at 758+), explains the map's M|T|F key shape can't address detail rows or whole-row creates, and points readers to the actual safety nets: DetailsTools.ValidateDetailWrite (line ~96, Series/Code/StateA...
- [x] **P14-003** [architecture] Live-mode reads throw BridgeUnavailable; sim/live observationally divergent
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 397, 426, 457, 517, 538, 880-888)
  - Why: EnsureLive() throws SyntheticStatusCodes.BridgeUnavailable whenever Mode == Live, so every read chokepoint (ListClients, ReadClient, ReadTableCore, ReadDetail, ReadEFStatus) is unreachable in live mode and always returns from the in-memory SimulationStore in sim mode.
  - Do: Rename EnsureLive() to ThrowIfLive() (or EnsureSimPath()) and update the call sites + xmldoc to make the inverted semantics obvious. Add a one-line summary on the method: "Throws BridgeUnavailable in Live mode — every wire chokepoint guards through this until Phase 9 wires the bridge + ODBC reader. Sim path is always reachable." Do NOT attempt the actual ODBC wire-up — that is Phase 4/9 work explicitly gated on Windows + Lacerte SDK and not actionable in this sim-only dev environment. Leave the ...
- [x] **P14-004** [correctness] COM instance leak: Writer/Reader never call Marshal.ReleaseComObject
  - File(s): src/LacertMCP.Server/Bridge/Writer.cs, src/LacertMCP.Server/Bridge/Reader.cs (line Writer.cs:40-60, 76-98, 115-143; Reader.cs:35-57, 63-110)
  - Why: Writer.AcquireWriter() and Reader.AcquireReader() Activator.CreateInstance a fresh COM object (LacerteSDK_Access.DBFWriter / .DBFReader) on EVERY wire call, and the wrapping methods (WriteToDBFDatabase, UpdateTaxReturn, WriteDetail, ReadFromDBFData) never release them.
  - Do: Apply the CanUseNewApisProbe.cs:96-103 pattern to all four COM call sites (Writer.WriteToDBFDatabase, Writer.UpdateTaxReturn, Writer.WriteDetail, Reader.ReadFromDBFData). Concretely: pull `var writer = AcquireWriter(...)` out of the try, then wrap the existing try/catch in an outer try/finally whose finally does `if (writer != null) { try { Marshal.ReleaseComObject(writer); } catch { /* best-effort */ } }`. Do NOT take the cached-instance route the audit mentions as an alternative — Writer/Reade...
- [x] **P14-005** [correctness] Idempotency cache has TOCTOU race; duplicate side-effects possible
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1129-1156)
  - Why: TryReplayIdempotent and StoreIdempotent are unsynchronized.
  - Do: Replace the unsynchronized `TryReplayIdempotent`/`StoreIdempotent` pair with per-key serialization. Concrete approach: add `private readonly ConcurrentDictionary<string, SemaphoreSlim> _idempotencyLocks = new();` Refactor the three write paths (WriteField line 589/673, WriteDetail line 692/750, CreateClient line 769/835) to call a single helper `Task<T> ExecuteIdempotentAsync<T>(string? key, string payload, Func<Task<T>> body)` that (1) returns body() directly if key is null, (2) acquires `_idem...
- [x] **P14-006** [docs] ARCHITECTURE.md states MCP SDK 1.3.0 but project pins 0.3.0-preview.4
  - File(s): C:/Users/VR/Projects/LacertMCP/ARCHITECTURE.md (line 10)
  - Why: ARCHITECTURE.md Stack section claims `ModelContextProtocol 1.3.0 (Microsoft + Anthropic official)`, but the csproj pins `0.3.0-preview.4` and DECISIONS.md 2026-05-30 'MCP SDK version pinning' entry explicitly overrides the original 1.3.0 claim, noting it was incorrect.
  - Do: Make two targeted markdown edits, both safe and in-session:  1. ARCHITECTURE.md line 10 — change `* **MCP SDK:** \`ModelContextProtocol\` 1.3.0 (Microsoft + Anthropic official), stdio transport.` to `* **MCP SDK:** \`ModelContextProtocol\` 0.3.0-preview.4 (Microsoft + Anthropic official preview; 1.x not yet on NuGet), stdio transport. See DECISIONS.md "MCP SDK version pinning" 2026-05-30.`  2. DECISIONS.md line 21 — append an inline correction marker to the "Language/runtime stack" entry so the ...
- [x] **P14-007** [docs] CLAUDE.md is entirely TypeScript — describes wrong stack
  - File(s): C:/Users/VR/Projects/LacertMCP/CLAUDE.md (line 18,55,56,74-83)
  - Why: CLAUDE.md (the project operating system) describes a TypeScript / Node / Zod / ESM stack — `Transport — stdio MCP server bound in src/index.ts`, `Adding a tool means: register in src/index.ts`, `npm run build`, `TypeScript strict mode, ES2022, ESM modules`, `MCP tools registered 
  - Do: Edit CLAUDE.md to align with DECISIONS.md 2026-05-30 stack entry: (1) Line 18 — replace "stdio MCP server bound in `src/index.ts`" with "stdio MCP server bound in `src/LacertMCP.Server/Program.cs` via `McpServer` + `WithStdioServerTransport()` + `WithToolsFromAssembly()`". (2) Line 55 — replace "register in `src/index.ts`, handle the request in the simulation store, update the `instructions` blurb, and add it to the README tool table" with "add an `[McpServerTool]`-decorated method on the approp...
- [x] **P14-008** [docs] REGRESSION_CHECKLIST.md still gates on npm/tsc, not dotnet
  - File(s): C:/Users/VR/Projects/LacertMCP/REGRESSION_CHECKLIST.md (line 7-15)
  - Why: The first three check items are `npm run build (tsc) passes with no errors`, `No new TypeScript errors in unrelated files`, `All relative imports keep their .js extension (Node16 ESM)`.
  - Do: Rewrite the top three sections of REGRESSION_CHECKLIST.md as follows. (1) Build/Compile → `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` must report `0 warnings, 0 errors`; note that system-wide `dotnet` is runtime-only — must use user-local SDK. (2) Tests → `& "$env:USERPROFILE\.dotnet\dotnet.exe" test src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` must report `481 passed, 11 skipped, 0 faile...
- [x] **P14-009** [dx-build] No CI workflow — build/test only run locally
  - File(s): C:/Users/VR/Projects/LacertMCP/.github/workflows/ (missing) (line n/a)
  - Why: No .github/workflows/, azure-pipelines.yml, or .gitlab-ci.yml exists.
  - Do: Add `.github/workflows/ci.yml` triggered on push + PR with a **windows-latest job as the primary gate** (high value, drop-in): `actions/setup-dotnet@v4` for the SDK, `actions/cache@v4` keyed on `**/packages.lock.json` or `**/*.csproj` hash for `~/.nuget/packages`, then `dotnet restore LacertMCP.sln`, `dotnet build LacertMCP.sln -c Debug -p:Platform=x64 --no-restore`, `dotnet test src/LacertMCP.Tests/LacertMCP.Tests.csproj -c Debug -p:Platform=x64 --no-build --logger "trx;LogFileName=test-results...
- [x] **P14-010** [dx-build] No release packaging — no way to produce a distributable artifact
  - File(s): C:/Users/VR/Projects/LacertMCP/scripts/release.ps1 (missing) (line n/a)
  - Why: The README install instructions ask the operator to point Claude Desktop / Cursor / Windsurf at `C:\path\to\LacertMCP\src\LacertMCP.Server\bin\x64\Release\LacertMCP.Server.exe` — i.e.
  - Do: Add `scripts/release.ps1` that (a) builds both projects: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build LacertMCP.sln -c Release -p:Platform=x64`, (b) stages `src/LacertMCP.Server/bin/x64/Release/` (verifying `LacertMCP.Server.exe`, `App.config`, and `Schema/FieldAccessMap/manifest.json` + `base/` + `overlays/` are all present — fail loudly if any are missing, since the Schema tree is the silent-failure trap), (c) stages `src/LacertMCP.Cli/bin/x64/Release/`, (d) emits `dist/LacertMCP-v{version}....
- [x] **P14-011** [dx-build] Not under git — no version control state for collaboration
  - File(s): C:/Users/VR/Projects/LacertMCP/ (line n/a)
  - Why: `git -C C:/Users/VR/Projects/LacertMCP status` returns 'fatal: not a git repository.' Despite a polished .gitignore, well-formed README, CLAUDE.md describing an 'AI session lifecycle' that involves multiple contractor-like agents, a CONTRIBUTING-style operating system file, and T
  - Do: In-session, do only the local-git work (defer remote + tagging to the user / Windows session): (1) Create `.gitattributes` with `* text=auto eol=lf` and per-type Windows-CRLF overrides for `*.cs`, `*.csproj`, `*.sln`, `*.props`, `*.targets`, `*.config`, `*.cmd`, `*.bat`, `*.ps1`; mark `*.dbf`, `*.pdf`, `*.png`, `*.ico` binary. (2) `git init -b main` then `git add . && git commit -m "Initial commit: v0.1.0 scaffolding (481 sim tests passing, 11 live-only skipped)"`. (3) Do NOT add a remote or pus...
- [x] **P14-012** [perf-resources] WireLog opens + closes log file on every record (per-call FS open)
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 211-223)
  - Why: WriteRecord calls File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8) inside the lock for every wire op.
  - Do: Replace the per-call File.AppendAllText with a cached StreamWriter keyed by full log file path (path includes UTC date, so a new path naturally appears at midnight rollover — no separate timer needed). Sketch: a static Dictionary<string, StreamWriter> guarded by _writeLock; on each WriteRecord, compute the target path, look up the writer, and if absent (new date or first call) close+remove any stale entries for a different path then open `new StreamWriter(new FileStream(path, FileMode.Append, Fi...
- [x] **P14-013** [perf-resources] Idempotency cache has no eviction, TTL, or size cap
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 89-90, 1129-1156)
  - Why: _idempotencyCache is a ConcurrentDictionary<string, IdempotencyCacheEntry> that grows monotonically.
  - Do: Keep the ConcurrentDictionary (no new package reference). Make CreatedAt earn its keep: (1) in TryReplayIdempotent, if entry.CreatedAt < UtcNow - 24h, TryRemove it and treat as miss; (2) in StoreIdempotent, after writing, if _idempotencyCache.Count > IdempotencyCacheMaxEntries (const = 10_000), scan and TryRemove all entries older than the TTL, and if still over cap, remove the oldest N by CreatedAt. Both paths are lazy/opportunistic — no Timer, no background thread, no race-prone scrubber. Expo...
- [x] **P14-014** [perf-resources] ListClients pulls full roster then paginates in-tool (no SQL TOP/OFFSET)
  - File(s): src/LacertMCP.Server/Tools/ClientsTools.cs (line 32-49 (tool) + src/LacertMCP.Server/Odbc/SqlTemplates.cs:56-57 (SQL))
  - Why: LacerteClientList calls _session.ListClients(nameFilter, int.MaxValue) — explicitly defeating any server-side cap — then hands the materialized list to Pagination.Page which slices in memory.
  - Do: Two-part sim-only fix, both verifiable off-Windows: (1) In ClientsTools.LacerteClientList, replace `int.MaxValue` with `offset + Math.Min(limit, Pagination.MaxLimit)` so the session materializes at most one page-worth of rows (Pagination.Page still computes correct `total` because we pass it the same bounded slice — actually no: total would be wrong. So instead, push limit/offset into LacerteSessionManager.ListClients itself: add overload `ListClients(filter, limit, offset)` that returns `(rows,...
- [x] **P14-015** [readme-cli] Missing Prompts section — three shipped prompts undiscoverable
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 101-134)
  - Why: README has a `Tools` section but no `Prompts` section, yet `WorkflowPrompts.cs` ships three first-class MCP prompts (`lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_bulk_field_update`) registered via `WithPromptsFromAssembly()`.
  - Do: Insert a new `## Prompts` section between `## Tools` (ends line 133) and `## CLI` (line 135). Use a three-column table: `Prompt | What it walks through | Args`. Concrete rows (lifted from each method's `[Description]` + parameter signatures):  | Prompt | What it walks through | Args | | --- | --- | --- | | `lacerte_client_intake` | Onboard a new client end-to-end: verify session → pick datapath/module/year → `client_create` → seed DATA1 fields → seed detail rows → verify roster. | `clientName?: ...
- [x] **P14-016** [readme-cli] Env var matrix missing six variables that the code actually reads
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 70-82)
  - Why: The env-var table omits several vars that production/test code consults: `LACERTE_DATA_ROOT` (SessionTools.LacerteListDatapaths uses it as the discovery root with `C:\Lacerte` fallback — and the README's `lacerte_list_datapaths` row references it on line 111), `LACERTE_MCP_FORCE_
  - Do: Two-part fix, both in-session: (A) README.md env table: add three rows — `LACERTE_DATA_ROOT` (optional, default `C:\Lacerte`, datapath discovery root used by `lacerte_list_datapaths`); `LACERTE_MCP_FORCE_FILE_STORE` (optional, `1` forces JSON file store instead of Windows Credential Manager — useful for headless/non-interactive Windows hosts and is used by the test harness); `LACERTE_PARITY_RECORDING` (testing only, path to recorded parity JSON consumed by `BothModesData` harness). (B) Fix three...
- [x] **P14-017** [readme-cli] CLI quick-start claim `doctor exit 1 off-Windows; that's expected` is wrong
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 28)
  - Why: Quick-start says `& lacerte-mcp.exe doctor   # exit 1 off-Windows; that's expected`.
  - Do: Replace line 28 with two lines that match the actual 0/1/2 contract and call out the fresh-checkout case explicitly: `& src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor   # exit 2 off-Windows = PARTIAL (Windows-only probes skipped); exit 1 = a probe failed (e.g. fresh checkout with no AppID — run 'lacerte-mcp register' or test fixture seeds it); exit 0 = all green (Windows + Lacerte install).` Optionally also fix the `& lacerte-mcp.exe` form referenced in the finding's `description` field ...
- [x] **P14-018** [security] WireLog redaction regex misses common PII fields (DOB, address, phone, email, names)
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 62-64)
  - Why: The redaction regex covers only SSN, EIN, BankAccountNumber, RoutingNumber.
  - Do: Three-part fix, all in WireLog.cs + WireLogTests.cs (no Windows/SDK dependency):  1) Expand the key-name regex alternation to cover Lacerte-realistic PII keys: SSN|TaxpayerSSN|SpouseSSN|DependentSSN|TIN|ITIN|PTIN|EIN|EmployerEIN|FederalID|FedEIN|BankAccountNumber|RoutingNumber|DateOfBirth|DOB|BirthDate|EmailID|Email|HomePhone|WorkPhone|CellPhone|PhoneNumber|FirstName|LastName|MiddleName|Address1|Address2|StreetAddress|City|ZipCode|Zip|Description.  2) Add a second pass that specifically targets ...
- [x] **P14-019** [security] Tool error context dict bypasses WireLog redaction — PII echoed to MCP client
  - File(s): src/LacertMCP.Server/Errors/WrapToolHandler.cs (line 72-85)
  - Why: WrapToolHandler.SerializeError serializes the LacerteSDKError.Context dictionary straight to the MCP wire with no redaction.
  - Do: Single-file fix in src/LacertMCP.Server/Errors/WrapToolHandler.cs: apply the existing WireLog.Redact() pass to the serialized JSON in BOTH SerializeError (line 84) and the success path (line 51) — `return WireLog.Redact(JsonSerializer.Serialize(envelope, JsonOptions));`. This reuses the project's proven SSN/EIN/BankAccountNumber/RoutingNumber regex and closes the wire-vs-disk asymmetry symmetrically (success envelopes also carry ODBC-read row data with those fields). Add an additional regex hit ...
- [x] **P14-020** [simplification] Hoist _moduleFileName + _moduleTaxTypes onto Modules so 3 copies become 1
  - File(s): src/LacertMCP.Server/Schema/Modules.cs, src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs, src/LacertMCP.Server/Tools/MetadataTools.cs, src/LacertMCP.Cli/Commands/FieldAccessCommand.cs, src/LacertMCP.Server/Session/SimulationStore.cs, src/LacertMCP.Server/Tools/CreateClientTools.cs (line FieldAccessMapLoader.cs:61-72; MetadataTools.cs:30-41; FieldAccessCommand.cs:29-40; SimulationStore.cs:265-277; CreateClientTools.cs:39-51)
  - Why: The Char->filename basename map ('I'->"individual", etc.) is duplicated verbatim in 3 places (FieldAccessMapLoader, MetadataTools, FieldAccessCommand).
  - Do: Extend Schema/Modules.cs as the single source. Add two fields to ModuleInfo: `string FileBasename` and `IReadOnlyList<string> TaxTypeAliases`. Populate them in the existing Modules.All initializer (the 9 rows become e.g. `new ModuleInfo('I', "Individual", "Individual", "individual", new[] { "Individual", "1040" })`). Add two convenience helpers — `Modules.FileBasenameFor(char) -> string?` and `Modules.AcceptsTaxType(char, string) -> bool` (case-insensitive, matching the existing semantics) — so ...
- [x] **P14-021** [spec-compliance] EF status SQL template omits EFilingId/EFId/InstanceId that tool returns
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Odbc/SqlTemplates.cs (line 159-163)
  - Why: EFileTools.LacerteEfStatus [Description] promises `flat rows with EFilingId, ClientId, EFId, InstanceId, Status, StatusDate.` The simulation path in LacerteSessionManager.ReadEFStatus() returns exactly those 6 keys.
  - Do: Rewrite `SqlTemplates.EfStatusJoin` so the SELECT list matches the sim contract exactly: `select ci.C1_0 as ClientId, ef.Id as EFilingId, ef.EFId, ef.InstanceId, s.EFStatus as Status, s.EFStatusDate as StatusDate from [DATA1-Client Info] as ci left outer join [EFilings] as ef on ci.C1_0 = ef.ClientId left outer join [EFStatuses] as s on ef.Id = s.EFilingId`. Also fix the misleading XML doc-comment from "Latest EF status per client" to "All EF status rows joined per client (full history)" — neith...
- [x] **P14-022** [spec-compliance] Pre-TY21 WriteToDBFDatabase row-number path uses int.Parse(clientNumber) placeholder
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 638-650)
  - Why: Spec §4 Write: `lacerte_field_write — pre-TY21: WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value).` The session manager's WriteField, when CanUseNewApis returns false (TY18-TY20), calls `_bridge.WriteField(appId, ctx.DataPath, ctx.Year, ctx.Module, table, rowNum, fie
  - Do: Add `int ResolveRowNumber(string appId, string dataPath, int year, char module, int table, string clientNumber)` to ILacerteBridge. Sim implementation enumerates the table's ConcurrentDictionary (Clients/Statuses/Displays — DATA1/5/6 are keyed by ClientNumber alone; DATA2/3/4 need extra keys but spec §4 pre-TY21 row-keyed writes only target the C{t}_0 row, so resolution is well-defined) and returns the 1-based ordinal. Live implementation issues `select ROWNUM from [DATA{t}-...] where C{t}_0 = ?...
- [x] **P14-023** [test-gaps] AutoReconnect: 2 of 4 whitelisted HRESULTs untested; ExternalException path untested
  - File(s): src/LacertMCP.Tests/Resilience/AutoReconnectTests.cs (line 94-120)
  - Why: AutoReconnect.TransientHResults whitelists 4 HRESULTs (0x800706BA, 0x80010108, 0x8001010A, 0x80010001 — AutoReconnect.cs:43-46) but tests only exercise the first two (RpcUnavailable + RpcDisconnected, AutoReconnectTests.cs:101-113).
  - Do: Replace IsTransient_RpcUnavailable_True and IsTransient_RpcDisconnected_True with one [Theory] over all 4 whitelisted HRESULTs (0x800706BA, 0x80010108, 0x8001010A, 0x80010001) using COMException. Add: (1) [Theory] IsTransient_ExternalException_TrueForAllWhitelisted — constructs a System.Runtime.InteropServices.ExternalException with each HRESULT and asserts true (pins the AutoReconnect.cs:148-151 branch); (2) [Fact] IsTransient_BaseException_HResultFallback_True — creates new Exception("x") { HR...
- [x] **P14-024** [test-gaps] FieldAccessMapLoader overlay precedence + 3 of 5 AssertWritable branches untested
  - File(s): src/LacertMCP.Tests/Schema/FieldAccessMapLoaderTests.cs (line 1-81)
  - Why: FieldAccessMapLoader.AssertWritable (FieldAccessMapLoader.cs:121-163) emits 5 distinct status codes — ExposedForRead, NotExposed (Hidden+NotExposed), FieldNotSupported (overlay), UnknownFieldAccess, and the defensive Error default.
  - Do: Extend the existing AccessMapTestFixture pattern (src/LacertMCP.Tests/Tools/AccessMapTestFixture.cs) — which already writes a real shard file and clears the static caches via reflection on _moduleCache/_overlayCache — to add: (1) three AssertWritable tests that seed a Hidden, NotExposed, and NotSupported entry respectively and assert StatusCodes.NotExposed(7) / NotExposed(7) / FieldNotSupported(26); (2) one overlay-precedence test that seeds the base shard with field X = ReadWrite and the ty24 o...
- [x] **P14-025** [test-gaps] lacerte_update_return tool has zero direct tests
  - File(s): src/LacertMCP.Server/Tools/UpdateReturnTools.cs (line 31-73)
  - Why: UpdateReturnTools is a registered MCP tool (one of only two public write entrypoints) but grepping the test tree for UpdateReturnTools / LacerteUpdateReturn / update_return returns zero matches.
  - Do: Create `src/LacertMCP.Tests/Tools/UpdateReturnToolsTests.cs` by copying the structure of `FieldWriteToolsTests.cs` and substituting `UpdateReturnTools` / `LacerteUpdateReturn`. Six facts (mirroring the existing sibling exactly): (1) happy-path success — assert `success==true`, `result.statusCode==1`, AND `result.api=="UpdateTaxReturn"` (the key delta vs. FieldWriteTools, since this string is the only operator-visible signal distinguishing the two write entrypoints); (2) ReadOnly session rejectio...

### MEDIUM severity

- [x] **P14-026** [architecture] LacerteUpdateReturn lies about the live API used (api="UpdateTaxReturn" for TY<2021)
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/UpdateReturnTools.cs (line 60-69)
  - Why: The tool response hardcodes `api = "UpdateTaxReturn"` regardless of the year-routing decision the session manager makes.
  - Do: Smallest correct fix: drop the `api` field from UpdateReturnTools' anonymous response (mirroring FieldWriteTools, which already omits it). The tool description already names "UpdateTaxReturn" as the intended entrypoint; the runtime response should not assert which wire actually ran. If a wire-truth label is desired later, add a `CodeResult { int status, bool usedNewApi }` return from session.WriteField rather than have the tool peek at session state — that keeps year-routing inside the session m...
- [x] **P14-027** [architecture] CreateClientTools duplicates simulation-store validation table inside tool layer
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/CreateClientTools.cs (line 35-51, 132-156)
  - Why: The tool keeps its own _moduleTaxTypes dictionary 'mirroring SimulationStore's' and reaches into _session.Context.Module to decide whether the TaxType is valid.
  - Do: Add `IReadOnlyList<string> AcceptedTaxTypes` to `Schema.ModuleInfo` (or a parallel `Modules.AcceptedTaxTypesByChar` dictionary) sourced from Lacerte_MCP_Structure_Spec.md. Replace both `_moduleTaxTypes` dictionaries (CreateClientTools.cs:39-51, SimulationStore.cs:265+) with calls to `Modules.GetByChar(module)?.AcceptedTaxTypes`. Keep the early-throw structured-error path in CreateClientTools intact — the gain is one catalog, not a new session API. Do not introduce `session.ValidateCreateClientAr...
- [x] **P14-028** [correctness] SimulationStore.WriteDetail mutates List<DetailRow> without a lock
  - File(s): src/LacertMCP.Server/Session/SimulationStore.cs (line 441-465)
  - Why: SimulationStore.WriteDetail does `list.Add(row)` and `list[replaceIndex.Value] = row` on the inner List<DetailRow> without any synchronization, even though Details is a ConcurrentDictionary<string, List<DetailRow>> shared across threads.
  - Do: Add `lock (list) { ... }` around the mutate block (lines 454-463) in SimulationStore.WriteDetail, matching the existing pattern in LacerteSessionManager.ApplySimDetailWrite. Do NOT remove the lock from ApplySimDetailWrite — contrary to the finding's framing, that method does not call WriteDetail; it goes straight to module.Details with its own logic. Both paths must hold the lock independently. While you're in the file, also lock the read side: wrap the `return list.ToList();` and the `foreach (...
- [x] **P14-029** [correctness] Universal C{t}_0 ReadOnly rule is case-sensitive; lowercase fields bypass it
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 84)
  - Why: `if (field == "C" + table.ToString() + "_0") return FieldAccess.ReadOnly;` is an ordinal case-sensitive equality.
  - Do: Normalize the field name once at the top of `GetFieldAccess` rather than scattering `StringComparison` arguments. Concretely, after the null guard on line 81: `var normField = field.ToUpperInvariant();` and use `normField` for the universal-rule comparison and the key construction. This guarantees the universal rule and base/overlay lookups stay consistent without needing to also re-key the deserialized dictionaries (which would otherwise require a post-deserialize copy into a `Dictionary<string...
- [x] **P14-030** [correctness] Sim CreateClient bypasses Registration, TaxType-Module, and ClientID-regex gates
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 991-1026)
  - Why: ApplySimCreateClient (the sim-mode write path for CreateClient) only checks ClientID length and duplicate ClientID; it ignores the IsRegistered latch, the TaxType↔Module match, and the ClientID character whitelist regex (^[A-Z0-9]+$).
  - Do: In ApplySimCreateClient (LacerteSessionManager.cs:991-1026), replace the direct DbfRow construction with a delegation to _sim!.SimulateCreateClient, then translate the (Status, ClientNumber) tuple back to the manager's throw-on-error contract:  ```csharp private CreateClientResult ApplySimCreateClient(SessionContext ctx, CreateClientArgs args) {     var (status, clientNumber) = _sim!.SimulateCreateClient(new SimulateCreateClientArgs(         ctx.DataPath, ctx.Year, ctx.Module,         args.Clien...
- [x] **P14-031** [correctness] Pre-TY21 row-number lookup uses int.Parse(clientNumber) with silent -1 fallback
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 644-650)
  - Why: In the live WriteField path for pre-TY21 (CanUseNewApis returns false), the code does `int rowNum = int.TryParse(clientNumber, out var rn) ? rn : -1;` and passes -1 to `_bridge.WriteField(...)`.
  - Do: In `LacerteSessionManager.cs` pre-TY21 live branch (around line 645), replace the silent `int.TryParse(...) ? rn : -1` with a fail-closed throw: `throw new LacerteSDKError(SyntheticStatusCodes.SimulationOnly, new Dictionary<string, object?> { ["op"] = "WriteField", ["reason"] = "Pre-TY21 live writes require a ClientNumber→rowNum lookup that is not yet implemented (Todo task #51 follow-up). Use TY21+ or run in simulation mode.", ["year"] = ctx.Year, ["module"] = ctx.Module, ["clientNumber"] = cli...
- [x] **P14-032** [correctness] TaxYearOverlay loaded per-year only; deltas don't propagate to later years
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 191-214)
  - Why: LoadOverlay reads `ty{yy}-not-supported.json` for exactly the requested year.
  - Do: The semantic is already decided (per-exact-year, full-replace, per Agent 04 §6) — don't change behavior, harden it: (1) Add a dated entry to DECISIONS.md citing Agent 04 §6: "Per-year overlay files are full-replace, not cumulative deltas. Each TY ships its own complete `tyNN-not-supported.json` listing every field dropped in that build, even if previously dropped." (2) Update the XML doc on `LoadOverlay` and the header on `Schema/FieldAccessMap/README.md` to state this explicitly; reword the REA...
- [x] **P14-033** [docs] README env var table missing LACERTE_DATA_ROOT, LACERTE_PARITY_RECORDING, LACERTE_MCP_FORCE_FILE_STORE
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 68-82)
  - Why: The 'Environment variables (full reference)' table is labeled 'full reference' but three env vars are read by production code and not documented: `LACERTE_DATA_ROOT` (read at `Tools/SessionTools.cs:146` in `LacerteListDatapaths` — drives the discovery root for `lacerte_list_datap
  - Do: Add three rows to the README env table at README.md:68-82 and mirror to .env.example (per ARCHITECTURE.md:180): (1) `LACERTE_DATA_ROOT` — optional, default `C:\Lacerte`, root scanned by `lacerte_list_datapaths` (user-facing — list under 'optional', not 'testing only'); (2) `LACERTE_MCP_FORCE_FILE_STORE` — testing/dev only, `1` bypasses Credential Manager and uses the file-based AppID store at `%APPDATA%\LacertMCP\config.json` (auto-on off-Windows); (3) `LACERTE_PARITY_RECORDING` — testing only, ...
- [x] **P14-034** [docs] README + ARCHITECTURE.md never mention the 3 MCP prompts that exist
  - File(s): multiple (line n/a)
  - Why: `src/LacertMCP.Server/Prompts/WorkflowPrompts.cs` exposes three `[McpServerPrompt]` entries — `lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_bulk_field_update` — registered via `WithPromptsFromAssembly()` per HANDOFF.md.
  - Do: Three concrete edits, all sim-safe: (1) README.md — add a "### Prompts (Phase X)" subsection under "## Tools" with a 3-row table: name | what it walks the agent through | params. Use the existing [Description] strings from WorkflowPrompts.cs verbatim as the descriptions. (2) ARCHITECTURE.md line 201 — delete the "MCP Prompts API for workflow bundles (`lacerte_client_intake`, `lacerte_review_efile_status`)" bullet entirely (don't move to a "recently landed" note — the deferred list should only li...
- [x] **P14-035** [docs] ARCHITECTURE.md Bridge file list misses Writer/Reader/Registration/EditionDetection/CanUseNewApisProbe
  - File(s): C:/Users/VR/Projects/LacertMCP/ARCHITECTURE.md (line 56,58)
  - Why: The Module Boundaries table line for Bridge lists only `LacerteAccessClient.cs + helpers`.
  - Do: Edit ARCHITECTURE.md Module Boundaries table:  1. **Replace the Bridge row** (line 56) so the Path column lists each file with a one-line responsibility:    - `Bridge/ILacerteBridge.cs` — interface seam for swappable bridge (live vs null/sim)    - `Bridge/LacerteAccessClient.cs` — late-bound `LacerteSDK_Access` aggregate + lifetime owner    - `Bridge/Registration.cs` — `ITPRegistration` ceremony, called once at startup (Boundary Invariant #8)    - `Bridge/Reader.cs` — `IDBFReader` COM surface (i...
- [x] **P14-036** [docs] ACCEPTANCE_CRITERIA.md uses 'npm run build' as the pass criterion; missing Prompts + post-pagination response shape rows
  - File(s): C:/Users/VR/Projects/LacertMCP/ACCEPTANCE_CRITERIA.md (line 17,89-93)
  - Why: The 'Strong example' rubric ends with `npm run build passes` (line 17) — wrong toolchain.
  - Do: Edit `C:/Users/VR/Projects/LacertMCP/ACCEPTANCE_CRITERIA.md`: (1) Line 17: replace `> `npm run build` passes.` with `> `dotnet build LacertMCP.sln -c Debug -p:Platform=x64` reports 0 warnings / 0 errors and `dotnet test src/LacertMCP.Tests` is green.`  (2) Line 49 (#42): replace `Returns {count, clients}` with `Returns the paginated envelope {rows, total, limit, offset, hasMore, nextOffset}; legacy {count, clients} keys retained for back-compat through v0.1.x and REMOVED in v0.2 per DECISIONS.md...
- [x] **P14-037** [docs] DECISIONS.md missing entries for landed subsystems: WireLog redaction, AutoReconnect backoff, Pagination contract
  - File(s): C:/Users/VR/Projects/LacertMCP/DECISIONS.md (line n/a)
  - Why: Three load-bearing architectural choices landed in wave-4 (per HANDOFF.md F11/F12/F13) without a corresponding `DECISIONS.md` entry: (1) WireLog log format + the specific PII redaction list (SSN/EIN/BankAccountNumber/RoutingNumber) — there is no log-schema or redaction-policy ent
  - Do: Append three dated entries to DECISIONS.md following the existing Chosen/Why/Alternatives/Downstream format. Source the facts from the XML doc comments already in WireLog.cs, AutoReconnect.cs, and Pagination.cs (plus the inline v0.2 notes in ClientsTools.cs:35-37 and TablesTools.cs:37). Each entry should be ~15-25 lines: (1) "WireLog format + PII redaction policy" — what's redacted (SSN/EIN/BankAccountNumber/RoutingNumber), what isn't (client names, addresses, dollar amounts — these are operatio...
- [x] **P14-038** [dx-build] No .editorconfig — formatting and style drift across files
  - File(s): C:/Users/VR/Projects/LacertMCP/.editorconfig (missing) (line n/a)
  - Why: There is no .editorconfig at the repo root and no analyzer/style packages (StyleCop, Microsoft.CodeAnalysis.NetAnalyzers, dotnet-format config).
  - Do: Add two files at repo root, skip the analyzer packages entirely. (1) Directory.Build.props with a single PropertyGroup containing TargetFramework=net48, PlatformTarget=x64, Platforms=x64, LangVersion=latest, Nullable=enable — then delete those 5 lines from each of the 4 csproj files (keep per-project overrides like OutputType, RootNamespace, AssemblyName). (2) .editorconfig with: root=true, [*] indent_style=space, insert_final_newline=true, trim_trailing_whitespace=true (exempt *.md); [*.cs] ind...
- [x] **P14-039** [dx-build] scripts/ folder is empty — no build/test/release wrappers
  - File(s): C:/Users/VR/Projects/LacertMCP/scripts/ (line n/a)
  - Why: The scripts/ and config/ directories exist but are empty.
  - Do: Add four wrappers under scripts/ that resolve dotnet once at the top:  (1) scripts/_dotnet.ps1 — shared helper exporting `$script:Dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"` with a fallback probe to `Get-Command dotnet` if the user-local SDK is missing, and a clear error if neither has an SDK (not just runtime).  (2) scripts/build.ps1 — dot-sources _dotnet.ps1, runs `& $Dotnet build $PSScriptRoot\..\LacertMCP.sln -c Debug -p:Platform=x64 @args`.  (3) scripts/test.ps1 — same pattern against s...
- [x] **P14-040** [dx-build] No assembly version stamping — binaries identify as 1.0.0.0 forever
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/LacertMCP.Server.csproj (line 15)
  - Why: The Server csproj sets `<GenerateAssemblyInfo>true</GenerateAssemblyInfo>` but never sets <Version>, <AssemblyVersion>, <FileVersion>, or <InformationalVersion>.
  - Do: Create C:/Users/VR/Projects/LacertMCP/Directory.Build.props with: `<Project><PropertyGroup><Version>0.1.0</Version><AssemblyVersion>0.1.0.0</AssemblyVersion><FileVersion>0.1.0.0</FileVersion><InformationalVersion>$(Version)+$(SourceRevisionId.Substring(0,7))</InformationalVersion></PropertyGroup></Project>` (guard the Substring with a Condition so it no-ops when SourceRevisionId is empty in non-git builds — otherwise sim CI breaks). Then: (1) add a `BuildVersion` static helper in LacertMCP.Serve...
- [x] **P14-041** [dx-build] No code-coverage configuration — 481-test discipline isn't measured
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj (line 14-21)
  - Why: Tests.csproj pulls Microsoft.NET.Test.Sdk + xunit + xunit.runner.visualstudio but does not reference coverlet.collector or coverlet.msbuild.
  - Do: Minimal, in-session: add `<PackageReference Include="coverlet.collector" Version="6.0.2" />` (and ideally `coverlet.msbuild` for `/p:CollectCoverage=true` invocations) to LacertMCP.Tests.csproj, plus a checked-in `coverlet.runsettings` that excludes the FieldAccessDumper and generated bridge code, and a one-line README snippet documenting `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings`. Skip the 80% threshold, ReportGenerator wiring, and coverage badge until a CI pi...
- [x] **P14-042** [perf-resources] BuildPayload SHA-256 + JsonSerializer runs on every write even when idempotencyKey is null
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 586-588, 691-695, 768, 1158-1175)
  - Why: WriteField, WriteDetail, and CreateClient ALL compute payload = BuildPayload(...) before calling TryReplayIdempotent, regardless of whether idempotencyKey was supplied.
  - Do: Two-part fix in LacerteSessionManager.cs: (1) In WriteField (~line 586), WriteDetail (~691), and CreateClient (~768), guard the BuildPayload call: `string payload = string.IsNullOrEmpty(idempotencyKey) ? string.Empty : BuildPayload("WriteField", ...)`. The existing TryReplayIdempotent + StoreIdempotent already no-op on null/empty key, so the empty payload is never inspected. (2) In BuildPayload (line 1158), hoist a `private static readonly JsonSerializerOptions s_payloadJsonOptions = new JsonSer...
- [x] **P14-043** [perf-resources] SimSnapshot deep-clones full active module per dry-run write
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1054-1098)
  - Why: SimSnapshot walks Clients, SubClients, States, StateSubs, Statuses, Displays, Forms, AND Details for the active module and CloneDbfRow's every row, then SimRestore replays the entire snapshot back into the live module by Clear+repopulate.
  - Do: Replace SimSnapshot/SimRestore with per-operation undo closures returned from a small helper. Concretely: (a) WriteField — capture `(table, key, field, hadPrior, priorValue)` before `row.Fields[field] = value`; restore by setting back or removing. Validation throws (SubClientIDRequired/StateAbbrevRequired/OutOfIndex) happen before any mutation, so the dry-run wrapper should only register an undo *after* ApplySimFieldWrite has located the row and is about to write — making failed dry-runs free. (...
- [x] **P14-044** [readme-cli] README Status line is stale (469/480 instead of 481/492)
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 5)
  - Why: The Status badge claims `v0.1.0 scaffolding — 469/480 tests passing (11 expected live-only skips)`.
  - Do: Edit README.md line 5 to read: `**Status:** v0.1.0 scaffolding — 481/492 tests passing (11 expected live-only skips), 0 failures, 0 warnings.` Keep the existing trailing prose. Skip the "CI badge" / ">= 481" suggestions — this is a single-operator personal tool with no CI, and a soft lower-bound count would itself drift; the right long-term answer (already partially in place) is that HANDOFF.md + REGRESSION_CHECKLIST.md are the source of truth and the README just mirrors the latest known-good nu...
- [x] **P14-045** [readme-cli] CLI table omits subcommand flags, hiding key knobs
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 138-144)
  - Why: The CLI table lists commands but no flags.
  - Do: Replace the current 5-row table in README.md (lines 137-143) with a per-command subsection. Each subsection should show: the full invocation signature with options, a one-line purpose, defaults for any optional flag, and a callout for non-obvious behavior. Specifically: (1) `register` — call out `--reset` as the AppID recovery path and warn it forces a fresh Intuit consent; (2) `status` — note `--show-app-id` is the documented backup-before-reinstall path; (3) `field-access discover` — explicitl...
- [x] **P14-046** [readme-cli] Missing high-value CLI subcommands operators will need
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/Program.cs (line 32-36)
  - Why: The CLI ships register/login/status/field-access/doctor, but several common operator verbs are absent: (1) `list-tools` / `tools` — dump the registered MCP tools + descriptions without booting the server in stdio mode (useful for client-config generation and sanity checks), (2) `
  - Do: Add two CLI verbs in-session (both implementable off-Windows, no Lacerte install required): (1) `lacerte-mcp tools [--json]` — reflect over `typeof(HealthTools).Assembly.GetTypes()`, filter to types with `[McpServerToolType]`, enumerate methods with `[McpServerTool]`, and dump `{name, description, parameters}` triples. The description text already lives in `[Description(...)]` attributes on the tool methods. Useful for client-config generation and CI smoke checks. Add ~40 lines + unit test that ...
- [x] **P14-047** [readme-cli] No Troubleshooting / FAQ section despite hand-off complexity
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line n/a)
  - Why: The setup involves an interactive Intuit consent dialog, a 24-hour ticket expiry, a 32-bit-vs-64-bit MSI trap (called out in DECISIONS.md), a mixed-mode CLR shim (App.config gotcha mentioned in passing on line 99), and ODBC DSN configuration.
  - Do: Add Troubleshooting section to README.md with symptom-fix entries for: register no dialog (interactive shell only), 24h expiry (run lacerte-mcp login), write crash (install 64-bit MSI), shim removed (restore App.config), DSN missing (use 64-bit odbcad32). Also fix StatusCodes.cs:92 and 05-error-mapping.md:167 to use LACERTE_DATA_PATH instead of LACERTE_DATAPATH.
- [x] **P14-048** [security] dataPath connection-string injection: '}' and other ODBC delimiters not handled
  - File(s): src/LacertMCP.Server/Odbc/ConnectionStringBuilder.cs (line 58-65)
  - Why: Build wraps dataPath in braces only if it contains ';'.
  - Do: In src/LacertMCP.Server/Odbc/ConnectionStringBuilder.cs Build(): (1) Reject upfront with ArgumentException if dataPath contains any control char (c < 0x20, including '\0', '\r', '\n', '\t') — these have no place in a filesystem path and are the highest-risk parser-confusion characters. (2) Always brace-wrap dataPath unconditionally and double any embedded '}': var safe = "{" + dataPath.Replace("}", "}}") + "}". Drop the IndexOf(';') branch entirely — the conditional bracing saves nothing and cre...
- [x] **P14-049** [security] dataPath path-traversal / write-outside-expected-root not validated
  - File(s): src/LacertMCP.Server/Tools/SessionTools.cs (line 69-96)
  - Why: LacerteSetContext accepts any string as dataPath and forwards it to SetContext, where the only validation is IsNullOrWhiteSpace.
  - Do: Land a new PathValidation.ValidateDataPath helper in src/LacertMCP.Server/Session/ and call it from LacerteSessionManager.SetContext immediately after the existing IsNullOrWhiteSpace check. Rules: (1) Path.GetFullPath inside try/catch to canonicalize and catch malformed input; (2) reject if Path.IsPathRooted is false; (3) reject any input where the original string contains '..' as a path segment (Regex on separator-bounded '..'); (4) reject UNC (starts with '\\\\' or '//') unless LACERTE_ALLOW_U...
- [x] **P14-050** [security] Late-bound COM dynamic dispatch invokes methods with caller-controlled arguments unchecked
  - File(s): src/LacertMCP.Server/Bridge/Writer.cs (line 80-82, 125-129)
  - Why: Writer.UpdateTaxReturn and Writer.WriteDetail pass operator-supplied field/value/series/description/etc.
  - Do: Two concrete changes, both sim-only:  1) In DetailsTools.ValidateDetailWrite, extend the signature to include the remaining strings and add a single shared `EnsureSafeText(field, value, maxLen)` helper that throws SyntheticStatusCodes.DetailWriteInvalid when: (a) length exceeds a conservative blanket cap (suggest 255 for Description, 50 for Prefix/Suffix/Source/CityAbbr/ImportSrc — matches the DBF column families common in the spec), or (b) the string contains any char in [\\x00-\\x08, \\x0B, \\...
- [x] **P14-051** [simplification] Collapse FieldWriteTools + UpdateReturnTools into shared base/helper
  - File(s): src/LacertMCP.Server/Tools/FieldWriteTools.cs, src/LacertMCP.Server/Tools/UpdateReturnTools.cs (line FieldWriteTools.cs 41-68; UpdateReturnTools.cs 43-71)
  - Why: LacerteFieldWrite and LacerteUpdateReturn are near-identical: same parameter list, same delegation to _session.WriteField(...), same response shape (UpdateReturnTools adds api='UpdateTaxReturn').
  - Do: Extract a private static helper into a new internal class (e.g. `Tools/Internal/FieldWriteCore.cs`) exposing `static Task<string> Run(LacerteSessionManager session, int table, string clientNumber, string field, object? value, int? subClientID, string? stateAbbrev, bool dryRun, string? idempotencyKey, string? apiTag = null)`. The helper owns the WrapToolHandler.Run + WriteField call + StatusCodes lookup + response shape, conditionally adding `api = apiTag` when non-null. Each tool's [McpServerToo...
- [x] **P14-052** [simplification] Inline EnsureLive — it's a misleading one-liner that always-throws in Live
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 880-888 (method); 397, 426, 457, 517, 538 (call sites))
  - Why: EnsureLive() is called in 5 read chokepoints (ListClients/ReadClient/ReadTable/ReadDetail/ReadEFStatus).
  - Do: Rename `LacerteSessionManager.EnsureLive()` to `RejectLiveReadsPendingOdbc()` (option (a) from the original recommendation), NOT inline it. Reasons to prefer rename over inline: (1) it removes the name collision with the legitimately-named `LacerteAccessClient.EnsureLive()` which has opposite semantics — the collision is a worse trap than the inconsistency with write paths; (2) keeping a single named method documents the Phase 4 follow-up in one place, so when ODBC wire-up lands, the deleter onl...
- [x] **P14-053** [spec-compliance] CreateClient marks EmailID/FirstName/LastName optional vs spec required-set
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/CreateClientTools.cs (line 62-69)
  - Why: Spec §4 (Create section): `lacerte_client_create — IClientList.SetDataPath → IClient (TaxType, ClientID ≤8 chars, EmailID, FirstName, LastName) → CreateClient.` REQUIREMENTS.md §Create echoes: `Create a new client (Individual, Corporate, etc.) with the minimum required fields (Ta
  - Do: Apply Option (a) — make the three fields required end-to-end, with a focused diff:  1. `src/LacertMCP.Server/Tools/CreateClientTools.cs`:    - Drop the `= null` defaults on `firstName`, `lastName`, `emailID` and change the type from `string?` to `string`.    - Rewrite the three `[Description]` strings to remove "Optional." and explain the spec requirement (e.g. "First name (individual) OR entity-name override. Required per spec — pass an empty string only if you have explicit live-SDK evidence t...
- [x] **P14-054** [spec-compliance] Tables 6 and 7 have no table-level write block; spec marks them Read only
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/FieldWriteTools.cs (line 42)
  - Why: Spec §2 access column for DATA6 = `Read only` and DATA7 = `Read only` (DATA5 is `Read only (non-global); some globals R/W`).
  - Do: Add a table-number gate in LacerteSessionManager.WriteField (the chokepoint — preferred over duplicating in both FieldWriteTools and UpdateReturnTools) immediately after RejectIfReadOnly and before the idempotency check. Throw `new LacerteSDKError(StatusCodes.ExposedForRead, new Dictionary<string,object?>{{"table", table}, {"reason", "DATA" + table + " is read-only per spec §2 (Display/Forms tables are not writable through the SDK)."}})` for table ∈ {6,7}. Leave DATA5 to per-field gating (spec a...
- [x] **P14-055** [spec-compliance] Detail-file Series column typed string in record vs numeric in SQL filter
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Odbc/SqlTemplates.cs (line 180-184)
  - Why: Spec §2 says detail-file columns are `Series, Prefix, Code, Suffix, StateAbbr, CityAbbr, Source, TSJ, ImportSrc, Amount, Description` with `Series ≈ Lacerte input screen, Code ≈ input field`.
  - Do: Adopt option (a) but stage it to preserve readability: (1) Change DetailRow.Series and DetailRow.Code from string to int in SimulationStore.cs. (2) In SeedData.cs add a static `SeriesIds` lookup (`Wages=11, Int=12, Div=13, Dep=14, Inc=20, State=30`) with a comment citing the Lacerte SDK Series ID table from spec §2, then rewrite seed rows as `new DetailRow(SeriesIds.Wages, "", 1, ...)` so the seed data stays human-readable but emits the numeric IDs live SQL expects. (3) Change DetailsTools.Lacer...
- [x] **P14-056** [test-gaps] EditionDetection live-throws branch + cached-once invariant untested
  - File(s): src/LacertMCP.Tests/Bridge/RegistrationTests.cs (line n/a)
  - Why: EditionDetection.Probe (EditionDetection.cs:38-63) has 2 branches: bridge==null returns the sim sentinel, bridge!=null throws BridgeUnavailable.
  - Do: Add src/LacertMCP.Tests/Bridge/EditionDetectionTests.cs with three tests: (1) `Probe_NullBridge_ReturnsSimSentinels` — asserts the three EditionDetection.Simulated* constants come back unchanged; (2) `Probe_LiveBridge_ThrowsBridgeUnavailable` — passes a stub ILacerteBridge (any throwing or no-op impl), asserts `ex.StatusCode == SyntheticStatusCodes.BridgeUnavailable`, `ex.Context["op"]?.ToString() == "EditionDetection.Probe"`, and `ex.Context.ContainsKey("reason")`; (3) `EditionInfo_Ctor_Rejects...
- [x] **P14-057** [test-gaps] WireLog redaction misses arrays, nested objects, and whitespace-padded JSON
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 62-64)
  - Why: The _redactRegex only matches scalar values: "(?<key>SSN|EIN|BankAccountNumber|RoutingNumber)":("..."|number|true|false|null).
  - Do: Replace the regex in WireLog.Redact with a JsonNode-walking redactor: parse the JSON; recursively descend through JsonObject/JsonArray; when a property name (case-insensitive) is SSN/EIN/BankAccountNumber/RoutingNumber, replace the entire subtree (scalar OR object OR array) with the string "***". On parse failure, fall back to the existing regex (the safe-net for malformed JSON the existing defensive catch can produce). Also: (a) correct the XML doc on _redactRegex which currently claims "arbitr...

### LOW severity

- [x] **P14-058** [architecture] MetadataTools calls Schema layer directly, skipping session manager
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Tools/MetadataTools.cs (line 99, 115)
  - Why: LacerteFieldAccessMap calls FieldAccessMapLoader.GetFieldAccess directly instead of routing through the session manager.
  - Do: Drop the local _moduleFileName dictionary in MetadataTools.cs entirely. Replace lines 52-61 with `var moduleInfo = Modules.GetByChar(module); if (moduleInfo == null) throw new LacerteSDKError(StatusCodes.InValidClientName, new Dictionary<string,object?>{ ["module"] = module.ToString(), ["validModules"] = string.Join(",", Modules.All.Select(m => m.Char)) });` and replace line 123 with `moduleName = moduleInfo.Name`. This (a) removes the dup, (b) fixes the cross-tool inconsistency with SessionTool...
- [x] **P14-059** [architecture] Inverted EnsureLive() name conflates 'no live wiring' with 'is live mode'
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 880-888)
  - Why: The helper is named EnsureLive() but its body throws BridgeUnavailable when Mode == Live.
  - Do: Rename `EnsureLive()` to `ThrowIfLiveUnwired()` and update all five call sites in one sweep. Replace the internal comment with: `// Read paths are sim-only until Phase 9 wires the bridge + ODBC. Sim falls through; live throws BridgeUnavailable.` This phrasing (a) makes the boolean direction obvious from the name alone, (b) survives the Phase 9 cut-over without another rename (the method body simply becomes a no-op or gets deleted once live reads are wired, and the call-site call becomes harmless...
- [x] **P14-060** [architecture] Session manager's _odbc field is `object? = null` placeholder, not typed
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 60)
  - Why: The session manager declares `private readonly object? _odbc = null;` with a 'Phase 4 task #39 still pending wire-up' comment.
  - Do: Replace `private readonly object? _odbc = null;   // Phase 4 task #39 still pending wire-up.` with a typed `private LacerteDsnReader? _odbc;` field. In the Live constructor branch, leave it null (do NOT eagerly construct — the connection string isn't available until SetContext supplies dataPath, and constructing a reader here would couple session ctor to ODBC config). Add a `// Phase 4 task #39 — constructed lazily on first ODBC read with the active DSN connection string; null until then.` comme...
- [x] **P14-061** [correctness] EnsureEditionProbed re-probes on every call when the live probe throws
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 283-295)
  - Why: The docstring promises 'one-shot edition probe ...
  - Do: Two-part fix, both sim-testable: (1) Cache the attempt, not just success — wrap the probe in try/catch inside EnsureEditionProbed and on failure assign _edition = new EditionInfo("Unavailable", "Unavailable", "Unavailable") (or add a private bool _editionProbed sentinel) so subsequent SessionStatus calls return the cached unavailable triple without re-entering Probe(). (2) Log the first failure once via WireLog.RecordSync (or a direct WireLog write) at warning level with op="EditionDetection.Pro...
- [x] **P14-062** [dx-build] App.config carries no configurable settings — env-only configuration
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/App.config (line 1-14)
  - Why: App.config is currently 14 lines of pure runtime-shim (`useLegacyV2RuntimeActivationPolicy` + `supportedRuntime v4.0/v4.6`) — load-bearing per the comment.
  - Do: Drop `LACERTE_DSN` from README.md (line 78), ARCHITECTURE.md (line 175), and .env.example (line 20) — the production ODBC path is intentionally DSN-less (driver + datapath + module + year), confirmed by `docs/research/agents/03-odbc-dbf-access.md` §2 and the fact that no .cs file reads the var. Replace those rows with a short note: "ODBC connections are DSN-less; the `LacerteDSII` DSN is only probed by `lacerte-mcp doctor` as an install-sanity check." Optionally also make DoctorCommand's hardcod...
- [x] **P14-063** [perf-resources] AssertWritable allocates a new map-key string on every access-map lookup
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 84, 86)
  - Why: GetFieldAccess fires on every write chokepoint (WriteField → AssertWritable → GetFieldAccess).
  - Do: Two-step fix, smallest-blast-radius first: (1) Universal-rule check on line 84: replace the concat-and-equals with an allocation-free comparison — `IsClientNumberField(field, table)` that checks `field.Length == (table < 10 ? 4 : 5)`, `field[0] == 'C'`, last two chars are `_0`, and middle chars parse to the table int (or simpler: precompute a `static readonly string[] _clientNumKeys = { "C0_0", "C1_0", ..., "C9_0" }` indexed by table for the common 0-9 range, fall back to concat for >9). (2) Com...
- [x] **P14-064** [security] Idempotency key compared with String.Equals — timing oracle when key is sensitive
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1129-1149)
  - Why: TryReplayIdempotent looks up the key in a ConcurrentDictionary and compares PayloadFingerprint with the standard String.Equals (line 1136), which short-circuits on the first byte mismatch.
  - Do: Fix the unbounded-cache leak; skip the constant-time-compare and the new synthetic code. Concretely: (1) Add `private const int MaxIdempotencyEntries = 1024;` and `private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);` constants to LacerteSessionManager. (2) In `TryReplayIdempotent`, after `TryGetValue` returns true, check `DateTimeOffset.UtcNow - entry.CreatedAt > IdempotencyTtl`; if so, `TryRemove` and return false (treat as miss — caller re-executes, same observable behavi...
- [x] **P14-065** [security] AppId leak via error contexts and CLI commands — partial masking inconsistent
  - File(s): src/LacertMCP.Cli/Commands/RegisterCommand.cs (line 56, 66, 72)
  - Why: SessionStatusSnapshot.AppId is correctly masked to '***' (LacerteSessionManager.cs:271), but the AppId leaks through several other channels: (1) RegisterCommand prints the full GUID to stdout (lines 56, 66, 72) — the rationale is 'save for backup' but the prompt also says it can 
  - Do: Two narrow, sim-friendly changes: (1) In LoginCommand.cs:52, replace `AppID  : {existing.Value:D}` with a masked form matching StatusCommand's Mask() helper (first 8 + ****) — login doesn't need the full GUID and runs more often than register. Extract Mask() from StatusCommand into AppIdStore (or a small Cli/Util class) so DoctorCommand line 195 and LoginCommand can share one canonical masking convention. (2) Add a unit/integration test in LacertMCP.Tests that: registers a known AppID, captures ...
- [x] **P14-066** [simplification] Remove dead ConnectionPool helper (never referenced)
  - File(s): src/LacertMCP.Server/Odbc/ConnectionPool.cs (line 1-44 (whole file))
  - Why: ConnectionPool.GetOrCreate is defined but referenced by no production or test code (grep finds zero callers outside its own definition).
  - Do: Delete src/LacertMCP.Server/Odbc/ConnectionPool.cs. Lift its design-note header (lines 1-14, the "why no second-layer pool" rationale) verbatim into LacerteDsnReader.cs as a `<remarks>` block on the class or as a file-top banner — that's the only load-bearing content. Update HANDOFF.md line 71 to drop "ConnectionPool" from the Odbc/ inventory. When Phase 4 task #39 wires ODBC into LacerteSessionManager, the session manager should `new LacerteDsnReader(connectionString)` directly (or take it via ...
- [x] **P14-067** [simplification] Drop no-op WireLog.ResetForTests + unused _testReset field
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 245-249)
  - Why: WireLog.ResetForTests() does '=> _testReset?.Invoke()' where _testReset is declared 'private static readonly Action? _testReset = null' and never reassigned.
  - Do: Delete lines 241-249 of src/LacertMCP.Server/Logging/WireLog.cs in one chunk: the `ResetForTests()` method, its XML doc, the half-finished `// Use reflection-free reset...` comment, and the `_testReset` field. Leave `EnableForTests`, `DisableForTests`, and `WriteForTest` untouched — those are the working test surface (confirmed by grep: zero callers reference `ResetForTests`, so no test churn). If a future test needs to reset the lazy env-resolved config, the cleanest path is to rewrap `_config`...
- [x] **P14-068** [simplification] ConcurrentDictionary fields and _odbc are over-engineered scaffolding
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 60 (_odbc field), 1215 (Dispose), 75-91 (caches))
  - Why: private readonly object? _odbc = null; is declared, never written, never read, only disposed in Dispose().
  - Do: Delete just two lines in `src/LacertMCP.Server/Session/LacerteSessionManager.cs`: line 60 (`private readonly object? _odbc = null;   // Phase 4 task #39 still pending wire-up.`) and line 1215 (`(_odbc as IDisposable)?.Dispose();`). Leave line 1214 (`(_bridge as IDisposable)?.Dispose();`) and all ConcurrentDictionary fields (`_newApisCache`, `_idempotencyCache`) untouched — those are actively read. The "Phase 4 task #39 pending" reminder is already preserved by the docstring on line 440 of the sa...
- [x] **P14-069** [simplification] Bridge.Registration carries dead RegisterWithStoredAppId + CredentialAccount
  - File(s): src/LacertMCP.Server/Bridge/Registration.cs (line 120-132 (RegisterWithStoredAppId), 38 (CredentialAccount const))
  - Why: RegisterWithStoredAppId is referenced only by tests (RegistrationTests.cs); production code in LacerteSessionManager.Register() inlines the GetStoredAppId() lookup directly (line 182-189).
  - Do: Delete `RegisterWithStoredAppId` (Registration.cs:109-132) and `CredentialAccount` const (line 38). Do NOT pursue option (a) — `RegisterWithStoredAppId` calls the static `Registration.Register` directly, which would bypass both the `ILacerteBridge` abstraction (used for test mocking and the sim/live split) and `AutoReconnect.WithRetry` (the F12 transient-COM retry wrapper). Inlining in `LacerteSessionManager.Register()` is correct as-is. Test cleanup: drop `RegisterWithStoredAppId_NoAppId_Throws...
- [x] **P14-070** [simplification] Move 'TODO Stream A' comments out — Stream A tasks #18/#20/#21/#23 have all landed
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 898-900, 1032-1035)
  - Why: Two 'TODO Stream A' comments in LacerteSessionManager.cs reference work that has been completed.
  - Do: Pick option (a) — delete the two stale TODO comments and replace them with a one-line note that explains the *current* design choice (not a deferred promise). Concretely: (1) At lines 898-900, replace the TODO block with: "// Sim helpers below operate on ModuleStore directly rather than delegating to // SimulationStore.SimulateFieldWrite/SimulateCreateClient/WriteDetail. The store // methods exist (Phase 2 tasks #19-21) but layer in their own TAPIStatus guards // that the session manager has alr...
- [x] **P14-071** [test-gaps] CLI doctor: Pass/Fail exit codes 0 and 1 are never explicitly asserted
  - File(s): src/LacertMCP.Tests/Cli/DoctorCommandTests.cs (line 16-80)
  - Why: DoctorCommandTests only asserts InRange(exit, 0, 2) for the general case and InRange(exit, 1, 2) for the off-Windows case.
  - Do: Minimal-blast-radius refactor: (1) Add `internal static int ComputeExitCode(IList&lt;ProbeResult&gt; results)` that does the hasFail/hasSkip walk + returns 0/1/2 — pure function, no I/O. (2) Keep `Run(TextWriter)` as the orchestrator but have it delegate the exit-code decision to ComputeExitCode. (3) Add three xUnit theories in DoctorCommandTests pinning exit codes against synthetic List&lt;ProbeResult&gt;: all-Pass→0, one-Fail→1, one-Skipped+no-Fail→2, plus Fail-trumps-Skip→1. (4) Optionally ma...

---

## Phase 15 — Round-2 audit implementation plan (2026-05-30)

Generated by a 12-lens deep audit + adversarial verification (98 agents, 78/86 confirmed). Full per-task context in `audit-findings-r2.json` at repo root. Ordering: severity, then lens.

**Pre-pass (BLOCKER — landed before parallel wave to unblock downstream):**
- P15-001 — Over-redaction regression from P14-018/P14-019. Split WireLog redaction into _diskRedactionKeys (broad) vs _wireRedactionKeys (narrow: true secrets only). WrapToolHandler uses narrow; WriteRecord uses broad.

**Parallel cluster waves (one agent per cluster, edits limited to owned file(s)):**


### HIGH severity

- [x] **P15-001** [archive-cleanup / N_readme] Stale test counts in REGRESSION_CHECKLIST.md + README.md after 695-test jump
  - File(s): C:/Users/VR/Projects/LacertMCP/REGRESSION_CHECKLIST.md, C:/Users/VR/Projects/LacertMCP/README.md (line REGRESSION_CHECKLIST.md:13, 20, 25; README.md:5)
  - Why: Round-1 explicitly jumped test totals from 481 to 695 and skip count from 11 to 10 (P14-008 and P14-044 land in HANDOFF.md/Todo.md), but two top-of-funnel docs still publish the old numbers.
  - Do: Edit four lines in REGRESSION_CHECKLIST.md plus one in README.md, and fix the matching wrong-claim in HANDOFF.md to keep the source-of-truth docs in lockstep:  1. REGRESSION_CHECKLIST.md:13 — change `481 passed, 11 skipped, 0 failed` → `695 passed, 10 skipped, 0 failed`.  2. REGRESSION_CHECKLIST.md:14 — change to: `The 10 skips are the 9 `[Fact(Skip = "Requires real Lacerte SDK …")]` cases under `Tests/Bridge/` (Reader, Writer, Registration, CanUseNewApisProbe, LacerteAccessClient) plus the one ...
- [x] **P15-002** [archive-cleanup / N_arch] ARCHITECTURE.md Invariant #7 + Configuration env table both stale after round-1
  - File(s): C:/Users/VR/Projects/LacertMCP/ARCHITECTURE.md (line 78, 171-181)
  - Why: Two doc-consistency holes in ARCHITECTURE.md that round-1 missed: (1) Invariant #7 (line 78) still reads `The access map is consulted before every write` even though P14-002's recommendation explicitly called for tightening it to `consulted before every per-field write (WriteFiel
  - Do: Three-edit composite fix, all sim-only:  (1) ARCHITECTURE.md line 78 — replace the current "The access map is consulted before every write. assertWritable(module, table, field, year) runs in the tool layer (or session manager — pick one and be consistent) before any COM call. Default-refuse on Unknown with force:true escape hatch." with: "**The access map is consulted before every per-field write.** `assertWritable(module, table, field, year)` runs inside `LacerteSessionManager.WriteField` befor...
- [x] **P15-003** [archive-cleanup / N_decisions] Multiple DECISIONS.md entries owed for round-1 architectural additions
  - File(s): C:/Users/VR/Projects/LacertMCP/DECISIONS.md (line n/a (additions))
  - Why: Round-1 added four load-bearing architectural choices to the codebase, but only three (WireLog redaction, AutoReconnect backoff, Pagination contract — P14-037) got dated DECISIONS.md entries.
  - Do: Append four dated 2026-05-30 entries to DECISIONS.md (Chosen/Why/Alternatives/Downstream shape, ~15-25 lines each), and fix the two collateral half-fixes in P14-002:  (1) **"Detail-row + whole-row writes are outside the access-map gate"** — Cite `LacerteSessionManager.WriteDetail` (~line 780+) and `CreateClient` (~line 870+), explain the map's `{module}|{table}|{field}` key shape cannot address detail rows (no primary key, no enumerable field set) or whole-row creates (multi-field atomic). Point...
- [x] **P15-004** [config-validation / B_session] LACERTE_DEFAULT_TAX_YEAR documented but never read by any code
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 127-157)
  - Why: README.md line 92, ARCHITECTURE.md line 175, and .env.example line 9 all advertise LACERTE_DEFAULT_TAX_YEAR=2024 as a 'recommended' env var that supplies a default year when a tool call omits one.
  - Do: Wire LACERTE_DEFAULT_TAX_YEAR end-to-end (option (a) in the original — cleaner than deleting because there are 3+ real fallback sites that currently use `DateTimeOffset.UtcNow.Year` as a degraded default). Concrete steps, all sim-safe:  1. **Parse + store in ctor.** In `LacerteSessionManager.cs:127-157`, after `_appName = TryGet(...)`, add:    ```csharp    var rawYear = TryGet(resolved, "LACERTE_DEFAULT_TAX_YEAR");    if (rawYear != null) {        if (!int.TryParse(rawYear, NumberStyles.Integer,...
- [x] **P15-005** [config-validation / N_env] LACERTE_APP_ID env var documented but ignored; only Credential Manager / file is read
  - File(s): .env.example (line 5-6)
  - Why: .env.example line 6 instructs the operator to set LACERTE_APP_ID=00000000-...
  - Do: Take option (b) — do NOT add env-var support. Adding an env > CredMan > file precedence chain would (i) break Boundary Invariant #8 (Registration happens ONCE at startup from a single source of truth), (ii) re-open an AppID-leak surface that DECISIONS.md (2026-05-30 "AppID GUID in Windows Credential Manager") explicitly closed by moving the GUID out of files/env, and (iii) invent a new contract for a one-operator personal tool. Concrete edit to `.env.example` lines 4-6: replace the two `LACERTE_...
- [x] **P15-006** [error-ux / A_logging] WireLog.Redact nukes the actionable "reason" key in every error envelope
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 109)
  - Why: P14-018 added "reason" and "rawError" to WireLog._sensitiveKeys to scrub COM-echoed taxpayer data, and P14-019 made WrapToolHandler.SerializeError run the same Redact pass on the wire envelope.
  - Do: Split the redaction surface into two callers so disk-log redaction (paranoid) and wire-envelope redaction (LLM-actionable) diverge cleanly:  1) In src/LacertMCP.Server/Logging/WireLog.cs, remove "rawError" and "reason" from _sensitiveKeys (line 109) AND from the _redactRegex alternation (line 137). These were the only two non-PII-shaped keys in the set — every other entry is a definite-PII column name. WireLog.Record path is unaffected for genuine PII keys; it loses key-name redaction of reason/...
- [x] **P15-007** [error-ux / C_bridge] BridgeInputValidation throws AppNotRegistered for unrelated appId-length/charset failures
  - File(s): src/LacertMCP.Server/Bridge/BridgeInputValidation.cs (line 109-138)
  - Why: ValidateAppId raises LacerteSDKError(SyntheticStatusCodes.AppNotRegistered = 9006) when the appId exceeds 128 chars or contains COM-unsafe characters.
  - Do: Add `public const int InvalidAppId = 9103;` in SyntheticStatusCodes.cs (9100-9199 auth-lifecycle range — confirmed free since P14-064 dropped its 9103 idea) with a StatusInfo entry: name "InvalidAppId", Severity.Error, userMessage "The supplied AppID is malformed or oversized (failed bridge-boundary shape check before COM dispatch).", remediation "If the AppID came from registration storage, run `lacerte-mcp unregister` then `lacerte-mcp register` to mint a fresh GUID. If it came from a LACERTE_...
- [x] **P15-008** [integration-flows / B_session] Bulk-update flow forces N tool calls; no batched write API exists
  - File(s): src/LacertMCP.Server/Tools/FieldWriteTools.cs, src/LacertMCP.Server/Session/LacerteSessionManager.cs (line FieldWriteTools.cs:41-54; LacerteSessionManager.cs:640-700)
  - Why: REQUIREMENTS §1.2 calls out bulk-updates (mailing addresses, EF flags, preparer assignments) as a first-class operator scenario, and the WorkflowPrompts.cs `lacerte_bulk_field_update` prompt explicitly walks the LLM through `lacerte_field_write` invoked once per client.
  - Do: Add `LacerteFieldWriteBulk` in a new `src/LacertMCP.Server/Tools/FieldWriteBulkTools.cs` that loops the existing `LacerteSessionManager.WriteField` chokepoint inside a single tool call. Signature: `(int table, string field, IList<BulkWriteTarget> targets, bool dryRun = false, bool continueOnError = false, string? idempotencyKeyPrefix = null, int? maxParallel = 1)` where `BulkWriteTarget = { string clientNumber, object? value, int? subClientID, string? stateAbbrev }`. Loop body MUST go through `W...
- [x] **P15-009** [integration-flows / B_session] Detail read has no Series/Code filter; SqlTemplates push-down dead code
  - File(s): src/LacertMCP.Server/Tools/DetailsTools.cs, src/LacertMCP.Server/Session/LacerteSessionManager.cs, src/LacertMCP.Server/Odbc/SqlTemplates.cs (line DetailsTools.cs:63-78; LacerteSessionManager.cs:574-587; SqlTemplates.cs:249-268)
  - Why: `lacerte_detail_read` returns the FULL per-client detail file — every W-2 row, every interest row, every dependent row interleaved.
  - Do: Wire the existing push-down through three layers. Sim-only, off-Windows actionable, ~80 lines + 2 tests.  (1) **LacerteSessionManager.ReadDetail overload** — add `ReadDetail(string clientNumber, int? series, int? code, int? limit, int? offset)` that:    - Sim path: builds a `Predicate<DetailRow>` (`r => (series == null || int.Parse(r.Series) == series) && (code == null || int.Parse(r.Code) == code)`), forwards to existing `SimulationStore.ReadDetail(dataPath, year, module, clientNumber, predicat...
- [x] **P15-010** [integration-flows / B_session] EF status review has no since-date filter; pulls full roster every time
  - File(s): src/LacertMCP.Server/Tools/EFileTools.cs, src/LacertMCP.Server/Session/LacerteSessionManager.cs, src/LacertMCP.Server/Odbc/SqlTemplates.cs (line EFileTools.cs:21-29; LacerteSessionManager.cs:594-617; SqlTemplates.cs:208-213)
  - Why: REQUIREMENTS §1.4 ("Watching e-file status across the firm") is the canonical poll-for-changes workflow, but `lacerte_ef_status` takes ZERO parameters: no `sinceStatusDate`, no `statusFilter` ("Rejected" / "Pending"), no `clientNumber`, no pagination.
  - Do: Extend the EF-status surface with server-side filters + pagination, reusing the Phase-14 plumbing:  1) **Tool signature** — `EFileTools.LacerteEfStatus(string? sinceStatusDate = null, string? statusFilter = null, string? clientNumber = null, int? limit = null, int? offset = null)`. Parameter contracts: `sinceStatusDate` is an ISO-8601 date (`yyyy-MM-dd`) parsed via `DateTime.TryParseExact(..., CultureInfo.InvariantCulture, ...)` — reject anything else with `LacerteSDKError(SyntheticStatusCodes.I...
- [x] **P15-011** [integration-flows / M_tests] No write-then-read round-trip test or test-only access-map override
  - File(s): src/LacertMCP.Tests/Parity/RoundTripTests.cs, src/LacertMCP.Tests/Parity/WriteParityTests.cs, src/LacertMCP.Tests/Integration/FullFlowTests.cs (line FullFlowTests.cs:51-62; WriteParityTests.cs:96-119)
  - Why: The FullFlowTests Step 5 comment is explicit: "WriteField with a force-bypass (access map is empty in v1)" and asserts a 9012 UnknownFieldAccess instead of asserting the write landed and the read sees it.
  - Do: Add two tests to src/LacertMCP.Tests/Integration/ that close the write-then-read trust gap, reusing the existing AccessMapTestFixture seam (no new infrastructure needed):  (1) FieldWriteRoundTripTests.cs — `[Collection(AccessMapMutationCollection.Name)]`, single fact `FieldWrite_HappyPath_ValueReadsBackVerbatim`:   using var fixture = new AccessMapTestFixture(); // seeds I|1|C1_15=ReadWrite   using var mgr = new LacerteSessionManager(new Dictionary<string,string?>());   await mgr.Register();   m...
- [x] **P15-012** [lifecycle / B_session] Idempotency per-key SemaphoreSlim map leaks unbounded across process lifetime
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 105-106, 1317-1334, 1364-1396)
  - Why: ExecuteWithIdempotencyLock does GetOrAdd into _idempotencyLocks for every distinct idempotency key and NEVER removes the SemaphoreSlim.
  - Do: Make the lock map a strict subset of the cache map. Three small, coordinated edits in `LacerteSessionManager.cs`:\n\n(1) Add a private helper `private void EvictIdempotencyEntry(string key)` that does `if (_idempotencyCache.TryRemove(key, out _) && _idempotencyLocks.TryRemove(key, out var sem)) sem.Dispose();`. \n\n(2) Replace the two TryRemove calls in `StoreIdempotent`'s prune loop (lines 1381 and 1392) with `EvictIdempotencyEntry(kv.Key)` and `EvictIdempotencyEntry(oldestKey)` respectively. A...
- [x] **P15-013** [mcp-protocol / A_logging] No CancellationToken plumbing — tools cannot honor MCP cancel notifications
  - File(s): src/LacertMCP.Server/Tools/*.cs + src/LacertMCP.Server/Session/LacerteSessionManager.cs + src/LacertMCP.Server/Errors/WrapToolHandler.cs (line multiple)
  - Why: A repo-wide grep for `CancellationToken` finds zero matches in the Server project.
  - Do: Three-tier rollout, all sim-verifiable: (1) Add `CancellationToken ct = default` as the trailing parameter on every `[McpServerTool]` method across the 15 Tools/*.cs files (ClientsTools, TablesTools, DetailsTools, FieldWriteTools, UpdateReturnTools, CreateClientTools, EFileTools, FormsTools, StatesTools, StatusTools, SubclientsTools, SessionTools, HealthTools, MetadataTools, Internal/FieldWriteCore) — the SDK injects it automatically on signature match. (2) Change `WrapToolHandler.Run` to overlo...
- [x] **P15-014** [observability / B_session] AutoReconnect retries are silent — `log` parameter never passed
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 223, 434, 719, 838, 940)
  - Why: Every one of the five `AutoReconnect.WithRetry`/`WithRetrySync` call sites in `LacerteSessionManager` passes the operation but omits the optional `ILogger? log` parameter.
  - Do: Two-file fix, no call-site churn, no DI plumbing:  1. **AutoReconnect.cs** — inside both WithRetry (line ~71) and WithRetrySync (line ~111), wrap the `log?.LogWarning` block with an unconditional WireLog.RecordSync emission so retries surface in the same lacerte-YYYYMMDD.log stream as the wrapped op:    ```csharp    var delay = BackoffMs[attempt];    WireLog.RecordSync<bool>("AutoReconnect.retry",        new { attempt = attempt + 1, max = MaxRetries + 1, hresult = ex.HResult.ToString("X8"), exTy...
- [x] **P15-015** [observability / A_logging] WireLog file grows unbounded — no rotation, no size cap, no eviction
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 556-557, 489-494)
  - Why: The log file path is `lacerte-YYYYMMDD.log` (FilenameForToday()), opened in `FileMode.Append` with no size check.
  - Do: Add three things to src/LacertMCP.Server/Logging/WireLog.cs (all sim-testable via existing EnableForTests + WriteForTest seams, no Windows dependency):  (1) Size cap. Before writing in WriteRecord (after GetOrOpenWriterLocked, before WriteLine), check `writer.BaseStream.Position + line.Length > _maxBytes`. If so, close+remove the cached writer, rename the existing path to `lacerte-YYYYMMDD-HHmmss.log` (UTC, no separator collision), and reopen via GetOrOpenWriterLocked. Default `_maxBytes = 100 *...
- [x] **P15-016** [observability / B_session] Idempotency cache hits/misses/conflicts/evictions are never logged
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1336-1396)
  - Why: `TryReplayIdempotent` returns a cached result without writing any log line, `StoreIdempotent` evicts entries silently, and `IdempotencyConflict (9102)` throws a structured error but produces no separate audit entry.
  - Do: Add four WireLog.RecordSync emissions in LacerteSessionManager.cs, all sim-buildable, all PII-safe (idempotency key is operator-supplied; PayloadFingerprint is base64 SHA-256).  1) In `TryReplayIdempotent` on cache hit (after line 1351, before `return true`): ```csharp WireLog.RecordSync<object?>("Idempotency.hit",     new { key, ageMs = (DateTimeOffset.UtcNow - entry.CreatedAt).TotalMilliseconds },     () => null); ``` This is the critical one — today a cache hit produces ZERO log entries becau...
- [x] **P15-017** [r1-quality / A_logging] Over-redaction in WrapToolHandler breaks operator diagnostics and read tools
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Errors/WrapToolHandler.cs (line 61, 94)
  - Why: P14-018/P14-019 wired WrapToolHandler.Run to call WireLog.Redact on BOTH the success envelope (line 61) and the error envelope (line 94).
  - Do: Split the redaction policy into two named keysets in WireLog.cs — this is the minimal surgical fix that preserves the P14-018/P14-019 hardening intent without destroying operator data.  Concrete diff plan (3 files, no Windows/SDK dependency):  1) WireLog.cs — split `_sensitiveKeys` into:    - `_diskRedactionKeys` (the current broad set — used only by `WriteRecord` on the disk path). Keeps Description/City/Zip/Names/rawError/reason because a third-party reading the log file shouldn't see those.  ...
- [x] **P15-018** [security-deep / D_odbc] ODBC row results bypass WireLog redaction (column-name keyed)
  - File(s): src/LacertMCP.Server/Odbc/LacerteDsnReader.cs (line 97-121)
  - Why: LacerteDsnReader.Query wraps `ExecuteReader` in `WireLog.RecordSync` with the SQL as args and the materialized row list as the result.
  - Do: Single-file fix in src/LacertMCP.Server/Logging/WireLog.cs:  1) In RedactNode (around line 314), promote _piiFieldCodes from "value-side only" to "key-side too". After the `_sensitiveKeys.Contains(name)` branch, add:    ```    if (_piiFieldCodes.Contains(name)) {        obj[name] = JsonValue.Create("***");        continue;    }    ```    This catches the ODBC dict-row shape `{"C1_15":"111-22-3333"}` regardless of value type (scalar, array, or nested object — same semantics as the existing _sensi...
- [x] **P15-019** [security-deep / D_odbc] Error context leaks SDK-echoed PII as substrings of message fields
  - File(s): src/LacertMCP.Server/Odbc/OdbcErrorMapper.cs (line 46-50)
  - Why: OdbcErrorMapper.Map stuffs the raw ODBC driver message into `context["odbcMessage"] = message`.
  - Do: Smallest fix that closes the leak and matches the project's existing redaction pattern:  1) Add two keys to WireLog._sensitiveKeys (Logging/WireLog.cs:89-110):    - `"odbcMessage"` — the raw driver text echoed by OdbcErrorMapper.cs:49.    - `"message"` — only used in WireLog's own internal catch blocks (lines 168, 200) where it carries `ex.Message`. The risk of over-redacting is low because production code that wants to log a benign string under "message" is not a pattern in this codebase (verif...
- [x] **P15-020** [spec-deeper / C_bridge] LacerteAccessClient.CreateClient bypasses BridgeInputValidation entirely
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Bridge/LacerteAccessClient.cs (line 206-252)
  - Why: Round-1 P14-050 introduced BridgeInputValidation as the defense-in-depth gate at every dynamic COM dispatch — but only Writer.WriteToDBFDatabase / UpdateTaxReturn / WriteDetail and Reader.ReadFromDBFData call it.
  - Do: Add `BridgeInputValidation.ValidateCreateClientArgs(string appId, string dataPath, string clientID, string taxType, string? firstName, string? lastName, string? emailID)` to Bridge/BridgeInputValidation.cs alongside the existing composite validators. Implementation: `const string op = "CreateClient"; ValidateAppId(appId, op); ValidateDataPath(dataPath, op); ValidateClientNumber(clientID, op); EnsureSafeText(taxType, op, "taxType", ShortStringMaxLength); EnsureSafeText(firstName, op, "firstName",...
- [x] **P15-021** [test-quality / B_session] No concurrency tests for new ExecuteWithIdempotencyLock SemaphoreSlim
  - File(s): src/LacertMCP.Tests/Session/LacerteSessionManagerTests.cs (line 262-310)
  - Why: Phase-14 task P14-005 added ExecuteWithIdempotencyLock at LacerteSessionManager.cs:1317, gating same-key writes through a per-key SemaphoreSlim to close a TOCTOU race between TryReplayIdempotent and StoreIdempotent.
  - Do: Add two xUnit facts to `src/LacertMCP.Tests/Session/LacerteSessionManagerTests.cs` directly after line 310, satisfying the P14-005 task body that was checked off without test coverage:  (1) `WriteDetail_SameKey_SamePayload_Concurrent_AppendsExactlyOnce` — `var mgr = NewSimManagerWithContext(); var row = new DetailRow("Wages", "", "1", "", "", "", "W2", "T", "", 5000.0, "concurrent"); var beforeCount = (await mgr.ReadDetail("1001")).Count; var tasks = Enumerable.Range(0, 50).Select(_ => mgr.Write...

### MEDIUM severity

- [x] **P15-022** [archive-cleanup / Z_misc] Stale TODO/FIXME comments persist after round-1 cleanup wave
  - File(s): multiple (line SyntheticStatusCodes.cs:69,170; SeedData.cs:99; LacerteSessionManager.cs:1285-1290; HealthTools.cs:33-35; SessionManagerBridgeWireupTests.cs:31,49)
  - Why: Round-1 HANDOFF.md line 20 claims `stale Stream A TODO comments cleared`, but several remain.
  - Do: Five concrete edits, all comment/dead-code only, no test changes needed:  (1) **SyntheticStatusCodes.cs** — Delete lines 60-71 (the "Renumbered from agent 05..." comment block + the two `BridgeUnavailableLegacy`/`SimulationOnlyLegacy` const declarations) AND lines 170-181 (the two StatusTable alias entries + "See TODO above" comment). Grep confirms zero external callers. Replace the deleted block with a one-line comment: `// 9201/9202 are the canonical bridge-range codes (DECISIONS.md 2026-05-30...
- [x] **P15-023** [archive-cleanup / N_handoff] HANDOFF.md test-landing breakdown doesn't sum to 695 + double-counts numbers
  - File(s): C:/Users/VR/Projects/LacertMCP/HANDOFF.md (line 92-104)
  - Why: The `Test landing breakdown (current)` block under `### Test landing breakdown (current)` does not arithmetically reconcile to the claimed 695 total.
  - Do: Replace the per-bucket breakdown in HANDOFF.md lines 92-103 with a single line: `- See per-namespace counts via `dotnet test --list-tests` (totals: 695 passing + 10 skipped = 705, 0 failures, 0 warnings).` Rationale: the breakdown drifts on every test landing, has now visibly failed to reconcile (sums to 614, not 695), and conflates skipped tests into the passing total in the Bridge row. If the operator wants the breakdown kept, the minimal fix is: (a) split Bridge into `Bridge: 38 + 62 + ~6 = ~...
- [x] **P15-024** [archive-cleanup / N_readme] README tool table still describes lacerte_health as returning hardcoded sim string after P14-001 fix
  - File(s): C:/Users/VR/Projects/LacertMCP/README.md (line 143)
  - Why: README.md line 143 still describes `lacerte_health` as `Health probe.
  - Do: Edit README.md line 143 to: `| lacerte_health | Health probe. Returns a structured { mode, status } envelope (mode = "simulation"|"live", status = "reachable"); live HRESULT probe via _session.HealthProbe() is the Phase-9 follow-up. | 0 |`. This matches both the live `[Description]` attribute on HealthTools.LacerteHealth and the in-code TODO. Skip the ACCEPTANCE_CRITERIA.md edit — line 92 only counts the tool, it doesn't describe its shape, so it's not actively wrong. Do not touch Todo.md / .tod...
- [x] **P15-025** [archive-cleanup / N_regression] ACCEPTANCE_CRITERIA.md missing rows for new CLI verbs + BridgeInputValidation entry in REGRESSION manual section
  - File(s): C:/Users/VR/Projects/LacertMCP/ACCEPTANCE_CRITERIA.md, C:/Users/VR/Projects/LacertMCP/REGRESSION_CHECKLIST.md (line ACCEPTANCE_CRITERIA.md: end-of-file; REGRESSION_CHECKLIST.md: 79-91, 107-113)
  - Why: P14-046 added two new CLI verbs (`lacerte-mcp tools`, `lacerte-mcp unregister --confirm`) and P14-050 added the `BridgeInputValidation` layer that fires before every dynamic-dispatch bridge call.
  - Do: Two doc edits, both sim-safe / no Windows.   (1) C:/Users/VR/Projects/LacertMCP/ACCEPTANCE_CRITERIA.md — insert a NEW section after #61 and before `### Phase 11 — MCP Prompts` titled `### Phase 9/10 — CLI verbs`. Cover ALL six verbs at once (not just the two new ones), since none are documented today — keeps the section coherent and resolves the larger gap the finding undershoots:   - `#69 — RegisterCommand` (mints + persists GUID via AppIdStore; `--reset` reuses storage primitive; exits 1 + Int...
- [x] **P15-026** [cli-ux / K_cli] status always emits JSON — no human-readable format for interactive use
  - File(s): src/LacertMCP.Cli/Commands/StatusCommand.cs (line 40-69)
  - Why: StatusCommand.Run unconditionally serializes the snapshot as JSON.
  - Do: Refactor StatusCommand to match the `tools`/`doctor` shape — human-default with `--json` opt-in. Concrete diff:  1. **StatusCommand.cs** — change `Run(bool showFull, TextWriter writer)` to `Run(bool showFull, bool asJson, TextWriter writer)`. Add an `--json` Option<bool> with description "Emit machine-parseable JSON instead of the human-readable summary." Wire it through SetHandler. Update the command description string at line 24 to "Print registration state, AppID (masked), mode, and storage b...
- [x] **P15-027** [cli-ux / K_cli] doctor has no --json output — asymmetric with status and tools
  - File(s): src/LacertMCP.Cli/Commands/DoctorCommand.cs (line 58-99)
  - Why: `status` (JSON) and `tools --json` both emit machine-parseable output for CI/scripts.
  - Do: Add a `--json` option to DoctorCommand.Build() and an overload `Run(TextWriter, bool asJson)`. Concrete shape — match StatusCommand+ToolsCommand conventions exactly so all three diagnostic verbs share one JSON contract: (1) Build(): add `var jsonOpt = new Option<bool>("--json", () => false, "Emit machine-parseable JSON instead of the human table."); cmd.AddOption(jsonOpt);` and pull it in the SetHandler the same way ToolsCommand does. (2) New overload `public static int Run(TextWriter writer, bo...
- [x] **P15-028** [cli-ux / K_cli] register/login Phase-9 stubs return exit 0 — silent no-op masquerades as success
  - File(s): src/LacertMCP.Cli/Commands/RegisterCommand.cs (line 91-106)
  - Why: InvokeLiveRegister (and the analogous block in LoginCommand line 61-71) prints `[stub] Lacerte SDK bridge not yet wired into the CLI (Phase 9 task #63)` and returns 0.
  - Do: Three coordinated edits + a small test class.  (1) RegisterCommand.cs:91-106 — In InvokeLiveRegister, change the Win32NT branch to: ``` Console.Error.WriteLine("[partial] AppID minted, but the Intuit consent ceremony is not yet wired into the CLI (Phase 9 task #63)."); Console.Error.WriteLine("         First MCP tool call will return NotSignedIn (9101) until Phase 9 ships and you re-run `lacerte-mcp register`."); Console.Error.WriteLine("         Track progress: Todo.md Phase 9 #63 (Bridge/Regis...
- [x] **P15-029** [cli-ux / K_cli] field-access discover stub message is misleading about why it fails
  - File(s): src/LacertMCP.Cli/Commands/FieldAccessCommand.cs (line 215-227)
  - Why: `discover` always prints `Bulk introspection requires live SDK + Windows.
  - Do: Three-part fix, all in-session, no Windows needed:\n\n(1) Rewrite the `field-access discover` handler message in `src/LacertMCP.Cli/Commands/FieldAccessCommand.cs:215-227` to: `"Bulk field-access introspection is not exposed through this command — use the standalone introspector instead: `LacertMCP.FieldAccessDumper.exe --module <I|C|P|S|F|R|T|N|B> --table <1..7>` (Windows + live Lacerte SDK required at runtime; project lives under src/LacertMCP.FieldAccessDumper/). To set entries by hand off-bo...
- [x] **P15-030** [config-validation / B_session] LACERTE_LIVE=1 AND LACERTE_SIMULATION=1 silently picks Simulation with no warning
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 131-135)
  - Why: The mode resolution `Mode = (!sim && live && onWindows) ? Live : Simulation` makes LACERTE_SIMULATION the trump card.
  - Do: Three coordinated changes, all sim-actionable in-session:  (1) `LacerteSessionManager` ctor (after line 135): if `live && sim` OR `live && !onWindows`, emit ONE warning via the existing logging surface — `WireLog.RecordSync<object?>("ModeResolution", new { resolved = Mode.ToString(), liveRequested = live, simRequested = sim, onWindows }, () => null!)` is consistent with the EditionDetection pattern already established at line 340. Also stash the reason on a new private field `_modeResolutionReas...
- [x] **P15-031** [config-validation / K_cli] Doctor command does not probe operating mode, AppID validity, or live readiness
  - File(s): src/LacertMCP.Cli/Commands/DoctorCommand.cs (line 124-234)
  - Why: RunAllProbes covers build version, Windows, x64 process, .NET 4.8, ODBC DSN, Lacerte SDK install dir, LACERTE_DATA_PATH presence, and AppID persisted.
  - Do: Add six probes to DoctorCommand.RunAllProbes (priority order — first three are the highest-yield):  (1) "Operating mode" probe — read LACERTE_LIVE + LACERTE_SIMULATION + onWindows. Mirror the resolution logic in LacerteSessionManager.cs:131-135 EXACTLY (consider extracting to a shared `SessionMode.Resolve(IEnvironment)` helper to guarantee parity). Emit Pass with detail "resolved: Simulation (off-Windows default)" / "resolved: Live (LACERTE_LIVE=1 on Windows)" / etc. Emit Warn when BOTH flags ar...
- [x] **P15-032** [config-validation / O_dx] App.config missing/corrupt produces no detectable error path or doctor probe
  - File(s): src/LacertMCP.Server/App.config (line n/a)
  - Why: The App.config is load-bearing for the live SDK COM activation (`useLegacyV2RuntimeActivationPolicy=true` + supportedRuntime v4.0) — without it `new LacerteSDK_Access.LacerteAccess()` throws on construction per the file's own header comment.
  - Do: Add `ProbeAppConfigShim()` to `DoctorCommand.cs` (slot it after probe #2 "x64 process" since it's a build-output check, not a Windows-only check — the file ships with the .exe regardless of OS). Logic: (a) resolve `var configPath = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile` (cross-platform, returns the .exe.config path even on Linux/Mono); fallback to `Process.GetCurrentProcess().MainModule.FileName + ".config"` if null/empty; (b) if file missing → Fail "App.config not next to L...
- [x] **P15-033** [config-validation / L_program] Program.cs Main has zero env validation; bad env values fail at first tool call
  - File(s): src/LacertMCP.Server/Program.cs (line 18-44)
  - Why: Main() builds a Host, adds the LacerteSessionManager singleton, and calls RunAsync.
  - Do: Add `src/LacertMCP.Server/Startup/EnvValidator.cs` invoked from Program.cs:Main BEFORE `builder.Build().RunAsync()`. Single public method `Validate(TextWriter stderr)` returns `(bool exitNow, int exitCode)`. Checks (all writing one-line warnings to stderr, none throwing):  (1) Boolean-shaped vars (LACERTE_LIVE, LACERTE_SIMULATION, LACERTE_DEBUG_WIRE, LACERTE_ALLOW_UNC, LACERTE_MCP_FORCE_FILE_STORE) — if value is non-null AND not exactly "1" AND not exactly "0" AND not exactly empty, warn `[start...
- [x] **P15-034** [config-validation / F_schema] FieldAccessMapLoader fails-open silently on missing/empty shards — every write becomes Unknown
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 235-258, 282-326)
  - Why: LoadModule (line 247-250) returns an empty Dictionary when the per-module JSON shard is missing from the build output.
  - Do: Three narrow additions, all sim-only:  (1) Add `FieldAccessMapLoader.GetShardCounts()` public static helper returning `(int modules, int overlays)` — scans `{assemblyDir}/Schema/FieldAccessMap/base/*.json` and `overlays/ty*-not-supported.json` and returns counts WITHOUT loading them. Pure file enumeration; no JSON parse cost; safe to call from hot path / startup.  (2) Add a Doctor probe `Field-access map shards` in DoctorCommand.RunAllProbes (insert after Build version): probe Pass if modules > ...
- [x] **P15-035** [error-ux / A_logging] WrapToolHandler.Run wraps unknown exceptions as StatusCodes.Error (0) which carries a useless userMessage
  - File(s): src/LacertMCP.Server/Errors/WrapToolHandler.cs (line 67-79)
  - Why: The fallback path turns any non-LacerteSDKError into LacerteSDKError(StatusCodes.Error).
  - Do: Three-file fix, all sim-testable, ~25 LOC + 4 tests: (1) src/LacertMCP.Server/Errors/SyntheticStatusCodes.cs — add `public const int InternalServerError = 9203;` in the 9200-9299 block (slot is open after BridgeUnavailable=9201, SimulationOnly=9202) and a StatusTable entry: name "InternalServerError", Severity.Error, userMessage "The MCP server itself raised an unexpected exception — this is not a Lacerte SDK error.", remediation "Capture the exceptionType from context, re-run with LACERTE_DEBUG...
- [x] **P15-036** [error-ux / E_errors] DBxError remediations (16-23) say "check the table file on disk" but never name the file
  - File(s): src/LacertMCP.Server/Errors/StatusCodes.cs (line 175-221)
  - Why: All eight DBxError entries (DB1Error..DB8Error) share the remediation "Check the table file on disk; run Lacerte's index-repair on the affected module/year." — but the only context the LLM has at this point is the integer code (e.g.
  - Do: Three-part fix, all sim-only: (1) Stop conflating "row not found" with "DBF disk error" in the sim path — in SimulationStore.cs:335,338, replace `ErrorForTable(table)` with `throw new LacerteSDKError(StatusCodes.OutOfIndex, new Dictionary<string,object?>{["op"]="WriteField",["table"]=table,["clientNumber"]=clientNumber,["subClientID"]=subId,["stateAbbrev"]=state,["reason"]="Row not found for the given identifiers"})`. OutOfIndex (TAPIStatus 4) is the spec-correct code for this and already has re...
- [x] **P15-037** [error-ux / E_errors] Synthetic vs SDK code distinction is buried in source comments, never documented for LLM consumers
  - File(s): src/LacertMCP.Server/Errors/SyntheticStatusCodes.cs, README.md (line n/a)
  - Why: The wire envelope returns `statusCode` as a bare integer with no marker for whether it came from the SDK (0-204 range) or was minted by MCP (9001-9202 range).
  - Do: Three-part fix, all sim-only, ~30 minutes:\n\n(1) src/LacertMCP.Server/Errors/WrapToolHandler.cs:82-95 — add one field to the SerializeError envelope after `statusName`: `[\"origin\"] = err.StatusCode >= 9000 ? \"synthetic\" : \"sdk\",`. Derivation lives at the serialization boundary (not on LacerteSDKError itself) so the existing ~25 throw-sites and constructors stay untouched. Test in src/LacertMCP.Tests/Errors/ (new file or extend WrapToolHandlerTests if it exists): (a) throw LacerteSDKError(...
- [x] **P15-038** [integration-flows / G_prompts] Tax-organizer / CSV-intake flow has no helper; LLM rebuilds mapping per row
  - File(s): src/LacertMCP.Server/Tools/CreateClientTools.cs, src/LacertMCP.Server/Prompts/WorkflowPrompts.cs (line CreateClientTools.cs:48-225; WorkflowPrompts.cs:36-93)
  - Why: REQUIREMENTS §1.3 names "Bootstrapping new clients from a CSV or Tax Organizer responses" as a primary operator flow.
  - Do: Three landed, scoped changes — all sim-doable, no Lacerte/Windows needed; defer the live-introspection-dependent piece behind F5.  (1) **Add `seedFields` to `lacerte_client_create`** — extend CreateClientTools.LacerteClientCreate with `Dictionary<string,string>? seedFields = null`. On success, after the CreateClient call returns the new ClientNumber, the tool loops the dictionary and calls WriteField for each (table=1, key=clientNumber, field=k, value=v). Wrap CreateClient + the seed-writes insi...
- [x] **P15-039** [integration-flows / M_tests] FullFlowTests covers only happy-path single-client; bulk/CSV/EF-poll flows untested
  - File(s): src/LacertMCP.Tests/Integration/FullFlowTests.cs (line FullFlowTests.cs:1-93)
  - Why: The integration directory contains ONE test class (FullFlowTests.cs) with ONE method that exercises Register→SetContext→ListClients→ReadClient→Write(failing)→CreateClient→re-list.
  - Do: Add four sibling files under src/LacertMCP.Tests/Integration/ that each pin down a cross-tool boundary Phase-14 touched but left without integration proof: (1) FullFlow_BulkUpdate.cs — ListClients -> for each of 5+ seeded clients call WriteField on a writable field (use access-map overlay fixture so a known-writable field exists, OR force=true with a known-non-PK field) with distinct idempotencyKeys, then ReadClient and assert the new value round-tripped; THEN replay one of the keys and assert n...
- [x] **P15-040** [integration-flows / J_details] EF status, detail rows, and forms tools lack pagination — paginated contract is uneven
  - File(s): src/LacertMCP.Server/Tools/EFileTools.cs, src/LacertMCP.Server/Tools/DetailsTools.cs, src/LacertMCP.Server/Tools/FormsTools.cs, src/LacertMCP.Server/Tools/SubclientsTools.cs, src/LacertMCP.Server/Tools/StatesTools.cs, src/LacertMCP.Server/Tools/StatusTools.cs (line EFileTools.cs:21-29; DetailsTools.cs:63-78; FormsTools.cs:20-33)
  - Why: Round-1 P14-014 added limit/offset pagination to `lacerte_client_list` and `lacerte_table_read`.
  - Do: Retrofit six tools with Pagination.Page<T>, mirroring the F13 pattern from ClientsTools/TablesTools. Concrete plan (all sim-actionable, no Windows required):  1) Add `int? limit = null, int? offset = null` parameters to each method:    - EFileTools.LacerteEfStatus (highest priority — no clientNumber filter, returns full eFilings across context)    - DetailsTools.LacerteDetailRead (high — 2000+ row partnership returns realistic)    - SubclientsTools.LacerteSubclientRead (medium — 100+ partner par...
- [x] **P15-041** [lifecycle / B_session] Idempotency cache eviction is O(n log n) per call at cap; no concurrent eviction safety
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1374-1395)
  - Why: When _idempotencyCache.Count > 10,000, StoreIdempotent runs (a) a full foreach over all entries checking CreatedAt against cutoff, and (b) if still over cap, _idempotencyCache.OrderBy(kv => kv.Value.CreatedAt).FirstOrDefault() — that's an O(n log n) sort over 10k entries on the h
  - Do: Add a single-flight eviction gate + batch eviction. In LacerteSessionManager.cs:  1. Add fields near line 114: ```csharp private const int IdempotencyEvictionBatchSize = 1024;  // ~10% of cap private int _evictingIdempotency;  // 0 = idle, 1 = eviction in progress ```  2. Refactor StoreIdempotent (line 1364) so the cap check is wrapped in an Interlocked guard — only one writer evicts at a time; other concurrent writers proceed without sorting (they'll evict next pass if still over cap): ```cshar...
- [x] **P15-042** [lifecycle / B_session] EditionDetection cached as 'Unavailable' forever on first transient probe failure
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 311-349 (EnsureEditionProbed))
  - Why: P14-061 made the edition probe cache-once-on-failure to honor the docstring contract 'never re-probed for the lifetime of the process'.
  - Do: Two minimal sim-testable fixes; defer the TTL/transient classification to Phase 9 when real COMExceptions can actually be thrown.  (1) Clear the edition cache in Register() — add `lock (_editionLock) { _edition = null; }` at the top of Register() (before the WireLog wrapper). This means any successful re-registration (24h auth refresh, mid-session Lacerte restart, operator-triggered re-register after a service hiccup) naturally re-probes the edition. Document the invariant in the _edition field ...
- [x] **P15-043** [lifecycle / B_session] No proactive 24h-auth-expiry probe; operator gets raw NotSignedIn instead of 9100 SessionExpired
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 262-301 (SessionStatus); every wire chokepoint at 640, 784, 874)
  - Why: The session manager knows ExpiresAt (set on Register at line 230/246) and SessionStatus correctly computes MinutesUntilExpiry.
  - Do: Add a synchronous expiry probe in LacerteSessionManager.cs and wire it into every chokepoint that runs after Register. Concrete shape:  ```csharp // Place next to RequireContext at line ~988. private void ThrowIfAuthExpired() {     // Only meaningful once Register has stamped ExpiresAt. Pre-register     // failures surface as AppNotRegistered/ContextNotSet from their own     // guards; we don't double-fire here.     if (ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value)     {       ...
- [x] **P15-044** [lifecycle / F_schema] FieldAccessMap shards loaded once, never invalidated; FieldAccessDumper output not picked up until process restart
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 50-54, 235-326)
  - Why: _moduleCache and _overlayCache are ConcurrentDictionary<...> populated lazily via GetOrAdd; entries are never invalidated except by the test-only ResetCacheForTests().
  - Do: Implement option (a) — explicit reload, skip the FileSystemWatcher path (a watcher would reload mid-tool, fight the dumper's multi-file rewrite, and create non-deterministic test behavior; the docstring at lines 32-38 already commits to single-load semantics as a feature). Concretely:\n\n1. **FieldAccessMapLoader.cs** — Promote `ResetCacheForTests` to a documented public API. Rename to `ReloadCaches()`, keep the body (`_moduleCache.Clear(); _overlayCache.Clear();`), and have `ResetCacheForTests`...
- [x] **P15-045** [lifecycle / D_odbc] OdbcConnection cached forever; no detection or recovery from server-side disconnect
  - File(s): src/LacertMCP.Server/Odbc/LacerteDsnReader.cs (line 50, 124-135)
  - Why: EnsureOpen opens an OdbcConnection on first Query and reuses it for the life of the reader.
  - Do: Two targeted changes in src/LacertMCP.Server/Odbc/LacerteDsnReader.cs (sim-only, no Windows needed): (1) In the catch (OdbcException ex) block at lines 112-118, before rethrowing, call a new private TryRecycleConnectionOnFatal(ex) that inspects ex.Errors[0]?.SQLState (defensive null guard) and disposes+nulls _connection when the state starts with "08" OR equals "IM002" OR starts with "HY" — i.e. exactly the set OdbcErrorMapper.MapSqlState already classifies as DBFNotAccessable/CodeBaseError on l...
- [x] **P15-046** [mcp-protocol / L_program] Program.cs has no graceful shutdown — ODBC/COM/idempotency leak on SIGTERM
  - File(s): src/LacertMCP.Server/Program.cs (line 18-44)
  - Why: Program.Main does `await builder.Build().RunAsync()` with no hosted-service registration, no IHostApplicationLifetime stop hook, and no Console.CancelKeyPress / AppDomain.ProcessExit handler.
  - Do: Two minimal edits to `src/LacertMCP.Server/Program.cs`, in this order of importance:\n\n(1) **Primary fix — dispose the Host.** Replace `await builder.Build().RunAsync().ConfigureAwait(false);` with:\n```csharp\nusing var host = builder.Build();\nawait host.RunAsync().ConfigureAwait(false);\n```\nThis alone fixes the graceful-shutdown path: the default ConsoleLifetime registered by `Host.CreateApplicationBuilder` already handles Console.CancelKeyPress and AppDomain.ProcessExit by calling StopAsy...
- [x] **P15-047** [mcp-protocol / J_details] Six read tools return unbounded result sets — MCP message-size footgun
  - File(s): src/LacertMCP.Server/Tools/DetailsTools.cs + EFileTools.cs + SubclientsTools.cs + StatesTools.cs + FormsTools.cs + StatusTools.cs + MetadataTools.cs (line DetailsTools.cs:62-78 / EFileTools.cs:21-29 / SubclientsTools.cs:21-34 / StatesTools.cs:20-52 / FormsTools.cs:20-33 / StatusTools.cs:21-34 / MetadataTools.cs:121-165)
  - Why: Round-1 P14-014 paginated ClientsTools and TablesTools, but LacerteDetailRead, LacerteEfStatus, LacerteStateRead, LacerteStateSubclientRead, LacerteFormsRead, LacerteSubclientRead, LacerteStatusRead, and LacerteReturnCodes still return the full unfiltered result set with no limit
  - Do: Apply Pagination.Page<T> to six tools, ranked by realistic wire-size risk; SKIP MetadataTools (both methods bounded by static or fixed-probe enumeration). Per-tool work mirrors the TablesTools.cs:30-52 pattern exactly — add `int limit = 100, int offset = 0` parameters with TablesTools' [Description] phrasing, wrap the session result via `Pagination.Pagination.Page(rows, limit, offset)`, and keep the legacy top-level collection key (`subclients`/`states`/`forms`/`statuses`/`rows`/`efStatuses`) + ...
- [x] **P15-048** [mcp-protocol / A_logging] WrapToolHandler error envelope is undocumented — README/ARCHITECTURE never specify the shape
  - File(s): src/LacertMCP.Server/Errors/WrapToolHandler.cs + README.md (line WrapToolHandler.cs:82-95 (definition); README.md (missing section))
  - Why: Every tool returns a serialized envelope: `{success:true, result:{...}}` on success, `{success:false, statusCode, statusName, severity, userMessage, remediation, context}` on error.
  - Do: Add a single `## Tool Response Envelope` section to README.md between the Tools tables (~line 154) and `## Prompts`. Three blocks: (1) success example `{"success": true, "result": {...tool-specific...}}` with one real example from `lacerte_client_list` (paginated rows); (2) error example `{"success": false, "statusCode": 9101, "statusName": "NotSignedIn", "severity": "error", "userMessage": "...", "remediation": "Run lacerte-mcp login...", "context": {}}`; (3) a one-liner field table (severity i...
- [x] **P15-049** [mcp-protocol / G_prompts] Prompts never exercised end-to-end through an MCP host — only static methods are tested
  - File(s): src/LacertMCP.Tests/Prompts/WorkflowPromptsTests.cs (line 1-78 (whole file))
  - Why: WorkflowPromptsTests calls the static methods directly (`WorkflowPrompts.ClientIntake()`) and asserts on the returned GetPromptResult.
  - Do: Add ONE new test class at C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/Integration/McpDiscoveryRoundtripTests.cs that stands up an in-process MCP server and drives it through a real JSON-RPC pair. Implementation sketch (xUnit, async, net48-safe):  1) Build server via Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder, register AddMcpServer().WithPromptsFromAssembly(typeof(LacertMCP.Server.Prompts.WorkflowPrompts).Assembly).WithToolsFromAssembly(typeof(LacertMCP.Server.Tools.Session...
- [x] **P15-050** [mcp-protocol / B_session] Sim-mode tools never gate on IsRegistered — observational divergence from live
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 640-868 (WriteField, WriteDetail, CreateClient); 451-617 (Read*))
  - Why: RequireContext() throws ContextNotSet (9006) when Context is null, but no chokepoint checks `IsRegistered`.
  - Do: Add a single private helper `RequireRegistered()` next to `RequireContext()` (around line 980) that throws `LacerteSDKError(StatusCodes.NotSignedIn, { ["reason"] = "Register() must succeed (AccessGranted) before this operation." })` when `!IsRegistered`. Call it as the first synchronous gate after `RequireContext()` in: WriteField (line 645), WriteDetail (788), ReadClient (488), ListClients (459), ReadTableCore (519), ReadDetail (579), ReadEFStatus (600). CreateClient already passes through Simu...
- [x] **P15-051** [mcp-protocol / J_writetools] FieldWriteTools.value parameter accepts object? with no client-side type whitelist
  - File(s): src/LacertMCP.Server/Tools/FieldWriteTools.cs + Tools/UpdateReturnTools.cs (line FieldWriteTools.cs:45 (param); UpdateReturnTools.cs:56 (param))
  - Why: Both write tools declare `object? value` with [Description] promising 'JSON-serializable scalar — string, number, or boolean.' Nothing enforces this.
  - Do: Land the missing step-2 from P14-050 as a new task (e.g. P15-001 "Finish P14-050: add type-whitelist normalizer at session WriteField chokepoint"). Concrete implementation:  1) Add `SyntheticStatusCodes.FieldWriteInvalid` (next free 90xx slot; reuse if already defined, else 9009 — verify against existing enum first) for non-scalar rejections.  2) In `LacerteSessionManager`, add a private static helper: ```csharp private static object? NormalizeFieldWriteValue(object? value, string field) {     i...
- [x] **P15-052** [observability / A_logging] No request/correlation ID threads through WireLog records
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 398-410)
  - Why: Every WireLog record carries `{ ts, op, args, durationMs, result|error }` but no correlation field.
  - Do: Add request-scoped correlation IDs in two coordinated changes: (1) WireLog.cs — add `public static AsyncLocal<string?> CurrentCorrelationId = new AsyncLocal<string?>();` (or a small `WireLogScope` class). In `WriteRecord`, after the `["durationMs"]` line, do `var cid = CurrentCorrelationId.Value; if (!string.IsNullOrEmpty(cid)) record["cid"] = cid;` so the field appears only when set (keeps disabled-path cost zero and existing JSON shape unchanged when no scope is active). (2) WrapToolHandler.Ru...
- [x] **P15-053** [observability / L_program] Stderr logging is set to Trace — production wire shows verbose framework chatter
  - File(s): src/LacertMCP.Server/Program.cs (line 24-27)
  - Why: `Program.cs` clears all logging providers, then adds a console provider with `options.LogToStandardErrorThreshold = LogLevel.Trace`.
  - Do: In src/LacertMCP.Server/Program.cs, replace the AddConsole block (lines 23-27) with: (a) read LACERTE_LOG_LEVEL env var once, parse to LogLevel via Enum.TryParse (case-insensitive); default to LogLevel.Information when unset / unparseable; (b) builder.Logging.SetMinimumLevel(parsed); (c) keep AddConsole with options.LogToStandardErrorThreshold = LogLevel.Trace — that is the CORRECT routing for an MCP stdio server (every surviving log line goes to stderr, never stdout) and the finding's suggestio...
- [x] **P15-054** [observability / J_health] No liveness/readiness check — an external watcher can't tell if MCP is alive
  - File(s): src/LacertMCP.Server/Tools/HealthTools.cs (line 30-41)
  - Why: `LacerteHealth` is the only health surface and it's an MCP-tool — invokable ONLY through the MCP JSON-RPC stdio transport.
  - Do: Add an unconditional (not WireLog-gated) heartbeat-file writer in `LacerteSessionManager`. Concrete plan, all off-Windows actionable: (1) Add `src/LacertMCP.Server/Diagnostics/HeartbeatWriter.cs` — a small class started by `LacerteSessionManager` ctor that writes `{HeartbeatDir}/.heartbeat` (single canonical path, default `%LOCALAPPDATA%/LacertMCP/.heartbeat` on Windows, `~/.local/share/LacertMCP/.heartbeat` elsewhere; override with `LACERTE_HEARTBEAT_DIR`; disable with `LACERTE_HEARTBEAT=0`). F...
- [x] **P15-055** [r1-quality / B_session] Idempotency lock map (_idempotencyLocks) is genuinely unbounded; only the cache evicts
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 105-115, 1317-1334, 1374-1395, 1465)
  - Why: P14-005 added per-key SemaphoreSlim serialization via ExecuteWithIdempotencyLock, and P14-013/P14-064 added a 10k-entry + 24h-TTL eviction policy to _idempotencyCache.
  - Do: In StoreIdempotent (LacerteSessionManager.cs:1364-1396), after each TryRemove call on _idempotencyCache, also try to remove and dispose the matching _idempotencyLocks entry — but guard against the race where a concurrent caller is currently holding the lock. Concrete pattern:  ```csharp private void TryEvictLock(string key) {     if (_idempotencyLocks.TryGetValue(key, out var sem) && sem.CurrentCount == 1)     {         if (_idempotencyLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key,...
- [x] **P15-056** [r1-quality / B_session] EFStatus sim/live divergence: SQL uses LEFT OUTER JOIN, sim uses INNER JOIN
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 604-615 (also Odbc/SqlTemplates.cs:208-213))
  - Why: P14-053 rewrote SqlTemplates.EfStatusJoin to use `left outer join [EFilings] ...
  - Do: Three-part fix, all in sim land:\n\n1) `src/LacertMCP.Server/Session/LacerteSessionManager.cs` ReadEFStatus (604-615): rewrite to start from `module.Clients.Values` (DATA1) and outer-join EFilings then EFStatuses, so the row set matches the SQL's `from ci left outer join ef left outer join s` shape exactly. Concrete LINQ:\n```\nvar joined = from c in module.Clients.Values\n             from f in module.EFilings.Where(x => x.ClientId == (string)c.Fields[\"C1_0\"]).DefaultIfEmpty()\n             f...
- [x] **P15-057** [r1-quality / A_logging] WireLog redaction will not match SDK-echoed sensitive keys that arrive in alternate casings or with embedded patterns
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Logging/WireLog.cs (line 89-110, 136-138)
  - Why: P14-024 expanded redaction key list.
  - Do: In WireLog.cs RedactNode (line 303-345), add a third condition inside the JsonObject branch that checks if the property NAME is itself a PII field code in _piiFieldCodes — independent of the parent shape. Concretely, before the existing `_sensitiveKeys.Contains(name)` check, add: `if (_piiFieldCodes.Contains(name)) { obj[name] = JsonValue.Create("***"); continue; }`. This covers the row-flat shape that ListClients/ReadClient/ReadTable returns via CloneRow (LacerteSessionManager.cs:1292). Also ex...
- [x] **P15-058** [security-deep / A_logging] WireLog _piiFieldCodes only covers DATA1-Individual; entity-module PII leaks
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 117-130)
  - Why: The PII field-code set hard-codes 10 codes from DATA1-Individual only (C1_14, C1_15, C1_19, C1_21, C1_27, C1_28, C1_29, C1_32, C1_71, C1_72).
  - Do: Two-part fix in src/LacertMCP.Server/Logging/WireLog.cs + Tests/Logging/WireLogTests.cs (no Windows/SDK dependency):  1) Switch ScrubFieldValuePairs from allowlist to denylist semantics for the WriteField {field, value} shape. Rationale: the curated _piiFieldCodes set covers only DATA1-Individual and there is no spec source for entity-module PII codes yet (Schema/FieldAccessMap/base/*.json are all empty stubs, and Lacerte_MCP_Structure_Spec.md line 172 explicitly defers "complete per-module fiel...
- [x] **P15-059** [spec-deeper / F_schema] TaxType alias list includes synthetic aliases the live SDK does not accept
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Schema/Modules.cs (line 45-53)
  - Why: Spec §2 explicitly lists the 9 valid ReturnType strings: `Individual, Corporate, Partnership, SCorporate, Fiduciary, Exempt, Estate, Gift, Benefit` — that's the ONLY accepted alphabet the ODBC connection string and the SDK's IClient(TaxType, ...) constructor recognize.
  - Do: Pick option (b) — preserve operator ergonomics, fix divergence. Concrete steps: (1) Add `Modules.CanonicalizeTaxType(char module, string taxType) -> string?` returning `info.ReturnType` when the input matches any alias case-insensitively (or null when no match). (2) In CreateClientTools.cs after the AcceptsTaxType gate at line 163, replace the verbatim `taxType` used downstream with `var canonical = Modules.CanonicalizeTaxType(moduleChar, taxType)!;` and pass `canonical` into `new CreateClientAr...
- [x] **P15-060** [spec-deeper / B_session] Idempotency cache uses absolute expiry — silent surprise re-execution at 24h boundary
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1336-1396)
  - Why: P14-013/P14-064 added a 24h TTL on idempotency entries 'matching the 24h Intuit auth window'.
  - Do: Drop the behavioral-change ask (sliding-window OR 9103 IdempotencyEvicted) — P14-064's verdict already chose absolute TTL with silent miss-on-expiry and rejected the new synthetic code as surface-bloat. Limit the work to two doc+test patches: (1) Update the three tool [Description] strings to surface the TTL bound — change CreateClientTools.cs:57, FieldWriteTools.cs:49, DetailsTools.cs:97 from "Same key + same payload returns the cached result" to "Same key + same payload returns the cached resu...
- [x] **P15-061** [spec-deeper / C_bridge] BridgeInputValidation.ValidateValue does not reject NaN/Infinity on numeric value writes
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Bridge/BridgeInputValidation.cs (line 287-293)
  - Why: P14-050 added `ValidateWriteDetailArgs` which explicitly rejects `double.NaN` / `double.IsInfinity` on `row.Amount` (lines 464-479) — but the generic field-write paths `ValidateWriteToDBFDatabaseArgs` and `ValidateUpdateTaxReturnArgs` call `ValidateValue(value, op)` which only br
  - Do: Extend BridgeInputValidation.ValidateValue (BridgeInputValidation.cs:287-293) with three additional branches before the existing string branch returns:  1. `if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))` → throw LacerteSDKError with SyntheticStatusCodes.DetailWriteInvalid, payload `{op, field:"value", value:d, reason:"value must be a finite real number (no NaN / Infinity)."}` — mirrors lines 467-477 verbatim so the error shape stays consistent across row.Amount and generic ...
- [x] **P15-062** [test-quality / B_session] No tests for idempotency cache TTL + size-cap eviction
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1336-1395)
  - Why: Phase-14 tasks P14-013 / P14-064 added cache size cap (IdempotencyCacheMaxEntries) and TTL eviction (IdempotencyTtl) at LacerteSessionManager.cs:1336+.
  - Do: Add three xUnit tests to src/LacertMCP.Tests/Session/LacerteSessionManagerTests.cs in the existing "Idempotency cache state machine" region. Use reflection (InternalsVisibleTo is already configured) on the private `_idempotencyCache` ConcurrentDictionary<string, IdempotencyCacheEntry> field. (1) `WriteDetail_IdempotencyKey_ExpiredEntry_ReExecutes`: do a first WriteDetail with idempotencyKey "exp1"; reflect into _idempotencyCache, retrieve the entry, and replace it with `new IdempotencyCacheEntry...
- [x] **P15-063** [test-quality / Z_misc] EnsureWritableFieldSeeded reflection helper triplicated across three test classes
  - File(s): multiple (line n/a)
  - Why: WireLogSessionIntegrationTests.cs (lines 36-62), SessionManagerBridgeWireupTests.cs (lines 38-83), and Resilience/SessionManagerAutoReconnectTests.cs (lines 26-52) each carry a near-identical 30-line copy of the static EnsureWritableFieldSeeded helper that reflects on FieldAccess
  - Do: Add `src/LacertMCP.Tests/AccessMapInMemorySeed.cs` (root `LacertMCP.Tests` namespace — keeps it visible to the three callers in Tests/Logging, Tests/Bridge, Tests/Resilience without an extra using) exposing one static method `EnsureWritableFieldSeeded(char module = 'I', int table = 1, string field = "C1_15", string source = "test-seed")` that performs the additive-only in-memory mutation under a private static lock — KEEP additive semantics (do not reset like AccessMapTestFixture; that class is ...
- [x] **P15-064** [test-quality / J_writetools] Dry-run 'does not persist' tests assert null == null and pass for wrong reason
  - File(s): src/LacertMCP.Tests/Tools/FieldWriteToolsTests.cs (line 96-122)
  - Why: FieldWriteToolsTests.FieldWrite_DryRun_DoesNotPersist (lines 96-122) and UpdateReturnToolsTests.UpdateReturn_DryRun_DoesNotPersist (lines 124-147) both read field C1_15 from BROW client 1001 before and after a dryRun write, then assert Assert.Equal(origValue, afterValue).
  - Do: Apply option (b) from the original recommendation — it's the property-under-test ("dry-run leaves observable state unchanged") and avoids coupling to the brittle seed-shape assumption. Concretely, in BOTH FieldWriteToolsTests.FieldWrite_DryRun_DoesNotPersist (FieldWriteToolsTests.cs:96-122) and UpdateReturnToolsTests.UpdateReturn_DryRun_DoesNotPersist (UpdateReturnToolsTests.cs:124-147): (1) Before the dry-run call, perform a real write to establish a known sentinel: `var seedJson = await tool.L...

### LOW severity

- [x] **P15-065** [cli-ux / K_cli] doctor's Unicode glyphs mojibake on default Windows console codepage
  - File(s): src/LacertMCP.Cli/Commands/DoctorCommand.cs (line 48-56)
  - Why: `ProbeResult.Glyph` returns the Unicode chars U+2713 (✓), U+26A0 (⚠), and U+2717 (✗).
  - Do: Two-line fix, both in-session: (1) In src/LacertMCP.Cli/Program.cs Main, before constructing the root command, add `try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected stream — leave alone */ }`. This handles the common case (cmd.exe, legacy PowerShell) without operator action and is a no-op on already-UTF-8 terminals. (2) Keep a defensive fallback inside DoctorCommand.ProbeResult.Glyph: change return type to `string` and return ASCII tags when `Console.OutputEncodi...
- [x] **P15-066** [cli-ux / K_cli] tools has no filtering — operator must grep ~30+ entries for the one they want
  - File(s): src/LacertMCP.Cli/Commands/ToolsCommand.cs (line 42-59)
  - Why: `tools` enumerates every registered MCP tool but offers only `--json`.
  - Do: Add three options to ToolsCommand.Build(): (1) `--filter <regex>` — case-insensitive regex matched against ToolEntry.Name; filter applied after DiscoverTools() in Run(). Wrap the Regex constructor in try/catch and return exit code 2 with a clear stderr message on invalid pattern. (2) `--names-only` — bypass the description/parameter block entirely; print one tool name per line, sorted ordinal. Pairs cleanly with shell pipelines and CI smoke (`lacerte-mcp tools --names-only | measure-object -line...
- [x] **P15-067** [cli-ux / K_cli] FieldAccessCommand RunList/RunSet write errors to Console.Error, bypassing writer parameter
  - File(s): src/LacertMCP.Cli/Commands/FieldAccessCommand.cs (line 81, 143, 166, 171)
  - Why: RunList(char, int, int, TextWriter) accepts a writer for its happy-path output but writes the 'Unknown module' error directly to `Console.Error.WriteLine` (line 81).
  - Do: Change RunList(char, int, int, TextWriter) -> RunList(char, int, int, TextWriter stdout, TextWriter stderr) and RunSet(...) similarly, mirroring UnregisterCommand.Run(bool, TextWriter, TextWriter). Replace the three Console.Error.WriteLine calls inside RunList/RunSet (FieldAccessCommand.cs:81, 166, 171) with stderr.WriteLine. In the two SetHandler closures (BuildListCommand line 60, BuildSetCommand line 138), pass Console.Out + Console.Error explicitly to the Run* call so the default is visible ...
- [x] **P15-068** [error-ux / E_errors] StatusInfo.UserMessage and Remediation strings are English-baked with no localization seam
  - File(s): src/LacertMCP.Server/Errors/StatusCodes.cs, SyntheticStatusCodes.cs, StatusInfo.cs (line multiple)
  - Why: Every userMessage and remediation in StatusCodes.StatusTable and SyntheticStatusCodes.StatusTable is a hardcoded English string.
  - Do: Docs-only fix in two locations, no code change:  1. DECISIONS.md — append a new dated entry: ``` ## 2026-05-30 — Status messages are en-US only (no localization seam)  StatusCodes.StatusTable, SyntheticStatusCodes.StatusTable, and the GetStatusInfo unknown-code fallback emit hardcoded English UserMessage and Remediation strings. StatusInfo has no CultureInfo overload.  Rationale: - Single-operator English-speaking US deployment per CLAUDE.md "Single operator". - The wire envelope already mixes o...
- [x] **P15-069** [error-ux / F_schema] FieldAccessMapLoader.AssertWritable for FieldAccess.Unknown buries the remediation in context.reason
  - File(s): src/LacertMCP.Server/Schema/FieldAccessMapLoader.cs (line 218-224)
  - Why: When a field isn't in the access map, AssertWritable throws SyntheticStatusCodes.UnknownFieldAccess (9012).
  - Do: Drop context.reason in all 5 AssertWritable branches and replace with typed keys.
- [x] **P15-070** [lifecycle / B_session] _newApisCache and _edition not cleared on Register() — re-auth doesn't refresh per-process derivatives
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 85-94, 186-256)
  - Why: Register() updates IsRegistered/RegisteredAt/ExpiresAt on success but does NOT clear _newApisCache or _edition.
  - Do: In LacerteSessionManager.Register() on AccessGranted (both sim path line 247 and live path line 231), invalidate the derived per-process caches so a successful re-auth refreshes them:  ```csharp // After setting IsRegistered/RegisteredAt/ExpiresAt on AccessGranted: lock (_editionLock) { _edition = null; } _newApisCache.Clear(); ```  Rationale per cache: - _edition: REQUIRED after P14-061. Without it, a transient first-probe failure (e.g. live bridge BridgeUnavailable during a pre-Phase-9 startup...
- [x] **P15-071** [lifecycle / A_logging] WireLog ProcessExit hook + AutoFlush ok for graceful exit but no flush on hard-kill; no rotation policy beyond per-day file
  - File(s): src/LacertMCP.Server/Logging/WireLog.cs (line 76-77, 432-461, 465-495, 497-522, 556-557)
  - Why: Two gaps in the WireLog lifecycle for long-running ops: (1) Rotation is per-UTC-day only — no per-file size cap, no compression, no retention.
  - Do: Scope this to the retention gap only — drop the midnight-wording and TerminateProcess bullets (the former is bikeshedding, the latter is OS-level and AutoFlush already covers content durability).  Single-file fix in src/LacertMCP.Server/Logging/WireLog.cs:  1) Add a `LACERTE_DEBUG_LOG_RETENTION_DAYS` env var resolved in ResolveConfig (default `14`, accepted range `1..365`, invalid → silent fallback to 14 — same "never break the operator path" rule as the existing path-validation fallback at line...
- [x] **P15-072** [observability / B_session] Read paths log args but not result-shape — no result-size telemetry
  - File(s): src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 455-617)
  - Why: Read chokepoints (`ListClients`, `ReadTable`, `ReadEFStatus`, `ReadSubclients`, `ReadStates`, `ReadStatuses`, `ReadForms`, `ReadDetail`) wrap their bodies in `WireLog.Record` — but the `result` field in the log line is the FULL payload, then post-redacted.
  - Do: In WireLog.WriteRecord, after JsonSerializer.Serialize but BEFORE Redact, post-process the parsed JsonNode to summarize large arrays under the top-level \"result\" key. Concretely: parse the serialized record back into a JsonNode (or build the record as a JsonObject from the start to skip the round-trip), and if the result value is a JsonArray with Count > LACERTE_DEBUG_LOG_RESULT_SAMPLE (default 5), replace it with {\"kind\":\"collection\",\"count\":N,\"sample\":[first 1-5 items]}. Apply the sa...
- [x] **P15-073** [r1-quality / C_bridge] Reader.cs bridge call bypasses WireLog while Writer.cs is wrapped — asymmetric debug coverage
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Bridge/Reader.cs (line 35-78)
  - Why: P14-004 added Marshal.ReleaseComObject to both Writer.cs and Reader.cs in parallel, and Writer.cs also goes through WireLog.RecordSync<int> for each of its three entry points (WriteToDBFDatabase, UpdateTaxReturn, WriteDetail).
  - Do: Mirror Writer.cs exactly in Reader.cs:35-78. Concrete edit: after the existing `BridgeInputValidation.ValidateReadFromDBFDataArgs(...)` call (which must stay outside the wrap so validation rejections don't pollute the log), wrap the rest of the body in `return WireLog.RecordSync<string>(\"Bridge.ReadFromDBFData\", new { dbf, dataPath }, () => { ... });`. Inside the lambda, keep the existing `var reader = AcquireReader(\"ReadFromDBFData\");` + try/catch/finally (ReleaseComObject) verbatim — both ...
- [x] **P15-074** [r1-quality / B_session] Idempotency cache: type-mismatch on cached Result misreports as 'different payload fingerprint'
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 1349-1359)
  - Why: In TryReplayIdempotent (lines 1349-1359): when the cached entry's PayloadFingerprint matches the new payload, the code tries `entry.Result is T match`.
  - Do: In TryReplayIdempotent (LacerteSessionManager.cs ~1349-1359), split the conflict throw into two distinct branches so the error message matches the actual failure mode: (1) `entry.PayloadFingerprint != payload` → keep existing reason "Idempotency key reused with a different payload fingerprint." and include the cached + requested fingerprint prefixes (first 8 chars only — full base64 is noise) in the context dict. (2) Fingerprint matches but `entry.Result is T` fails → throw with reason "Idempote...
- [x] **P15-075** [r1-quality / O_dx] release.ps1 wipes the entire output directory and re-creates it — silently destroys prior artifacts
  - File(s): C:/Users/VR/Projects/LacertMCP/scripts/release.ps1 (line 23, 78-90)
  - Why: release.ps1 derives staging from -OutputDir if supplied (line 23), then unconditionally does `Remove-Item -LiteralPath $staging -Recurse -Force` (line 79) if it exists.
  - Do: Restructure the staging layout to isolate the wipe: introduce a versioned subdirectory `$stageRoot = Join-Path $staging "LacertMCP-v$Version"` AFTER step 2 resolves $Version (currently line 43), and only `Remove-Item` that subdirectory, not `$staging` itself. Concretely: (a) delete the unconditional wipe at line 78-79; (b) after $Version is resolved, compute `$stageRoot` and `if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }`; (c) point `$serverStage`...
- [x] **P15-076** [security-deep / K_cli] AppID GUID strings linger in the GC heap with no SecureString equivalent
  - File(s): src/LacertMCP.Cli/Storage/AppIdStore.cs (line 221-250)
  - Why: TryCredRead allocates a managed `byte[]` from the credential blob, converts to a managed `string` via `Encoding.Unicode.GetString`, and returns it.
  - Do: Narrow, low-risk fix only — skip the SecureString rewrite. In src/LacertMCP.Cli/Storage/AppIdStore.cs:  (1) TryCredRead (line ~221): wrap the byte[] allocation in a try/finally and zero it: ``` byte[] bytes = null; try {     bytes = new byte[cred.CredentialBlobSize];     Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);     value = Encoding.Unicode.GetString(bytes);     return true; } finally {     if (bytes != null) Array.Clear(bytes, 0, bytes.Length);     if (credPtr != IntPtr.Zero) C...
- [x] **P15-077** [spec-deeper / B_session] TaxYear floor enforced as 2018 but TY18 is documented support boundary — fence-post is silent
  - File(s): C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/Session/LacerteSessionManager.cs (line 378-386)
  - Why: Spec §1: 'Lacerte TY18+ installed'.
  - Do: Two-part fix, sim-actionable in one session:  1. Add `TaxYearTooNew = 9014` (or next free 9xxx) to `src/LacertMCP.Server/Errors/SyntheticStatusCodes.cs` with StatusInfo: name "TaxYearTooNew", message "Tax year exceeds installed Lacerte coverage.", remediation "Pass a TaxYear at or before the current calendar year (DateTime.Today.Year). The Lacerte SDK has no data for unreleased years."  2. In `LacerteSessionManager.SetContext` (line 378-386), after the existing `< 2018` check, add:    ```csharp ...
- [x] **P15-078** [test-quality / A_logging] Single-logfile assertion races UTC midnight rollover
  - File(s): src/LacertMCP.Tests/Logging/WireLogSessionIntegrationTests.cs (line 117-118)
  - Why: WireLog filenames are 'lacerte-' + DateTime.UtcNow.ToString('yyyyMMdd') + '.log' (Server/Logging/WireLog.cs:557).
  - Do: Apply the fix to ALL THREE tests (finding missed WireLogRedactionTests.cs:219): WireLogSessionIntegrationTests.cs:118, WireLogTests.cs:67, WireLogRedactionTests.cs:219. Replace `Assert.Single(files)` + `File.ReadAllLines(files[0])` with a date-roll-agnostic pattern: enumerate all "lacerte-*.log" files in the dir, assert at least one exists, then concatenate their lines (`files.SelectMany(File.ReadAllLines).ToList()`) before running the existing op/redaction assertions. This is preferable to an I...
