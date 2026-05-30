# Regression Checklist

Run before declaring any task complete. Catches silent breakage in adjacent code paths.

## Build / Compile

- [ ] `npm run build` (tsc) passes with no errors.
- [ ] No new TypeScript errors in unrelated files.
- [ ] All relative imports keep their `.js` extension (Node16 ESM).

## Server startup

- [ ] `npm run dev` boots without throwing.
- [ ] Simulation banner prints when expected (off-Windows or `LACERTE_SIMULATION=1`).
- [ ] All expected tools appear in the tool list (count matches `src/index.ts` registrations).

## Session / context

- [ ] `lacerte_session_status` returns a coherent payload in both modes.
- [ ] `lacerte_set_context` with a bad datapath surfaces a structured error (not a raw exception).
- [ ] `lacerte_can_use_new_apis` returns the right boolean for TY18, TY20, TY21, TY24.

## Read path

- [ ] `lacerte_client_list` returns rows for the seeded simulation dataset.
- [ ] `lacerte_table_read` works against tables 1, 2, 3 (covers TY21+ branching code).
- [ ] `lacerte_detail_read` returns the seeded series/code/amount rows.

## Write path

- [ ] `lacerte_field_write` against a ReadOnly field returns `isError: true` with the access-denied code (no SDK call attempted).
- [ ] `lacerte_field_write` against a ReadWrite field round-trips through the simulation store.
- [ ] TY21+ context routes writes to `UpdateTaxReturn` (verified via debug log).

## Create path

- [ ] `lacerte_client_create` rejects ClientID > 8 chars before any SDK call.
- [ ] `lacerte_client_create` rejects TaxYear < 2018.
- [ ] Happy path returns the newly created client number.

## Error mapping

- [ ] Every TAPIStatus value referenced by a code path has an entry in `src/errors/status-codes.ts`.
- [ ] No tool returns a raw integer status; all wrapped in `{ success: false, error, statusCode }`.

## Handoff readiness

- [ ] `Todo.md` updated truthfully — only fully working items checked off.
- [ ] `HANDOFF.md` updated with Last Session Summary, Verify Before Continuing, Next Task, Context Notes.
- [ ] Partial work documented in Context Notes with file paths and line numbers.

---

## Manual Live-Mode Verification

Phase 9 (#62-#68) ships the COM bridge as scaffolding only — every call compiles + tests in sim mode, but the actual wire round-trip is only verifiable on a Windows + Lacerte box. Before any live release, an operator MUST run each item below end-to-end against:

- **Box:** Windows 10/11 x64 with Lacerte TY24 + LacerteSDK installed and `LacerteDSII` DSN registered.
- **Test client:** `JOHNS` (or any seeded client) in TY24 Individual module.
- **Test datapath:** `C:\Lacerte\24data` (or wherever Lacerte's installer placed the per-year `?data` directory).
- **AppID:** minted via `lacerte-mcp register` and persisted in Credential Manager.

Mark each box `[x]` only after the operator personally observed the tool succeed against real DBF files.

### Bridge wire round-trip (every method on `ILacerteBridge`)

- [ ] `LacerteAccessClient` ctor on Windows + SDK present → `IsConnected == true`. Verify via `lacerte-mcp doctor` (probe added in Phase 11).
- [ ] `Bridge.Registration.Register(appName, appId, 2024)` against a fresh AppID surfaces Intuit consent + sign-in dialogs; operator clicks through; returns `AccessGranted (201)`. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `Bridge.Registration.Register` against a previously-approved AppID within 24h cache window returns `AccessGranted (201)` SILENTLY (no dialogs). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `Bridge.CanUseNewApisProbe.Probe(appId, 2024, 'I')` returns `true`. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `Bridge.CanUseNewApisProbe.Probe(appId, 2020, 'I')` returns `false` (pre-TY21 branch). Exercised end-to-end on `JOHNS` client / TY20 Individual / `C:\Lacerte\20data` datapath.
- [ ] `Bridge.Writer.WriteToDBFDatabase(appId, dataPath, "DATA1I20.DBF", rowNum, "C1_5", "test")` against TY20 client returns `Success (1)` and the value persists. Exercised end-to-end on `JOHNS` client / TY20 Individual / `C:\Lacerte\20data` datapath.
- [ ] `Bridge.Writer.UpdateTaxReturn(appId, dataPath, "DATA1I24.DBF", "JOHNS", "C1_5", "test", null, null)` returns `Success (1)` and the value persists. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `Bridge.Writer.WriteDetail(appId, dataPath, "JOHNS", detailRow)` appends and persists. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `Bridge.Reader.ReadFromDBFData(appId, dataPath, "DATA1I24.DBF")` returns non-empty XML. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `LacerteAccessClient.CreateClient(appId, dataPath, 2024, 'I', "TST01", "Individual", "Test", "User", "test@example.com")` returns `Success (1)` + a new `ClientNumber`. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.

### Read tools (Phase 5)

- [ ] `lacerte_session_status` returns coherent payload in both modes. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_client_list` returns the seeded Lacerte client roster. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_client_read("JOHNS")` returns the C1_* fields. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_table_read(table=1)` returns all DATA1 rows. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_table_read(table=2, clientNumber="JOHNS")` routes through the TY21+ `[JOHNS-SubClientInfo]` query form. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_table_read(table=3, clientNumber="JOHNS", stateAbbrev="CA")` routes through TY21+ `[JOHNS-StateClientInfo]`. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_subclient_read("JOHNS")` returns DATA2 rows. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_state_read("JOHNS")` returns DATA3 rows. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_status_read("JOHNS")` returns DATA5 F4 status. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_forms_read("JOHNS")` returns DATA7 forms. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_detail_read("JOHNS")` returns the per-client detail-file rows. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_ef_status` returns rows from `EFilings JOIN EFStatuses` (TY19+). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.

### Write tools (Phase 6)

- [ ] `lacerte_field_write` on a writable field (e.g. C1_5 address line) persists and a subsequent `lacerte_client_read` returns the new value. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_field_write` against a ReadOnly field returns `9001 FieldIsReadOnly` WITHOUT touching the wire. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_field_write` with `dryRun: true` returns Success without persisting (verified by re-reading the unchanged value). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_update_return` against a TY24 client routes through `IDBFWriter.UpdateTaxReturn` (verified via `LACERTE_DEBUG_WIRE=1` log). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_detail_write` with a valid Series/Code/Amount appends a new detail row. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.

### Create tools (Phase 7)

- [ ] `lacerte_client_create({clientID:"TST01", taxType:"Individual", firstName:"Test", lastName:"User", emailID:"test@example.com"})` returns a new ClientNumber and a subsequent `lacerte_client_list` includes it. Exercised end-to-end on the `C:\Lacerte\24data` datapath / TY24 Individual.
- [ ] `lacerte_client_create` rejects ClientID > 8 chars BEFORE any wire call. Exercised end-to-end.
- [ ] `lacerte_client_create` rejects TaxYear < 2018 BEFORE any wire call. Exercised end-to-end.

### Auth lifecycle (Phase 9)

- [ ] `lacerte-mcp register` ceremony on a fresh box: GUID minted, persisted in Credential Manager, dialogs pop, AccessGranted returned. Exercised end-to-end on a fresh Windows install.
- [ ] MCP server startup within the 24h cache window: `Register()` returns silently (no dialogs). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] After 24h elapse OR explicit logout: every tool call short-circuits to synthetic `9101 ReAuthRequired` with the `lacerte-mcp login` remediation message. Exercised end-to-end.

### Sanity / observability

- [ ] `lacerte-mcp doctor` returns exit 0 — all 7 probes green (Windows version, x64, .NET 4.8, ODBC DSN, SDK install, env, AppID). Exercised end-to-end.
- [ ] `LACERTE_DEBUG_WIRE=1` writes a `logs/lacerte-YYYYMMDD.log` with COM call args + SQL strings (Phase 11 #75). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] No tool ever surfaces a raw `TAPIStatus` integer — every error response is the structured `{success:false, statusCode, statusName, severity, userMessage, remediation, context}` shape (Boundary Invariant #9). Exercised end-to-end.
