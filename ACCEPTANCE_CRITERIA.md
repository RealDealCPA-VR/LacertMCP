# Acceptance Criteria

Per-task definitions of "done." Each `Todo.md` task should reference an entry here once it is non-trivial. Weak example vs strong example below — write strong ones.

## Weak example

> Add field-write tool

## Strong example

> `lacerte_field_write` is registered and listed by the MCP server.
> Calling it with `{module: 'I', table: 1, clientNumber: 'TEST01', field: 'C1_2', value: 'Acme LLC'}` in simulation mode returns `{success: true, statusCode: 1, statusName: 'Success'}` and the field is reflected on the next `lacerte_client_read` call.
> Calling it against a ReadOnly field (e.g. `C1_0`) returns `{success: false, statusCode: 8, statusName: 'ExposedForRead', error: 'Field C1_0 is ReadOnly'}` — and the simulation store is not mutated.
> Calling it with `clientNumber: 'TOOLONGCLIENTID'` returns a validation error before any SDK call (client-side enforcement of the 8-char rule).
> When the session context is TY21+, the call routes through `UpdateTaxReturn(...)` keyed by `clientNumber` (visible in `LACERTE_DEBUG_WIRE=1` log).
> When the session context is TY18–TY20, the call routes through `WriteToDBFDatabase(...)` keyed by row number — and the simulation harness resolves the row from `clientNumber`.
> `dotnet build LacertMCP.sln -c Debug -p:Platform=x64` reports 0 warnings / 0 errors and `dotnet test src/LacertMCP.Tests` is green.

---

## Initial criteria (filled as tasks land)

### Phase 0 — Foundations
* `lacerte_session_status`, `lacerte_set_context`, `lacerte_can_use_new_apis`, `lacerte_list_modules`, `lacerte_list_datapaths` — see per-task entries.

(Add entries as Todo.md grows.)

---

### Phase 5 — Read tools

All Phase 5 tools share these baseline guarantees (do not repeat per entry):
* Wrapped with `WrapToolHandler.Run(...)` → success returns `{success: true, result}`; failure returns `{success: false, statusCode, statusName, severity, userMessage, remediation, context}`.
* Receive `LacerteSessionManager` via constructor injection (Boundary Invariant 3).
* NEVER construct SQL, talk to ODBC, or touch `SimulationStore` directly — only through the session chokepoints (Boundary Invariant 1).
* Every parameter has `[Description("...")]`; every tool method has `[McpServerTool, Description("...")]`.
* Tool names are snake_case starting with `lacerte_`.

#### #41 — `SessionTools.cs`

* `lacerte_session_status` returns `{mode, isRegistered, registeredAt, expiresAt, minutesUntilExpiry, context, appName, appId}`. Pure in-memory read; zero wire I/O. Safe to call before `Register()`. `context` is JSON `null` until `lacerte_set_context` runs.
* `lacerte_set_context` accepts `dataPath`, `taxYear`, `module` (single character), optional `readOnly`. Echoes the resolved context. Returns 9003 (`TaxYearTooOld`) for year < 2018; 24 (`InValidClientName`) for unknown module or empty string.
* `lacerte_can_use_new_apis` returns `{taxYear, module, canUseNewApis, apiPath}` where `apiPath ∈ {"UpdateTaxReturn", "WriteToDBFDatabase"}`. Cached per `(year, module)` in the session manager. Sim returns true for year >= 2021.
* `lacerte_list_modules` returns the 9-module catalog `{count, modules: [{module, name, returnType}]}` from `Schema/Modules.cs`. Static; zero I/O.
* `lacerte_list_datapaths` scans `LACERTE_DATA_ROOT` (fallback `C:\Lacerte`) for `{YY}Tax\{Module}Data` folders. Returns `{root, rootExists, count, datapaths: [{path, year, module, moduleName}]}`. Empty result when root doesn't exist (no throw).

#### #42 — `ClientsTools.cs`

* `lacerte_client_list` accepts optional `nameFilter` (case-insensitive prefix), `limit` (default 100), and `offset` (default 0). Returns the paginated envelope `{rows, total, limit, offset, hasMore, nextOffset}`; legacy `{count, clients}` keys retained for back-compat through v0.1.x and REMOVED in v0.2 per DECISIONS.md (add the decision entry if missing). Filter matches C1_2 (last name) on module I, C1_1 (entity name) on other modules.
* `lacerte_client_read` accepts `clientNumber`. Returns `{clientNumber, found, client}`; `client` is null when not found.
* Both return 9009 (`ContextNotSet`) when `lacerte_set_context` has not been called.

#### #43 — `TablesTools.cs`

* `lacerte_table_read` accepts `table` (1-7), optional `clientNumber`, optional `stateAbbrev`, optional `limit` (default 100), and optional `offset` (default 0). Returns `{table, clientNumber, stateAbbrev, rows, total, limit, offset, hasMore, nextOffset}`; legacy top-level `count` retained for back-compat through v0.1.x and REMOVED in v0.2 per DECISIONS.md.
* Table 8+ returns 5 (`DBNotSupported`) — DATA8 is reserved.
* TY21+ branching is the session manager's job; the tool just passes the table number through.
* Returns 9009 without context.

#### #44 — `SubclientsTools.cs`

* `lacerte_subclient_read` accepts `clientNumber`. Returns `{clientNumber, count, subclients}`. Empty array when no subclients exist (not an error).
* Returns 9009 without context.

#### #45 — `StatesTools.cs`

* `lacerte_state_read` accepts `clientNumber`, optional `stateAbbrev`. Returns `{clientNumber, stateAbbrev, count, states}`.
* `lacerte_state_subclient_read` accepts `clientNumber`, optional `stateAbbrev`. Returns `{clientNumber, stateAbbrev, count, stateSubclients}`.
* Both return 9009 without context.

#### #46 — `StatusTools.cs`

* `lacerte_status_read` accepts `clientNumber`. Returns `{clientNumber, count, statuses}` from DATA5 F4-status rows.
* Read-only (DATA5 is read-only outside a few global flags); no write equivalent in this stream.
* Returns 9009 without context.

#### #47 — `FormsTools.cs`

* `lacerte_forms_read` accepts `clientNumber`. Returns `{clientNumber, count, forms}` from DATA7 form rows.
* Read-only.
* Returns 9009 without context.

#### #49 — `EFileTools.cs`

* `lacerte_ef_status` takes no arguments — reads from the active context. Returns `{count, efStatuses}` with each row carrying `{EFilingId, ClientId, EFId, InstanceId, Status, StatusDate}`.
* TY19+ only (sim returns whatever the seed provides).
* Returns 9009 without context.

#### #50 — Acceptance + README

* This section exists in `ACCEPTANCE_CRITERIA.md`.
* README has a "Tools (Phase 5 — Read)" table listing all 15 Phase 5 read tools plus `lacerte_health` (Phase 0 health probe).
* All Phase 5 tool tests pass under `dotnet test --filter "FullyQualifiedName~Tools.<Name>ToolsTests"`.

---

### Phase 6/7/8 — Write + Create + Metadata tools

All write tools share these baseline guarantees (do not repeat per entry):
* Wrapped with `WrapToolHandler.Run(...)` — success returns `{success: true, result}`; failure returns the canonical error envelope.
* Receive `LacerteSessionManager` via constructor injection (Boundary Invariant 3).
* NEVER construct SQL or COM calls (Boundary Invariant 1) — only through session chokepoints.
* Accept `dryRun: bool = false` and `idempotencyKey: string? = null`; both passed through to the session manager which owns snapshot/restore + the cache.
* Read-only session (`SetContext(..., readOnly: true)`) short-circuits to synthetic `9001 FieldIsReadOnly` BEFORE any access-map / wire / cache work.
* Same idempotency key + same payload → cached result; same key + different payload → synthetic `9102 IdempotencyConflict`.
* Every parameter carries a `[Description("...")]`.

#### #51 — `FieldWriteTools.cs` (`lacerte_field_write`)

* Pre-TY21 entrypoint. Calls `_session.WriteField(table, clientNumber, field, value, subClientID, stateAbbrev, dryRun, idempotencyKey)`.
* Happy path on a `ReadWrite` field in the access map returns `{statusCode: 1, statusName: 'Success', table, clientNumber, field, dryRun}`.
* Writing `C1_0` (universal `ReadOnly` rule) returns `statusCode: 8` (`ExposedForRead`).
* Read-only session → `9001`. Missing context → `9009`.
* `dryRun: true` runs the full validation + access-map gate, returns the would-be status, and leaves the store untouched (verified by re-reading the field).
* Idempotency conflict: same `idempotencyKey` with a different `value` returns `9102`.

#### #52 — `UpdateReturnTools.cs` (`lacerte_update_return`)

* TY21+ entrypoint. Shares `_session.WriteField` with `lacerte_field_write` (year-routed internally per Boundary Invariant 5). Documented as two MCP tools for surface clarity to the model.
* Response carries `api: "UpdateTaxReturn"` to disambiguate when the operator wants the explicit TY21+ path.

#### #53 — `DetailsTools.cs` (`lacerte_detail_read` + `lacerte_detail_write`)

* `lacerte_detail_read(clientNumber)` returns `{clientNumber, count, rows}` from the per-client detail file.
* `lacerte_detail_write` runs **mandatory client-side validation BEFORE the session call** — the SDK does not validate detail-row shape. On failure throws `LacerteSDKError(9008 DetailWriteInvalid)` with a per-field `reason` in `context`:
  * `series` — required, non-empty (v1 accepts any string).
  * `code` — must match `^-?\d+$`.
  * `stateAbbr` — empty OR must match `^[A-Z]{2}$`.
  * `tsj` — empty OR one of `T | S | J`.
* Happy path appends a new row to the in-memory detail list; `lacerte_detail_read` immediately reflects the new row (count + 1).
* `dryRun: true` validates + simulates but rolls back; `lacerte_detail_read` shows unchanged count.
* `replaceIndex: int` overwrites the row at that zero-based index instead of appending; out-of-range returns the SDK's `OutOfIndex` status.

#### #54 — Read-only mode acceptance

* Every write tool (`lacerte_field_write`, `lacerte_update_return`, `lacerte_detail_write`, `lacerte_client_create`) returns `{success: false, statusCode: 9001, statusName: 'FieldIsReadOnly'}` when the active context was set with `readOnly: true`. The gate fires synchronously before access-map / idempotency / dry-run / wire work.

#### #55 — Dry-run acceptance

* Every write tool accepts `dryRun: true`. On success returns the would-be TAPIStatus; the in-memory store is untouched (verified via a read after the dry-run). The session manager snapshots the active module, executes the write, and restores in `finally`.

#### #56 — Idempotency acceptance

* Every write tool accepts `idempotencyKey: string?`. Same key + same payload returns the cached result without re-executing the write. Same key + different payload throws synthetic `9102 IdempotencyConflict`. Payload fingerprint is SHA-256 over the canonicalized `{op, args}` JSON.

#### #57 — `Bridge/ClientList.cs`

* Wraps `IClientList.SetDataPath → IClient(TaxType, ClientID, EmailID, FirstName, LastName) → CreateClient`. Translates TAPIStatus to `LacerteSDKError`. (Live wiring lands in Phase 9; sim path goes through `_session.CreateClient`.)

#### #58 — `CreateClientTools.cs` (`lacerte_client_create`)

* Client-side validation runs BEFORE the session call so synthetic 9xxx errors land instead of bare SDK status codes:
  * `clientID` is required, length ≤ 8, matches `^[A-Z0-9]+$`. Length-fail → `9002 ClientIDTooLong` with `reason` mentioning `LengthExceeded`. Bad chars → `StatusCodes.InValidClientName (24)`.
  * `taxType` is required and must match the active module per the `_moduleTaxTypes` table — wrong-module → `StatusCodes.TaxModuleMismatch (14)`.
  * Year < 2018 (via context) → synthetic `9003 TaxYearTooOld`.
* Happy path returns `{statusCode: 1, statusName: 'Success', clientNumber, clientID, dryRun}` where `clientNumber` is the freshly-minted sim identifier.
* Duplicate ClientID for the active module/year returns `StatusCodes.DuplicateClient (12)`.

#### #59 — All-error-paths sim coverage

* Tests in `CreateClientToolsTests` exercise: happy path, `9002 ClientIDTooLong` (`TOOLONGNAME`), `InValidClientName` (`BAD!`), `DuplicateClient` (`BROW`), `TaxYearTooOld` via SetContext, `TaxModuleMismatch` (`Corporate` on module I), empty TaxType, read-only session (9001), dry-run no-persist, idempotency conflict (9102).

#### #60 — `MetadataTools.cs` (`lacerte_field_access_map`)

* `lacerte_field_access_map(module, table, year)` returns `{module, moduleName, table, year, count, fields: { fieldName: accessName }}`. Always includes `C{t}_0 → ReadOnly` (universal rule). Enumerates `C{t}_0..C{t}_25` + `_G1/_G2/_G3` group fields; skips Unknown-resolved entries so the response only reports cataloged fields.
* Invalid module → `InValidClientName (24)`. Table outside 1..7 → `DBNotSupported (5)`. Year < 2018 → synthetic `9003`.

#### #61 — `MetadataTools.cs` (`lacerte_return_codes`)

* `lacerte_return_codes` returns `{count, codes: [{code, name, severity, userMessage, remediation, origin}]}` for every entry in `StatusCodes.StatusTable` (TAPIStatus 0..28 + TPSDKStatus 200..204) AND `SyntheticStatusCodes.StatusTable` (9xxx). `origin` is one of `TAPIStatus | TPSDKStatus | Synthetic`. At least 30 entries shipped.

---

### Phase 11 — MCP Prompts

All Phase 11 prompts share these baseline guarantees (do not repeat per entry):
* Each prompt lives on a `[McpServerPromptType]`-decorated static class (`WorkflowPrompts`) and is exposed by a `[McpServerPrompt(Name = "lacerte_*")]`-decorated static method.
* Registered via `.WithPromptsFromAssembly()` on the `McpServer` builder in `Program.cs` so they appear in the MCP `prompts/list` response without per-prompt wiring.
* Each method returns a single-message `GetPromptResult` whose lone `PromptMessage` has `Role.User` and a `TextContentBlock` body.
* Prompt bodies reference ONLY real `lacerte_*` tool names — no fabricated tools. Every tool name in the body must resolve to an actual `[McpServerTool]` in the registered tool assembly.
* Each prompt's parameter list carries `[Description("...")]` on every argument so `prompts/list` surfaces useful help text.
* `prompts/get` round-trips through the MCP test harness in `WorkflowPromptsTests`.

#### #79 — `WorkflowPrompts.cs`

* `lacerte_client_intake(clientName?)` — onboarding walkthrough. Body references `lacerte_session_status`, `lacerte_list_datapaths`, `lacerte_set_context`, `lacerte_client_create`, `lacerte_field_write`, `lacerte_field_access_map`, `lacerte_detail_write`, `lacerte_client_list`, and `lacerte_client_read`. `clientName` is optional; default behavior is to ask the user.
* `lacerte_review_ef_status(taxYear?)` — e-file triage sweep. Body references `lacerte_session_status`, `lacerte_set_context`, `lacerte_ef_status`, and `lacerte_detail_read`. `taxYear` is optional; default uses the active context's year.
* `lacerte_bulk_field_update(table=1, field?, previewCount=3)` — bulk-write workflow that ENFORCES a dry-run preview AND idempotency keys. Body references `lacerte_session_status`, `lacerte_set_context`, `lacerte_field_access_map`, `lacerte_field_write`, `lacerte_client_list`, and `lacerte_client_read`. STOPs on the first non-Success status — does not skip+continue.
