# Lacerte MCP

A local Windows MCP server that exposes Intuit Lacerte tax software (TY18+) as tools an LLM can call. Wraps the Lacerte SDK (`LacerteSDK_Access` COM/.NET + `LacerteDSII` ODBC) over JSON-RPC/stdio.

**Status:** v0.1.0 scaffolding — 481/492 tests passing (11 expected live-only skips), 0 failures, 0 warnings. Read/Write/Create tools, simulation store, ODBC layer, COM bridge scaffolding, CLI, prompts, and hardening primitives all land. Live verification requires a Windows + Lacerte + SDK box (see `REGRESSION_CHECKLIST.md` "Manual Live-Mode Verification" section).

## What this is

* **Local bridge** — runs on the same Windows machine as Lacerte. There is no hosted variant possible (Lacerte SDK is local-only).
* **Read/Write/Create** — read DBF tables 1–7 + detail files + EF status; write per-field with TY21+ branching; create new clients.
* **Two modes** — `live` (real SDK) and `simulation` (in-memory DBF fixtures, default off-Windows) so most development can happen anywhere.

## Architecture (one-line)

`MCP client ⇄ stdio ⇄ Tool layer ⇄ Session manager ⇄ {COM bridge | ODBC reader | Simulation store}`

Full architecture in [ARCHITECTURE.md](ARCHITECTURE.md). Per-decision rationale in [DECISIONS.md](DECISIONS.md). Product truth in [REQUIREMENTS.md](REQUIREMENTS.md).

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

For an operator install, run `./scripts/release.ps1` to produce `dist/LacertMCP-v0.1.0.zip`, unzip it on the operator box (e.g. to `C:\LacertMCP\`), then point Claude Desktop at the staged server binary:

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

For dev / sim against the source tree, swap `C:\\LacertMCP\\server\\LacertMCP.Server.exe` for `C:\\path\\to\\LacertMCP\\src\\LacertMCP.Server\\bin\\x64\\Release\\LacertMCP.Server.exe`.

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
| `LACERTE_DEBUG_WIRE` | optional | `0` | `1` logs COM + SQL to `logs/lacerte-YYYYMMDD.log`. Secrets redacted. |
| `LACERTE_DEBUG_LOG_PATH` | optional | `./logs` | Override the log directory. |
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

## Prompts

Three first-class MCP prompts (registered via `WithPromptsFromAssembly()` in `Program.cs`, discoverable through the standard MCP `prompts/list` handshake):

| Prompt | What it walks the agent through | Args |
| --- | --- | --- |
| `lacerte_client_intake` | Onboard a new client end-to-end: verify session → pick datapath/module/year → `lacerte_client_create` → seed DATA1 fields → seed detail rows → verify roster. | `clientName?: string` |
| `lacerte_review_ef_status` | Triage e-file status: pull EFiling+EFStatus join, group by Status, surface Rejected first, flag stale Pending/Transmitted (>72h). TY19+ only. | `taxYear?: int` |
| `lacerte_bulk_field_update` | Bulk field write across many clients with mandatory dry-run preview (first N), idempotency keys, stop-on-first-error. | `table: int = 1`, `field: string` (required), `previewCount: int = 3` |

Canonical step lists live in `src/LacertMCP.Server/Prompts/WorkflowPrompts.cs`.

## CLI

TL;DR:

| Command | Purpose |
| --- | --- |
| `lacerte-mcp register` | One-time AppID mint + Intuit consent + sign-in dialogs. |
| `lacerte-mcp login` | Re-call `Register()` to refresh the 24h auth ticket. |
| `lacerte-mcp status` | JSON snapshot — no wire I/O. |
| `lacerte-mcp unregister` | Wipe the persisted AppID (typed `--confirm` required). |
| `lacerte-mcp tools` | List every MCP tool the server registers (reflection — no stdio boot). |
| `lacerte-mcp field-access {set,list,discover}` | Manage the per-field access map (`discover` requires live SDK). |
| `lacerte-mcp doctor` | 7-probe setup verification; exit 0/1/2 contract. |

### `lacerte-mcp register [--app-name X] [--tax-year Y] [--reset]`

One-time AppID mint + Intuit consent + sign-in ceremony. Mints a per-install AppID GUID, persists it via the AppID store (Credential Manager on Windows, file fallback off-Windows), then on Windows + live SDK calls `ITPRegistration.Register(...)` which surfaces the consent + Intuit dialogs to the interactive shell.

- `--app-name` (default `Operator Lacerte MCP`) — friendly name shown in the Intuit consent dialog.
- `--tax-year` (default = prior calendar year) — tax year the registration is scoped to.
- `--reset` (default `false`) — **operator recovery path**: mints a fresh GUID even if one exists, forcing a new Intuit consent ceremony. Use this when the AppID is lost or corrupted.

Must be run from an **interactive PowerShell** (not a service / scheduled task) so the consent dialog can surface.

### `lacerte-mcp login [--app-name X] [--tax-year Y]`

Re-call `Register()` to refresh the 24h Intuit auth ticket. Use this when `lacerte_session_status` reports `ticketExpired: true` or when a tool starts returning `NotSignedIn` errors. Reuses the persisted AppID; no fresh consent dialog unless the ticket is fully gone.

- `--app-name` (default `Operator Lacerte MCP`)
- `--tax-year` (default = prior calendar year)

### `lacerte-mcp status [--show-app-id]`

JSON snapshot of registration state, mode, storage backend, and platform. Zero wire I/O — safe to run on a cron. By default the AppID is masked (first 8 chars + `****`); `--show-app-id` prints the full GUID, useful for **backup before a Lacerte reinstall**.

### `lacerte-mcp unregister --confirm`

Wipe the persisted AppID GUID from the active storage backend (Windows Credential Manager on Windows, the file fallback off-Windows). This is the discoverable counterpart to `register --reset` — use it for a clean uninstall or when you need to fully decommission an install (today the only alternative is `cmdkey /delete` or hand-editing the JSON file).

- `--confirm` (default `false`) — **required**. Without it the command refuses (exit 1) and prints a preview of what would be deleted. After `unregister`, the next `lacerte-mcp register` will mint a fresh GUID and force a new Intuit consent ceremony (Windows + live SDK).
- No-op when nothing is persisted: exits 0 with a "nothing to unregister" message so it's safe to run in cleanup scripts.

### `lacerte-mcp tools [--json]`

List every MCP tool the server registers, by reflecting over the `LacertMCP.Server` assembly. Mirrors the SDK's own `WithToolsFromAssembly()` discovery — no stdio transport boot required, runs anywhere (including off-Windows). Useful for sanity-checking the tool surface before pasting into a Claude Desktop / Cursor MCP config, or for quick before/after diffs between Phase milestones.

- Default human-readable table: tool name, description, parameter names + types.
- `--json` emits a machine-parseable array of `{ name, description, declaringType, parameters[] }` triples.

### `lacerte-mcp field-access {set | list | discover}`

Operator surface for the per-field access map (`Schema/FieldAccessMap/base/<module>.json`).

- `list --module <I|C|P|S|F|R|T|N|B> --table <1..7> [--year YYYY]` — print every entry for a module/table at the requested year (overlays applied).
- `set --module <X> --table <N> --field <F> --access <ReadWrite|ReadOnly|Hidden|NotExposed|NotSupported|Unknown> [--data-type ...] [--max-length ...] [--source manual|spec|introspect] [--note ...]` — upsert one field entry into the base map (re-serialized sorted so PR diffs stay stable).
- `discover` — **stub today; always exits 2.** Bulk field-access introspection needs the live SDK; tracked as follow-up F5 in `Todo.md`.

### `lacerte-mcp doctor`

Seven-probe setup verification: Windows version, x64 process, .NET 4.8, ODBC DSN, SDK install dir, env vars (`LACERTE_DATA_PATH`), persisted AppID. Exit-code contract:

- `0` — all probes green.
- `1` — at least one probe failed (e.g. fresh checkout with no AppID; ODBC DSN missing).
- `2` — at least one probe was skipped (no fails) — typical off-Windows result since the Windows-only probes can't run.

## Troubleshooting

The setup involves an interactive Intuit consent dialog, a 24-hour ticket expiry, a 32-bit-vs-64-bit MSI trap, a mixed-mode CLR shim, and ODBC DSN configuration. The most common symptoms and their fixes:

### `lacerte-mcp register` opened no dialog (or hangs)

- **Symptom:** The command returns immediately or hangs with no Intuit consent window.
- **Diagnosis:** Run from a **non-interactive** shell (SSH session, Windows service, scheduled task) — the Intuit ITPRegistration COM call needs a desktop session to surface its UI.
- **Fix:** Open a normal interactive PowerShell on the operator's desktop session and re-run `lacerte-mcp register`. Confirm with `lacerte-mcp status` that `registered: true`.

### Tools start returning `NotSignedIn` after a day

- **Symptom:** Reads + writes that worked yesterday now fail with status `NotSignedIn (9101)` or `TicketExpired (9103)`.
- **Diagnosis:** Intuit's auth ticket has a 24-hour lifetime. The session manager surfaces a clear remediation, but operators can short-circuit it.
- **Fix:** From an interactive PowerShell, run `lacerte-mcp login --app-name "Operator Lacerte MCP" --tax-year 2024`. This re-calls `Register()` to mint a fresh 24h ticket using the existing AppID.

### `lacerte_field_write` or `lacerte_detail_write` crashes the server with an INSERT error

- **Symptom:** Live-mode writes throw an opaque ODBC INSERT crash, often with `DBFNotAccessable (2)` or a generic Win32 fault.
- **Diagnosis:** The 32-bit Lacerte SDK MSI is installed instead of the 64-bit MSI. The 32-bit driver has a known INSERT bug and is unsupported by this MCP (see `DECISIONS.md` 2026-05-30 "64-bit host process and 64-bit Lacerte SDK MSI required").
- **Fix:** Uninstall the 32-bit MSI from Add/Remove Programs, then install the 64-bit Lacerte SDK MSI from [Intuit's developer portal](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started). Verify via the 64-bit `odbcad32.exe` (under `C:\Windows\System32`) that the `LacerteDSII` DSN appears under "System DSN".

### Server fails to start with `BadImageFormatException` or `MissingMethodException`

- **Symptom:** `LacertMCP.Server.exe` immediately crashes on boot with a CLR loader error.
- **Diagnosis:** The `App.config` next to `LacertMCP.Server.exe` was deleted or modified. The Lacerte SDK is a mixed-mode CLR assembly that needs `useLegacyV2RuntimeActivationPolicy="true"` plus `supportedRuntime v4.0 / .NETFramework v4.6` set in the host's `.config`.
- **Fix:** Restore `App.config` from source control (or re-run `dotnet build` — MSBuild copies it next to the output). Don't delete it.

### `lacerte_list_datapaths` returns an empty list on a known-good install

- **Symptom:** The discovery tool returns `{ datapaths: [] }` even though `C:\Lacerte\24Data` etc. exist.
- **Diagnosis:** `LACERTE_DATA_ROOT` is not set and the install isn't under the `C:\Lacerte` default. Or worse — code on this box was written assuming `LACERTE_DATAPATH` (no underscore between `DATA` and `PATH`); the **real** env var is `LACERTE_DATA_PATH`. Reading the wrong name silently falls through to discovery.
- **Fix:** Set `LACERTE_DATA_ROOT=C:\path\to\your\Lacerte\root` in the MCP client config and restart. For per-context defaults, set `LACERTE_DATA_PATH=C:\Lacerte\24Data` — **note the underscore**.

### `lacerte-mcp doctor` reports ODBC DSN missing on a system with the SDK installed

- **Symptom:** Probe 4 fails: "ODBC DSN `LacerteDSII` not found in registry."
- **Diagnosis:** The Lacerte SDK installer registers the DSN under the 64-bit ODBC hive (`HKLM\SOFTWARE\ODBC\ODBC.INI\LacerteDSII`). 32-bit `odbcad32.exe` (the one in `C:\Windows\SysWOW64`) won't see it.
- **Fix:** Confirm via the 64-bit `odbcad32.exe` at `C:\Windows\System32\odbcad32.exe` → "System DSN" tab. If absent, reinstall the 64-bit Lacerte SDK MSI.

## Reference

* Source spec: [Lacerte_MCP_Structure_Spec.md](Lacerte_MCP_Structure_Spec.md)
* Operating system for this codebase: [CLAUDE.md](CLAUDE.md)
* Architecture: [ARCHITECTURE.md](ARCHITECTURE.md)
* Decisions log: [DECISIONS.md](DECISIONS.md)
* Acceptance criteria: [ACCEPTANCE_CRITERIA.md](ACCEPTANCE_CRITERIA.md)
* Regression checklist (incl. manual live-mode verification): [REGRESSION_CHECKLIST.md](REGRESSION_CHECKLIST.md)
* Build sequence + remaining work: [Todo.md](Todo.md)
* Research outputs: [docs/research/agents/](docs/research/agents/)
