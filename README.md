# Lacerte MCP

A local Windows MCP server that exposes Intuit Lacerte tax software (TY18+) as tools an LLM can call. Wraps the Lacerte SDK (`LacerteSDK_Access` COM/.NET + `LacerteDSII` ODBC) over JSON-RPC/stdio.

**Status:** v0.1.0 scaffolding — 469/480 tests passing (11 expected live-only skips), 0 failures, 0 warnings. Read/Write/Create tools, simulation store, ODBC layer, COM bridge scaffolding, CLI, prompts, and hardening primitives all land. Live verification requires a Windows + Lacerte + SDK box (see `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification" section).

## What this is

* **Local bridge** — runs on the same Windows machine as Lacerte. There is no hosted variant possible (Lacerte SDK is local-only).
* **Read/Write/Create** — read DBF tables 1–7 + detail files + EF status; write per-field with TY21+ branching; create new clients.
* **Two modes** — `live` (real SDK) and `simulation` (in-memory DBF fixtures, default off-Windows) so most development can happen anywhere.

## Architecture (one-line)

`MCP client ⇄ stdio ⇄ Tool layer ⇄ Session manager ⇄ {COM bridge | ODBC reader | Simulation store}`

Full architecture in [ARCHITECTURE.md](ARCHITECTURE.md). Per-decision rationale in [DECISIONS.md](DECISIONS.md). Product truth in [REQUIREMENTS.md](REQUIREMENTS.md).

## Quick start (dev / simulation mode)

```powershell
# Use the user-local .NET 8 SDK (system-wide dotnet is runtime-only on this box)
& "$env:USERPROFILE\.dotnet\dotnet.exe" build LacertMCP.sln -c Debug -p:Platform=x64
& "$env:USERPROFILE\.dotnet\dotnet.exe" test  src/LacertMCP.Tests/LacertMCP.Tests.csproj --no-build -c Debug -p:Platform=x64

# Smoke-test the CLI
& src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe status
& src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor   # exit 1 off-Windows; that's expected
```

The MCP server binary is `src/LacertMCP.Server/bin/x64/Debug/LacertMCP.Server.exe`. It runs in simulation mode by default off-Windows or when `LACERTE_SIMULATION=1`.

## Install paths (per MCP client)

### Claude Desktop (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "lacerte": {
      "command": "C:\\path\\to\\LacertMCP\\src\\LacertMCP.Server\\bin\\x64\\Release\\LacertMCP.Server.exe",
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

### Environment variables (full reference)

| Var | Required? | Default | Purpose |
|---|---|---|---|
| `LACERTE_LIVE` | one of LIVE / SIMULATION required | unset | `1` enables live SDK on Windows. |
| `LACERTE_SIMULATION` | one of LIVE / SIMULATION required | `1` off-Windows | `1` forces simulation regardless of platform. |
| `LACERTE_DATA_PATH` | live mode | unset | Default datapath (e.g. `C:\Lacerte\24data`). |
| `LACERTE_DEFAULT_TAX_YEAR` | recommended | unset | Default year used when `lacerte_set_context` omits one. |
| `LACERTE_APP_NAME` | recommended | `LacerteMCP` | Friendly name in the SDK consent dialog. |
| `LACERTE_APP_ID` | runtime read-only | from Credential Manager | Source of truth is Windows Credential Manager (set via `lacerte-mcp register`). |
| `LACERTE_DSN` | optional | `LacerteDSII` | ODBC DSN name. |
| `LACERTE_DEBUG_WIRE` | optional | `0` | `1` logs COM + SQL to `logs/lacerte-YYYYMMDD.log`. Secrets redacted. |
| `LACERTE_DEBUG_LOG_PATH` | optional | `./logs` | Override the log directory. |
| `LACERTE_SIM_EXPIRED` | testing only | unset | `1` simulates an expired 24h auth session. |

## Lacerte SDK install (Windows operator)

1. **Install Intuit Lacerte** (TY18+) and complete its first-run setup. The MCP cannot bootstrap an uninitialized Lacerte.
2. **Install the 64-bit Lacerte SDK MSI** from [Intuit's developer portal](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started). The 32-bit driver has a known INSERT crash and is not supported by this MCP. (See `DECISIONS.md` 2026-05-30 "64-bit host process and 64-bit Lacerte SDK MSI required".)
3. **Verify the install** with the doctor command:
   ```powershell
   & src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe doctor
   ```
   Exit 0 means all 7 probes are green (Windows version, x64 process, .NET 4.8, ODBC DSN, SDK install dir, env, AppID).
4. **Run the one-time registration** ceremony — this surfaces the consent + Intuit sign-in dialogs:
   ```powershell
   & src/LacertMCP.Cli/bin/x64/Debug/lacerte-mcp.exe register --app-name "Operator Lacerte MCP" --tax-year 2024
   ```
   Mints an AppID GUID, persists it to Windows Credential Manager, calls `ITPRegistration.Register()`, and prints the GUID for backup.
5. **Within 24 hours, set `LACERTE_LIVE=1` in your MCP client config** and the server will silently re-attach to the cached session. After 24h, run `lacerte-mcp login` to refresh.

The `App.config` next to `LacertMCP.Server.exe` carries `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime v4.0 / .NETFramework v4.6` — load-bearing for the Lacerte mixed-mode CLR shim. Don't delete it.

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
| `lacerte_health` | Health probe. Returns "reachable (sim)" in sim mode. | 0 |

### Write / Create / Metadata tools (Phases 6-8)

| Tool | What it does | Phase |
| --- | --- | --- |
| `lacerte_field_write` | Write one DBF field (pre-TY21 entrypoint). Honors readOnly/dryRun/idempotencyKey. | 6 |
| `lacerte_update_return` | Write one DBF field (TY21+ entrypoint, `UpdateTaxReturn`). Same gates as above. | 6 |
| `lacerte_detail_write` | Append/replace a detail-file row with mandatory client-side validation. | 6 |
| `lacerte_client_create` | Create a new client (ClientID ≤ 8 chars, TaxYear ≥ 2018, TaxType matches module). | 7 |
| `lacerte_field_access_map` | Return per-field ReadOnly/ReadWrite/Hidden/NotExposed access map. | 8 |
| `lacerte_return_codes` | Discoverable resource: all 28 TAPIStatus + 5 TPSDKStatus + 11 synthetic 9xxx codes. | 8 |

## CLI

| Command | Purpose |
| --- | --- |
| `lacerte-mcp register` | One-time AppID mint + Intuit consent + sign-in dialogs. |
| `lacerte-mcp login` | Re-call `Register()` to refresh the 24h auth ticket. |
| `lacerte-mcp status` | JSON snapshot — no wire I/O. |
| `lacerte-mcp field-access {set,list,discover}` | Manage the per-field access map (`discover` requires live SDK). |
| `lacerte-mcp doctor` | 7-probe setup verification; exit 0/1/2 contract. |

## Reference

* Source spec: [Lacerte_MCP_Structure_Spec.md](Lacerte_MCP_Structure_Spec.md)
* Operating system for this codebase: [CLAUDE.md](CLAUDE.md)
* Architecture: [ARCHITECTURE.md](ARCHITECTURE.md)
* Decisions log: [DECISIONS.md](DECISIONS.md)
* Acceptance criteria: [ACCEPTANCE_CRITERIA.md](ACCEPTANCE_CRITERIA.md)
* Regression checklist (incl. manual live-mode verification): [REGRESSION_CHECKLIST.md](REGRESSION_CHECKLIST.md)
* Build sequence + remaining work: [Todo.md](Todo.md)
* Research outputs: [docs/research/agents/](docs/research/agents/)
