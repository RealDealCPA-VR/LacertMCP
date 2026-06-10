# Lacerte MCP — Instruction Manual

A complete reference for installing, configuring, and operating the Lacerte MCP server. Written for the tax professional or developer encountering the project for the first time. Self-contained: you should not need to read any other document to install, configure, and use every feature.

> Conventions used throughout this manual: code blocks use language tags; tool names are exact (`lacerte_*` for MCP tools, `lacerte-mcp` for CLI subcommands); paths use forward slashes (Windows accepts these in JSON config files — see Section 6).

---

## Table of contents

- [0. Quick reference card](#0-quick-reference-card)
- [1. What is Lacerte MCP](#1-what-is-lacerte-mcp)
- [2. Who is it for](#2-who-is-it-for)
- [3. Prerequisites and system requirements](#3-prerequisites-and-system-requirements)
- [4. Installation](#4-installation)
  - [4a. Try-it-in-simulation](#4a-try-it-in-simulation)
  - [4b. Production install](#4b-production-install)
- [5. First five minutes](#5-first-five-minutes)
- [6. Configuration in your MCP client](#6-configuration-in-your-mcp-client)
- [7. Tool catalog](#7-tool-catalog)
  - [7a. Session and context](#7a-session-and-context)
  - [7b. Read](#7b-read)
  - [7c. Write](#7c-write)
  - [7d. Create](#7d-create)
  - [7e. Discovery and metadata](#7e-discovery-and-metadata)
  - [7f. Schema management](#7f-schema-management)
  - [7g. Observability](#7g-observability)
  - [7h. Operator accelerators](#7h-operator-accelerators)
- [8. Prompts catalog](#8-prompts-catalog)
- [9. CLI reference](#9-cli-reference)
- [10. Environment variables](#10-environment-variables)
- [11. Common workflows](#11-common-workflows)
- [12. Error reference](#12-error-reference)
- [13. Wire conventions cheat sheet](#13-wire-conventions-cheat-sheet)
- [14. Best practices](#14-best-practices)
- [15. Two modes: simulation vs live](#15-two-modes-simulation-vs-live)
- [16. Operational notes](#16-operational-notes)
- [17. Troubleshooting](#17-troubleshooting)
- [18. Architecture overview](#18-architecture-overview)
- [19. Where to go next](#19-where-to-go-next)
- [20. Glossary](#20-glossary)

---

## 0. Quick reference card

```text
SERVER BINARY  (the MCP server itself, registered with your host)
  src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe

CLI BINARY     (operator utilities — register, login, status, doctor, tools)
  src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe

KEY CLI COMMANDS
  lacerte-mcp status                # show registration + mode (no wire I/O)
  lacerte-mcp doctor                # 13-probe install verification
  lacerte-mcp tools --names-only    # list every MCP tool by reflection
  lacerte-mcp register              # mint AppID + Intuit consent ceremony
  lacerte-mcp login                 # refresh the 24-hour Intuit auth ticket
  lacerte-mcp unregister --confirm  # wipe persisted AppID

MCP CLIENT CONFIG FILES
  Claude Desktop : %APPDATA%/Claude/claude_desktop_config.json
  Cursor         : ~/.cursor/mcp.json
  Windsurf       : Settings -> MCP servers

MINIMAL LIVE-MODE CONFIG BLOCK
  { "command": "C:/LacertMCP/server/LacertMCP.Server.exe",
    "env": { "LACERTE_LIVE": "1",
             "LACERTE_DATA_PATH": "C:/Lacerte/24Data",
             "LACERTE_DEFAULT_TAX_YEAR": "2024" } }

MINIMAL SIMULATION-MODE CONFIG (no Lacerte install needed)
  { "command": "C:/path/to/LacertMCP.Server.exe",
    "env": { "LACERTE_SIMULATION": "1" } }

FIRST CALL AFTER INSTALL
  /lacerte_session_status   (Claude Desktop prompt picker; or just ask "show me the lacerte session status")

LOG FILE (when LACERTE_DEBUG_WIRE=1)
  ./logs/lacerte-YYYYMMDD.log  (override with LACERTE_DEBUG_LOG_PATH)

HEARTBEAT FILE
  %TEMP%/lacerte-mcp-heartbeat-{pid}.json   (30s cadence by default)
```

---

## 1. What is Lacerte MCP

Lacerte MCP is a local Windows MCP server that exposes Intuit Lacerte tax software (tax years 2018 and later) as tools an LLM can call. It wraps the Lacerte SDK — the `LacerteSDK_Access` COM/.NET assembly and the `LacerteDSII` ODBC driver — and surfaces 42 typed tools over JSON-RPC/stdio.

In plain English: when you connect Claude Desktop (or Cursor, Windsurf, or any other stdio-compatible MCP host) to this server, the model gains the ability to read clients, dependents, states, forms, e-file statuses, and detail rows out of your Lacerte install, and to write individual fields, append detail rows, and create new clients. Two modes are supported: `live` (real SDK against your real Lacerte install) and `simulation` (an in-memory store seeded with 8 sample clients, default off-Windows). Simulation mode is observationally identical to live for the happy path, so most exploration, testing, and demos can happen anywhere — no Lacerte install required.

What the MCP does **not** do (call this out up front to avoid a triage loop with the LLM):

- **No e-filing actions.** `lacerte_ef_status` reads status; submitting / retransmitting / correcting an e-file still happens in the Lacerte UI.
- **No printing or PDF rendering.** Use Lacerte's File → Print or Print to PDF for rendered output.
- **No organizer, client portal, letter template, or diagnostics-detail access.** Those live in non-DBF Lacerte surfaces this MCP doesn't wire up.
- **Single-operator only.** Don't point two MCP servers at the same `LACERTE_DATA_PATH` simultaneously — there's no concurrency control.
- **No archive / closed-copy access.** The MCP sees the live working set under `{YY}Tax\{Module}Data`, not Lacerte's archive folders.
- **No live UI refresh.** Writes land on disk; the Lacerte editor only rereads on client close/reopen.

---

## 2. Who is it for

The primary operator is a tax professional running Lacerte locally on Windows who wants to drive Lacerte from an LLM-powered assistant for:

1. Reading client information without opening Lacerte and clicking through screens.
2. Bulk-updating fields across many clients (mailing addresses, EF flags, preparer assignments).
3. Bootstrapping new clients from a CSV or an upstream system (Tax Organizer responses).
4. Watching e-file status across the firm.
5. Pulling tax-detail context into review chats without manual lookup.

A secondary audience is the developer wiring this MCP into a custom assistant or stdio host. The same surface serves both: a typed tool catalog, structured error envelopes, and CLI utilities for setup verification.

The product bar is **"works flawlessly on the operator's box,"** not "ships to thousands of firms." See `REQUIREMENTS.md` for the full operator profile and capabilities matrix.

---

## 3. Prerequisites and system requirements

Live mode:

- **Operating system:** Windows 10 / 11 / Server 2019+ (64-bit). Windows is mandatory in live mode; the Lacerte SDK is local-only.
- **Lacerte install:** Intuit Lacerte tax year 2018 or later, completed first-run setup. The MCP cannot bootstrap an uninitialized Lacerte.
- **Lacerte SDK MSI:** the **64-bit** MSI from [Intuit's developer portal](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started). The 32-bit MSI has a known INSERT crash and is **not supported**. See `DECISIONS.md` 2026-05-30 "64-bit host process and 64-bit Lacerte SDK MSI required".
- **.NET Framework 4.8 runtime** (preinstalled on supported Windows builds).
- **MCP host:** Claude Desktop, Cursor, Windsurf, opencode, or any stdio-capable MCP client.

Simulation mode (for development / off-Windows demo):

- macOS, Linux, or Windows. No Lacerte install required.
- The .NET runtime to execute `LacertMCP.Server.exe` (shipped self-contained in the release zip).

For building from source:

- .NET 8 SDK (used to build the net48 target). On this codebase the user-local `$env:USERPROFILE\.dotnet\dotnet.exe` is required because the system-wide `dotnet` is runtime-only. PowerShell wrappers under `scripts/` resolve this automatically.

---

## 4. Installation

You can install three ways:

1. **Simulation only** (any OS, no Lacerte install) — Section 4a.
2. **Production install** (Windows + Lacerte + SDK) — Section 4b.
3. **Build from source** (developer iteration) — see `README.md` "Quick start (dev / simulation mode)" and `scripts/build.ps1`.

### 4a. Try-it-in-simulation

You don't need Windows or a Lacerte install to kick the tires. The server ships with an in-memory 8-client seed fixture (`src/LacertMCP.Server/Session/Fixtures/SeedData.cs`).

**Step 1.** Obtain a built server binary. Either run `./scripts/build.ps1` (PowerShell) or download a release zip and unpack it. The binary you want is `LacertMCP.Server.exe`.

**Step 2.** Add a sim-mode block to your MCP host's config. The Claude Desktop variant lives at `%APPDATA%/Claude/claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "lacerte": {
      "command": "C:/path/to/LacertMCP.Server.exe",
      "env": {
        "LACERTE_SIMULATION": "1"
      }
    }
  }
}
```

**Step 3.** Restart Claude Desktop (or your host). Type `/` and confirm that `lacerte_client_intake`, `lacerte_review_ef_status`, `lacerte_field_write_bulk`, and `lacerte_csv_intake` appear in the prompt picker.

**Step 4.** Drive the four seeded datapaths. Type these prompts verbatim:

| Prompt | What the LLM calls | Seeded ClientIDs returned |
| --- | --- | --- |
| "Set my Lacerte context to 2020 Individual at `C:\Lacerte\20Tax\IData`, then list every client." | `lacerte_set_context` + `lacerte_client_list` | `SMIT`, `JONE`, `REJE` |
| "Switch to 2024 Individual at `C:\Lacerte\24Tax\IData`, then list every client." | `lacerte_set_context` + `lacerte_client_list` | `BROW`, `PEND` |
| "Switch to 2024 Corporate at `C:\Lacerte\24Tax\CData`, then list every client and show me the state rows for ACME." | `lacerte_set_context` + `lacerte_client_list` + `lacerte_state_read` | `ACME` (CA + NY), `WIDG` (TX) |
| "Switch to 2024 Partnership at `C:\Lacerte\24Tax\PData`, then list every client and read PART's subclients." | `lacerte_set_context` + `lacerte_client_list` + `lacerte_subclient_read` | `PART` (3 partners) |

All 8 seeded ClientIDs (`SMIT`, `JONE`, `REJE`, `BROW`, `PEND`, `ACME`, `WIDG`, `PART`) live across these 4 sim datapaths. Anything that works here works against a real Lacerte install once you flip `LACERTE_LIVE=1`.

### 4b. Production install

Five steps. Each must succeed before moving on.

**1. Install Intuit Lacerte** (tax year 2018 or later) and complete its first-run setup on the operator's box.

**2. Install the 64-bit Lacerte SDK MSI** from [Intuit's developer portal](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started). Confirm via the 64-bit `odbcad32.exe` (under `C:\Windows\System32`) that the `LacerteDSII` DSN appears under "System DSN".

**3. Stage the MCP binaries.** Run `./scripts/release.ps1` to produce `dist/LacertMCP-v0.1.0/LacertMCP-v0.1.0.zip`. Unzip on the operator box (e.g. to `C:\LacertMCP\`). Confirm `App.config` sits next to `LacertMCP.Server.exe` — it carries the mixed-mode CLR shim required by the Lacerte SDK. **Do not delete or modify `App.config`.**

**4. Verify the install with the doctor command:**

```powershell
& C:\LacertMCP\cli\lacerte-mcp.exe doctor
```

Exit 0 means every probe is green. Doctor probes cover OS / runtime (Windows, x64, .NET 4.8, App.config shim), install (ODBC DSN, Lacerte SDK install dir, Lacerte COM ProgID), config (operating mode, `LACERTE_DATA_PATH`, `LACERTE_DATA_ROOT`, FieldAccessMap shards), and identity (AppID GUID). Canonical list lives in `src/LacertMCP.Cli/Commands/DoctorCommand.cs`.

Exit codes (see `DoctorCommand.cs` XML doc):
- **0** — every probe green, no warnings
- **1** — at least one probe failed
- **2** — PARTIAL: at least one probe warned (advisory; remediation printed) or skipped (typical off-Windows), no fails
- **3** — not used by doctor (reserved for "not implemented here")

**5. Run the one-time registration ceremony:**

```powershell
& C:\LacertMCP\cli\lacerte-mcp.exe register --app-name "Operator Lacerte MCP" --tax-year 2024
```

This mints (or reuses) the AppID GUID, persists it to Windows Credential Manager, then runs the **real Intuit consent ceremony** via `ITPRegistration.Register()` (`Bridge/Registration.cs`) — watch the desktop for the consent + Intuit sign-in dialogs. Exit **0** means `AccessGranted` and the 24-hour sign-in ticket is live; exit **1** means the ceremony ran but did not grant access (or threw); exit **2** means PARTIAL — the Lacerte SDK COM surface is unavailable on this box (AppID persisted; install the 64-bit SDK MSI and re-run). Full exit-code table in Section 9. The end-to-end ceremony against a real Intuit dialog remains pending verification on a Windows + Lacerte box (see `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification").

Within 24 hours of a successful registration, set `LACERTE_LIVE=1` in your MCP client config — the server will silently re-attach to the cached session. After 24 hours, run `lacerte-mcp login` to refresh.

---

## 5. First five minutes

Once Claude Desktop / Cursor / Windsurf can see the `lacerte` server, walk this three-step verification:

**1.** Type: **"Show me the Lacerte data folders on this machine."**
→ The LLM calls `lacerte_list_datapaths` and returns the discovered `{YY}Tax\{Module}Data` folders under `LACERTE_DATA_ROOT` (default `C:\Lacerte`).

**2.** Type: **"Set my active context to 2024 Individual at C:\\Lacerte\\24Tax\\IData."**
→ The LLM calls `lacerte_set_context(dataPath, taxYear, module)`.

**3.** Type: **"List my first 10 clients."**
→ The LLM calls `lacerte_client_list(limit=10)`.

A successful response in Claude Desktop's tool-call expander looks like:

```json
{
  "success": true,
  "result": {
    "rows": [{ "clientNumber": "1001", "C1_1": "...", "C1_2": "..." }],
    "total": 2,
    "limit": 10,
    "offset": 0,
    "hasMore": false,
    "nextOffset": null
  },
  "cid": "a1b2c3d4"
}
```

If the first call returns `success: false` with `statusCode: 9007 LoginExpired` (proactive probe) or raw SDK `statusCode: 203 NotSignedIn` (reactive path), your 24-hour ticket expired — run `lacerte-mcp login` from PowerShell and retry. If the call returns `statusCode: 9006 AppNotRegistered`, run `lacerte-mcp register` first.

The `cid` (8-hex correlation ID) appears on every response. Use it to grep the wire log (`logs/lacerte-YYYYMMDD.log` when `LACERTE_DEBUG_WIRE=1`) for the full causal chain of a tool call.

---

## 6. Configuration in your MCP client

The server binary is `LacertMCP.Server.exe`; the CLI binary is `lacerte-mcp.exe`. Only the server is registered with your MCP host. (A frequent mistake: pointing the host at `lacerte-mcp.exe` instead.)

### Claude Desktop

File: `%APPDATA%/Claude/claude_desktop_config.json`

Live mode (Windows + Lacerte + SDK):

```json
{
  "mcpServers": {
    "lacerte": {
      "command": "C:/LacertMCP/server/LacertMCP.Server.exe",
      "env": {
        "LACERTE_LIVE": "1",
        "LACERTE_DATA_PATH": "C:/Lacerte/24Data",
        "LACERTE_DEFAULT_TAX_YEAR": "2024",
        "LACERTE_APP_NAME": "Operator Lacerte MCP",
        "LACERTE_DEBUG_WIRE": "0"
      }
    }
  }
}
```

Simulation mode (no Lacerte install required):

```json
{
  "mcpServers": {
    "lacerte": {
      "command": "C:/LacertMCP/server/LacertMCP.Server.exe",
      "env": {
        "LACERTE_SIMULATION": "1"
      }
    }
  }
}
```

> Tip: Windows accepts forward slashes in JSON paths. `"C:/LacertMCP/..."` works and avoids the double-backslash gotcha when hand-editing.

### Cursor

File: `~/.cursor/mcp.json` (same JSON shape as Claude Desktop). After saving, reload the window; `@lacerte` should surface the tools.

### Windsurf

Open Settings → MCP servers. Paste the same JSON block. Restart Windsurf.

### Generic stdio MCP host

```yaml
command: C:/path/to/LacertMCP.Server.exe
transport: stdio
env:
  LACERTE_LIVE: "1"
  LACERTE_DATA_PATH: C:/Lacerte/24Data
```

Sim variant:

```yaml
command: C:/path/to/LacertMCP.Server.exe
transport: stdio
env:
  LACERTE_SIMULATION: "1"
```

To switch from live to sim mode, drop `LACERTE_LIVE` + `LACERTE_DATA_PATH` and add `LACERTE_SIMULATION=1`.

---

## 7. Tool catalog

The server registers 42 MCP tools (the authoritative categorization is `HelpTools.ToolsByCategory()`, kept in lock-step with the registered surface by a bidirectional parity test — `lacerte_help(topic: "tools")` reports the same 42). Each tool method is decorated with `[McpServerTool]` and lives under `src/LacertMCP.Server/Tools/` (plus `Prompts/PromptsCatalogTool.cs` for `lacerte_prompts`). The exact tool name on the wire is the snake_case form of the C# method name (e.g. `LacerteClientList` → `lacerte_client_list`). Discovery happens via `WithToolsFromAssembly()` in `Program.cs`.

Every tool returns one of two JSON envelope shapes (see `Errors/WrapToolHandler.cs`):

```json
{ "success": true,  "result": { /* tool payload */ }, "cid": "a1b2c3d4" }
{ "success": false, "statusCode": 9012, "statusName": "UnknownFieldAccess",
  "origin": "synthetic", "severity": "error",
  "userMessage": "...", "remediation": "...",
  "context": { /* structured retry hints, redacted */ }, "cid": "a1b2c3d4" }
```

Tools are organized below by intent, not alphabetically.

### 7a. Session and context

These tools touch no SDK wire — they read in-memory session state, the static schema catalog, or the local filesystem. Source: `Tools/SessionTools.cs`, `Tools/HealthTools.cs`.

#### `lacerte_session_status`

Returns mode, registration state, 24h expiry, active context, debug-log state, and a `nextSteps` array of bootstrap hints. Call this **first** on every fresh session.

- Parameters: none.
- Response: `{ mode, isRegistered, registeredAt, expiresAt, minutesUntilExpiry, context: { dataPath, taxYear, module, readOnly }?, appName, appId, debugWireEnabled, wireLogPath, nextSteps[] }`. `mode` is lowercase (`"simulation"` or `"live"`).
- Example:
  ```json
  { "name": "lacerte_session_status", "arguments": {} }
  ```

#### `lacerte_set_context`

Sets the active (dataPath, taxYear, module) triple used by every subsequent read/write tool. Validates `taxYear >= 2018` and `module` in `I/C/P/S/F/R/T/N/B`.

- Required: `dataPath: string` (absolute path), `taxYear: int`, `module: string` (length 1).
- Optional: `readOnly: bool = false` — when true, every write tool short-circuits to 9001 (`FieldIsReadOnly`) before any side effect.
- Response: `{ dataPath, taxYear, module, readOnly }`.
- Example:
  ```json
  { "name": "lacerte_set_context",
    "arguments": { "dataPath": "C:\\Lacerte\\24Tax\\IData",
                   "taxYear": 2024, "module": "I", "readOnly": false } }
  ```

#### `lacerte_can_use_new_apis`

Returns whether the (taxYear, module) pair supports the TY21+ `UpdateTaxReturn` API path. Cached per pair.

- Required: `taxYear: int`, `module: string` (length 1).
- Response: `{ taxYear, module, canUseNewApis, apiPath }` (`apiPath` ∈ `"UpdateTaxReturn"` / `"WriteToDBFDatabase"`).

#### `lacerte_list_modules`

Returns the 9-module Lacerte catalog (I, C, P, S, F, R, T, N, B) with display names and ODBC ReturnType strings. Static schema; zero wire I/O.

- Parameters: none.
- Response: `{ count, rows: [{ module, name, returnType }], modules: <same as rows, v0.1 alias> }`.

#### `lacerte_list_datapaths`

Discovers Lacerte data paths under the install root by scanning for `{YY}Tax\{Module}Data` folders. Root resolves from `LACERTE_DATA_ROOT`, falling back to `C:\Lacerte`.

- Parameters: none.
- Response: `{ root, rootExists, count, rows: [{ path, taxYear, module, moduleName }], datapaths: <alias>, hint? }`. `hint` is non-null when the scan returns zero rows.

#### `lacerte_health`

Health probe. Returns `{ mode, status }` plus `{ heartbeatPath, heartbeatSeconds }` when `LACERTE_HEARTBEAT_SECONDS > 0`. `status` is `"reachable"` on the happy path. Used to confirm the MCP stdio plumbing and SDK runtime shim are functional.

- Parameters: none.

### 7b. Read

Read tools delegate to `LacerteSessionManager` which dispatches to either `SimulationStore` (sim mode) or the ODBC `LacerteDsnReader` (live mode). Live mode is strictly reads-only — all mutations go through the COM bridge (Boundary Invariant #6 in `ARCHITECTURE.md`). All read tools accept pagination (`limit`, `offset`) and a `CancellationToken`.

#### `lacerte_client_list`

Lists clients in the active context. Optional case-insensitive substring filter on the conventional name fields — Individual modules match against both first name (C1_1) and last name (C1_2); non-Individual modules match against the entity name (C1_1).

- Optional: `nameFilter: string?`, `limit: int? = 100` (4-state contract: `null` = default 100; `0` = count-only with empty rows; positive up to 1000; negative rejected), `offset: int? = 0`.
- Response: `{ rows, total, limit, offset, hasMore, nextOffset, count, clients }` (last two are v0.1 aliases, removed v0.2).

#### `lacerte_client_read`

Reads one client by ClientNumber from DATA1.

- Required: `clientNumber: string`.
- Response: `{ clientNumber, found, client, hint? }` — envelope is never null. Check `found` (or `client != null`), not the top-level result. `hint` is populated when the client is not present.

> **ClientNumber vs ClientID:** ClientNumber is the SDK-minted identifier (sim's monotonic counter, e.g. `"1001"`); ClientID is the operator-chosen 8-char identifier (e.g. `"BROW"`). They are **not** interchangeable. To resolve a name to a ClientNumber, use `lacerte_client_find`.

#### `lacerte_client_find`

Resolves a plain-English client name to ClientNumber(s) in the active context. Returns a disambiguation envelope shaped for an LLM picker, with a Levenshtein-based `didYouMean` fallback when the match count is 0.

- Required: `name: string` (non-empty).
- Optional: `limit: int? = 20` (max 100, positive only).
- Response: `{ matches: [{ clientNumber, clientId, firstName, lastName, email }], total, truncated, didYouMean? }`.

#### `lacerte_client_query`

Server-side filtered search over one DATA1..DATA7 table in the active context — the token-economical way to answer roster-wide questions ("all clients missing an email", "every DATA1 row where C1_5 is empty"). The filter runs server-side and only matching rows are returned, so prefer this over paging `lacerte_client_list` / `lacerte_table_read` through the context window whenever you need a subset defined by a field predicate. Use `lacerte_client_list` for plain enumeration and `lacerte_client_find` for name-to-ClientNumber resolution.

- Required: `fieldName: string` (a row key exactly as `lacerte_table_read` returns it — a FoxPro DBF column code like `C1_3`, or one of the injected camelCase meta keys `clientNumber` / `clientId` / `subClientId` / `stateAbbrev`; case-sensitive), `op: string` (one of `eq` / `ne` / `contains` / `startsWith` / `empty` / `notEmpty`, case-insensitive; the string ops compare with OrdinalIgnoreCase, `empty` matches missing OR null OR `""` and ignores `value`).
- Optional: `value: string = ""` (ignored for `empty`/`notEmpty`), `table: int = 1` (1-7; DATA8 rejected), `limit: int = 100`, `offset: int = 0` (pagination applies to the *filtered* set).
- Response: `{ filter: { table, fieldName, op, value }, rows, total, limit, offset, returnedCount, hasMore, nextOffset }` — `rows` carry the same shape as `lacerte_table_read`; `total` is the matched count across the full table (not the page size, not the table size).

#### `lacerte_table_read`

Generic DBF table reader for DATA1..DATA7. Optional `clientNumber` + `stateAbbrev` filters narrow the result. TY21+ branching is handled by the session manager; the tool just passes the table number through.

- Required: `table: int` (1-7; 8 reserved/rejected).
- Optional: `clientNumber: string?`, `stateAbbrev: string?`, `limit: int = 100`, `offset: int = 0`.
- Response: `{ table, clientNumber, stateAbbrev, rows, total, limit, offset, hasMore, nextOffset, count }`.

Tables: 1 = Client, 2 = SubClient, 3 = State, 4 = StateSubClient, 5 = Status (F4), 6 = Display (read-only), 7 = Forms (read-only).

#### `lacerte_subclient_read`

Reads DATA2 subclient rows for one client. Subclients are dependents (Individual), partners (Partnership), shareholders (S-Corp), or beneficiaries (Fiduciary).

- Required: `clientNumber: string`.
- Optional: `limit: int = 100`, `offset: int = 0`.
- Response: `{ clientNumber, rows, total, limit, offset, hasMore, nextOffset, count, subclients }`.

#### `lacerte_state_read`

Reads DATA3 state rows for one client. Each row carries state-level overrides (apportionment, addbacks, residency).

- Required: `clientNumber: string`.
- Optional: `stateAbbrev: string?`, `limit: int = 100`, `offset: int = 0`.
- Response: `{ clientNumber, stateAbbrev, rows, total, ..., states }`.

#### `lacerte_state_subclient_read`

Reads DATA4 state-subclient rows for one client. Each row is a per-state override on a subclient (e.g. partner-level state apportionment). Generates the largest result sets in the API for multistate partnerships.

- Required: `clientNumber: string`.
- Optional: `stateAbbrev: string?`, `limit: int = 100`, `offset: int = 0`.
- Response: `{ clientNumber, stateAbbrev, rows, total, ..., stateSubclients }`.

#### `lacerte_status_read`

Reads DATA5 F4-status rows for one client. Surfaces per-client status indicators (completed, e-file ready, locked).

- Required: `clientNumber: string`.
- Optional: `limit: int = 100`, `offset: int = 0`.
- Response: `{ clientNumber, rows, total, ..., statuses }`.

#### `lacerte_forms_read`

Reads DATA7 form rows for one client. Returns the list of forms attached to the return.

- Required: `clientNumber: string`.
- Optional: `limit: int = 100`, `offset: int = 0`.
- Response: `{ clientNumber, rows, total, ..., forms }`.

#### `lacerte_detail_read`

Reads detail rows for a client from the per-client `{CLIENT}.{Mod}{YY}` file in original row order. Detail files are unbounded by design — paging is the canonical defense against MCP message-size limits.

- Required: `clientNumber: string`.
- Optional: `limit: int = 100`, `offset: int = 0`, `series: int?` (numeric Lacerte Series identifier — 11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners).
- Response: `{ clientNumber, rows, total, limit, offset, hasMore, nextOffset, count }`.

#### `lacerte_ef_status`

Reads per-client e-file status by joining EFilings and EFStatuses in the active context. **TY19+ only.**

- Optional: `sinceUtc: string?` (ISO-8601: `yyyy-MM-dd` / `yyyy-MM-ddTHH:mm:ss` / `yyyy-MM-ddTHH:mm:ssZ`), `limit: int = 100`, `offset: int = 0`.
- Response: `{ rows, total, limit, ..., efStatuses }`. Flat row fields: `EFilingId, ClientId, EFId, InstanceId, Status, StatusDate`.

Bad `sinceUtc` parse throws `9017 InvalidArgument` (not 9013) with `{ argument, value, reason }` context.

### 7c. Write

Write tools route through `LacerteSessionManager.WriteField` which:
1. Enforces the session `readOnly` flag (throws 9001 if set).
2. Calls `assertWritable(...)` against the access map (throws 9012 / 9001 / etc.).
3. Year-routes to `WriteToDBFDatabase` (pre-TY21) or `UpdateTaxReturn` (TY21+).
4. Returns a structured TAPIStatus result.

#### `lacerte_field_write`

Writes a single field value to a DATA1..DATA5 row. DATA6/DATA7 are read-only per spec and rejected with `ExposedForRead`. This is the primary write entrypoint for all years; `lacerte_update_return` is a thin alias.

- Required: `table: int` (1-5), `clientNumber: string`, `field: string` (e.g. `C1_1`, `C2_15`, `C4_G3`), `value: object?` (JSON-serializable scalar — string, number, or boolean).
- Optional: `subClientId: int?` (required for DATA2/4), `stateAbbrev: string?` (required for DATA3/4), `dryRun: bool = false`, `idempotencyKey: string?` (strongly recommended).
- Response: `{ statusCode, statusName, table, clientNumber, field, dryRun }`.

> **Idempotency keys:** Pass a stable string keyed on op+target+intent (e.g. `"field_write-1001-C1_1"`). Same key + same payload returns the cached result for up to 24 hours; same key + different payload throws `9102 IdempotencyConflict`. The payload fingerprint includes the active session context (dataPath, taxYear, module) and the `dryRun` flag — reusing a key after a `lacerte_set_context` switch or across a dry-run/commit pair is a loud 9102, never a silent replay. Only **Success** results are cached; a failed write retried under the same key re-executes. (Full semantics in Section 16.) Without an idempotency key, an MCP transport timeout + LLM auto-retry re-executes the write (safe at the cell level for `field_write`, but the LLM cannot verify whether the first call landed).

#### `lacerte_update_return`

Alias for `lacerte_field_write`, kept to mirror Intuit's TY21+ `UpdateTaxReturn` developer-doc surface. Behavior is identical — both funnel through the same session chokepoint, and the session year-routes the backend call. Prefer `lacerte_field_write` in new code; only use this when a workflow explicitly references the `UpdateTaxReturn` SDK API by name.

- Parameters: same as `lacerte_field_write`.

#### `lacerte_detail_write`

Appends a detail row, or replaces the row at `replaceIndex` if supplied. Runs **mandatory client-side validation** before the session call — the SDK does not validate detail-row shape, and a malformed row silently corrupts the return.

- Required: `clientNumber: string`, `series: string` (numeric, e.g. `"11"` = W-2), `code: string` (numeric, leading minus allowed), `amount: double?`, `description: string`.
- Optional row-key segments (all default to empty): `prefix`, `suffix`, `stateAbbrev` (validated as `^[A-Z]{2}$` when non-empty), `cityAbbr`, `source`, `tsj` (one of T/S/J or empty), `importSrc`.
- Optional: `replaceIndex: int?`, `dryRun: bool = false`, `idempotencyKey: string?`.
- Response: `{ statusCode, statusName, clientNumber, replaceIndex, dryRun }`.

> **Critical:** in live mode, passing `replaceIndex` throws `9019 LiveReplaceIndexUnsupported`. The Lacerte SDK's `WriteToDetailFile` only appends; there is no documented update-by-index path. To correct a row: read existing rows via `lacerte_detail_read`, append the corrected row, then delete the stale one (delete-tool ships in a future phase). Without the idempotency key, an MCP transport retry will **append a duplicate row** — silently doubling a W-2/1099/dependent.

#### `lacerte_field_write_bulk`

Fans out N field writes through one MCP call. Iterates the same `WriteField` chokepoint per entry (DATA6/7 still rejected, access-map gate still applied, dry-run undo still honored, idempotency-lock still serialized per key). Aggregates per-row outcomes into `{ totalCount, successCount, errorCount, perItem[] }`.

- Required: `table: int`, `entries: BulkWriteEntry[]` where each entry is `{ clientNumber, field, value, subClientId?, stateAbbrev? }`.
- Optional preflight assertions: `taxYear: int?` (must match active context — throws 9016 on drift), `readOnly: bool?` (must match active context — throws 9016 on drift), `expectedCount: int?` (must equal `entries.Length` — throws 9021 on mismatch).
- Optional: `dryRun: bool = false`, `continueOnError: bool = false`, `idempotencyKey: string?` (used as a prefix; per-row key becomes `"{prefix}-{index}-{clientNumber}-{field}"` — **positional**, so a batch retry must resubmit the same `entries[]` in the same order; repeated client+field pairs in one batch never alias).
- Limits: entries capped at 500 by default (override via `LACERTE_BULK_WRITE_MAX`); exceeding the cap throws `9020 BulkBatchTooLarge` before any row runs. Honors MCP cancellation between rows (partial envelope with `stoppedEarly: true` + `processedCount`).

> **Preview/commit prefixes:** dry-run results ARE stored in the idempotency cache (`dryRun` is part of the fingerprint), so the commit pass MUST use a different prefix than the preview pass. Convention: preview with `bulk-<batchId>-preview`, commit with `bulk-<batchId>`. Reusing the preview prefix on the commit fails every previewed row with `9102 IdempotencyConflict`. The envelope's `recoveryHint` repeats this after a dry-run pass.

#### `lacerte_detail_write_bulk`

Bulk-appends (or sim-only replaces, per-entry via `replaceIndex`) detail rows across one or many clients in one MCP call — the canonical tool for intake workflows that seed many W-2 / 1099 / dependent rows instead of looping `lacerte_detail_write` per row. Runs the same **mandatory client-side row validation** per entry before the session call (bad rows fail with `9008 DetailWriteInvalid`), and iterates the existing detail-write chokepoint — read-only rejection, dry-run undo, per-key idempotency lock, and the live `9019` replaceIndex rejection all still apply per row.

- Required: `entries: BulkDetailEntry[]` where each entry mirrors the single detail-write params — `{ clientNumber, series, code, amount, description }` required, plus the 7 optional row-key segments (`prefix`, `suffix`, `stateAbbrev`, `cityAbbr`, `source`, `tsj`, `importSrc`, all defaulting to empty) and optional `replaceIndex`.
- Optional: `dryRun: bool = false`, `continueOnError: bool = false` (default stops at the first failure and marks the remainder skipped), `idempotencyKey: string?` (used as a prefix; per-row key becomes `"{prefix}-{index}-{clientNumber}-{series}-{code}"` — positional).
- Response: `{ requestedCount, processedCount, succeededCount, failedCount, skippedCount, stoppedEarly, rows[] }`.
- Limits: shares the 500-entry cap with `lacerte_field_write_bulk` (`LACERTE_BULK_WRITE_MAX`); exceeding it throws `9020 BulkBatchTooLarge` before any row runs. Honors MCP cancellation between rows.

> **Critical:** detail files have no primary key — a retried append without an idempotency prefix silently doubles W-2/1099/dependent amounts. Always pass a prefix; a batch retry then replays already-appended rows from cache and re-executes only failed/skipped rows. Use the `detail-<batchId>-preview` / `detail-<batchId>` prefix convention for dry-run vs commit.

### 7d. Create

#### `lacerte_client_create`

Creates a new client in the active datapath/taxYear/module. Wraps `LacerteSessionManager.CreateClient` with client-side validation gates that mirror the SDK's pre-check behavior.

- Required: `clientId: string` (≤ 8 chars, alphanumeric `^[A-Z0-9]+$`; lowercase accepted and normalized to uppercase), `taxType: string` (must match the active module — e.g. `"Individual"` or `"1040"` for I; the tool canonicalizes operator-aliased values to the spec strings before calling the SDK), `firstName: string`, `lastName: string`, `emailId: string`.
- Optional: `dryRun: bool = false`, `idempotencyKey: string?`.
- Response: `{ statusCode, statusName, clientNumber, clientId, dryRun }`.

Validation order:
1. `clientId` non-empty and ≤ 8 chars (synthetic `9002 ClientIDTooLong` otherwise).
2. `clientId` matches `^[A-Z0-9]+$` (SDK `24 InValidClientName` otherwise).
3. `taxType` non-whitespace (`14 TaxModuleMismatch` otherwise).
4. `firstName`, `lastName`, `emailId` non-whitespace (each throws `24 InValidClientName` with `field` key).
5. `taxType` accepted for the active module (`14 TaxModuleMismatch` with `acceptedTaxTypes` context).
6. Active context's `taxYear >= 2018` (synthetic `9003 TaxYearTooOld` otherwise).

Per-module accepted TaxType aliases live on `Schema.Modules.TaxTypeAliases`.

#### `lacerte_client_create_bulk`

Bulk-creates N clients in the active datapath/taxYear/module from one MCP call — the canonical tool for bootstrapping clients from a CSV roster (REQUIREMENTS.md #3) instead of looping `lacerte_client_create` per row. Each entry runs the identical single-create validation + TaxType canonicalization before hitting the session chokepoint (registration gate, read-only rejection, dry-run undo, and idempotency cache all still apply per row).

- Required: `entries: BulkCreateEntry[]` where each entry is `{ clientId, taxType, firstName, lastName, emailId }` — all five required, mirroring `lacerte_client_create`. Empty array returns a no-op envelope with `requestedCount: 0`.
- Optional: `dryRun: bool = false` (dry-run rows report `wouldCreate: true` with `clientNumber: null` — a dry run cannot mint a real ClientNumber, in both sim and live), `continueOnError: bool = false` (pass `true` to e.g. surface ALL duplicate clientIds in one pass), `idempotencyKey: string?` (used as a prefix; per-row key becomes `"{prefix}-{index}-{clientId}"` — positional).
- Response: `{ requestedCount, processedCount, succeededCount, failedCount, skippedCount, stoppedEarly, rows[] }`.
- Limits: entries capped at **100** by default — creates are heavier than field writes — override via `LACERTE_BULK_CREATE_MAX`; exceeding the cap throws `9020 BulkBatchTooLarge` before any row runs. Honors MCP cancellation between rows.

With an idempotency prefix, resubmitting the same batch replays already-created rows from cache **with their original clientNumbers** instead of minting duplicates; without one, a transport retry re-attempts every create and already-created rows fail with `12 DuplicateClient`. Use the `create-<batchId>-preview` / `create-<batchId>` prefix convention for dry-run vs commit.

### 7e. Discovery and metadata

#### `lacerte_field_access_map`

Returns the resolved field access map for `(module, table, taxYear)`. Includes the universal `C{t}_0 → ReadOnly` rule and every cataloged field for the module/table from the loaded JSON shard. The same gate runs server-side via `AssertWritable` before any write.

- Required: `module: string` (length 1), `table: int` (1-7), `taxYear: int` (≥ 2018).
- Response: `{ module, moduleName, table, taxYear, count, fields: { fieldName: accessEnum } }`. Access enum values: `ReadWrite, ReadOnly, Hidden, NotExposed, NotSupported, Unknown`.

The v1 base shards ship empty for many modules — only `C{t}_0` is cataloged ReadOnly until `LacertMCP.FieldAccessDumper.exe` populates them.

#### `lacerte_return_codes`

Returns the full Lacerte SDK return-code table: TAPIStatus 0-28 + TPSDKStatus 200-204 + synthetic 9xxx. Each row carries `code`, `name`, `severity`, `userMessage`, `remediation`, `origin` (TAPIStatus / TPSDKStatus / Synthetic), and `originGroup` (`sdk` / `synthetic` — matches the wire-envelope `origin` field for direct filtering).

- Parameters: none.
- Response: `{ count, rows, codes: <v0.1 alias> }`.

#### `lacerte_help`

Returns a distilled README — what this MCP is, what it does **not** do, modes, tool categories, env vars, first-call example, and related prompts. Pure static content; safe to call before `lacerte_set_context`.

- Optional: `topic: string?` — one of `"modes"`, `"tools"`, `"env"`, `"limits"`, `"first-call"`, `"prompts"`. Null returns the full overview. Aliases: `envvars`, `quickstart`, `overview`, etc.
- Response: depends on topic (see `Tools/HelpTools.cs`).

#### `lacerte_prompts`

Lists the 4 MCP prompts (see Section 8) and returns body text for hosts that don't expose a Prompts UI. Wrapper around `Prompts/PromptsCatalogTool.cs`.

#### CLI-only discovery: `lacerte-mcp tools`

The CLI offers a richer reflection-based tool listing without booting stdio. See Section 9.

### 7f. Schema management

#### `lacerte_reload_schema`

Drops the in-process `FieldAccessMap` caches so freshly written shards on disk take effect without restarting the server (which would invalidate the Intuit 24h auth ticket). Use this after running `LacertMCP.FieldAccessDumper.exe`.

- Parameters: none.
- Response: `{ modulesReloaded, overlaysReloaded, schemaRoot }`. Counts reflect cache entries evicted, not shards on disk. The next field-write or lookup lazily repopulates the cache from disk.

Single-load semantics remain the default; no `FileSystemWatcher` surprise reloads. This is an explicit operator-triggered reload.

### 7g. Observability

All observability tools have zero wire I/O and are safe to call pre-registration. Source: `Tools/ObservabilityTools.cs`.

#### `lacerte_recent_log`

Returns the in-process tail of WireLog records — the LLM's self-diagnostic surface for `cid`-correlated forensics. Independent of `LACERTE_DEBUG_WIRE` (the disk-log gate); the ring is always-on with a ~50KB cap (default 256 entries; override via `LACERTE_RECENT_LOG_CAPACITY`, clamped 16..4096).

- Optional: `cid: string?` (8-hex correlation ID from a prior error envelope; filter to entries matching this cid), `limit: int = 50` (newest-first; 1..256).
- Response: `{ entries: [{ ts, op, cid?, durationMs, error? }], totalSeen, droppedSinceProcessStart, capacity, filteredByCid }`.

#### `lacerte_request_stats`

Returns process-lifetime aggregate counters — uptime, total tool calls / successes / errors, per-op count + p50/p95/p99 latency, idempotency cache hits/misses/conflicts, AutoReconnect attempts/exhausted totals. Counters reset only on process restart.

- Parameters: none.

#### `lacerte_cache_state`

Snapshot of the three in-process caches the session manager owns — idempotency, new-APIs branch decisions, edition probe. Surfaces idempotency `count/cap/TTL/nearCap/peakCount` so the LLM can diagnose `9102 IdempotencyConflict` near eviction pressure, per-(taxYear, module) memoized new-APIs answers, and edition-probe state including the P14-061 cached-failure sentinel.

- Parameters: none.
- Response: `{ idempotency: { count, cap, ttlHours, nearCap, oldestCreatedAt, newestCreatedAt, peakCount, persistEnabled, persistPath }, newApis: { entries, count }, edition: { probed, sentinelFailure, edition, build, sdkVersion }, snapshotAtUtc }`.

#### `lacerte_resource_usage`

Process telemetry. Pure in-process perf counters; no wire I/O.

- Parameters: none.
- Response: `{ pid, uptimeSeconds, workingSetMB, peakWorkingSetMB, privateMemoryMB, handleCount, threadCount, gcTotalMemoryMB, gcCollections: { gen0, gen1, gen2 }, processorTimeMs, is64BitProcess, isServerGC }`.

#### `lacerte_active_locks`

Snapshot of currently-held idempotency semaphores. Lists each held write-key (8-char SHA-256 prefix), seconds held, and total held / total-tracked counts. Diagnostic surface for "tool call hangs — what's in flight?"

- Optional: `limit: int = 20` (1..500).
- Response: `{ idempotency: [{ keyHashPrefix, held, heldForSeconds }], totalLockEntries, heldCount, returnedCap, snapshotAtUtc }`.

### 7h. Operator accelerators

These tools fan out across multiple session reads to collapse common multi-step workflows into one call. Source: `Tools/ClientSummaryTools.cs`, `ClientDiffTools.cs`, `ClientResourceTools.cs`, `FirmDashboardTools.cs`, `PreFilingCheckTools.cs`, `YearOverYearTools.cs`, `FieldHistoryTools.cs`.

#### `lacerte_client_summary`

One-call snapshot of a client in the active context. Fans out `ReadClient + ReadSubclients + ReadStates + ReadStatuses + ReadForms + ReadEFStatus` in a single envelope. Replaces 5-7 round-trips for "what's the deal with client X?"

- Required: `clientNumber: string`.
- Optional: `includeDetail: bool = false` (when true, also fan out to `ReadDetail`).
- Response: `{ summaryVersion, clientNumber, found, context: { dataPath, taxYear, module, returnType }, identity: { clientId, name, firstName, lastName, email, filingStatus, taxYear, module, returnType }, counts: { subclients, statesFiled, formsCount, statusRowsSet, detailRows? }, states[], statesTruncated, flags: { completed, eFileReady, locked, hasErrors }, efStatus: { latestStatus, latestStatusDate, perStateBreakdown }, cursors }`.

#### `lacerte_diff_clients`

Compares two clients field-by-field across DATA1 (identity), DATA2 (subclients), DATA3 (states), and optionally DATA6 (detail rows). Returns **only** the rows that differ. Use for "did I import the right one?", "spot-check a proforma rollover", "how does this couple differ from their parents?". Same-context only in v1 (cross-context diff rejected with 9017). Rejects A==B with 9017.

- Required: `client1Number: string`, `client2Number: string` (must differ).
- Optional: `includeSubclients: bool = true`, `includeStates: bool = true`, `includeDetail: bool = false`, `ignoreFields: string[]?`.
- Response: `{ client1Number, client2Number, summary: { matchedFieldsCount, differingFieldsCount, ignoredCount }, dataOne: { fields: [{ field, a, b }] }, subclients, states, detail, ignoredFields[] }`.

Detail rows are compared across **all 11 `DetailRow` fields** — rows differing only in TSJ (taxpayer vs spouse), Source, Prefix, Suffix, CityAbbr, or ImportSrc now surface as differences. Rows that lack a usable diff key are counted in `rowsWithoutKey: { a, b }` instead of silently vanishing from the comparison.

#### `lacerte_export_client`

Exports one client as a portable, self-documenting envelope keyed by the `lacerte://client/{clientNumber}` URI. Mirrors the JSON shape of an MCP Resource read (promotes to a true Resource when SDK 1.x ships). The context echo pins the (datapath, year, module) the row was resolved against.

- Required: `clientNumber: string` (digits only, 1-16 chars).
- Response: `{ uri, clientNumber, found, context: { dataPath, taxYear, module }, client: { ...row... }, hint? }`.

#### `lacerte_firm_dashboard`

One-call Monday-morning firm snapshot. Fans out across every `(taxYear, module)` tuple under `LACERTE_DATA_ROOT` and returns per-tuple client counts + EF-status histogram + latest StatusDate watermark, plus a firm-level rollup. Replaces ~30 round-trips. Pure-read: the operator's current `set_context` is snapshotted and restored even on exception.

- Optional: `yearFilter: int?`, `modulesFilter: string?` (default `"ICPSFRTNB"`), `sinceUtc: string?`.

Per-tuple errors are surfaced on that row's `error` field — one bad datapath does not poison the aggregate. The whole snapshot-set-restore span is serialized against `lacerte_year_over_year` via a shared internal gate (the two fan-out tools can't interleave context mutations across awaits).

#### `lacerte_pre_filing_check`

Pre-filing readiness check. Runs a catalog of rules against one client in the active context and returns whether the return is ready to e-file. **Covers the four modules a solo practitioner files — Individual ('I', 1040), Partnership ('P', 1065), Corporate ('C', 1120), and S-Corporation ('S', 1120S)**; the remaining modules (F/R/T/N/B) return `readiness="unsupported-module"` with a hint.

- Required: `clientNumber: string`.
- Optional: `severity: string = "normal"` (or `"strict"` to promote every warning to a blocker).
- Response: `{ clientNumber, module, readiness: "ready"|"blocked"|"warn"|"unsupported-module", passed, blockers[], warnings[], checkedRules[], skippedRules[] }` (`module` is the single-character DBF code).

Rule catalog:
- **Module-agnostic** (run on I/P/C/S): required-name (C1_1 — first name for I, entity name for P/C/S), required-email (C1_3), forms-at-least-one, main-form presence (`forms-has-1040` / `forms-has-1065` / `forms-has-1120` / `forms-has-1120s` per module), status-rows-present (DATA5 returns at least one row — surfaces "never ran F4 status" as a warning, not a blocker).
- **Individual-only**: required-last-name (C1_2), dependents-have-description (every Series=14 detail row has non-empty Description). On P/C/S these are reported in `skippedRules` with a not-applicable reason — never silently passed.
- **Partnership-only**: partners-present (DATA2 partner rows — 0 rows is a blocker; exactly 1 row warns, since a 1065 needs at least two partners).

Gate order is fixed regardless of module — input validation, then context (9009), then module support, then existence — so the response shape never depends on which gate fired. An unknown `clientNumber` returns a success envelope `{ clientNumber, found: false, hint }` (same shape as `lacerte_client_read`), not an SDK error.

#### `lacerte_year_over_year`

Diffs one client across two tax years. Uses the active context's `dataPath/module` to derive the per-year paths by rewriting the `{YY}Tax` segment (e.g. `24Tax` → `23Tax`). Returns only changed/delta rows.

- Required: `clientNumber: string`, `fromYear: int` (≥ 2018, < toYear), `toYear: int` ((toYear - fromYear) ≤ 5).
- Optional: `scope: string = "all"` (one of `"demographics"`, `"detailTotals"`, `"all"`), `seriesIds: int[]?` (default `[11, 12, 13, 20]` = Wages/Interest/Dividends/Income; ignored when `scope == "demographics"`).
- Response: `{ clientNumber, fromYear, toYear, scope, seriesIds, fromDataPath, toDataPath, foundInFromYear, foundInToYear, demographicsDiff, detailTotalsDiff, errors }`. **Always check `foundInFromYear` / `foundInToYear`** — a typo'd clientNumber returns both `false` instead of masquerading as "no changes between years". Error rows use the canonical `taxYear` key.

The active context's dataPath must follow the canonical `{root}\{YY}Tax\{Module}Data` layout — flat-share alternate layouts surface an actionable error. Active context is snapshotted and restored even on exception, and the scan span is serialized against `lacerte_firm_dashboard` via a shared internal gate so the two fan-out tools can't interleave context mutations.

#### `lacerte_field_history`

Reads the audit history for one `(clientNumber, field)` pair from the WireLog JSONL records. **Requires `LACERTE_DEBUG_WIRE=1`** — when disabled, returns an empty rows list with a hint pointing at the env var.

- Required: `clientNumber: string`, `field: string` (exact field name, e.g. `"C1_3"` for email).
- Optional: `sinceUtc: string?` (default = now - 14 days), `limit: int = 100` (clamped to 1000; `<= 0` returns `rows: []` with the unfiltered `total` and `truncated: true`).

Each row carries `succeeded: true/false` — failed writes (WireLog error records) surface with `succeeded: false` plus the error text, so a rejected write is distinguishable from a landed one. Rows are returned oldest-first; when `total` exceeds `limit`, the **newest** rows are kept (the truncated older tail is the recoverable-from-archives part).

The `value` column is always the redacted `"***"` sentinel — disk-scope redaction is unconditional per the WireLog policy. The operator's original write payload is recoverable from their input record, not from this tool. Counts only outer `op="WriteField"` records (skips inner `Bridge.*` mirrors to avoid double-counting).

---

## 8. Prompts catalog

Four first-class MCP prompts registered via `WithPromptsFromAssembly()` in `Program.cs`. Discoverable through the standard MCP `prompts/list` handshake. In Claude Desktop, type `/` to surface them; in Cursor, `@lacerte`. Source: `src/LacertMCP.Server/Prompts/WorkflowPrompts.cs`.

### `lacerte_client_intake`

Walks an operator through creating a brand-new Lacerte client end-to-end. References real tool names — the LLM's tool-use loop is deterministic.

- Args: `clientName?: string` (optional friendly name to seed the intake script).
- Steps (canonical):
  1. Verify session via `lacerte_session_status`.
  2. Confirm or set active context via `lacerte_list_datapaths` + `lacerte_set_context`.
  3. Create the client record via `lacerte_client_create`.
  4. Seed core fields via `lacerte_field_write` (with distinct per-field idempotency keys).
  5. Seed Schedule C / W-2 detail rows via `lacerte_detail_write`.
  6. Verify roster via `lacerte_client_read`.

### `lacerte_review_ef_status`

Triages e-file status across the active module's client roster. **TY19+ only.**

- Args: `taxYear?: int` (filter; default = active context's year).
- Steps: pull EFiling+EFStatus join, group by Status, surface Rejected first, flag stale Pending/Transmitted (>72h), report counts per bucket + per-row NextAction.

### `lacerte_field_write_bulk`

Applies the same field write across many clients with mandatory dry-run preview, per-row idempotency keys, and stop-on-first-error. Name matches the underlying tool (was `lacerte_bulk_field_update` pre-P16).

- Args: `table: int = 1`, `field: string` (required), `previewCount: int = 3`.
- Steps: confirm session, sanity-check the field is `ReadWrite` via `lacerte_field_access_map`, build target client list, dry-run preview of first `previewCount`, real write only if preview was 100% clean, verify a sample of 3 random clients post-write.

### `lacerte_csv_intake`

Bulk-onboards clients from a CSV / Tax-Organizer export. Requires a one-shot column→field mapping declaration upfront, per-row idempotency keys, stop-on-first-error.

- Args: `sourceLabel?: string`, `rowCount?: int`.
- Steps: confirm session, declare column→field mapping once, validate row payloads before any write, per-row create + seed with stop-on-first-error, per-row outcome report.

---

## 9. CLI reference

The CLI binary is `lacerte-mcp.exe` (under `src/LacertMCP.Cli/bin/x64/Debug/` after build, or `cli/` in the release zip). Seven subcommands. All commands accept `--help` for in-line usage. Exit-code convention:

- **0** — success.
- **1** — hard failure (missing args, IO error, no match for `--name`, invalid `--access`, a denied/failed register/login ceremony, etc.).
- **2** — partial / skipped (e.g. `doctor` with at least one probe warned or skipped, `register`/`login` when the Lacerte SDK COM surface is unavailable, `tools` with invalid regex or mutually-exclusive flags).
- **3** — reserved for "not implemented through this verb" (e.g. `field-access discover`). No longer used by `register`/`login` — the old Phase-9 exit-3 `[partial]` stub contract is retired.

Source: `src/LacertMCP.Cli/Commands/*.cs`.

### `lacerte-mcp register [--app-name X] [--tax-year Y] [--reset]`

One-time AppID mint + Intuit consent + sign-in ceremony. Mints a per-install AppID GUID, persists it via the AppID store (Credential Manager on Windows, file fallback off-Windows), then on Windows calls `ITPRegistration.Register(appName, appId, taxYear)` through `Bridge/Registration.cs`, which surfaces the consent + Intuit sign-in dialogs on the interactive desktop and returns the SDK's TPSDKStatus outcome (reported with the catalog name + remediation from `StatusCodes.GetStatusInfo`).

| Flag | Type | Default | Purpose |
| --- | --- | --- | --- |
| `--app-name` | string | `"Operator Lacerte MCP"` | Friendly name shown in the Intuit consent dialog. |
| `--tax-year` | int | prior calendar year | Tax year the registration is scoped to. |
| `--reset` | bool | `false` | **Operator recovery path** — mints a fresh GUID even if one exists, forcing a new Intuit consent ceremony. Use when the AppID is lost or corrupted. |

Must be run from an **interactive PowerShell** (not a service or scheduled task) so the consent dialog can surface.

Exit codes (aligned with doctor's 0 green / 1 red / 2 partial convention; exit **3 is retired** for this verb — update any wrapping script written against the old `[partial]` exit-3 stub):
- **0** — `AccessGranted (201)`: ceremony complete, 24h sign-in ticket live. Also exit 0 off-Windows, where the ceremony is impossible by definition — the command prints `[skipped]`, persists the AppID, and stays green for cross-platform dev smoke loops.
- **1** — the ceremony ran but did not grant access (`ConsentDenied`, `NotSignedIn`, `RegVersionBlocked`, `LacerteTaxSetupRequired`, unknown status), or it threw a real error. Status name + remediation print on stderr.
- **2** — **PARTIAL:** on Windows but the Lacerte SDK COM surface is unavailable (`BridgeUnavailable` — `TPRegistration` ProgID missing / COM activation failed). The AppID is persisted and intact; install the 64-bit SDK MSI, confirm `lacerte-mcp doctor` shows the "Lacerte COM ProgID" probe green, then re-run.

### `lacerte-mcp login [--app-name X] [--tax-year Y]`

Re-calls `Register()` to refresh the 24-hour Intuit auth ticket (register and login end in the same `ITPRegistration.Register` wire call). Use this when `lacerte_session_status` reports `ticketExpired: true` or when a tool starts returning `LoginExpired (9007)`. Reuses the persisted AppID; no fresh consent dialog unless the ticket is fully gone.

| Flag | Type | Default | Purpose |
| --- | --- | --- | --- |
| `--app-name` | string | `"Operator Lacerte MCP"` | Must match the name used at first register. |
| `--tax-year` | int | prior calendar year | Tax year scope for the login. |

Exit codes: same contract as `register` — **0** `AccessGranted` (or off-Windows no-op), **1** failed refresh, **2** PARTIAL (SDK COM surface unavailable). A failed refresh exits loudly so a cron wrapper can alarm instead of silently letting the ticket lapse.

If no AppID is found, prints `"No AppID found. Run lacerte-mcp register first."` to stderr and exits **1**.

### `lacerte-mcp status [--show-app-id] [--json]`

Snapshot of registration state, mode, storage backend, and platform. Zero wire I/O — safe to run on a cron. Default output is a human-readable summary (7-line table); pass `--json` for the machine-parseable shape.

| Flag | Type | Default | Purpose |
| --- | --- | --- | --- |
| `--show-app-id` | bool | `false` | Print the full AppID GUID instead of the masked form (first 8 chars + `****`). Useful for backup before a Lacerte reinstall. |
| `--json` | bool | `false` | Emit machine-parseable JSON. |

JSON shape: `{ registered, appId, appName, mode, storageBackend, storageLocation, platform, buildVersion, lastRegisterAttempt }`. `mode` is `"live"` or `"simulation"`; `storageBackend` is `"windows-credential-manager"` or `"file"`.

When no AppID is persisted, the human output appends a self-curing `Next:` hint with the right next command for the current platform.

Exit code: always **0**.

### `lacerte-mcp unregister --confirm`

Wipes the persisted AppID GUID from the active storage backend (Windows Credential Manager on Windows, the file fallback off-Windows). Discoverable counterpart to `register --reset`. After `unregister`, the next `lacerte-mcp register` mints a fresh GUID and forces a new Intuit consent ceremony.

| Flag | Type | Default | Purpose |
| --- | --- | --- | --- |
| `--confirm` | bool | `false` | **Required.** Without it the command refuses (exit 1) and prints a preview of what would be deleted. |

Exit codes:
- **0** — wipe successful, or no AppID persisted (no-op).
- **1** — refused without `--confirm`. The preview body is on stdout (redirectable / pipeable); the one-line refusal banner is on stderr.

### `lacerte-mcp tools [--json] [--filter <regex>] [--name <exact>] [--names-only]`

Lists every MCP tool the server registers, by reflecting over the `LacertMCP.Server` assembly. Mirrors the SDK's own `WithToolsFromAssembly()` discovery — no stdio transport boot required, runs anywhere (including off-Windows). Parameter listings mirror the MCP wire schema: `CancellationToken` parameters are excluded (they are infrastructure, not wire-visible arguments).

| Flag | Type | Default | Purpose |
| --- | --- | --- | --- |
| `--json` | bool | `false` | Emit a machine-parseable JSON array. Mutually exclusive with `--names-only`. |
| `--filter` | string | (none) | Case-insensitive regex matched against tool name (e.g. `'^lacerte_ef_'`). Exit **1** if zero matches. Exit **2** on invalid regex. |
| `--name` | string | (none) | Deep-print one tool's full parameter surface (incl. per-parameter descriptions, declaring type, optionality). Exit **1** if no match. When combined with `--filter`, the name must also satisfy the filter. |
| `--names-only` | bool | `false` | One tool name per line, no descriptions. Pairs cleanly with shell pipelines. Mutually exclusive with `--json`. |

Exit codes:
- **0** — listing produced output.
- **1** — zero matches (with `--filter`), or no match (with `--name`).
- **2** — invalid regex or `--json` + `--names-only` combined (usage error).

Sample `--name` output:

```text
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

**`field-access list --module <X> --table <N> [--year YYYY]`** — prints every entry for a module/table at the requested year (overlays applied).

| Flag | Type | Required | Default |
| --- | --- | --- | --- |
| `--module` | string (length 1) | yes | — |
| `--table` | int (1-7) | yes | — |
| `--year` | int | no | prior calendar year |

**`field-access set --module <X> --table <N> --field <F> --access <A> [--data-type T] [--max-length N] [--source S] [--note STR] [--force]`** — upserts one field entry into the base map (re-serialized sorted so PR diffs stay stable).

| Flag | Type | Required | Default | Purpose |
| --- | --- | --- | --- | --- |
| `--module` | string (length 1) | yes | — | I/C/P/S/F/R/T/N/B |
| `--table` | int (1-7) | yes | — | DBF table number |
| `--field` | string | yes | — | Must match `<letter><table>_<n>` unless `--force` |
| `--access` | string | yes | — | `ReadWrite` / `ReadOnly` / `Hidden` / `NotExposed` / `NotSupported` / `Unknown` |
| `--data-type` | string | no | — | Declared data type (string/int/...) |
| `--max-length` | int | no | — | Max length in chars |
| `--source` | string | no | `"manual"` | Provenance tag (`spec` / `introspect` / `manual`) |
| `--note` | string | no | — | Description / note |
| `--force` | bool | no | `false` | Override the field-name shape check |

**`field-access discover`** — redirect stub; always exits **3**. Prints a one-line pointer to the standalone introspector: `LacertMCP.FieldAccessDumper.exe --module <X> --table <N>` (Windows + live SDK).

### `lacerte-mcp doctor [--json]`

Setup verification across OS / runtime, install, config, and identity probes. Canonical probe list lives in `src/LacertMCP.Cli/Commands/DoctorCommand.cs` XML doc (13 probes spanning Build version, Windows version, x64 process, App.config runtime shim, .NET 4.8, ODBC DSN `LacerteDSII`, Lacerte SDK install, Lacerte COM ProgID, Operating mode, `LACERTE_DATA_PATH`, `LACERTE_DATA_ROOT`, FieldAccessMap base shards, persisted AppID).

| Flag | Type | Default | Purpose |
| --- | --- | --- | --- |
| `--json` | bool | `false` | Emit `{ exitCode, probes: [{ name, status, detail, remediation? }] }`. |

Exit-code contract:
- **0** — every probe green, no warnings.
- **1** — at least one probe failed (e.g. fresh checkout with no AppID; ODBC DSN missing).
- **2** — **PARTIAL**: at least one probe warned (advisory — Warn rows now print their remediation line, R6-29) or was skipped, with no fails. Skips are the typical off-Windows result since the Windows-only probes can't run; a warn-only box also lands here instead of claiming "all probes green" with exit 0.
- **3** is **not used by doctor** — it is reserved across the CLI for "not implemented here" verbs.

Status badges adapt to the active terminal encoding: pretty Unicode glyphs (`✓ ⚠ ✗`) on UTF-8 terminals (Windows Terminal, PowerShell 7), `[OK] [WARN] [FAIL] [--]` on legacy code pages.

---

## 10. Environment variables

Resolved at process startup unless otherwise noted. Live in your MCP host's `env` block (Section 6) or in a `.env` file copied from `.env.example`. New env vars must be documented in README + `.env.example` + this manual — keep the trio in lock-step.

| Var | Required? | Default | Purpose |
| --- | --- | --- | --- |
| `LACERTE_LIVE` | one of LIVE / SIMULATION required | unset | `1` enables live SDK on Windows. |
| `LACERTE_SIMULATION` | one of LIVE / SIMULATION required | `1` off-Windows | `1` forces simulation regardless of platform. |
| `LACERTE_DATA_PATH` | advisory | unset | Quoted in ODBC-error remediations and probed by `lacerte-mcp doctor`. **NOT** auto-applied to session context — every session must still call `lacerte_set_context`. |
| `LACERTE_DEFAULT_TAX_YEAR` | recommended | unset | Default year used when `lacerte_set_context` omits one. |
| `LACERTE_APP_NAME` | recommended | `LacerteMCP` | Friendly name in the SDK consent dialog. |
| `LACERTE_APP_ID` | runtime read-only | from Credential Manager | Source of truth is Windows Credential Manager (set via `lacerte-mcp register`). Used only as an emergency override; remediation strings call out this case. |
| `LACERTE_DATA_ROOT` | optional | `C:\Lacerte` | Root scanned by `lacerte_list_datapaths` for `{YY}Tax\{Module}Data` folders. |
| `LACERTE_DEBUG_WIRE` | optional | `0` | `1` logs COM + SQL to `logs/lacerte-YYYYMMDD.log`. Secrets redacted (disk scope — broader than wire scope). |
| `LACERTE_DEBUG_LOG_PATH` | optional | `./logs` | Override the log directory. |
| `LACERTE_DEBUG_LOG_MAX_BYTES` | optional | unset | Per-file size cap for log rotation. |
| `LACERTE_DEBUG_LOG_MAX_DIR_BYTES` | optional | unset | Whole-directory size cap. |
| `LACERTE_DEBUG_LOG_RETENTION_DAYS` | optional | unset | Per-day retention for log rotation. |
| `LACERTE_DEBUG_LOG_RESULT_SAMPLE` | optional | unset | Number of sample rows to record for large result arrays. |
| `LACERTE_DEBUG_LOG_FULL_RESULTS` | optional | `0` | `1` disables result summarization in the wire log. |
| `LACERTE_LOG_LEVEL` | optional | (host default) | Operator-tunable host log verbosity (parsed via `LogLevelEnv`). |
| `LACERTE_HEARTBEAT_SECONDS` | optional | `30` | Heartbeat JSON write cadence to `%TEMP%/lacerte-mcp-heartbeat-{pid}.json`. `0`, a negative value, or an **unparseable** value disables the writer; values above **86400** (24h) are clamped down to 86400 (an over-clamp value would otherwise overflow `Task.Delay`). External watchers enumerate `lacerte-mcp-heartbeat-*.json` and read file mtime + JSON to check liveness + 24h-auth state without speaking MCP. |
| `LACERTE_BULK_WRITE_MAX` | optional | `500` | Per-call entry cap shared by `lacerte_field_write_bulk` **and** `lacerte_detail_write_bulk`; exceeding throws 9020. |
| `LACERTE_BULK_CREATE_MAX` | optional | `100` | Per-call entry cap on `lacerte_client_create_bulk` (creates are heavier than field writes); exceeding throws 9020. |
| `LACERTE_RECENT_LOG_CAPACITY` | optional | `256` | In-process WireLog ring capacity (clamped 16..4096). |
| `LACERTE_ALLOW_UNC` | optional | unset | `1` permits `\\server\share` UNC dataPaths in `lacerte_set_context`; without it UNC paths are rejected with 9013. |
| `LACERTE_IDEMPOTENCY_PERSIST` | optional | on | Idempotency-cache disk persistence (replay protection across restarts) is **default ON** — the safer posture for duplicate-write protection. Only the **exact string `0`** disables it (`"false"`/`"off"` leave it enabled; the startup validator warns on such values). |
| `LACERTE_IDEMPOTENCY_PERSIST_PATH` | optional | `%APPDATA%/lacerte-mcp/idempotency-cache.json` | Override the persistence path. |
| `LACERTE_SIM_EXPIRED` | testing only | unset | `1` simulates an expired 24h auth session. |
| `LACERTE_MCP_FORCE_FILE_STORE` | testing / dev | unset | `1` bypasses Windows Credential Manager and uses the file-based AppID store at `%APPDATA%\lacerte-mcp\config.json` (auto-on off-Windows). |
| `LACERTE_PARITY_RECORDING` | testing only | unset | Path to recorded parity JSON consumed by the `BothModesData` test harness. |

> ODBC connections are DSN-less — `ConnectionStringBuilder` composes them from driver + datapath + module + year, so no `LACERTE_DSN` env var is honored or needed. The `LacerteDSII` DSN name is only probed by `lacerte-mcp doctor` as an install-sanity check; it is not used at runtime.

The idempotency-cache TTL is **24 hours**, hard-coded as `IdempotencyTtl` in `LacerteSessionManager.cs:221` — there is no env var to tune it today.

---

## 11. Common workflows

Each workflow is a numbered step-by-step with the actual tool calls. Paste the natural-language prompts to your MCP host; the LLM picks the right tools.

### Workflow A — Onboarding a new client from intake notes

Prefer the `lacerte_client_intake` prompt (Section 8) — type `/lacerte_client_intake` in Claude Desktop and the LLM follows the canonical script. Manual version:

1. **Verify session.** Ask: *"Show the Lacerte session status."* → `lacerte_session_status`. Confirm `isRegistered: true` and a non-null `context`. If `nextSteps` carries a remediation, follow it first.
2. **Set context.** Ask: *"Switch to 2024 Individual at `C:\Lacerte\24Tax\IData`."* → `lacerte_set_context(dataPath, taxYear=2024, module="I")`.
3. **Create the client.** Ask: *"Create a new client SMIT, taxType 1040, first name John, last name Smith, email john@example.com."* → `lacerte_client_create({ clientId: "SMIT", taxType: "1040", firstName: "John", lastName: "Smith", emailId: "john@example.com" })`. Capture the returned `clientNumber`.
4. **Seed core fields** via `lacerte_field_write` with distinct per-field idempotency keys: `intake-SMIT-C1_1`, `intake-SMIT-C1_3`, etc. Reusing one key across fields triggers `9102 IdempotencyConflict`.
5. **Seed detail rows** (W-2, 1099, dependents) via `lacerte_detail_write`. Required fields: `clientNumber, series, code, amount, description`. **Pass `idempotencyKey`** — otherwise a transport retry appends duplicates.
6. **Verify the roster.** Ask: *"List the clients in this context."* → `lacerte_client_list` and confirm the new ClientNumber appears.

### Workflow B — Bulk-updating an address field across many clients

Prefer the `lacerte_field_write_bulk` prompt. Manual version:

1. `lacerte_session_status` → confirm registered + correct context.
2. **Sanity-check the field is writable.** `lacerte_field_access_map(module="I", table=1, taxYear=2024)`. The target field must appear in `fields` with access exactly `ReadWrite`. **STOP if** the field is absent or any of `Unknown / ReadOnly / Hidden / NotExposed / NotSupported`.
3. **Build the target client list.** Either an explicit `[clientNumber...]` from the operator, or via `lacerte_client_list` with a filter.
4. **Dry-run preview of the first 3.** Call once with:
   ```json
   { "name": "lacerte_field_write_bulk",
     "arguments": {
       "table": 1,
       "entries": [
         { "clientNumber": "1001", "field": "C1_5", "value": "100 Main St" },
         { "clientNumber": "1002", "field": "C1_5", "value": "100 Main St" },
         { "clientNumber": "1003", "field": "C1_5", "value": "100 Main St" }
       ],
       "dryRun": true,
       "continueOnError": false,
       "idempotencyKey": "addr-batch-2024-q4-preview"
     } }
   ```
5. **Inspect `perItem[]`.** If any row's `success` is false (or `statusCode` non-Success), STOP. Report the failed clientNumber + structured error.
6. **Real write.** Same call shape with the full `entries` array, `dryRun: false`, and a **different** `idempotencyKey` prefix (`"addr-batch-2024-q4"`, dropping the `-preview` suffix). This is mandatory, not stylistic: `dryRun` is part of every row's payload fingerprint and dry-run results ARE cached, so reusing the preview prefix makes the commit's first previewed row throw `9102 IdempotencyConflict` — and with `continueOnError: false` the whole real write stops at row 0. Previewed rows are NOT skipped on the commit; every row in the commit array is written for real.
7. **Verify a sample.** Pick 3 random successful rows from `perItem[]` and call `lacerte_client_read` for each; confirm the new value. To RETRY a failed commit, reuse the **same** commit prefix — succeeded rows replay from cache; failed/skipped rows re-execute.

### Workflow C — Reviewing e-file status across your firm

Two flavors. For one tax year + module, use the `lacerte_review_ef_status` prompt. For the whole firm:

1. `lacerte_firm_dashboard` — one call, fans out across every `(taxYear, module)` tuple under `LACERTE_DATA_ROOT`. Returns per-tuple client counts + EF-status histogram + latest StatusDate watermark, plus a firm-level rollup.
2. **Drill down to Rejected returns.** For the tuple with the most rejections, ask: *"Switch context to {dataPath, taxYear, module} and list the rejected e-files."* → `lacerte_set_context` + `lacerte_ef_status`. Filter rows where `Status == "Rejected"`.
3. **Per-client triage.** For each Rejected row, `lacerte_detail_read(clientNumber=ef.ClientId)` to surface rejection reason fields.
4. **Flag stale Pending/Transmitted.** Anything with `StatusDate > 72h` ago is "stale, escalate."

For the polling cadence, pass `sinceUtc` to either `lacerte_firm_dashboard` or `lacerte_ef_status` so each cycle returns only the delta since the last poll.

### Workflow D — Pre-filing readiness check

1. Set context to the client's `(dataPath, taxYear, module)` — the rule catalog covers modules I, P, C, and S.
2. `lacerte_pre_filing_check(clientNumber="1001")` — returns `{ readiness, passed, blockers[], warnings[], checkedRules[], skippedRules[] }`. On entity modules, Individual-only rules appear in `skippedRules` with a not-applicable reason rather than silently passing.
3. If `readiness == "blocked"`, fix every entry in `blockers[]` via the appropriate write tool, then re-run.
4. If `readiness == "warn"`, decide whether to address the warnings or proceed. Pass `severity="strict"` to promote warnings to blockers.
5. If `readiness == "unsupported-module"` (modules F/R/T/N/B), the module has no rule catalog yet — surface readiness manually via `lacerte_client_read` / `lacerte_table_read` / `lacerte_detail_read`.

### Workflow E — Year-over-year client comparison

1. Set context to the client's "current year" `(dataPath, taxYear, module)`. The dataPath must follow `{root}\{YY}Tax\{Module}Data` — flat-share layouts surface an actionable error.
2. `lacerte_year_over_year(clientNumber="1001", fromYear=2023, toYear=2024, scope="all")` — returns only changed/delta rows.
3. For deeper detail comparisons, pass `scope="detailTotals"` with custom `seriesIds`.
4. (Or: `lacerte_diff_clients(client1Number, client2Number)` — for same-context comparisons, e.g. spousal returns.)

### Workflow F — Debugging a failed write via cid + recent_log

1. The write returns:
   ```json
   { "success": false, "statusCode": 9012, "statusName": "UnknownFieldAccess",
     "origin": "synthetic", "cid": "a1b2c3d4", "remediation": "..." }
   ```
2. **Immediate self-diagnosis** — call `lacerte_recent_log(cid="a1b2c3d4", limit=50)`. The ring is always-on (independent of `LACERTE_DEBUG_WIRE`). You get the causal chain for that one call.
3. **Disk-log forensics** — if `LACERTE_DEBUG_WIRE=1`, grep `logs/lacerte-YYYYMMDD.log` (resolved path is on `lacerte_session_status.wireLogPath`) for `"cid":"a1b2c3d4"`. Disk scope is broader; SSN/EIN/email/address columns are scrubbed there but operator-actionable strings survive.
4. **Field-history audit** — `lacerte_field_history(clientNumber, field)` reads the WireLog JSONL records for outer `op="WriteField"` entries on that pair (requires `LACERTE_DEBUG_WIRE=1`).
5. **Cache state probe** — for 9102 conflicts near eviction pressure, call `lacerte_cache_state` to inspect `idempotency.count/cap/nearCap/peakCount`.

### Workflow G — Hot-reloading the field-access schema after running the dumper

When you fix `UnknownFieldAccess (9012)` by running the standalone introspector, do **not** restart the server (that invalidates the Intuit 24h auth ticket).

1. On a Windows + live-Lacerte box, run: `LacertMCP.FieldAccessDumper.exe --module I --table 1` — produces fresh per-module shards under `Schema/FieldAccessMap/base/`.
2. From your MCP host, call `lacerte_reload_schema` — drops the in-process caches. Returns `{ modulesReloaded, overlaysReloaded, schemaRoot }`.
3. Re-attempt the write that failed with 9012. The next field-write or lookup lazily repopulates the cache from disk.

The cached AppID + 24h auth ticket are preserved across the reload.

---

## 12. Error reference

Every tool returns either `{ success: true, result, cid }` or `{ success: false, statusCode, statusName, severity, userMessage, remediation, origin, context?, cid }`. The `origin` discriminator separates SDK-origin codes (operator fixes something in Lacerte) from synthetic codes (operator fixes the MCP call):

- `"sdk"` — codes 0..204. Real Lacerte SDK TAPIStatus (0..28) or TPSDKStatus (200..204). Look up in Intuit's SDK docs; remediation is typically operator-side.
- `"synthetic"` — codes 9001..9299. Minted by this MCP **before** any SDK call (pre-flight validation 9001-9099, auth lifecycle 9100-9199, COM bridge/runtime 9200-9299). Remediation is typically MCP-side (fix tool args, flip a server config flag, or run a CLI command).

Call `lacerte_return_codes` for the live catalog with names, severities, user messages, and remediation strings.

### TAPIStatus 0-28 (SDK general)

Source: `src/LacertMCP.Server/Errors/StatusCodes.cs`.

| Code | Name | Severity | When it fires | Recommended action |
| --- | --- | --- | --- | --- |
| 0 | Error | error | Generic SDK error with no further detail. | Re-run with `LACERTE_DEBUG_WIRE=1`; check Windows Event Viewer for SDK traces. |
| 1 | Success | success | OK. | None. |
| 2 | DBFNotAccessable | error | DBF file missing, locked, or unreachable. | Confirm `LACERTE_DATA_PATH` exists and is read/write; close the file in Lacerte if open exclusively. |
| 3 | CodeBaseError | error | DBF index is corrupt. | Lacerte → File → Repair Indexes for the affected module/year. |
| 4 | OutOfIndex | error | Pre-TY21 row number doesn't exist. | Re-query the table or switch to the TY21+ `UpdateTaxReturn` path (keyed by ClientNumber). |
| 5 | DBNotSupported | error | Table not supported for this module/year. | Check `lacerte_list_modules`. |
| 6 | NotImplemented | error | SDK doesn't implement this operation. | File with Intuit. |
| 7 | NotExposed | error | Field exists in DBF but SDK refuses to expose it. | Remove the field from the request payload. |
| 8 | ExposedForRead | warning | Field is read-only. | Use a read tool, not a write tool. |
| 9 | ExposedForWrite | warning | Field is write-only. | Don't try to read this field. |
| 10 | ExposedForReadWrite | warning | Field supports read+write. | None — informational. |
| 11 | VersionBlocked | error | Lacerte/SDK build too old. | Upgrade Lacerte; or set `LACERTE_DEFAULT_TAX_YEAR` to a supported year. |
| 12 | DuplicateClient | error | ClientID already exists for this tax year/module. | Pick a different clientId, or `lacerte_client_read` the existing record. |
| 13 | LengthExceeded | error | A field value is too long (clientId > 8 chars is the most common cause). | Trim; clientId must be ≤ 8 alphanumeric chars. |
| 14 | TaxModuleMismatch | error | Connection's ReturnType doesn't match the module character of the addressed DBF. | Re-call `lacerte_set_context` with the correct module. |
| 15 | DetailCreationError | error | Failed to create the per-client detail file for a new client. | Verify disk space + write permission on the datapath. |
| 16-23 | DB1Error..DB8Error | error | Table-specific DBF I/O error. Context carries `{expectedDbf, dataPath}`. | Run Lacerte → File → Repair Indexes for the affected module/year. |
| 24 | InValidClientName | error | Token failed validation — clientId character whitelist, single-char module code, OR a required name field missing. Reused for three gates. | Read `context.reason` and `context.field/module` for the specific rule. Common: clientId must match `^[A-Z0-9]+$` (no spaces, hyphens, or apostrophes); module must be one of I/C/P/S/F/R/T/N/B; firstName/lastName/emailId required for `lacerte_client_create`. |
| 25 | InValidTaxYear | error | Tax year < 2018 or not installed. | Set `LACERTE_DEFAULT_TAX_YEAR >= 2018`, or install the Lacerte module for that year. |
| 26 | FieldNotSupported | error | Field name doesn't exist in this module/year's schema. | Cross-check `lacerte_field_access_map`. |
| 27 | InValidSubClientID | error | `subClientId` must be > 0 for DATA2/4 writes. | Pass a positive integer subClientId. |
| 28 | InValidStateAbbrev | error | `stateAbbrev` required and must be a valid 2-letter code for DATA3/4 writes. | Pass the 2-letter postal code (e.g. CA, NY). |

### TPSDKStatus 200-204 (SDK registration)

| Code | Name | Severity | When it fires | Recommended action |
| --- | --- | --- | --- | --- |
| 200 | ConsentDenied | error | Operator declined consent in the Lacerte registration dialog. | Re-run `lacerte-mcp register` and click Accept. |
| 201 | AccessGranted | success | Registration succeeded. | None. |
| 202 | RegVersionBlocked | error | Lacerte/SDK below the minimum version. | Upgrade Lacerte, then re-run `lacerte-mcp register`. |
| 203 | NotSignedIn | error | No active Intuit login or 24h ticket expired. The one-time consent is still valid. | Run `lacerte-mcp login`. Fall back to `lacerte-mcp register` if `login` reports the AppID is unknown. |
| 204 | LacerteTaxSetupRequired | error | Lacerte itself isn't installed or fully initialized. | Install Lacerte TY18+ and complete first-run setup. |

### Synthetic 9001-9299 (MCP-side)

Source: `src/LacertMCP.Server/Errors/SyntheticStatusCodes.cs`.

| Code | Name | Range | When it fires | Recommended action |
| --- | --- | --- | --- | --- |
| 9001 | FieldIsReadOnly | preflight | Caller tried to write a ReadOnly/Hidden/NotExposed/session-readOnly field. | Remove the field; or use the corresponding read tool. |
| 9002 | ClientIDTooLong | preflight | `clientId` > 8 chars or contains a disallowed character. | Trim to ≤ 8 alphanumeric. |
| 9003 | TaxYearTooOld | preflight | Tax year < 2018. | Pass `taxYear >= 2018`. |
| 9004 | StateAbbrevRequired | preflight | DATA3/4 write without `stateAbbrev`. | Pass a 2-letter `stateAbbrev`. |
| 9005 | SubClientIDRequired | preflight | DATA2/4 write without `subClientId`. | Pass a positive integer `subClientId`. |
| 9006 | AppNotRegistered | preflight | A read/write tool called before `lacerte-mcp register`. | Run `lacerte-mcp register` on the Lacerte box; restart the MCP server. |
| 9007 | LoginExpired | preflight | 24h Intuit auth ticket lapsed (proactive probe). | Run `lacerte-mcp login`; restart the MCP server. |
| 9008 | DetailWriteInvalid | preflight | `lacerte_detail_write` failed client-side validation. | Fix the failed field per the validation message. |
| 9009 | ContextNotSet | preflight | Read/write tool called before `lacerte_set_context`. | Call `lacerte_list_datapaths` + `lacerte_list_modules` + `lacerte_set_context`. |
| 9012 | UnknownFieldAccess | preflight | Field not in access map. | In-session: call `lacerte_field_access_map`. Permanent fix: run `LacertMCP.FieldAccessDumper.exe` then `lacerte_reload_schema`. |
| 9013 | InvalidDataPath | preflight | dataPath failed validation (relative, `..` traversal, control chars, UNC without `LACERTE_ALLOW_UNC=1`, outside `LACERTE_DATA_ROOT`). | Pass an absolute local path under the Lacerte install root. |
| 9014 | TaxYearTooNew | preflight | Tax year exceeds installed Lacerte coverage (above current year + 1). | Pass a `taxYear` at or before current calendar year. |
| 9015 | FieldWriteInvalid | preflight | `lacerte_field_write` `value` was not a JSON-serializable scalar. | Pass a scalar — string, number, bool, or null. Use `lacerte_detail_write` for multi-field row payloads. |
| 9016 | ContextMismatch | preflight | Caller-supplied `taxYear` or `readOnly` does not match active session context. | Re-set context OR drop the mismatching assertion. Do NOT re-set with the same value. Inspect `context.expectedTaxYear/actualTaxYear`. |
| 9017 | InvalidArgument | preflight | A tool argument failed validation. See `context.reason`. | Fix the argument per `reason`; do NOT change context/registration. |
| 9019 | LiveReplaceIndexUnsupported | preflight | `lacerte_detail_write` `replaceIndex` supplied in live mode. | Read existing rows, append corrected row, delete stale one via future `lacerte_detail_delete`. |
| 9020 | BulkBatchTooLarge | preflight | A bulk tool's entries exceed its cap (field/detail bulk: 500 via `LACERTE_BULK_WRITE_MAX`; create bulk: 100 via `LACERTE_BULK_CREATE_MAX`). | Split into ≤ `maxAllowed` chunks with a shared `idempotencyKey` prefix; or override the cap via the env var in `context.envOverride`. |
| 9021 | BulkBatchCountMismatch | preflight | `expectedCount` does not match `entries.Length`. | Inspect `context.expected/actual`; fix entries or update assertion. |
| 9101 | ReAuthRequired | auth | 24h Intuit auth ticket expired (reactive detection — NotSignedIn slipped past the proactive probe). | Run `lacerte-mcp login`. |
| 9102 | IdempotencyConflict | auth | Same idempotency key reused with a different payload fingerprint — different args, a flipped `dryRun`, or a `lacerte_set_context` switch of dataPath/taxYear/module (the context triple is part of the fingerprint). | Pick a fresh key (e.g. the `-preview`/commit prefix convention), or replay the original payload byte-for-byte in the original context. |
| 9103 | InvalidAppId | auth | Supplied AppID failed bridge-boundary shape check. | Re-run `lacerte-mcp unregister` then `lacerte-mcp register`; or fix the `LACERTE_APPID` env var. |
| 9201 | BridgeUnavailable | bridge | Live mode requested but the .NET/COM bridge process didn't start or crashed. | Confirm Windows + SDK installed. Enable `LACERTE_DEBUG_WIRE=1`, restart, reproduce; grep the log for `cid`. |
| 9202 | SimulationOnly | bridge | Live-only op (e.g. `lacerte_client_create`) called in simulation mode. | Set `LACERTE_LIVE=1` on Windows + Lacerte; or stub the op in sim. |
| 9203 | InternalServerError | bridge | MCP server raised an unexpected exception — not an SDK error. | Capture `exceptionType` from context. Enable `LACERTE_DEBUG_WIRE=1`, reproduce, file an issue with the log block. |

### "When you see X, do Y" mapping

| Symptom | Likely code | Action |
| --- | --- | --- |
| Tool calls all fail right after install | 9006 AppNotRegistered | Run `lacerte-mcp register`. |
| Tool calls were working, now fail with auth-flavored error | 9007 LoginExpired / 203 NotSignedIn / 9101 ReAuthRequired | Run `lacerte-mcp login`. |
| Write returns "field is unknown" | 9012 UnknownFieldAccess | `lacerte_field_access_map` to confirm; then dumper + `lacerte_reload_schema` if shard is empty. |
| Write returns `success: true` but Lacerte UI shows old value | none (UI staleness) | Close & reopen the client in Lacerte. The write actually landed; verify via `lacerte_client_read`. |
| Replaying a write returns success without going to the wire | (idempotency cache hit) | Expected. Different idempotency key needed for a genuinely-new operation. |
| Replaying returns 9102 | 9102 IdempotencyConflict | Different payload with the same key. Pick a fresh key OR replay the original payload byte-for-byte. |
| Bulk write rejected before any row runs | 9016 / 9020 / 9021 | Inspect `context.expected*/actual*`. |
| Detail write rejected in live mode for replaceIndex | 9019 LiveReplaceIndexUnsupported | Append + (future) delete pattern. |
| Live mode tools return "bridge unavailable" | 9201 BridgeUnavailable | Confirm Windows + 64-bit SDK; grep wire log for cid. |
| MCP server crashes / returns unexpected error | 9203 InternalServerError | Server-side bug — file an issue with the cid'd log block. |

---

## 13. Wire conventions cheat sheet

Canonical as of round 3 (P16). Enforced by `NamingConventionsTests.cs` (13 reflection-driven sentinels).

**Tool parameters (always camelCase):**

| Name | Type | Notes |
| --- | --- | --- |
| `taxYear` | int | Never `year`. |
| `stateAbbrev` | string ("" or 2-letter postal) | Never `stateAbbr`. |
| `fieldName` / `field` | string | `field` on single-write tools, `fieldName` on the bulk entry shape — but the bulk entry property is `Field` (canonicalized in P16-026). |
| `module` | string (length 1) | Never `char`. JSON binding sends `"I"` not `'I'`. |
| `clientId` / `emailId` / `subClientId` | string / string / int? | Microsoft acronym convention — `Id` mixed-case. Never ALL-CAPS `clientID`. |
| `dataPath` | string | Absolute path. |
| `dryRun` | bool | Default `false`. |
| `readOnly` | bool | Session-level flag in `lacerte_set_context`; per-call assertion in `lacerte_field_write_bulk`. |
| `idempotencyKey` | string? | Strongly recommended on writes. |

**Tool response envelopes** (outer always `{success:true, result:{...}, cid}` from `WrapToolHandler`):

- Collection key: `rows` (canonical). Legacy domain aliases (`clients`, `forms`, `subclients`, `states`, `stateSubclients`, `statuses`, `efStatuses`, `modules`, `datapaths`, `codes`) retained as v0.1 back-compat siblings; removed in v0.2.
- Counts: `total` (full-set size), `limit` (requested), `returnedCount` (actual page size — emitted on every paginated envelope; legacy `count` retained as a sibling until v0.2).
- Pagination: `hasMore`, `nextOffset`.
- `module` wire key: the single-character DBF code (`"I"`, `"C"`, ...) everywhere — on requests AND response envelopes (R6-18). Friendly names ride the separate `moduleName` key where surfaced (e.g. `lacerte_list_datapaths`).
- Mode strings: lowercase (`"sim"` / `"live"`, also `"simulation"` / `"live"` in `mode` field of session_status + health).
- Correlation ID: `cid` (8-hex AsyncLocal, also stamped in nested WireLog records).

**Error envelope** (when WrapToolHandler catches `LacerteSDKError`):

```json
{
  "success": false,
  "statusCode": 9012,
  "statusName": "UnknownFieldAccess",
  "origin": "synthetic",
  "severity": "error",
  "userMessage": "...",
  "remediation": "...",
  "context": { "...": "..." },
  "cid": "a1b2c3d4"
}
```

- `origin` ∈ `{"sdk", "synthetic"}` — disambiguates 28 SDK + 5 TPSDKStatus codes vs synthetic 9xxx.
- `severity` ∈ `{"error", "warning", "success"}` (lowercase).
- `userMessage` and `remediation` are **en-US only** (`DECISIONS.md` 2026-05-30 "Status messages are en-US only").
- `context` is a structured retry-hint dict. Redacted of true secrets (wire scope) before leaving the process.
- `cid` is the same 8-hex value used elsewhere on the response.

---

## 14. Best practices

**Pass `idempotencyKey` on every write.** Same key + same payload returns the cached result for up to 24 hours. Same key + different payload throws 9102. Without a key, a transport timeout + LLM auto-retry can append a duplicate detail row (catastrophic for W-2 / 1099 / dependents).

**Use `dryRun: true` to preview bulk operations.** `lacerte_field_write_bulk` honors dryRun per-row; pair with a 3-row preview before the real batch. The canonical bulk-write prompt enforces this.

**Set `readOnly: true` at the session level when you only want reads.** `lacerte_set_context(..., readOnly: true)` hard-blocks every write tool with 9001 before any side effect — ideal for review / triage sessions.

**Check `lacerte_session_status` before writes** to confirm registration and context. The response's `nextSteps[]` array preemptively surfaces "run lacerte-mcp register / call lacerte_set_context / lacerte-mcp login (auth expires in N min)" before you hit a 9006/9007/9009 error envelope.

**Use the `cid` in error envelopes to find log entries.** Every tool call gets an 8-hex correlation ID. Disk wire log: `grep "cid":"a1b2c3d4" logs/lacerte-YYYYMMDD.log`. In-process ring (always-on): `lacerte_recent_log(cid="a1b2c3d4")`.

**Prefer the bulk tools over a Python/JS loop of single calls.** `lacerte_field_write_bulk` (cap 500), `lacerte_detail_write_bulk` (shares the 500 cap), and `lacerte_client_create_bulk` (cap 100) each give you a single MCP round-trip, aggregated outcomes, positional per-row idempotency keys, and stop-on-first-error. Similarly, prefer `lacerte_client_query` over paging the full roster client-side when selecting bulk targets by a field predicate.

**Always pass an `expectedCount` on a bulk write you got from an LLM.** This catches LLM-vs-operator intent drift before any row runs ("the operator said 47 clients but the LLM emitted 200").

**Never reuse one idempotencyKey across multiple field writes.** Each different field needs a distinct key (e.g. `intake-SMIT-C1_1` and `intake-SMIT-C1_3`). Same key + different payload throws 9102.

**Use the operator accelerators (Section 7h) instead of fan-out chains.** `lacerte_client_summary` replaces 5-7 round-trips. `lacerte_firm_dashboard` replaces ~30. `lacerte_diff_clients` is the right tool for "did I import the right one?".

**Call `lacerte_help` early** in any host that doesn't expose a Prompts UI. Single MCP call, zero wire I/O, returns the distilled README including the "what this MCP does NOT do" list — saves the LLM from asking "can you e-file?" / "can you print?" on every fresh session.

**Don't restart the server to refresh schema shards.** A restart invalidates the Intuit 24h auth ticket. Use `lacerte_reload_schema` instead.

---

## 15. Two modes: simulation vs live

| Behavior | Simulation | Live |
| --- | --- | --- |
| Trigger | Default off-Windows OR `LACERTE_SIMULATION=1` | `OS == Win32NT && LACERTE_LIVE=1 && LACERTE_SIMULATION != "1"` |
| Backend | `Session/SimulationStore.cs` in-memory `ConcurrentDictionary` | COM `LacerteAccessClient` + ODBC `LacerteDsnReader` |
| Read path | Sim store query | ODBC SQL templates (TY21+ branched) |
| Write path | Sim store mutation | COM `WriteToDBFDatabase` (pre-TY21) or `UpdateTaxReturn` (TY21+) |
| Registration | `register` returns `AccessGranted` synchronously | Runs `ITPRegistration.Register()` ceremony |
| Seed data | 8 clients across 4 datapaths (`SMIT/JONE/REJE/BROW/PEND/ACME/WIDG/PART`) | Whatever's in your real Lacerte install |
| `lacerte_client_create` | Appends to the in-memory store | Real SDK call; persists to DBF |
| `lacerte_detail_write replaceIndex` | Honored via `ApplySimDetailWriteWithUndo` | **Rejected with 9019** (SDK only appends) |
| 24h auth ticket | N/A | Real; tracked via `ExpiresAt` in session manager |
| Wire log | Records sim calls if enabled | Records COM + ODBC calls |
| Heartbeat writer | Same shape; mode field shows "simulation" | Mode field shows "live" |
| Mutation across processes | Each process has its own store | Real DBF on disk — **don't point two MCPs at the same datapath** |
| Off-Windows | Default mode | Cannot run — Lacerte SDK is local-only |

**The non-negotiable rule:** simulation and live must be observationally identical for the happy path. A tool can't tell which mode it's running in by reading the response. When you change a live behavior, change the simulation too (or stub it). See `CLAUDE.md` "Two-mode discipline" and `DECISIONS.md` 2026-05-30 "Two-mode parity rule".

Mode is resolved once in the `LacerteSessionManager` constructor and is immutable for the process lifetime.

---

## 16. Operational notes

### 24-hour Intuit auth ticket

The Intuit auth ticket has a 24-hour lifetime. The session manager stamps `registeredAt + 24h` at startup and rejects calls past expiry without firing the Intuit dialog (Boundary Invariant #8 in `ARCHITECTURE.md`).

- **Proactive expiry probe:** `LacerteSessionManager.ThrowIfAuthExpired` surfaces synthetic `9007 LoginExpired` with remediation.
- **Reactive path:** raw SDK `NotSignedIn (203)` propagates unchanged if it slips past the probe; `Resilience/AutoReconnect.cs` does not re-auth inside a tool call.
- **Refresh:** run `lacerte-mcp login` from an interactive PowerShell. Reuses the persisted AppID; no fresh consent dialog unless the ticket is fully gone.
- **Detect expiry:** `lacerte_session_status` returns `minutesUntilExpiry` and surfaces a `nextSteps[]` entry below 10 minutes (configured via `ExpirySoonThresholdMinutes` in `SessionTools.cs:159`).

### Wire log

Enable via `LACERTE_DEBUG_WIRE=1`.

- **File location:** `./logs/lacerte-YYYYMMDD.log` relative to the MCP server's working directory. Override with `LACERTE_DEBUG_LOG_PATH`. Path is symlink/junction-resolved and allowlist-checked (P17-010 / P17-016).
- **Format:** newline-delimited JSON (JSONL). Each line is one tool-call structured record with `ts, op, cid, durationMs, args, result, error?`.
- **Rotation:** opt-in via `LACERTE_DEBUG_LOG_MAX_BYTES` (per-file cap), `LACERTE_DEBUG_LOG_MAX_DIR_BYTES` (directory cap), and `LACERTE_DEBUG_LOG_RETENTION_DAYS`.
- **Result summarization:** large result arrays are reduced to `{kind, count, sample}` on insert. Override sample size via `LACERTE_DEBUG_LOG_RESULT_SAMPLE`. Disable summarization with `LACERTE_DEBUG_LOG_FULL_RESULTS=1`.
- **Redaction split:** the on-disk log uses a **broad** keyset (`RedactionScope.Disk`) that scrubs identity + address columns. The on-wire envelope uses a **narrow** keyset (`RedactionScope.Wire`) that scrubs only true secrets (SSN/EIN/TIN, bank/routing, DOB, email, phone) and preserves operator-actionable strings like `context.reason`, `context.rawError`, detail-row `Description`, address `City`/`Zip`. The asymmetry is intentional — see `WireLog.cs:176,222` and `DECISIONS.md` "WireLog redaction: HashSet walker, two scopes".

The disk log file handle stays open for the process lifetime. If your filesystem fills, the writer falls back gracefully — see `Logging/WireLog.cs`.

### Heartbeat writer

Default 30-second cadence; disable with `LACERTE_HEARTBEAT_SECONDS=0`.

- **File location:** `%TEMP%/lacerte-mcp-heartbeat-{pid}.json` (per-process so two MCP instances don't clobber each other — P17-014).
- **JSON shape:** `{ pid, mode, registered, expiresAt, minutesUntilExpiry, buildVersion, writtenAtUtc }`.
- **External watchers:** Task Scheduler / NSSM / PowerShell scripts can detect a hung server via file mtime + read the JSON for liveness + 24h-auth state, all without speaking MCP.

### Idempotency cache

- **TTL:** 24 hours (constant `IdempotencyTtl` in `LacerteSessionManager.cs`; no env override today).
- **Capacity:** sliding eviction; per-key `SemaphoreSlim` for write serialization.
- **Fingerprint:** unkeyed SHA-256 over (op, session context `dataPath`/`taxYear`/`module`, args, `dryRun`) — stable across process restarts (R6-01 removed the per-process HMAC key that broke persisted replay after a real restart). Including `dryRun` (P18) means a preview-then-commit pair sharing one key conflicts instead of silently replaying; including the context triple (R6-03) means a "stable" key reused after a `lacerte_set_context` year/module switch surfaces `9102` instead of silently replaying the old context's result while the new-context write never lands.
- **Success-only caching (R6-04):** only `Success` results are stored. The live bridge returns raw failure ints (e.g. `DBFNotAccessable (2)` while the return is open in the Lacerte UI) — those are never cached, so closing the file and retrying the same key actually executes instead of replaying a sticky 24h failure.
- **Persistence:** **default ON** (replay protection across host restarts is the safer posture); disable with `LACERTE_IDEMPOTENCY_PERSIST=0` (exact string `0` only). Storage at `%APPDATA%/lacerte-mcp/idempotency-cache.json`; override with `LACERTE_IDEMPOTENCY_PERSIST_PATH`. Writes are multi-instance safe (R6-11): each process writes a per-PID `.tmp` and every persist is **merge-on-write** (the current disk file is re-read and merged before replace), so two MCP instances — e.g. Claude Desktop + Cursor — don't clobber each other's entries. Expired entries are filtered on load.
- **Replay:** same key + same fingerprint → cached result (across restarts too); same key + different fingerprint → `9102 IdempotencyConflict`.
- **Conflict envelope:** the SHA-256 prefix is hidden by default (P17-007) to prevent brute-force oracle attacks on small payloads; revealed only under `LACERTE_DEBUG_WIRE=1`.
- **Bulk per-row keys:** positional — `{prefix}-{index}-{clientNumber}-{field}` (field bulk), `{prefix}-{index}-{clientNumber}-{series}-{code}` (detail bulk), `{prefix}-{index}-{clientId}` (create bulk). A batch retry must resubmit the same `entries[]` in the same order.
- **Preview/commit convention:** dry-run pass uses `bulk-<batchId>-preview`, commit uses `bulk-<batchId>` — a shared key across `dryRun` values throws `9102`.

### AutoReconnect

Source: `Resilience/AutoReconnect.cs`.

- **Trigger:** raw SDK `NotSignedIn (203)` from the live bridge on an idempotent read.
- **Action:** one-shot reconnect attempt; logs `op="AutoReconnect"` with `cid`. Does **not** re-auth the 24h Intuit ticket (Boundary Invariant #8).
- **Non-idempotent writes:** not retried.
- **Counters:** `lacerte_request_stats` surfaces `autoReconnectAttempts` and `autoReconnectExhausted`.

---

## 17. Troubleshooting

### "No tools appear in Claude Desktop"

Three independent failure modes present with the same silent symptom (most hosts swallow spawned-process stderr by default). Walk this diagnostic ladder:

1. **Confirm the server binary boots.** From PowerShell on the same machine: `& "C:\path\to\LacertMCP.Server.exe"` (use the absolute path from your host config). If it crashes with a CLR loader error, see "BadImageFormatException" below. If it hangs waiting for stdio, that's healthy — Ctrl+C and continue.
2. **Confirm the assembly registers tools at all.** Run `& "C:\path\to\lacerte-mcp.exe" tools --names-only` (reflection only, no stdio handshake). If this lists 42 tools beginning with `lacerte_` (the listing mirrors the MCP wire schema — `CancellationToken` parameters are excluded), the server is healthy and the problem is in the host config: typically (a) wrong path in `command` field — note that `LacertMCP.Server.exe` and `lacerte-mcp.exe` are **different binaries**, only the former is the MCP server; (b) unescaped backslashes in the JSON path string (use forward slashes); (c) `LACERTE_LIVE=1` set on a machine with no registered AppID. Run `lacerte-mcp doctor` to confirm.
3. **Inspect the host's stderr capture.** Claude Desktop writes spawned-MCP stderr to `%APPDATA%\Claude\logs\mcp*.log`. Cursor writes to `~/.cursor/logs/`. Windsurf surfaces it in its log pane.

### "doctor shows red"

Identify which probe failed. The output names every probe; each red carries a one-line remediation pointer. Common patterns:

- **ODBC DSN `LacerteDSII` not found** — the SDK installer registers it under the 64-bit ODBC hive (`HKLM\SOFTWARE\ODBC\ODBC.INI\LacerteDSII`). 32-bit `odbcad32.exe` (under `SysWOW64`) won't see it. Use the 64-bit `odbcad32.exe` at `C:\Windows\System32\odbcad32.exe` → "System DSN" tab. If absent, reinstall the 64-bit SDK MSI.
- **AppID GUID missing** — fresh checkout. Run `lacerte-mcp register`.
- **App.config shim missing** — `App.config` next to `LacertMCP.Server.exe` was deleted. Restore from source control or re-run `dotnet build`.
- **`LACERTE_DATA_PATH` directory does not exist** — fix the env var to the correct Lacerte data folder.
- **FieldAccessMap base shards missing** — the build did not copy `Schema/FieldAccessMap/base/*.json` next to the exe. Re-run `dotnet build` or re-extract the release zip.

### "Register doesn't open the Intuit dialog"

- **Exit 2 with `PARTIAL` on stderr:** the Lacerte SDK COM surface is unavailable (`TPRegistration` ProgID missing / COM activation failed) — typically the 64-bit SDK MSI isn't installed. The AppID is persisted and intact. Install the 64-bit MSI, confirm `lacerte-mcp doctor` shows the "Lacerte COM ProgID" probe green, then re-run `lacerte-mcp register`. (Older docs/scripts referenced an exit-3 `[partial]` Phase-9 stub — that contract is retired; the ceremony is wired via `Bridge/Registration.cs`.)
- **Exit 0/1 but no dialog appeared:** you're running from a **non-interactive** shell (SSH, Windows service, scheduled task). The Intuit `ITPRegistration` COM call needs a desktop session. Open a normal interactive PowerShell on the operator's desktop session and re-run. Confirm with `lacerte-mcp status` that `registered: true`.

### "24-hour login expired"

Symptom: reads + writes that worked yesterday now fail with `LoginExpired (9007)` (proactive) or raw SDK `NotSignedIn (203)` (reactive). Remediation is in the envelope.

Fix: from an interactive PowerShell, run `lacerte-mcp login --app-name "Operator Lacerte MCP" --tax-year 2024`. This re-calls `Register()` to mint a fresh 24h ticket using the existing AppID. Exit 0 = `AccessGranted`; exit 1 = the refresh failed loudly (status name + remediation on stderr — fall back to `lacerte-mcp register` if the AppID is unknown); exit 2 = SDK COM surface unavailable.

### "BridgeUnavailable on a Lacerte install"

Symptom: live-mode writes throw an opaque ODBC INSERT crash, often with `DBFNotAccessable (2)` or a generic Win32 fault. Or `9201 BridgeUnavailable` envelope.

Diagnosis: the **32-bit** Lacerte SDK MSI is installed instead of the 64-bit MSI. The 32-bit driver has a known INSERT bug and is unsupported.

Fix: uninstall the 32-bit MSI from Add/Remove Programs, then install the 64-bit Lacerte SDK MSI from Intuit's developer portal. Verify via 64-bit `odbcad32.exe`.

If you're already on 64-bit and seeing 9201: enable `LACERTE_DEBUG_WIRE=1`, restart, reproduce, and grep the log for the `cid` value on the error envelope. The bridge stderr will be there. Set the flag **before** the failure — it must be on to capture the bridge startup error.

### "BadImageFormatException or MissingMethodException on server boot"

The `App.config` next to `LacertMCP.Server.exe` was deleted or modified. The Lacerte SDK is a mixed-mode CLR assembly that needs `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime v4.0 / .NETFramework v4.6` set in the host's `.config`.

Fix: restore `App.config` from source control or re-run `dotnet build` (MSBuild copies it next to the output).

### "Build succeeds but tests fail"

Run the project-specific verifier first: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test C:/Users/VR/Projects/LacertMCP/src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64`.

Expected counts: **0 failed**, with the current pass/skip totals pinned in `HANDOFF.md` "Verify Before Continuing" (the pass count grows as tests land; the operative invariants are 0 failed + the documented skip set). When the `LACERTE_PARITY_RECORDING` fixture is absent, ~29 Live-leg parity-theory rows drop and `LiveLegParityPlaceholder.LiveLeg_AwaitsRecordedReplayFixture` adds 1 honest Skip.

The 11 expected skips are:
- 9 `[Fact(Skip = "Requires real Lacerte SDK")]` in `Tests/Bridge/`.
- 1 Windows-only Credential Manager P/Invoke in `Tests/Cli/AppIdStoreTests.cs`.
- 1 `LiveLegParityPlaceholder` (flips green when the operator drops a recording under `tests/Parity/fixtures/`).

If you see failures beyond these, check the test output for `[Collection("AccessMapMutation")]` / `[Collection("Cli (serial)")]` ordering issues — tests that touch static state are serialized by collection markers.

### "Wire log fills disk"

Defaults are unbounded — enable rotation explicitly:

```text
LACERTE_DEBUG_LOG_MAX_BYTES=10485760           # 10 MiB per file
LACERTE_DEBUG_LOG_MAX_DIR_BYTES=104857600      # 100 MiB total
LACERTE_DEBUG_LOG_RETENTION_DAYS=14            # delete files older than 14 days
LACERTE_DEBUG_LOG_RESULT_SAMPLE=3              # smaller sample for big rows
```

These take effect at process start.

### "`lacerte_field_write` returned success but my change isn't visible in Lacerte"

Lacerte's editor displays a cached view of the currently-open return; DBF / COM writes land on disk, but the editor does not reread them while the client is open. **Structural** to how the Lacerte desktop app works, not an MCP defect.

Verify-then-refresh:
1. Verify via the MCP: `lacerte_client_read` (or `lacerte_table_read`) against the same client + field. If the MCP read returns the new value, the write succeeded; the Lacerte UI is just stale.
2. Refresh the Lacerte UI: File → Close Client and reopen. The editor will reread from disk.

If the MCP read still shows the old value, the write itself failed silently — escalate (check the wire log when `LACERTE_DEBUG_WIRE=1`).

### "`lacerte_list_datapaths` returns an empty list"

Either `LACERTE_DATA_ROOT` is not set and the install isn't under `C:\Lacerte`, or the env var was misspelled. The **real** name is `LACERTE_DATA_PATH` (path-with-underscore) and `LACERTE_DATA_ROOT` (root-with-underscore). Watch for `LACERTE_DATAPATH` (no underscore) — that's wrong and silently falls through.

Set `LACERTE_DATA_ROOT=C:\path\to\your\Lacerte\root` in the MCP client config and restart.

### "Writes return `UnknownFieldAccess (9012)` after running the dumper"

The server caches shards at startup and does **not** re-scan disk on every write. A restart would invalidate the Intuit 24h ticket. Call `lacerte_reload_schema` instead — see Workflow G.

---

## 18. Architecture overview

The MCP server runs as a stdio-bound process on the same Windows machine as Lacerte. There is no hosted variant possible — the Lacerte SDK is local-only. Tools never construct SQL or COM calls directly; they speak in C# parameters to the session manager, which dispatches to either the COM bridge (`Bridge/LacerteAccessClient.cs`) for writes, the ODBC layer (`Odbc/LacerteDsnReader.cs`) for reads, or the in-memory simulation store. Status codes are always structured; the on-wire response envelope is generated by `Errors/WrapToolHandler.cs`. The 24-hour Intuit auth ticket is never refreshed inside a tool call — that happens only via the CLI.

```text
┌────────────────┐  JSON-RPC/stdio  ┌─────────────────────────────────────────┐
│  MCP client    │ ◄──────────────► │  LacertMCP.Server (this, net48 x64)     │
└────────────────┘                  │                                         │
                                    │  Tools/*.cs — [McpServerTool] attrs     │
                                    │      │                                  │
                                    │      ▼                                  │
                                    │  Session/LacerteSessionManager.cs       │
                                    │   - Registration / 24h login state      │
                                    │   - Context (datapath, year, module)    │
                                    │   - CanUseNewApis branching             │
                                    │   - Read-only gate (synth 9001)         │
                                    │   - Idempotency cache (synth 9102)      │
                                    │   - Dry-run snapshot/restore            │
                                    │      │                                  │
                                    │      ├─► Bridge/LacerteAccessClient.cs  │ ← live: COM/.NET
                                    │      ├─► Odbc/LacerteDsnReader.cs       │ ← live: ODBC reads
                                    │      └─► Session/SimulationStore.cs     │ ← sim:  in-memory
                                    │                                         │
                                    │  Schema/  — modules, tables, fields     │
                                    │  Errors/  — TAPIStatus + 9xxx synth     │
                                    └─────────────────────────────────────────┘
```

Boundary invariants live in `ARCHITECTURE.md` "Boundary Invariants". The two-mode discipline rule is in `CLAUDE.md` "Two-mode discipline".

---

## 19. Where to go next

- [README.md](README.md) — high-level intro + install table + tool table.
- [ARCHITECTURE.md](ARCHITECTURE.md) — layer responsibilities + boundary invariants + data flow.
- [DECISIONS.md](DECISIONS.md) — dated tradeoff log (cross-tool naming, ODBC reads-only, 64-bit MSI, two-mode parity, etc.).
- [REGRESSION_CHECKLIST.md](REGRESSION_CHECKLIST.md) — pre-completion verification list, including manual live-mode verification.
- [REQUIREMENTS.md](REQUIREMENTS.md) — product truth + operator capabilities matrix.
- [ACCEPTANCE_CRITERIA.md](ACCEPTANCE_CRITERIA.md) — what "done" means per task.
- [Lacerte_MCP_Structure_Spec.md](Lacerte_MCP_Structure_Spec.md) — source spec mined from the Lacerte SDK Developer Instructions PDF.
- [HANDOFF.md](HANDOFF.md) — current session state + verify-before-continuing checklist.
- [Todo.md](Todo.md) — ordered task queue (Phases 14-18 complete; Phase 9 + Windows-gated work pending).
- [CLAUDE.md](CLAUDE.md) — project operating system for AI-assisted development.
- [docs/research/agents/](docs/research/agents/) — research outputs that informed decisions.
- Source spec for prompts: [src/LacertMCP.Server/Prompts/WorkflowPrompts.cs](src/LacertMCP.Server/Prompts/WorkflowPrompts.cs).
- CLI source: [src/LacertMCP.Cli/Commands/](src/LacertMCP.Cli/Commands/).
- Error catalogs: [src/LacertMCP.Server/Errors/StatusCodes.cs](src/LacertMCP.Server/Errors/StatusCodes.cs) + [SyntheticStatusCodes.cs](src/LacertMCP.Server/Errors/SyntheticStatusCodes.cs).

---

## 20. Glossary

- **MCP** — Model Context Protocol. Anthropic + Microsoft's open spec for connecting LLMs to tools / resources / prompts over a transport (stdio in our case). The C# SDK is `ModelContextProtocol` 0.3.0-preview.4 on NuGet (1.x not yet released).
- **Lacerte** — Intuit's professional tax-prep desktop application. Multi-module: Individual (1040), Corporate (1120), Partnership (1065), S-Corp (1120S), Fiduciary (1041), Exempt (990), Estate (706), Gift (709), Benefit (5500).
- **Lacerte SDK** — Intuit's developer SDK for Lacerte. Two surfaces: `LacerteSDK_Access` (a mixed-mode .NET/COM assembly for writes + registration) and the `LacerteDSII` ODBC driver (for reads, FoxPro-style).
- **TAPIStatus** — the Lacerte SDK return code enum for general SDK calls. Range 0..28. See Section 12.
- **TPSDKStatus** — the Lacerte SDK return code enum for registration calls. Range 200..204. Folded into the same flat namespace as TAPIStatus, with the `VersionBlocked` collision resolved by namespacing as `RegVersionBlocked = 202`.
- **DBF** — dBASE table file format. Lacerte stores its main tables (DATA1..DATA8) as DBF files under `{YY}Tax\{Module}Data\`.
- **TY18+** — tax year 2018 and later. The SDK floor; `lacerte_set_context` rejects `taxYear < 2018` with `9003 TaxYearTooOld`.
- **TY21+** — tax year 2021 and later. The branch point for the new `UpdateTaxReturn` write API; pre-TY21 writes go through `WriteToDBFDatabase`. The session manager handles the branch (Boundary Invariant #5).
- **cid** — correlation ID. An 8-hex `AsyncLocal` value stamped by `WrapToolHandler` on every tool call. Appears on the response envelope, on nested `WireLog.Record` entries in the disk log, and on the in-process WireLog ring entries surfaced by `lacerte_recent_log`. Use it to grep the whole causal chain for one tool call.
- **AppID** — the per-install GUID minted by `lacerte-mcp register`. Persisted to Windows Credential Manager (`target=lacerte-mcp account=app-id`) or to `%APPDATA%/lacerte-mcp/config.json` off-Windows. Bearer credential — back it up via `lacerte-mcp status --show-app-id`.
- **COM ProgID** — the COM-registered program identifier for a Lacerte SDK object. Probed by `lacerte-mcp doctor` as an install-sanity check.
- **ODBC DSN** — Data Source Name. The Lacerte SDK installer registers a DSN named `LacerteDSII` under the 64-bit ODBC hive. The MCP uses DSN-less connection strings at runtime (composed from driver + datapath + module + year by `Odbc/ConnectionStringBuilder.cs`); the DSN itself is only probed by doctor as an install-sanity check.
- **DATA1..DATA7** — the seven DBF tables the MCP exposes. 1=Client (`lacerte_client_*`), 2=SubClient (dependents/partners/shareholders), 3=State, 4=StateSubClient, 5=Status (F4), 6=Display (read-only), 7=Forms (read-only). DATA8 is reserved and rejected.
- **Series ID** — the Lacerte detail-file screen identifier. Numeric (11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners). Drives the `series` filter on `lacerte_detail_read` and the validation on `lacerte_detail_write`.
- **Detail file** — the per-client `{CLIENT}.{Mod}{YY}` row-ordered store with no primary key. Read via `lacerte_detail_read`, written via `lacerte_detail_write`. Live writes append only; `replaceIndex` is sim-only.
- **Module character** — single letter identifying the Lacerte module. I (Individual), C (Corporate), P (Partnership), S (S-Corp), F (Fiduciary), R (Exempt), T (Estate), N (Gift), B (Benefit). Always typed `string` length-1 on the wire (P16).
- **ClientNumber vs ClientID** — ClientNumber is the SDK-minted monotonic identifier (sim's counter, e.g. `"1001"`); ClientID is the operator-chosen 1-8 character alphanumeric (e.g. `"BROW"`). Both are surfaced in seeded fixtures as separate fields. Never interchange them.
- **Wire log scope (Disk vs Wire)** — Disk scope is a broad redaction keyset for the on-disk debug log (scrubs identity + address columns). Wire scope is a narrower subset for the on-the-wire response envelope (scrubs only true secrets). The asymmetry preserves operator-actionable diagnostic strings in tool responses while keeping the disk log safe.
- **Idempotency key** — a stable client-supplied string keyed on op+target+intent. Same key + same payload returns the cached result for up to 24 hours; same key + different payload throws 9102. Without a key, transport retries can append duplicate rows.
- **Dry-run** — `dryRun: true` runs the full validation + access-map gate then rolls back without persisting. Returns the would-be TAPIStatus. Used by bulk-write preview workflows.
- **Read-only session** — `lacerte_set_context(..., readOnly: true)` hard-blocks every write tool with `9001 FieldIsReadOnly` before any side effect. Hard, session-level safety gate.
- **Sim mode / Live mode** — see Section 15. Two-mode discipline: observationally identical for the happy path.
- **AutoReconnect** — the optional one-shot reconnect on idempotent reads when raw SDK `NotSignedIn (203)` slips past the proactive expiry probe. Does **not** refresh the 24h ticket.
- **WrapToolHandler** — the decorator in `Errors/WrapToolHandler.cs` that formats success/error envelopes as JSON and maps `LacerteSDKError` to consistent error shapes. Any tool that bypasses it is a bug.
- **LacerteSessionManager** — the singleton that owns registration lifecycle, context, read-only / dry-run / idempotency gates, and dispatches reads to ODBC, writes to COM, both to simulation when in sim mode.

---

*Document version: tracks v0.1.x of the Lacerte MCP. Generated against the current source tree under `C:/Users/VR/Projects/LacertMCP/`. For corrections, edit this file and submit a PR.*
