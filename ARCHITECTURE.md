# Architecture

Locked from research synthesis on 2026-05-30. Update only when a structural rule intentionally changes, and log the change in `DECISIONS.md`.

---

## Stack

* **Language / runtime:** C# / **.NET Framework 4.8** (net48), x64.
* **MCP SDK:** `ModelContextProtocol` 0.3.0-preview.4 (Microsoft + Anthropic official preview; 1.x not yet on NuGet), stdio transport. See `DECISIONS.md` "MCP SDK version pinning" 2026-05-30.
* **COM interop:** Direct references to `LacerteSDK_Access.dll` / `LacerteSDK_Interfaces.dll` / `LacerteSDK_SharedInterfaces.dll`.
* **ODBC:** `System.Data.Odbc` built-in, against `LacerteDSIIDriver` (DSN `LacerteDSII` or DSN-less).
* **Tests:** xUnit.
* **Runtime shim:** App.config with `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime v4.0 / .NETFramework v4.6`.

Stack decision rationale: `DECISIONS.md` 2026-05-30 "Language/runtime stack" entry + `docs/research/agents/01-stack-decision.md`.

---

## System at a Glance

```
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
                                    │   - Idempotency cache (synth 9102 conflict)│
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

---

## Module Boundaries

Each layer has one responsibility. Crossing layers is the most common form of drift — don't.

| Layer | Path | Responsibility | Must NOT do |
|---|---|---|---|
| **Tool** | `LacertMCP.Server/Tools/*.cs` | `[McpServerTool]` methods. Validate input via parameter attributes, call session manager, format response. | Construct SQL strings. Read/write the simulation store directly. Hold persistent state. Catch generic Exception (use `WrapToolHandler`). |
| **Session** | `LacertMCP.Server/Session/LacerteSessionManager.cs` | Manage registration lifecycle, context (datapath/year/module), TY21+ routing, read-only/dry-run/idempotency gates. Dispatch reads to ODBC, writes to COM bridge, both to simulation when in sim mode. | Know about specific tools. Validate field access (that's the schema layer). Construct SQL directly (use Odbc helpers). |
| **Bridge** | `LacertMCP.Server/Bridge/`: `ILacerteBridge.cs` (interface seam for swappable bridge — live vs null/sim), `LacerteAccessClient.cs` (late-bound `LacerteSDK_Access` aggregate + lifetime owner), `Registration.cs` (`ITPRegistration` ceremony, called once at startup per Boundary Invariant #8), `Reader.cs` (`IDBFReader` COM surface — introspection only; 99% reads go via ODBC per #6), `Writer.cs` (`IDBFWriter`: pre-TY21 `WriteToDBFDatabase`, TY21+ `UpdateTaxReturn`, detail-file writes), `EditionDetection.cs` (cached `{LacerteEdition, LacerteBuild, SdkVersion}` probe — ≤1× per process), `CanUseNewApisProbe.cs` (TY21+ branch probe; cache lives on session manager). | Hold session state. Be called from tools directly. |
| **ODBC** | `LacertMCP.Server/Odbc/*.cs` | SQL reads only (no writes — spec §1 corrected per `DECISIONS.md` 2026-05-30). Connection-string building. TY21+/pre-TY21 SQL template branching. SQLSTATE → TAPIStatus normalization. | Execute INSERT/UPDATE/DELETE. Hold session state. |
| **Simulation** | `LacertMCP.Server/Session/SimulationStore.cs` + `SimulationStore.Query.cs` + `SimulationStore.Seed.cs` (three partials) + `Session/Fixtures/SeedData.cs` | In-memory implementation of the SDK for dev/tests. Seeded data. Same TAPIStatus return shape as live. | Be referenced by tools. Diverge behaviorally from live. |
| **Schema** | `LacertMCP.Server/Schema/*.cs` + `Schema/FieldAccessMap/*.json` | Module characters table, table catalog, per-field access map (with TY-version overlays), TAPIStatus constants. | Hold runtime mutable state. |
| **Errors** | `LacertMCP.Server/Errors/*.cs` | `LacerteSDKError` exception, status-code table, synthetic 9xxx codes, `WrapToolHandler` decorator. | Anything else. |
| **Logging** | `LacertMCP.Server/Logging/WireLog.cs` | Structured wire-level trace of COM/ODBC calls when `LACERTE_DEBUG_WIRE=1`. PII-redacted via two-scope `HashSet<string>` walker (disk: broad keyset incl. identity + address; wire: narrow secrets-only). See `DECISIONS.md` "WireLog redaction: HashSet walker, two scopes" and `WireLog.cs:176,222`. | Log secrets (AppID, ticket bodies, PII fields). Be required for correctness — the logging-disabled path must behave identically. |
| **Resilience** | `LacertMCP.Server/Resilience/AutoReconnect.cs` | Detect `NotSignedIn` / disconnect; the proactive expiry probe `LacerteSessionManager.ThrowIfAuthExpired` surfaces synthetic `9007 LoginExpired` with remediation, while the reactive path (an SDK `NotSignedIn 203` slipping past the probe) is propagated unchanged. Optional one-shot reconnect on idempotent reads. | Re-auth the 24h Intuit ticket inside a tool call (Boundary Invariant #8). Retry non-idempotent writes. |
| **Pagination** | `LacertMCP.Server/Pagination/Pagination.cs` | Stable cursor encoding for large reads. Limit/offset envelope (`rows, total, limit, offset, hasMore, nextOffset`). | Hold session state. Embed SQL. |
| **Prompts** | `LacertMCP.Server/Prompts/WorkflowPrompts.cs` | `[McpServerPrompt]` workflow scripts registered via `WithPromptsFromAssembly()`. | Execute tools directly. Bypass the session manager. |
| **CLI** | `LacertMCP.Cli/` (separate project) | `lacerte-mcp register`, `lacerte-mcp login`, `lacerte-mcp status`, `lacerte-mcp field-access set/list/discover`, `lacerte-mcp doctor`. | Touch the MCP server runtime — CLI runs in its own process. |
| **Entrypoint** | `LacertMCP.Server/Program.cs` | `Host.CreateApplicationBuilder` → `AddMcpServer` → `WithStdioServerTransport` → register tool classes via attribute discovery → start. | Implement tool logic. |

### Boundary Invariants

Non-negotiable unless explicitly changed via `DECISIONS.md`.

1. **Tools never construct SQL or COM calls.** They speak in C# parameters to the session manager. If a tool needs an unusual SQL shape, extend the ODBC layer. If it needs a COM call not yet wrapped, extend the bridge.
2. **Simulation and live must be observationally identical** for the happy path. A tool cannot tell which mode it's in by reading the response. When you change a live behavior, change the simulation in the same commit (or stub) so they stay in sync. (See `DECISIONS.md` 2026-05-30 "Two-mode parity rule.")
3. **One session manager instance per process.** Constructed lazily in `Program.cs` and shared via the DI container. Tools receive it via constructor injection or `[FromServices]`.
4. **Tool registration is centralized.** Every tool class is discovered via assembly scan in `Program.cs`; adding a new tool means dropping a class with `[McpServerToolType]` into `Tools/`.
5. **TY21+ branching is the session manager's job, not the tool's.** Tools call `session.ReadTable(...)` and the session picks the right SQL template / COM call based on cached `CanUseNewApis(year, module)`. Tools should not see the branch.
6. **ODBC is reads-only.** All mutations go through the COM bridge. `Odbc/` exposes no `ExecuteNonQuery` surface. (See `DECISIONS.md` 2026-05-30 "ODBC is reads-only.")
7. **The access map is consulted before every per-field write.** `assertWritable(module, table, field, year)` runs inside `LacerteSessionManager.WriteField` before any COM call. Default-refuse on `Unknown` with `force:true` escape hatch. Detail-row writes (`WriteDetail`) and whole-row `CreateClient` are deliberately outside this gate — see `DECISIONS.md` "Detail-row + whole-row writes are outside the access-map gate".
8. **The 24h Intuit auth ticket is never refreshed inside a tool call.** Re-auth ceremonies happen only via the CLI in an interactive shell. The proactive `LacerteSessionManager.ThrowIfAuthExpired` probe surfaces synthetic statusCode `9007 LoginExpired` with remediation; the reactive path (raw SDK `NotSignedIn 203` slipping past the probe) is propagated unchanged through `Resilience/AutoReconnect.cs` since `LacerteSDKError` carries a TAPIStatus and those are never transient.
9. **Status codes are always structured.** No tool returns a raw integer; every error is `{ success: false, statusCode, statusName, severity, userMessage, remediation, context }`.

---

## Data Flow

### A typical tool call (read path)

1. MCP client invokes `lacerte_client_list` over stdio.
2. MCP SDK validates input against the tool method's parameter schema (derived from C# parameter attributes).
3. Tool handler in `Tools/Clients.cs` translates parameters into a context (datapath/year/module if not already set).
4. Handler calls `session.ListClients(context)`.
5. Session manager:
   - **Live mode:** picks the SQL template (`select * from [DATA1-Client Info]`) and calls `OdbcReader.Query(...)`. Rows parsed to `DbfRow` records.
   - **Simulation mode:** calls `SimulationStore.GetClients(context)` and returns matching rows.
6. Handler wraps `{ count, clients }` in the MCP response shape.

### A typical tool call (write path)

1. Client invokes `lacerte_field_write` over stdio.
2. Tool handler validates input + calls `session.WriteField(module, table, clientNumber, field, value, year)`.
3. Session manager:
   - Checks read-only flag → throws `LacerteReadOnlyError` (statusCode 9001) if set.
   - Calls `assertWritable(...)` → throws `LacerteSDKError(ExposedForRead/NotExposed/...)` if the access map rejects.
   - **Live mode:** picks `WriteToDBFDatabase` (pre-TY21) or `UpdateTaxReturn` (TY21+) on the COM bridge, calls, translates the `TAPIStatus` return to `LacerteSDKError` if non-Success.
   - **Simulation mode:** calls `SimulationStore.SimulateFieldWrite(...)` which returns the same TAPIStatus values.
4. Handler returns success or `WrapToolHandler` converts the thrown error to the MCP error shape.

### Session lifecycle

* Session is registered once at MCP startup via a silent `Register()` call (rides the 24h cache if warm).
* If startup `Register()` returns `NotSignedIn`, the session manager marks itself expired; every tool call that touches `RequireContext` short-circuits to synthetic `9007 LoginExpired` (via `LacerteSessionManager.ThrowIfAuthExpired`) with remediation pointing to `lacerte-mcp register` / `lacerte-mcp login`.
* Otherwise the manager holds the `ITPRegistration` reference for the lifetime of the process; stamps `registeredAt + 24h` and rejects calls past expiry without firing the Intuit dialog.
* No `lacerte_session_disconnect` analog — `ITPRegistration` is multi-process-friendly; disconnect is implicit at process exit.

---

## Operating Modes

| Mode | Trigger | Behavior |
|---|---|---|
| **Simulation** | Default off-Windows OR `LACERTE_SIMULATION=1` is set. | All requests served from `SimulationStore`. Seeded fixtures. Safe for dev/tests. `register` returns `AccessGranted` synchronously. |
| **Live** | `Environment.OSVersion.Platform == Win32NT` AND `LACERTE_LIVE=1` AND `LACERTE_SIMULATION != "1"`. | COM + ODBC against the installed Lacerte SDK. Requires prior `lacerte-mcp register` CLI ceremony. |

Mode is resolved once in `LacerteSessionManager` constructor and is immutable for process lifetime.

---

## Tool Conventions

Every tool follows this shape (C# attribute style):

```csharp
[McpServerToolType]
public class ClientsTools
{
    private readonly LacerteSessionManager _session;
    public ClientsTools(LacerteSessionManager session) => _session = session;

    [McpServerTool, Description("List all clients for the active module + tax year.")]
    public async Task<string> LacerteClientList(
        [Description("Filter by last-name prefix (case-insensitive).")] string? lastNameFilter = null,
        [Description("Limit returned rows. Default 100, max 1000.")] int limit = 100)
    {
        return await WrapToolHandler.Run(async () =>
        {
            var rows = await _session.ListClients(lastNameFilter, limit);
            return JsonSerializer.Serialize(new { count = rows.Count, clients = rows });
        });
    }
}
```

Errors propagate via `LacerteSDKError` → `WrapToolHandler` converts to `{ success: false, statusCode, ... }`.

---

## Persistence

* **Simulation:** in-memory `ConcurrentDictionary` stores. Lost on process exit. Seeded on construction.
* **Live:** persistence is Lacerte's own DBF + detail files. The MCP is stateless beyond the in-memory session manager.
* **Configuration:** App ID GUID in Windows Credential Manager (per `DECISIONS.md` 2026-05-30). `lastSuccessfulRegisterAt` cached in `%APPDATA%/lacerte-mcp/session.json` so a process restart within the 24h window doesn't lose the timestamp.

There is no project-side persistent caching of live responses today. If we ever need to cache or persist a request log, that's a new subsystem and requires an `ARCHITECTURE.md` update.

---

## Configuration

Environment-variable driven, resolved once in `Program.cs`. The full list lives in `README.md` and `.env.example`:

| Var | Purpose |
|---|---|
| `LACERTE_APP_NAME` | Friendly name shown in the SDK consent dialog. |
| `LACERTE_APP_ID` | (Read-only at runtime; the source of truth is Credential Manager.) |
| `LACERTE_DEFAULT_TAX_YEAR` | Used when a tool call omits one. |
| `LACERTE_DATA_PATH` | Advisory only — quoted in ODBC-error remediations and probed by `lacerte-mcp doctor`. Does NOT auto-seed session context; every session calls `lacerte_set_context`. |
| `LACERTE_LIVE` | `1` → enable live mode. |
| `LACERTE_SIMULATION` | `1` → force simulation regardless of platform. |
| `LACERTE_DEBUG_WIRE` | `1` → log COM calls + SQL to `logs/`. |
| `LACERTE_DEBUG_LOG_PATH` | Override log directory. |
| `LACERTE_HEARTBEAT_SECONDS` | Heartbeat write cadence (sec) to `%TEMP%/lacerte-mcp-heartbeat.json`; `0` disables. Default `30`. |
| `LACERTE_SIM_EXPIRED` | `1` → simulate an expired 24h session (testing only). |
| `LACERTE_DATA_ROOT` | Root scanned by `lacerte_list_datapaths` when discovering `{YY}Tax\{Module}Data` folders (default `C:\Lacerte`). |
| `LACERTE_MCP_FORCE_FILE_STORE` | `1` → forces the file-based AppID store at `%APPDATA%\lacerte-mcp\config.json`; auto-on off-Windows. |
| `LACERTE_PARITY_RECORDING` | Testing only — path to recorded parity JSON consumed by the `BothModesData` test harness. |

New env vars must be documented in `README.md`, `.env.example`, AND this table — keep the trio in lock-step.

> ODBC connections are DSN-less — `ConnectionStringBuilder` composes them from driver + datapath + module + year. No `LACERTE_DSN` env var is honored; the `LacerteDSII` DSN name is only probed by `lacerte-mcp doctor` as an install-sanity check.

---

## Build & Runtime

* `dotnet build` produces `LacertMCP.Server.exe` + `LacertMCP.Server.exe.config`.
* `dotnet run --project LacertMCP.Server` for dev iteration; `dotnet watch run` for hot reload.
* `xunit` tests via `dotnet test`.
* The .exe.config carries the `useLegacyV2RuntimeActivationPolicy` + `supportedRuntime v4.0 / .NETFramework v4.6` shim — required for the Lacerte mixed-mode CLR load.

---

## What This Architecture Does Not Yet Cover

These are deferred subsystems. When introduced, require an architecture update:

* HTTP / WebSocket transport alongside stdio.
* Persistent caching of live responses.
* Streaming iterator-based pagination for very large queries.
* Multi-Lacerte-install support (one process serving multiple SDK installs).
