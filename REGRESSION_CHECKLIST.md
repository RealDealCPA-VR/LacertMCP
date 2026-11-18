# Regression Checklist

Run before declaring any task complete. Catches silent breakage in adjacent code paths.

## Build / Compile

- [ ] `& "$env:USERPROFILE\.dotnet\dotnet.exe" build C:/Users/VR/Projects/LacertMCP/LacertMCP.sln -c Debug -p:Platform=x64` reports **0 warnings, 0 errors**.
- [ ] System-wide `dotnet` is runtime-only on this box — the user-local SDK at `$env:USERPROFILE\.dotnet` is the only one that can build the solution.
- [ ] No new C# warnings/errors in unrelated projects (`LacertMCP.Server`, `LacertMCP.Cli`, `LacertMCP.FieldAccessDumper`, `LacertMCP.Tests`).

## Tests

- [ ] `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64` reports `0 failed` with `skipped` matching the count documented in `HANDOFF.md` "Verify Before Continuing" (currently **11**, all live-only / fixture-gated). The pass count grows over time as tests land — the operative invariants are 0 failed + the documented skip set.
- [ ] The 11 skips are the 9 `[Fact(Skip = "Requires real Lacerte SDK …")]` cases under `Tests/Bridge/` (Reader, Writer, Registration, CanUseNewApisProbe, LacerteAccessClient), the one Windows-only Credential Manager P/Invoke round-trip in `Tests/Cli/AppIdStoreTests.cs`, plus `LiveLegParityPlaceholder.LiveLeg_AwaitsRecordedReplayFixture` (flips green when a parity recording lands under `tests/Parity/fixtures/`).

## Server startup

- [ ] `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe` starts the stdio MCP server without throwing (kill after confirming startup).
- [ ] Simulation banner prints when expected (off-Windows or `LACERTE_SIMULATION=1`).
- [ ] All expected tools appear in the tool list — count matches the `[McpServerTool]`-decorated methods under `src/LacertMCP.Server/Tools/` plus `Prompts/PromptsCatalogTool.cs` (43 tools today — count the decorated methods rather than trusting a hard-coded number, since this drifts every time a tool is added; `HelpTools.ToolsByCategory()` must list the same set, enforced by the bidirectional parity test in `HelpToolsTests`).

## CLI smoke

- [ ] `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status` exits **0** with a JSON payload.
- [ ] `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor` is expected to exit **2** off-Windows = PARTIAL (Windows-only probes skipped, no real failures); exit 1 means a probe genuinely failed (e.g. AppID missing on a fresh checkout — run `lacerte-mcp register` to fix); exit 0 = all green, no warns (Windows + Lacerte install). Warn-only runs also exit 2 (PARTIAL) and print per-warn remediation lines (R6-29). Per DoctorCommand.cs XML doc.

## Dumper smoke

- [ ] `& C:/Users/VR/Projects/LacertMCP/src/LacertMCP.FieldAccessDumper/bin/x64/Debug/LacertMCP.FieldAccessDumper.exe` prints `no fields to probe in sim; run on Windows for live introspection.` in the dev env.

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

- [ ] Every TAPIStatus value referenced by a code path has an entry in `src/LacertMCP.Server/Errors/StatusCodes.cs`.
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
- [ ] `BridgeInputValidation` fires PRE-wire on Writer + Reader paths. With `LACERTE_DEBUG_WIRE=1` enabled, invoke each of the four wired bridge paths with a deliberately bad input — (a) `Writer.WriteToDBFDatabase` with `dataPath` containing `\x00`, (b) `Writer.UpdateTaxReturn` with `clientNumber` > 8 chars, (c) `Writer.WriteDetail` with a detail `Source` > 50 chars, (d) `Reader.ReadFromDBFData` with `dbf` failing the `^[A-Z0-9]{1,12}\.[A-Z0-9]{1,4}$` pattern. For each: confirm the tool surfaces the structured error AND that the tail of `logs/lacerte-YYYYMMDD.log` contains NO record for the rejected call (the validator throws before `WireLog.AppendCall` runs). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
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
- [ ] `lacerte_detail_write` with a valid Series/Code/Amount (server started with `LACERTE_LIVE_DETAIL_WRITES=1`) appends a new detail row — see "FP-2 live items" below. Exercised end-to-end on a TEST client / TY24 Individual / `C:\Lacerte\24data` datapath.

### Create tools (Phase 7)

- [ ] `lacerte_client_create({clientID:"TST01", taxType:"Individual", firstName:"Test", lastName:"User", emailID:"test@example.com"})` returns a new ClientNumber and a subsequent `lacerte_client_list` includes it. Exercised end-to-end on the `C:\Lacerte\24data` datapath / TY24 Individual.
- [ ] `lacerte_client_create` rejects ClientID > 8 chars BEFORE any wire call. Exercised end-to-end.
- [ ] `lacerte_client_create` rejects TaxYear < 2018 BEFORE any wire call. Exercised end-to-end.

### Auth lifecycle (Phase 9 + R6-05 CLI ceremony wiring)

- [ ] `lacerte-mcp register` ceremony on a fresh box: GUID minted, persisted in Credential Manager, Intuit consent + sign-in dialogs pop on the interactive desktop, operator clicks through, command prints `AccessGranted (201) — consent ceremony complete.` and exits **0**. Exercised end-to-end on a fresh Windows + Lacerte install.
- [ ] `lacerte-mcp register` with the operator declining the consent dialog: command prints `FAIL: consent ceremony returned ConsentDenied (200)` with the catalog remediation on stderr and exits **1** (no exit 3 — that code is retired for register/login). Exercised end-to-end on the Windows + Lacerte box.
- [ ] `lacerte-mcp register` on Windows WITHOUT the Lacerte SDK MSI installed: command prints the `PARTIAL:` block on stderr (AppID persisted; pointer at `lacerte-mcp doctor` + the 64-bit MSI) and exits **2**. Exercised end-to-end on a Windows box with no SDK.
- [ ] `lacerte-mcp login` within a valid consent window: refreshes the 24h ticket silently (no dialogs), prints `AccessGranted (201)`, exits **0**; MCP tools reach the live SDK afterwards. Exercised end-to-end on the Windows + Lacerte box.
- [ ] `lacerte-mcp login` with no persisted AppID: prints `No AppID found. Run lacerte-mcp register first.` and exits **1**. (Sim-verifiable off-Windows; the full exit-code matrix is also pinned by `Cli/RegisterLoginCeremonyExitCodeTests.cs` via the injectable ceremony seam.)
- [ ] MCP server startup within the 24h cache window: `Register()` returns silently (no dialogs). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] After 24h elapse OR explicit logout: every tool call that touches `RequireContext` short-circuits to synthetic `9007 LoginExpired` with the `lacerte-mcp register` / `lacerte-mcp login` remediation message; calls that bypass `RequireContext` (e.g. `lacerte_session_status`) still succeed and report `registered: false`. Exercised end-to-end.

### Round-6 tools (bulk create / bulk detail / client query)

Sim-mode behavior for all three tools is pinned by the test suite (`Tools/ClientCreateBulkToolsTests.cs` / `Tools/DetailWriteBulkToolsTests.cs` / `Tools/ClientQueryToolsTests.cs` — caps, positional per-row idempotency keys, continueOnError/skipped accounting, dry-run rollback, cancellation). The items below are the live-wire halves an operator must observe on the Windows + Lacerte box:

- [ ] `lacerte_client_create_bulk` with 3 entries + `idempotencyKey` prefix: 3 real clients created (verify via `lacerte_client_list`); resubmitting the identical batch with the same prefix replays from cache with the ORIGINAL clientNumbers and creates no duplicates. Exercised end-to-end on the `C:\Lacerte\24data` datapath / TY24 Individual.
- [ ] `lacerte_client_create_bulk` exceeding `LACERTE_BULK_CREATE_MAX` (default 100): throws `9020 BulkBatchTooLarge` BEFORE any row runs (no partial creates on disk). Exercised end-to-end.
- [ ] `lacerte_detail_write_bulk` with 3 W-2 rows + `idempotencyKey` prefix: rows append and persist (verify via `lacerte_detail_read`); resubmitting the identical batch does NOT double the rows. Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] `lacerte_detail_write_bulk` with an entry carrying `replaceIndex` in live mode: that row fails `9019 LiveReplaceIndexUnsupported`; with `continueOnError:false` the remainder is marked skipped. Exercised end-to-end.
- [ ] `lacerte_client_query(fieldName: "C1_3", op: "empty")` against the real roster returns exactly the clients with no email (spot-check 3 against `lacerte_client_read`), and `total` matches the matched count, not the table size. Exercised end-to-end on the `C:\Lacerte\24data` datapath / TY24 Individual.

### Sanity / observability

- [ ] `lacerte-mcp doctor` returns exit 0 — every probe green (canonical list in `DoctorCommand.cs` XML doc; includes Windows version, x64, App.config shim, .NET 4.8, ODBC DSN, SDK install, COM ProgID, Operating mode, LACERTE_DATA_PATH + directory, LACERTE_DATA_ROOT + directory, FieldAccessMap shards, AppID). Exercised end-to-end.
- [ ] `LACERTE_DEBUG_WIRE=1` writes a `logs/lacerte-YYYYMMDD.log` with COM call args + SQL strings (Phase 11 #75). Exercised end-to-end on `JOHNS` client / TY24 Individual / `C:\Lacerte\24data` datapath.
- [ ] No tool ever surfaces a raw `TAPIStatus` integer — every error response is the structured `{success:false, statusCode, statusName, severity, userMessage, remediation, context}` shape (Boundary Invariant #9). Exercised end-to-end.

### Round-7 live items
- [ ] A live `lacerte_field_write` while the return is open in Lacerte returns `success:false` + `statusCode 2 DBFNotAccessable` (not `success:true`).
- [ ] `lacerte-mcp register` succeeds from the CLI exe (its own `lacerte-mcp.exe.config` carries the legacy-runtime shim); `doctor` App.config probe is green for both exe configs.
- [ ] `lacerte_state_read` on a TY24 client returns rows and the state is in `C3_G1` (Todo R7-L1 — resolved from source; this is the live sanity check).
- [ ] Live `lacerte_detail_write` with `LACERTE_LIVE_DETAIL_WRITES` unset returns 9202 SimulationOnly whose reason says the path is built but off until verified, and how to enable it (FP-2; the SDK writes detail files via ODBC only — Todo R7-L2/R7-SRC8).
- [ ] `lacerte_client_create` live returns Success with `clientNumber: null` + `clientNumberHint` (the SDK returns no number — Todo R7-L3), and `lacerte_client_find` then resolves it by ClientID.
- [ ] `lacerte-mcp register` returns exit 0 on Grant (raw TPSDKStatus 1 → 201 AccessGranted — Todo R7-SRC2) and activates `LacerteSDK_Access.LacerteAccess` → `CreateTPRegister()` (R7-SRC1).

### Round-8 live items
- [ ] TY24 `lacerte_subclient_read` / `lacerte_state_subclient_read` rows carry `subClientId` (mapped from `C2_1` / `C4_1`), and a live write to a client with 2+ dependents reports `verified: true` (not "can't identify the row"). DATA4's `C4_1` is from the SDK PDF; confirm DATA2 uses `C2_1` too.
- [ ] A live field write's read-back really is fresh: change a field in Lacerte's UI, write it back over MCP, confirm `verified` reflects the value on disk (driver-manager pooling could hand the "fresh" read a pooled connection — if so, add `Pooling=false` for the verification connection).
- [ ] Writing a checkbox field with JSON `true` / `false` returns `verified: true` and note the stored form (X / Y / 1).
- [ ] `lacerte_client_query` on table 2 for a TY24 datapath returns rows across clients (the per-client tables are walked through the roster); check how long it takes on the real firm size.
- [ ] A TY24 client with no dependents: `lacerte_subclient_read` returns an empty list, not a DBNotSupported error (if the per-client table doesn't exist, map 42S02 to empty for single-client reads too).
- [ ] The server starts and answers `/healthz` immediately even when the 24h Intuit ticket is cold (sign-in now runs in the background); note whether the SDK shows its sign-in window at startup.
- [ ] Performance on the real firm: `lacerte_client_list` now reads and sorts the whole DATA1 roster per call (sim parity); time it on the real data root (especially over a file share). If slow, try a driver-side `order by C1_0` with `top N`.
- [ ] Installer re-run on an install made by the v0.2.x installer (no install.json): the data root, port and mode carry over from the old LacertMCP.vbs.

### FP-2 live items (detail-row writes over ODBC)
Live detail writes stay OFF (9202) until every item below passes. Use a TEST client created for this (never a real taxpayer), with the return closed in Lacerte unless an item says otherwise.
- [ ] Turn it on: add `LACERTE_LIVE_DETAIL_WRITES=1` to the server's environment and restart; the startup banner shows `liveDetailWrites=on`. With the variable unset the banner shows `off` and `lacerte_detail_write` returns 9202 with the "built but off" reason.
- [ ] Dry run first: `lacerte_detail_write(clientNumber, series:"11", code:"1", amount:1234.56, description:"FP2 TEST W-2", source:"Source", tsj:"T", dryRun:true)` returns Success, `verified: null`, `snapshot: null`; no file appears in `%APPDATA%\lacerte-mcp\detail-snapshots` and `lacerte_detail_read` is unchanged.
- [ ] Write one W-2-type row (same call, `dryRun:false`, with an `idempotencyKey`): Success, `verified: true`, and `snapshot` names a new `<yyyyMMdd-HHmmss>-I24-<client>.json` holding the client's rows from BEFORE the write. A second write to the same client returns the same `snapshot` path (no second file).
- [ ] Open the client in Lacerte's UI and confirm the row is on the W-2 screen with the right amount, Taxpayer/Spouse assignment and description, and that Lacerte recalculates without errors. Note what Lacerte shows for Prefix/Suffix (the INSERT omits them when blank — confirm the driver's default is what Lacerte expects for a first W-2, or require explicit values; the SDK says standard fields' Suffix starts at 1000).
- [ ] Confirm `lacerte_detail_read` shows the new row and the read-back compared the driver's real column shapes (Series/Code numeric, TSJ stored as `Taxpayer`, Source as `Source`); if `verified` came back 9022 on a row that is correct in Lacerte, record the stored shape and fix `DetailRowWriter.RowMatches`.
- [ ] Re-sending the identical call with the same `idempotencyKey` replays (no second row in Lacerte).
- [ ] Description length: write rows whose descriptions are 20, 30, 40 and 60 characters to a couple of Series (W-2, interest, dependents). A description longer than the field allows must come back as 9022 (read-back mismatch from truncation), not `verified: true`; record each field's limit for the field dictionary (FP-3).
- [ ] Worker on its own: in PowerShell, `'{}' | & "<install>\server\LacertMCP.Server.exe" --detail-write-worker; $LASTEXITCODE` prints exactly one JSON line with `"ok":false` and exit code 2, and starts no server (no port, no sign-in window).
- [ ] Worker failure path: during a live write make the driver fail (e.g. the data share disconnected after the backup, or a datapath the driver can't open) and confirm the tool returns a clean error (9201 or a mapped ODBC code) with `rowWritten` set, and that the server keeps answering other tools afterwards (`lacerte_session_status`, `lacerte_client_list`) without a restart or a new Intuit sign-in. If the 64-bit driver ever crashes on INSERT, the error must say the driver "failed or crashed" and the server must stay up.
- [ ] With the client open in Lacerte's UI, a write returns a clean error (locked file) and no partial row appears after closing it.
- [ ] Worker visibility: no console window flashes on the desktop during a write; Task Manager shows a short-lived second `LacertMCP.Server.exe` per write and none left behind.
- [ ] Restore drill: from a `snapshot` file, re-enter (or delete) rows in Lacerte so the client matches the backup; note anything the file lacks for a scripted restore (the restore command is a follow-up).

### FP-2 restore live items (`lacerte-mcp detail-restore`)
Removing detail rows over the Lacerte ODBC driver (DELETE) is UNVERIFIED. Run every item on a TEST client (never a real taxpayer), with the return closed in Lacerte and no agent writing to that client, before using `apply` on a real return.
- [ ] Journal: after a few live `lacerte_detail_write` calls to the TEST client, `<backup>.journal.jsonl` sits next to the backup `.json`, has one line per row written (outcome `verified`), and the backup's `journal` field names it. `lacerte-mcp detail-restore list` shows the client, module, year, data path, time and the journaled row count.
- [ ] Plan: enter one extra row by hand in Lacerte and edit the amount of one MCP-written row; `detail-restore plan --journal <file>` lists the untouched MCP rows as "still present", the edited one as "already gone" with a "looks edited" warning, and never mentions the hand-entered row. Nothing changes in Lacerte.
- [ ] `apply` without `--confirm` prints the plan and changes nothing.
- [ ] DELETE semantics, one row: `apply --journal <file> --confirm` with one MCP-written row present; it reports "removed and verified", the row is gone in Lacerte's UI, every other row (including the hand-entered one) is still there, and Lacerte recalculates without errors. Record the DELETE's rows-affected and whether the driver matched the row at all (if it reports "removed nothing", the driver compares differently from the read path - e.g. TSJ/Source stored differently or case-sensitive - record what it stores).
- [ ] DELETE semantics, identical copies: enter the same row by hand so the client has 2 identical copies plus 1 MCP-written copy; `apply` removes exactly one (DELETE + 2 re-inserts), the client ends with 2 copies, and Lacerte shows both. Note whether the re-inserted copies differ from hand-entered ones in Prefix/ImportSrc (the command warns if they do).
- [ ] Over-matching guard: hand-enter a row with the same Series/Code/Suffix/amount whose Description starts with the MCP row's Description (e.g. "ACME" vs "ACME CORP"); `plan` marks the MCP row NOT REMOVABLE and `apply` skips it untouched. Then check on the driver whether `DELETE ... WHERE Description = 'ACME'` would have removed "ACME CORP" too (xBase prefix comparison) - if not, the guard is merely conservative.
- [ ] Second `apply` on the same journal is a no-op (no worker started, "Removed 0").
- [ ] Failure handling: with the client open in Lacerte's UI (or the data share disconnected), `apply` stops at the first row with a clean message saying nothing was changed (or exactly what to re-enter), and `<journal>.restored.jsonl` records it; after closing the client, `apply` finishes.
- [ ] Worker on its own: `'{"connectionString":"x","sql":"DELETE FROM [X-Detail]","op":"deleteDetailRow"}' | & "<install>\server\LacertMCP.Server.exe" --detail-write-worker; $LASTEXITCODE` prints one `"ok":false` line and exit code 2. The CLI runs the worker from its own folder (`<install>\cli\LacertMCP.Server.exe`, copied with the CLI) - confirm it is there and no console window flashes during `apply`.
