# Lacerte MCP

A local Windows MCP server that exposes Intuit Lacerte tax software (TY18+) as tools an LLM can call. Wraps the Lacerte SDK (`LacerteSDK_Access` COM/.NET + `LacerteDSII` ODBC) over JSON-RPC/stdio.

**Status:** v0.1.0 scaffolding — all suites passing, 0 failures, 0 warnings (current pass/skip totals tracked in `HANDOFF.md` "Verify Before Continuing"; skip count is stable at 10 live-only). Read/Write/Create tools, simulation store, ODBC layer, COM bridge scaffolding, CLI, prompts, observability (correlation IDs, heartbeat writer, wire-log redaction split), bulk-write tool, schema-reload tool, doctor probe expansion, and idempotency cache all land. Live verification requires a Windows + Lacerte + SDK box (see `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification" section).

## What this is

* **Local bridge** — runs on the same Windows machine as Lacerte. There is no hosted variant possible (Lacerte SDK is local-only).
* **Read/Write/Create** — read DBF tables 1–7 + detail files + EF status; write per-field with TY21+ branching; create new clients.
* **Two modes** — `live` (real SDK) and `simulation` (in-memory DBF fixtures, default off-Windows) so most development can happen anywhere.

## What this MCP does NOT do

Bounding expectations up front saves a triage loop with the LLM:

* **No e-filing actions** — read-only EF status via `lacerte_ef_status` (TY19+). Submitting, retransmitting, or correcting an e-file still happens in Lacerte's own UI.
* **No printing, PDF rendering, or third-party export** — DBF read/write + COM field-write only. Use Lacerte's File → Print or Print to PDF for any rendered output.
* **No organizer, client portal, letter template, or diagnostics-detail access** — those live in non-DBF Lacerte surfaces this MCP doesn't wire up.
* **Single-operator only** — no concurrency control across multiple Lacerte users sharing one datapath. Don't point two MCP servers at the same `LACERTE_DATA_PATH` simultaneously.
* **No archived / closed-copy / prior-firm-transfer file access** — the MCP sees the live working set under `{YY}Tax\{Module}Data`, not Lacerte's archive folders.
* **No live UI refresh** — writes land on disk, but the Lacerte editor only rereads on client close/reopen. Verify a write actually landed via `lacerte_client_read` / `lacerte_table_read` (see Troubleshooting); the Lacerte UI staleness is structural, not an MCP defect.

If you need any of the above, drive Lacerte directly — this MCP is designed to complement, not replace, the desktop UI.

## Architecture (one-line)

`MCP client ⇄ stdio ⇄ Tool layer ⇄ Session manager ⇄ {COM bridge | ODBC reader | Simulation store}`

Full architecture in [ARCHITECTURE.md](ARCHITECTURE.md). Per-decision rationale in [DECISIONS.md](DECISIONS.md). Product truth in [REQUIREMENTS.md](REQUIREMENTS.md).

## Try it (simulation mode)

You don't need Windows or a Lacerte install to kick the tires — the MCP ships with an in-memory 8-client seed fixture (`src/LacertMCP.Server/Session/Fixtures/SeedData.cs`) so you can wire the server into Claude Desktop (or any stdio MCP host) on macOS / Linux / Windows-without-Lacerte and ask the LLM to read the seeded clients.

**1. Build + point Claude Desktop at the sim-mode server.** Use the JSON snippet under "Install paths → Claude Desktop → Sim mode" below (the `LACERTE_SIMULATION=1` env var is the only thing that matters).

**2. Type these prompts verbatim in Claude Desktop to drive each seeded datapath:**

| Prompt to paste | What the LLM calls under the hood | Seeded ClientIDs you should see back |
| --- | --- | --- |
| "Set my Lacerte context to 2020 Individual at `C:\Lacerte\20Tax\IData`, then list every client." | `lacerte_set_context` + `lacerte_client_list` | `SMIT` (single), `JONE` (joint, 1 dep), `REJE` (EF rejected) |
| "Switch to 2024 Individual at `C:\Lacerte\24Tax\IData`, then list every client." | `lacerte_set_context` + `lacerte_client_list` | `BROW` (joint, 2 deps), `PEND` (EF Transmitted) |
| "Switch to 2024 Corporate at `C:\Lacerte\24Tax\CData`, then list every client and show me the state rows for ACME." | `lacerte_set_context` + `lacerte_client_list` + `lacerte_state_read` | `ACME` (CA + NY), `WIDG` (TX) |
| "Switch to 2024 Partnership at `C:\Lacerte\24Tax\PData`, then list every client and read PART's subclients." | `lacerte_set_context` + `lacerte_client_list` + `lacerte_subclient_read` | `PART` (3 partners: Alice / Bob / Carol) |

All 8 seeded ClientIDs (`SMIT`, `JONE`, `REJE`, `BROW`, `PEND`, `ACME`, `WIDG`, `PART`) live across the four sim datapaths. The simulation store is observationally identical to live mode for the happy path (CLAUDE.md two-mode discipline) so anything that works here works against a real Lacerte install once you flip `LACERTE_LIVE=1`.

## Quick start (dev / simulation mode)

The repo ships PowerShell wrappers under `scripts/` that resolve the user-local SDK automatically (the system-wide `dotnet` is runtime-only on most operator boxes):

```powershell
./scripts/build.ps1   # dotnet build LacertMCP.sln -c Debug -p:Platform=x64
./scripts/test.ps1    # dotnet test ... --no-build
./scripts/smoke.ps1   # status + doctor + dumper + server-startup probe (HANDOFF.md "Verify Before Continuing")
```

Or invoke the SDK directly:

```powershell
# Use the user-local .NET 8 SDK (system-wide dotnet is runtime-only on this box)
& "$env:USERPROFILE\.dotnet\dotnet.exe" build LacertMCP.sln -c Debug -p:Platform=x64
& "$env:USERPROFILE\.dotnet\dotnet.exe" test  src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64

# On-demand coverage spot-check (P14-041 — no CI gate, just measurement)
& "$env:USERPROFILE\.dotnet\dotnet.exe" test src/LacertMCP.Tests/LacertMCP.Tests.csproj --collect:"XPlat Code Coverage" --settings src/LacertMCP.Tests/coverlet.runsettings

# Smoke-test the CLI
& src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status
& src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor   # exit 2 off-Windows = PARTIAL (Windows-only probes skipped); exit 1 = a probe failed (e.g. fresh checkout with no AppID — run `lacerte-mcp register` or let a sim test fixture seed it); exit 0 = all green (Windows + Lacerte install)
```

The MCP server binary is `src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe`. It runs in simulation mode by default off-Windows or when `LACERTE_SIMULATION=1`.

## Install paths (per MCP client)

### Claude Desktop (`claude_desktop_config.json`)

For an operator install, run `./scripts/release.ps1` to produce `dist/LacertMCP-v0.1.0/LacertMCP-v0.1.0.zip` (the zip is staged inside a per-version subdirectory so prior release artifacts aren't wiped — the script also prints the exact path on success). Unzip it on the operator box (e.g. to `C:\LacertMCP\`), then point Claude Desktop at the staged server binary:

```json
{
  "mcpServers": {
    "lacerte": {
      "command": "C:\\LacertMCP\\server\\LacertMCP.Server.exe",
      "env": {
        "LACERTE_LIVE": "1",
        "LACERTE_DATA_PATH": "C:\\Lacerte\\24data",
        "LACERTE_DEFAULT_TAX_YEAR": "2024",
        "LACERTE_APP_NAME": "Operator Lacerte MCP",
        "LACERTE_DEBUG_WIRE": "0"
      }
    }
  }
}
```

> Tip: Windows accepts forward slashes in JSON paths — `"command": "C:/LacertMCP/server/LacertMCP.Server.exe"` works and avoids the double-backslash gotcha when hand-editing the config.

**Sim mode** — no Lacerte install required; uses the in-memory 8-client seed fixture (`src/LacertMCP.Server/Session/Fixtures/SeedData.cs`). Use this to confirm the wiring before pointing the MCP at real return data:

```json
{
  "mcpServers": {
    "lacerte": {
      "command": "C:\\LacertMCP\\server\\LacertMCP.Server.exe",
      "env": {
        "LACERTE_SIMULATION": "1"
      }
    }
  }
}
```

First call to verify wiring: `lacerte_set_context dataPath="C:\\Lacerte\\24Tax\\IData" taxYear=2024 module="I"` then `lacerte_client_list` returns the seeded BROW + PEND clients. The other seeded datapaths are `C:\Lacerte\20Tax\IData` (SMIT, JONE, REJE), `C:\Lacerte\24Tax\CData` (ACME, WIDG), and `C:\Lacerte\24Tax\PData` (PART with 3 partner subclients).

For dev / sim against the source tree, swap `C:\\LacertMCP\\server\\LacertMCP.Server.exe` for `C:\\path\\to\\LacertMCP\\src\\LacertMCP.Server\\bin\\x64\\Release\\LacertMCP.Server.exe`. To switch from live to sim mode, drop `LACERTE_LIVE` + `LACERTE_DATA_PATH` from the env block and add `LACERTE_SIMULATION=1` (the in-memory seed provides the datapaths above).

### Cursor / Windsurf / opencode (same JSON shape)

Drop the same block into the host's MCP-servers config file (Cursor: `~/.cursor/mcp.json`; Windsurf: settings → MCP servers).

### Generic stdio MCP host

```yaml
command: C:\path\to\LacertMCP.Server.exe
transport: stdio
env:
  LACERTE_LIVE: "1"
  LACERTE_DATA_PATH: C:\Lacerte\24data
```

Sim variant (no Lacerte required):

```yaml
command: C:\path\to\LacertMCP.Server.exe
transport: stdio
env:
  LACERTE_SIMULATION: "1"
```

### Environment variables (full reference)

| Var | Required? | Default | Purpose |
|---|---|---|---|
| `LACERTE_LIVE` | one of LIVE / SIMULATION required | unset | `1` enables live SDK on Windows. |
| `LACERTE_SIMULATION` | one of LIVE / SIMULATION required | `1` off-Windows | `1` forces simulation regardless of platform. |
| `LACERTE_DATA_PATH` | advisory only | unset | Quoted in ODBC-error remediations and probed by `lacerte-mcp doctor`. NOT auto-applied to session context — every session must still call `lacerte_set_context`. |
| `LACERTE_DEFAULT_TAX_YEAR` | recommended | unset | Default year used when `lacerte_set_context` omits one. |
| `LACERTE_APP_NAME` | recommended | `LacerteMCP` | Friendly name in the SDK consent dialog. |
| `LACERTE_APP_ID` | runtime read-only | from Credential Manager | Source of truth is Windows Credential Manager (set via `lacerte-mcp register`). |
| `LACERTE_DEBUG_WIRE` | optional | `0` | `1` logs COM + SQL to `logs/lacerte-YYYYMMDD.log`. Secrets redacted. |
| `LACERTE_DEBUG_LOG_PATH` | optional | `./logs` | Override the log directory. |
| `LACERTE_HEARTBEAT_SECONDS` | optional | `30` | Heartbeat JSON write cadence to `%TEMP%/lacerte-mcp-heartbeat.json` (pid, mode, auth countdown, build version). Set `0` to disable. External watchers read the file mtime + JSON to check liveness + 24h-auth state without speaking MCP. |
| `LACERTE_SIM_EXPIRED` | testing only | unset | `1` simulates an expired 24h auth session. |
| `LACERTE_DATA_ROOT` | optional | `C:\Lacerte` | Root scanned by `lacerte_list_datapaths` when discovering `{YY}Tax\{Module}Data` folders. |
| `LACERTE_MCP_FORCE_FILE_STORE` | testing / dev | unset | `1` bypasses Windows Credential Manager and uses the file-based AppID store at `%APPDATA%\lacerte-mcp\config.json` (auto-on off-Windows). |
| `LACERTE_PARITY_RECORDING` | testing only | unset | Path to recorded parity JSON consumed by the `BothModesData` test harness (live ↔ sim contract recorder). |

> ODBC connections are DSN-less — `ConnectionStringBuilder` composes them from driver + datapath + module + year, so no `LACERTE_DSN` env var is honored or needed. The `LacerteDSII` DSN name is only probed by `lacerte-mcp doctor` as an install-sanity check; it is not used at runtime.

## Lacerte SDK install (Windows operator)

1. **Install Intuit Lacerte** (TY18+) and complete its first-run setup. The MCP cannot bootstrap an uninitialized Lacerte.
2. **Install the 64-bit Lacerte SDK MSI** from [Intuit's developer portal](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started). The 32-bit driver has a known INSERT crash and is not supported by this MCP. (See `DECISIONS.md` 2026-05-30 "64-bit host process and 64-bit Lacerte SDK MSI required".)
3. **Verify the install** with the doctor command:
   ```powershell
   & src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor
   ```
   Exit 0 means every probe is green. The doctor surface covers OS/runtime (Windows, x64, .NET 4.8, App.config shim), install (ODBC DSN, Lacerte SDK install dir, Lacerte COM ProgID), config (operating mode, LACERTE_DATA_PATH, LACERTE_DATA_ROOT, FieldAccessMap shards), and identity (AppID GUID) — see `src/LacertMCP.Cli/Commands/DoctorCommand.cs` XML doc for the canonical, always-current list. Exit 2 (PARTIAL) means at least one probe was skipped (typical off-Windows). Exit 1 means at least one probe genuinely failed.
4. **Run the one-time registration** ceremony:
   ```powershell
   & src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe register --app-name "Operator Lacerte MCP" --tax-year 2024
   ```
   **Phase 9 caveat (v0.1.x):** Today this mints the AppID GUID and persists it to Windows Credential Manager, then exits **3** with a `[partial]` message on stderr explaining the Intuit consent ceremony is not yet wired (tracked as Phase 9 task #63 in `Todo.md`). The full live-mode boot path is not available until Phase 9 ships. **Simulation mode (the default off-Windows and on-Windows when `LACERTE_LIVE` is unset) works fully today** — you only hit the Phase-9 gap if you try to flip `LACERTE_LIVE=1`. Once Phase 9 lands, this command will additionally call `ITPRegistration.Register()` to surface the Intuit consent + sign-in dialog and exit 0.
5. **Within 24 hours, set `LACERTE_LIVE=1` in your MCP client config** and the server will silently re-attach to the cached session. After 24h, run `lacerte-mcp login` to refresh (today this is also a Phase 9 stub — see the troubleshooting section below).

The `App.config` next to `LacertMCP.Server.exe` carries `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime v4.0 / .NETFramework v4.6` — load-bearing for the Lacerte mixed-mode CLR shim. Don't delete it.

## First call from your LLM host

Once Claude Desktop / Cursor / Windsurf can see the `lacerte` server, type `/` in Claude Desktop to surface MCP prompts — you should see `lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_field_write_bulk`, and `lacerte_csv_intake` from the `lacerte` server. (Cursor: `@lacerte`. Windsurf: see the host's prompt picker.)

> If `/` does not surface any `lacerte_` prompts (or the tool picker shows nothing from the `lacerte` server), jump to the "Claude Desktop / Cursor MCP config saved, but no `lacerte_` tools appear" entry in the Troubleshooting section below for the 3-step diagnostic ladder.

For a hands-on three-step verify the wiring works, type these prompts verbatim:

1. **"Show me the Lacerte data folders on this machine."** → LLM calls `lacerte_list_datapaths`.
2. **"Set my active context to 2024 Individual at C:\\Lacerte\\24Tax\\IData."** → LLM calls `lacerte_set_context`.
3. **"List my first 10 clients."** → LLM calls `lacerte_client_list(limit=10)`.

A successful response in Claude Desktop's tool-call expander looks like:

```json
{
  "success": true,
  "result": { "rows": [{ "ClientNumber": "1001", "ClientName": "..." }], "total": 2, "limit": 10, "offset": 0, "hasMore": false, "nextOffset": null },
  "cid": "a1b2c3d4"
}
```

If the first call returns `success: false` with `statusCode: 9007 LoginExpired` (proactive expiry probe) or raw SDK `statusCode: 203 NotSignedIn` (reactive path), your 24h ticket expired — run `lacerte-mcp login` from PowerShell and retry. See the Troubleshooting section below for the full failure-mode catalog.

## Tools

### Read tools (Phase 5)

| Tool | What it does | Phase |
| --- | --- | --- |
| `lacerte_session_status` | Snapshot of mode, registration, 24h expiry, active context. Zero wire I/O. | 5 |
| `lacerte_set_context` | Set the active (dataPath, taxYear, module) triple + optional readOnly flag. | 5 |
| `lacerte_can_use_new_apis` | Whether (taxYear, module) supports TY21+ `UpdateTaxReturn`; cached per pair. | 5 |
| `lacerte_list_modules` | The 9-module catalog (I/C/P/S/F/R/T/N/B) with names + ODBC ReturnType strings. | 5 |
| `lacerte_list_datapaths` | Discover `{YY}Tax\{Module}Data` folders under `LACERTE_DATA_ROOT` (fallback `C:\Lacerte`). | 5 |
| `lacerte_client_list` | List clients in the active context. Optional name-prefix filter + `limit`/`offset` pagination (returns `rows`, `total`, `limit`, `offset`, `hasMore`, `nextOffset`; legacy `count`/`clients` retained until v0.2). | 5 |
| `lacerte_client_read` | Read one client by ClientNumber from DATA1. | 5 |
| `lacerte_table_read` | Generic DBF table reader for DATA1..DATA7. Optional client + state filters and `limit`/`offset` pagination (returns `rows`, `total`, `limit`, `offset`, `hasMore`, `nextOffset`; legacy `count` retained until v0.2). | 5 |
| `lacerte_subclient_read` | DATA2 subclient rows (dependents, partners, shareholders, beneficiaries). | 5 |
| `lacerte_state_read` | DATA3 state rows for one client. Optional state filter. | 5 |
| `lacerte_state_subclient_read` | DATA4 state-subclient rows for one client. Optional state filter. | 5 |
| `lacerte_status_read` | DATA5 F4-status rows for one client (read-only). | 5 |
| `lacerte_forms_read` | DATA7 form rows for one client (read-only). | 5 |
| `lacerte_detail_read` | Per-client detail file rows (Series/Code/Amount/Description). | 5 |
| `lacerte_ef_status` | TY19+ EFilings JOIN EFStatuses for the active context. | 5 |
| `lacerte_health` | Health probe. Returns `{ mode, status }` (mode = `"simulation"`/`"live"`, status = `"reachable"`). When `LACERTE_HEARTBEAT_SECONDS > 0` (default 30s), the envelope also surfaces `{ heartbeatPath, heartbeatSeconds }` so an external watcher (Task Scheduler / NSSM / PowerShell) can detect a hung server via file mtime without speaking MCP. Set `LACERTE_HEARTBEAT_SECONDS=0` to disable. Live HRESULT probe via `_session.HealthProbe()` is a Phase-9 follow-up. | 0 |

### Write / Create / Metadata tools (Phases 6-8)

| Tool | What it does | Phase |
| --- | --- | --- |
| `lacerte_field_write` | Write one DBF field (pre-TY21 entrypoint). Params: `table`, `clientNumber`, `field`, `value`, `subClientId?`, `stateAbbrev?`, `dryRun?`, `idempotencyKey?`. Honors readOnly/dryRun/idempotencyKey. | 6 |
| `lacerte_update_return` | Write one DBF field (TY21+ entrypoint, `UpdateTaxReturn`). Params: same as `lacerte_field_write`. Same gates as above. | 6 |
| `lacerte_field_write_bulk` | N writes through one MCP call. Entries: `{clientNumber, field, value, subClientId?, stateAbbrev?}`. Optional `taxYear` / `readOnly` preflight + `continueOnError` flag. Per-row idempotency key is `{prefix}-{clientNumber}-{field}`. | 6 |
| `lacerte_detail_write` | Append/replace a detail-file row with mandatory client-side validation. Required: `clientNumber`, `series`, `code`, `amount`, `description`. Optional (default empty): `prefix`, `suffix`, `stateAbbrev`, `cityAbbr`, `source`, `tsj`, `importSrc`. | 6 |
| `lacerte_client_create` | Create a new client. Params: `clientId` (≤ 8 alphanumeric chars), `taxType`, `firstName`, `lastName`, `emailId`, `dryRun?`, `idempotencyKey?`. | 7 |
| `lacerte_field_access_map` | Return per-field ReadOnly/ReadWrite/Hidden/NotExposed access map. Params: `module` (string, length-1), `table`, `taxYear`. | 8 |
| `lacerte_return_codes` | Discoverable resource: all 28 TAPIStatus + 5 TPSDKStatus + 11 synthetic 9xxx codes. Each row carries both `origin` (TAPIStatus/TPSDKStatus/Synthetic) and `originGroup` (sdk/synthetic — matches the error-envelope `origin` field). | 8 |
| `lacerte_reload_schema` | Drop the in-process FieldAccessMap caches so freshly written shards on disk take effect without restarting the server (which would invalidate the Intuit 24h auth ticket). Returns `{ modulesReloaded, overlaysReloaded, schemaRoot }`. | 13 |

## Prompts

Four first-class MCP prompts (registered via `WithPromptsFromAssembly()` in `Program.cs`, discoverable through the standard MCP `prompts/list` handshake):

| Prompt | What it walks the agent through | Args |
| --- | --- | --- |
| `lacerte_client_intake` | Onboard a new client end-to-end: verify session → pick datapath/module/year → `lacerte_client_create` → seed DATA1 fields → seed detail rows → verify roster. | `clientName?: string` |
| `lacerte_review_ef_status` | Triage e-file status: pull EFiling+EFStatus join, group by Status, surface Rejected first, flag stale Pending/Transmitted (>72h). TY19+ only. | `taxYear?: int` |
| `lacerte_field_write_bulk` | Bulk field write across many clients with mandatory dry-run preview (first N), idempotency keys, stop-on-first-error. Name matches the `lacerte_field_write_bulk` tool (P16-052 — was `lacerte_bulk_field_update` pre-P16). | `table: int = 1`, `field: string` (required), `previewCount: int = 3` |
| `lacerte_csv_intake` | Bulk-onboard clients from a CSV / Tax-Organizer export with one-shot column→field mapping declaration, per-row idempotency keys, stop-on-first-error. | `sourceLabel?: string`, `rowCount?: int` |

Canonical step lists live in `src/LacertMCP.Server/Prompts/WorkflowPrompts.cs`.

## Response envelope

Every tool returns a JSON string with one of two shapes — success or error. Both are produced by `src/LacertMCP.Server/Errors/WrapToolHandler.cs` (the canonical implementation; any tool that bypasses it is a bug — see `ACCEPTANCE_CRITERIA.md` lines 33/100 for the spec). The shape is stable across all tools, in both simulation and live mode.

**Success:**

```json
{
  "success": true,
  "result": { "rows": [/* ... */], "total": 42, "limit": 100, "offset": 0, "hasMore": false, "nextOffset": null },
  "cid": "a1b2c3d4"
}
```

`result` is the tool-specific payload (the example above is a trimmed `lacerte_client_list` response). `cid` is an 8-hex per-request correlation ID stamped by `WrapToolHandler`; the same `cid` appears on nested `WireLog.Record` entries in the disk log so an operator can grep the whole causal chain for one tool call.

**Error:** see the `## Errors` section below for the full field list, semantics, and `origin` discriminator. The error envelope carries the same top-level `success: false`, the structured `statusCode` / `statusName` / `severity` / `userMessage` / `remediation` / `context` fields documented there, and the same `cid` correlation ID.

**Redaction split (P15-006):** both envelopes are run through `WireLog.Redact(..., RedactionScope.Wire)` before they leave the process — a *narrow* keyset that scrubs only true secrets (SSN / EIN / TIN, bank / routing, DOB, email, phone). Operator-actionable strings like `context.reason`, `context.rawError`, detail-row `Description`, and address columns (`City` / `Zip`) survive on the wire so the LLM consumer has a real diagnostic to act on. The on-disk debug log (`logs/lacerte-YYYYMMDD.log`, when `LACERTE_DEBUG_WIRE=1`) uses a separate, broader keyset (`RedactionScope.Disk`) that does scrub those operator-facing columns — the asymmetry is intentional.

## Errors

Every tool returns one of two JSON envelope shapes — success or error. The error shape is consistent across all tools and includes a discriminator (`origin`) so consumers know where the fix lives:

```json
{
  "success": false,
  "statusCode": 9012,
  "statusName": "UnknownFieldAccess",
  "origin": "synthetic",
  "severity": "error",
  "userMessage": "...",
  "remediation": "...",
  "context": { /* structured retry hints, redacted of true secrets */ }
}
```

The `origin` field is either:

- `"sdk"` — codes **0–204**, real Lacerte SDK TAPIStatus (0–28) or TPSDKStatus (200–204) state. Look up the code in Intuit's SDK documentation; remediation is **operator-side** (the operator typically needs to fix something in Lacerte itself, re-auth, or correct a tax-data input).
- `"synthetic"` — codes **9001–9299**, minted by this MCP **before** any SDK call. Pre-flight validation (9001–9099), auth lifecycle (9100–9199), or COM bridge / runtime (9200–9299). Remediation is usually **MCP-side** (fix the tool call args, flip a server config flag, or run a CLI command like `lacerte-mcp register`).

Call the `lacerte_return_codes` tool for the full code catalog with names, severities, user messages, remediation strings, and the same `origin` discriminator per code. See `DECISIONS.md` 2026-05-30 "Synthetic statusCode range" + 2026-05-30 "Error envelope carries `origin` discriminator" for the design rationale.

All `userMessage` and `remediation` strings are **en-US only** (see `DECISIONS.md` 2026-05-30 "Status messages are en-US only"). SDK-echoed text in `context.reason` / `context.rawError` is passed through unmodified.

## CLI

TL;DR:

| Command | Purpose |
| --- | --- |
| `lacerte-mcp register` | One-time AppID mint + Intuit consent + sign-in dialogs. |
| `lacerte-mcp login` | Re-call `Register()` to refresh the 24h auth ticket. |
| `lacerte-mcp status` | Human-readable summary (pass `--json` for machine-parseable) — no wire I/O. |
| `lacerte-mcp unregister` | Wipe the persisted AppID (typed `--confirm` required). |
| `lacerte-mcp tools` | List every MCP tool the server registers (reflection — no stdio boot). Supports `--filter <regex>` / `--name <exact>` / `--names-only` / `--json`. |
| `lacerte-mcp field-access {set,list,discover}` | Manage the per-field access map. `discover` exits 3 with a pointer to the standalone `LacertMCP.FieldAccessDumper.exe`. |
| `lacerte-mcp doctor` | Pre-flight probes for the install; exit 0/1/2 contract (3 reserved for "not implemented here", see `field-access discover`). |

### `lacerte-mcp register [--app-name X] [--tax-year Y] [--reset]`

One-time AppID mint + Intuit consent + sign-in ceremony. Mints a per-install AppID GUID, persists it via the AppID store (Credential Manager on Windows, file fallback off-Windows), then on Windows + live SDK calls `ITPRegistration.Register(...)` which surfaces the consent + Intuit dialogs to the interactive shell.

- `--app-name` (default `Operator Lacerte MCP`) — friendly name shown in the Intuit consent dialog.
- `--tax-year` (default = prior calendar year) — tax year the registration is scoped to.
- `--reset` (default `false`) — **operator recovery path**: mints a fresh GUID even if one exists, forcing a new Intuit consent ceremony. Use this when the AppID is lost or corrupted.

Must be run from an **interactive PowerShell** (not a service / scheduled task) so the consent dialog can surface.

**Exit codes:**
- `0` — fully complete (Phase 9+: AppID minted AND Intuit ceremony succeeded; today: only on non-Windows where the ceremony is intentionally skipped for cross-platform dev convenience).
- `1` — hard failure (IO error writing the AppID store; missing required args).
- `3` — **partial / Phase 9 incomplete:** AppID was minted (or re-used) and persisted, but the Intuit consent ceremony was not invoked. Today this is the expected outcome on Windows. Wrapping PowerShell scripts must treat exit 3 as "AppID step ready, live-mode boot blocked until Phase 9 ships" — not as a hard failure to retry.

### `lacerte-mcp login [--app-name X] [--tax-year Y]`

Re-call `Register()` to refresh the 24h Intuit auth ticket. Use this when `lacerte_session_status` reports `ticketExpired: true` or when a tool starts returning `LoginExpired (9007)` errors. Reuses the persisted AppID; no fresh consent dialog unless the ticket is fully gone.

- `--app-name` (default `Operator Lacerte MCP`)
- `--tax-year` (default = prior calendar year)

**Exit codes:** same shape as `register`. Code `3` means the 24h ticket refresh did not actually fire (today's Phase 9 stub) — tools will return `LoginExpired (9007)` once the current ticket lapses.

### `lacerte-mcp status [--show-app-id] [--json]`

Snapshot of registration state, mode, storage backend, and platform. Zero wire I/O — safe to run on a cron. Default output is a human-readable summary (P15-026); pass `--json` for the machine-parseable shape (the JSON payload is preserved verbatim for backward compatibility — any script already consuming it just needs to add `--json`). By default the AppID is masked (first 8 chars + `****`); `--show-app-id` prints the full GUID, useful for **backup before a Lacerte reinstall**.

### `lacerte-mcp unregister --confirm`

Wipe the persisted AppID GUID from the active storage backend (Windows Credential Manager on Windows, the file fallback off-Windows). This is the discoverable counterpart to `register --reset` — use it for a clean uninstall or when you need to fully decommission an install (today the only alternative is `cmdkey /delete` or hand-editing the JSON file).

- `--confirm` (default `false`) — **required**. Without it the command refuses (exit 1) and prints a preview of what would be deleted. After `unregister`, the next `lacerte-mcp register` will mint a fresh GUID and force a new Intuit consent ceremony (Windows + live SDK).
- No-op when nothing is persisted: exits 0 with a "nothing to unregister" message so it's safe to run in cleanup scripts.

### `lacerte-mcp tools [--json] [--filter <regex>] [--name <exact>] [--names-only]`

List every MCP tool the server registers, by reflecting over the `LacertMCP.Server` assembly. Mirrors the SDK's own `WithToolsFromAssembly()` discovery — no stdio transport boot required, runs anywhere (including off-Windows). Useful for sanity-checking the tool surface before pasting into a Claude Desktop / Cursor MCP config, or for quick before/after diffs between Phase milestones.

- Default human-readable table: tool name, description, parameter names + types.
- `--json` emits a machine-parseable array of `{ name, description, declaringType, parameters[] }` triples.
- `--filter <regex>` — case-insensitive regex matched against tool name (e.g. `--filter '^lacerte_ef_'` for e-file tools). Exit 2 on invalid regex.
- `--name <exact>` — deep-print one tool's full parameter surface (incl. per-parameter descriptions, declaring type, optionality). Exit 1 if no match. Recommended when composing MCP client configs — the live assembly is always authoritative even when the static table above drifts.
- `--names-only` — one tool name per line, no descriptions; pairs cleanly with shell pipelines and CI.

Sample `--name` output:

```
$ lacerte-mcp tools --name lacerte_field_write
lacerte_field_write
  Write one DBF field (pre-TY21 entrypoint) ...
  declaringType: LacertMCP.Server.Tools.FieldWriteTools
  parameters:
    - table: int
        DBF table number (1..7).
    - clientNumber: string
        Lacerte ClientNumber, e.g. "1001".
    ...
```

### `lacerte-mcp field-access {set | list | discover}`

Operator surface for the per-field access map (`Schema/FieldAccessMap/base/<module>.json`).

- `list --module <I|C|P|S|F|R|T|N|B> --table <1..7> [--year YYYY]` — print every entry for a module/table at the requested year (overlays applied).
- `set --module <X> --table <N> --field <F> --access <ReadWrite|ReadOnly|Hidden|NotExposed|NotSupported|Unknown> [--data-type ...] [--max-length ...] [--source manual|spec|introspect] [--note ...]` — upsert one field entry into the base map (re-serialized sorted so PR diffs stay stable).
- `discover` — **redirect stub; always exits 3** (distinct from doctor's exit 2 = env-skip). Prints a one-line pointer to the standalone introspector: `LacertMCP.FieldAccessDumper.exe --module <X> --table <N>` (Windows + live SDK).

### `lacerte-mcp doctor`

Setup verification across OS/runtime, install, config, and identity probes (canonical list in `src/LacertMCP.Cli/Commands/DoctorCommand.cs` XML doc — currently 13 probes spanning Build version, Windows version, x64 process, App.config runtime shim, .NET 4.8, ODBC DSN `LacerteDSII`, Lacerte SDK install, Lacerte COM ProgID, Operating mode (sim/live with conflict warnings), `LACERTE_DATA_PATH` (env + dir-exists), `LACERTE_DATA_ROOT` (env + dir-exists), FieldAccessMap base shards, persisted AppID). Exit-code contract:

- `0` — every probe green.
- `1` — at least one probe failed (e.g. fresh checkout with no AppID; ODBC DSN missing).
- `2` — at least one probe was skipped (no fails) — typical off-Windows result since the Windows-only probes can't run.
- `3` is **not used by doctor** — it is reserved across the CLI for "not implemented here" verbs (see `field-access discover`).

## Troubleshooting

The setup involves an interactive Intuit consent dialog, a 24-hour ticket expiry, a 32-bit-vs-64-bit MSI trap, a mixed-mode CLR shim, and ODBC DSN configuration. The most common symptoms and their fixes:

### `lacerte-mcp register` exits 3 with `[partial]` on stderr (v0.1.x expected state)

- **Symptom:** The command prints the AppID, then a `[partial] AppID minted, but the Intuit consent ceremony is not yet wired into the CLI (Phase 9 task #63)` line on stderr, and exits 3.
- **Diagnosis:** This is the documented v0.1.x state — Phase 9 task #63 has not landed. The AppID half succeeded; the Intuit consent half is a stub.
- **Fix:** Use simulation mode (do not set `LACERTE_LIVE=1`) until Phase 9 ships. Watch `Todo.md` Phase 9 #63 (`Bridge/Registration.cs`) for the upgrade. Once it lands, re-run `lacerte-mcp register` and the consent dialog will surface.

### `lacerte-mcp register` opened no dialog after Phase 9 lands (or hangs)

- **Symptom:** Post-Phase-9, the command exits 0 but no Intuit consent window appeared.
- **Diagnosis:** Run from a **non-interactive** shell (SSH session, Windows service, scheduled task) — the Intuit ITPRegistration COM call needs a desktop session to surface its UI.
- **Fix:** Open a normal interactive PowerShell on the operator's desktop session and re-run `lacerte-mcp register`. Confirm with `lacerte-mcp status` that `registered: true`.

### Tools start returning `LoginExpired (9007)` after a day

- **Symptom:** Reads + writes that worked yesterday now fail with status `LoginExpired (9007)` (proactive expiry probe) or raw SDK `NotSignedIn (203)` (if the reactive path slips past the probe). Both envelopes include the remediation `Run lacerte-mcp register (or lacerte-mcp login) to re-authenticate`.
- **Diagnosis:** Intuit's auth ticket has a 24-hour lifetime. The session manager surfaces a clear remediation, but operators can short-circuit it.
- **Fix:** From an interactive PowerShell, run `lacerte-mcp login --app-name "Operator Lacerte MCP" --tax-year 2024`. This re-calls `Register()` to mint a fresh 24h ticket using the existing AppID.
  - **Until Phase 9 ships, `lacerte-mcp login` also exits 3 with `[partial]`** — there is no manual ticket refresh available in v0.1.x; the only workable mode today is simulation. The earlier draft of this section referred to `NotSignedIn (9101)` / `TicketExpired (9103)` — both incorrect: `9101` is `ReAuthRequired` (a reserved-but-not-thrown code) and `9103` is `InvalidAppId` (the bridge-boundary AppID shape check), not a 24h expiry symptom.

### `lacerte_field_write` or `lacerte_detail_write` crashes the server with an INSERT error

- **Symptom:** Live-mode writes throw an opaque ODBC INSERT crash, often with `DBFNotAccessable (2)` or a generic Win32 fault.
- **Diagnosis:** The 32-bit Lacerte SDK MSI is installed instead of the 64-bit MSI. The 32-bit driver has a known INSERT bug and is unsupported by this MCP (see `DECISIONS.md` 2026-05-30 "64-bit host process and 64-bit Lacerte SDK MSI required").
- **Fix:** Uninstall the 32-bit MSI from Add/Remove Programs, then install the 64-bit Lacerte SDK MSI from [Intuit's developer portal](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started). Verify via the 64-bit `odbcad32.exe` (under `C:\Windows\System32`) that the `LacerteDSII` DSN appears under "System DSN".

### Server fails to start with `BadImageFormatException` or `MissingMethodException`

- **Symptom:** `LacertMCP.Server.exe` immediately crashes on boot with a CLR loader error.
- **Diagnosis:** The `App.config` next to `LacertMCP.Server.exe` was deleted or modified. The Lacerte SDK is a mixed-mode CLR assembly that needs `useLegacyV2RuntimeActivationPolicy="true"` plus `supportedRuntime v4.0 / .NETFramework v4.6` set in the host's `.config`.
- **Fix:** Restore `App.config` from source control (or re-run `dotnet build` — MSBuild copies it next to the output). Don't delete it.

### Claude Desktop / Cursor MCP config saved, but no `lacerte_` tools appear in the host's tool list

- **Symptom:** The MCP-servers config block is in place, the host was restarted, but typing `/` (Claude Desktop) or `@` (Cursor) surfaces no `lacerte_` prompts and the tool picker shows nothing from the `lacerte` server.
- **Diagnosis:** Three independent failure modes — boot crash, broken assembly registration, or host-config mismatch — present with the same silent symptom because most MCP hosts swallow the spawned process's stderr by default.
- **Fix (3-step diagnostic ladder):**
  1. **Confirm the server binary actually boots.** From PowerShell on the same machine the host is running on, run `& "C:\path\to\LacertMCP.Server.exe"` directly (use the absolute path you put in the host config). If it exits immediately with a CLR loader error, jump to the `BadImageFormatException` entry above (App.config missing or modified). If it hangs waiting for stdio input, that is healthy — Ctrl+C and continue to step 2.
  2. **Confirm the assembly registers tools at all.** Run `& "C:\path\to\lacerte-mcp.exe" tools --names-only` (reflection only, no stdio handshake — see the CLI section above). If this lists ~22 tools beginning with `lacerte_`, the server is healthy and the problem is in the host config: typically (a) wrong path in the host JSON `command` field — note that `LacertMCP.Server.exe` and `lacerte-mcp.exe` are **different binaries**, only the former is the MCP server; (b) unescaped backslashes in the JSON path string (use forward slashes — `"command": "C:/LacertMCP/server/LacertMCP.Server.exe"` — or doubled backslashes); (c) `LACERTE_LIVE=1` set in the host's `env` block on a machine with no registered AppID, causing the server to fail at boot before completing the MCP handshake (run `lacerte-mcp doctor` to confirm AppID state). If `tools --names-only` returns nothing or errors, the build is broken — run `dotnet build src/LacertMCP.sln` and retry.
  3. **Inspect the host's stderr capture for the spawned process.** Claude Desktop writes spawned-MCP stderr to `%APPDATA%\Claude\logs\mcp*.log` (one file per server, named after the `mcpServers` key). Cursor writes to its own `~/.cursor/logs/` tree; Windsurf surfaces it in its log pane. The exit code and any uncaught exception will be there. If the log is empty, the host may not be invoking the binary at all — re-check the config path on disk.

### `lacerte_field_write` returned success but my change is not visible in Lacerte

- **Symptom:** A field-write or detail-write tool returns `success: true` with a valid `cid`, but the Lacerte editor window still displays the old value when the operator looks at the return.
- **Diagnosis:** Lacerte's editor displays a cached view of the currently-open return; DBF / COM writes land on disk, but the editor does not reread them while the client is open in the UI. This is structural to how the Lacerte desktop app works, not an MCP defect — but the same write replayed through the MCP read tools will show the new value immediately.
- **Fix (verify-then-refresh):**
  1. **Verify the write actually landed via the MCP.** Call `lacerte_client_read` or `lacerte_table_read` (whichever covers the field you wrote) against the same client and field. If the MCP read returns the new value, the write succeeded and the Lacerte UI is just stale — proceed to step 2. If the read still shows the old value, the write itself failed silently (escalate — check the wire log when `LACERTE_DEBUG_WIRE=1`).
  2. **Refresh the Lacerte UI.** In Lacerte, **File → Close Client** and reopen the client, or restart Lacerte. The editor will reread from disk and surface the new value.

### `lacerte_list_datapaths` returns an empty list on a known-good install

- **Symptom:** The discovery tool returns `{ datapaths: [] }` even though `C:\Lacerte\24Data` etc. exist.
- **Diagnosis:** `LACERTE_DATA_ROOT` is not set and the install isn't under the `C:\Lacerte` default. Or worse — code on this box was written assuming `LACERTE_DATAPATH` (no underscore between `DATA` and `PATH`); the **real** env var is `LACERTE_DATA_PATH`. Reading the wrong name silently falls through to discovery.
- **Fix:** Set `LACERTE_DATA_ROOT=C:\path\to\your\Lacerte\root` in the MCP client config and restart. For per-context defaults, set `LACERTE_DATA_PATH=C:\Lacerte\24Data` — **note the underscore**.

### `lacerte-mcp doctor` reports ODBC DSN missing on a system with the SDK installed

- **Symptom:** Probe 4 fails: "ODBC DSN `LacerteDSII` not found in registry."
- **Diagnosis:** The Lacerte SDK installer registers the DSN under the 64-bit ODBC hive (`HKLM\SOFTWARE\ODBC\ODBC.INI\LacerteDSII`). 32-bit `odbcad32.exe` (the one in `C:\Windows\SysWOW64`) won't see it.
- **Fix:** Confirm via the 64-bit `odbcad32.exe` at `C:\Windows\System32\odbcad32.exe` → "System DSN" tab. If absent, reinstall the 64-bit Lacerte SDK MSI.

### Writes return `UnknownFieldAccess (9012)` after running `LacertMCP.FieldAccessDumper.exe`

- **Symptom:** The standalone introspector produced fresh per-module shards under `Schema/FieldAccessMap/base/`, but writes still fail with `9012 UnknownFieldAccess`.
- **Diagnosis:** The server caches the shards at startup and does NOT re-scan disk on every write. A naive fix would be to restart the server — but that invalidates the Intuit 24h auth ticket and forces the 5-minute consent ceremony.
- **Fix:** Call the `lacerte_reload_schema` MCP tool. It drops the in-process `FieldAccessMap` caches so fresh shards take effect immediately; the cached AppID + auth ticket are preserved. Returns `{ modulesReloaded, overlaysReloaded, schemaRoot }`. Do **not** restart the server for this purpose.

## Reference

* Source spec: [Lacerte_MCP_Structure_Spec.md](Lacerte_MCP_Structure_Spec.md)
* Operating system for this codebase: [CLAUDE.md](CLAUDE.md)
* Architecture: [ARCHITECTURE.md](ARCHITECTURE.md)
* Decisions log: [DECISIONS.md](DECISIONS.md)
* Acceptance criteria: [ACCEPTANCE_CRITERIA.md](ACCEPTANCE_CRITERIA.md)
* Regression checklist (incl. manual live-mode verification): [REGRESSION_CHECKLIST.md](REGRESSION_CHECKLIST.md)
* Build sequence + remaining work: [Todo.md](Todo.md)
* Research outputs: [docs/research/agents/](docs/research/agents/)
