# Decisions

Log meaningful technical tradeoffs here. Every entry: **what was chosen**, **why**, **alternatives rejected**, **downstream tradeoffs**. Dated.

---

## 2026-05-30 — Project bootstrap & operating-system pattern adoption

**Chosen:** Mirror the operating-system pattern from the sibling `Quickbooks MCP Desktop` project — `HANDOFF.md` + `Todo.md` + `ARCHITECTURE.md` + `DECISIONS.md` + `REQUIREMENTS.md` + `REGRESSION_CHECKLIST.md` + `ACCEPTANCE_CRITERIA.md` + `CLAUDE.md`.

**Why:** Proven across 90+ landed tasks on the QB MCP. Same operator, same workflow expectations, same need for cross-session continuity.

**Alternatives rejected:** Single README + ad-hoc notes (drifts after 3 sessions). Inline todos in source comments (invisible to handoff). Single ARCHITECTURE.md with everything (loses separation of product vs structural truth).

**Downstream:** Every implementation session must read `HANDOFF.md` + `Todo.md` first and write a fresh `HANDOFF.md` before stopping.

---

## 2026-05-30 — Language/runtime stack: C# / .NET Framework 4.8 (replaces tentative TS entry)

**Chosen:** Pure C# / .NET solution targeting **.NET Framework 4.8** for the bridge exe, using the official **`ModelContextProtocol` NuGet SDK** (Microsoft + Anthropic, GA Feb 2026, v1.3.0). MCP transport: stdio. ODBC: `System.Data.Odbc` built-in. COM: direct references to `LacerteSDK_Access.dll`, `LacerteSDK_Interfaces.dll`, `LacerteSDK_SharedInterfaces.dll`.

**Why:** Lacerte SDK requires (a) loading the `LacerteSDK_Access` .NET assembly via COM CCW and (b) an `<exe>.config` with `useLegacyV2RuntimeActivationPolicy="true"` + `supportedRuntime v4.0 / .NETFramework v4.6`. Node's `winax` is classic-COM IDispatch only, has no documented CLR-hosting story, and `node.exe` doesn't naturally honor the runtime-activation shim. .NET 4.8 satisfies both requirements for free. C# MCP SDK is feature-equivalent to the TS SDK we use on QB MCP (`WithStdioServerTransport()` + `[McpServerTool]` attribute routing).

**Alternatives rejected:**
- **TypeScript + Node + winax (mirror QB MCP):** No documented path to load `LacerteSDK_Access` .NET assembly. `node.exe` config story for the runtime shim is undocumented. QB MCP's own `winax` dep is `optionalDependencies` and its live COM path is **stubbed** — so the pattern is unproven even for the easier QBXMLRP2 case.
- **Hybrid TS-MCP + C#-helper:** Extra IPC surface, two languages, two build pipelines, marginal velocity gain over single-language C#.
- **.NET 8 host:** Mixed-mode CLR loading is a .NET Framework concept; .NET 8 can do COM interop but the `useLegacyV2RuntimeActivationPolicy` shim is read by the .NET Framework loader, not the .NET 8 loader. Falling back to net48 sidesteps the entire ambiguity.

**Downstream:**
- `src/` directory becomes a .NET solution: `LacertMCP.sln`, `LacertMCP.Server.csproj` (net48), `LacertMCP.Tests.csproj` (xUnit).
- Tool layer uses `[McpServerTool]` attributes instead of `server.tool(...)` registrations.
- Zod equivalent is the SDK's JSON-Schema attribute generation + manual parameter `[Description]`.
- TS snippets in research reports 03–06 must be translated to C# during implementation — pattern stays, syntax changes.
- AI-pair-programming velocity slightly lower in C# than TS; bounded scope (finite SDK surface) limits impact.

**Source:** `docs/research/agents/01-stack-decision.md`.

---

## 2026-05-30 — ODBC is reads-only; all writes go through COM (corrects spec §1)

**Chosen:** The MCP's `src/Odbc/` namespace exposes only read operations. All writes (`UPDATE`, `INSERT`, `DELETE`, `CreateClient`) go through the COM bridge (`WriteToDBFDatabase` pre-TY21, `UpdateTaxReturn` TY21+, `IClient.CreateClient` for new clients).

**Why:** The source spec §1 line "full read/write of tables 1–7 plus read/write of tax detail files, via SQL over ADO/ADO.Net" is aspirational. Intuit's own moderator confirmed on the Lacerte SDK community that **INSERTs through ODBC are unsupported** ("we can't create a client using the ODBC driver"), and the 32-bit `LacerteDSIIDriver` is known to **crash the host process on INSERT**. UPDATE may work for a subset of fields but isn't the SDK's supported path.

**Alternatives rejected:** Trying ODBC writes opportunistically as an optimization — too risky (host crash on the 32-bit driver), no upside (COM path works for everything).

**Downstream:**
- Update spec §1 wording in a future doc pass (note in `HANDOFF.md` to do this when convenient).
- `LacertMCP.Server/Odbc/LacerteDsnReader.cs` exposes only `ExecuteReader` style methods, no `ExecuteNonQuery`.
- Acceptance criteria for write tools never reference ODBC.

**Source:** `docs/research/agents/03-odbc-dbf-access.md` §6.

---

## 2026-05-30 — 64-bit host process and 64-bit Lacerte SDK MSI required

**Chosen:** The MCP host runs as 64-bit. Operators must install the **64-bit Lacerte SDK MSI**.

**Why:** ODBC drivers are bitness-bound to the host process. The 32-bit `LacerteDSIIDriver` has a known crash on `INSERT`. 64-bit is the modern path; community evidence supports it as the default for SDK consumers.

**Alternatives rejected:** 32-bit MCP — would inherit the INSERT crash even on reads-only ODBC (the bridge writes through COM, but a 32-bit `LacerteSDK_Access` instance behaves differently from the 64-bit one). Both-bitness host — twice the maintenance, no win.

**Downstream:** `LacertMCP.Server.csproj` sets `<PlatformTarget>x64</PlatformTarget>`. `REQUIREMENTS.md` install section names the x64 MSI explicitly.

**Source:** `docs/research/agents/03-odbc-dbf-access.md` §1.

---

## 2026-05-30 — One-time CLI `lacerte-mcp register`, not lazy first-call

**Chosen:** Registration + login are CLI ceremonies (`lacerte-mcp register`, `lacerte-mcp login`) the operator runs in an interactive PowerShell. The MCP server process never calls `Register()` in a way that would surface a dialog to a headless host.

**Why:** Lacerte's `Register()` triggers a consent dialog AND an Intuit sign-in window — both GUI. The MCP runs under headless clients (Claude Desktop, Cursor) that can't see, dismiss, or time out these dialogs. Lazy first-call would block whatever tool the agent calls behind two modal windows the agent has no idea exist; the agent appears hung.

**Alternatives rejected:**
- **MCP launches a helper window:** Intuit's sign-in window is owned by Lacerte's process; we can't reparent or control it.
- **Require Lacerte already open:** Lacerte being warm doesn't bypass SDK-scoped Register/Login consent.

**Downstream:**
- `LacertMCP.Cli/` project carries `register`, `login`, `status` subcommands.
- At MCP startup, attempt `Register()` once silently (rides 24h cache if warm); on `NotSignedIn`, all tools return synthetic 9007 with remediation pointing to `lacerte-mcp login`.
- Never auto-re-Register inside a tool call — would fire the Intuit dialog in the wrong process.

**Source:** `docs/research/agents/02-auth-flow.md` §1–3.

---

## 2026-05-30 — AppID GUID in Windows Credential Manager, never hard-coded

**Chosen:** One AppID GUID per install, minted on first `lacerte-mcp register`, stored in Windows Credential Manager under service `lacerte-mcp` / account `app-id`. Fallback to `%APPDATA%/lacerte-mcp/config.json` (user-only ACL) if Credential Manager is unavailable.

**Why:** GUID is installation-identifying and must survive reinstalls; permanent per the SDK spec (changing forces a fresh consent ceremony). Hard-coded would mean every operator's install shares an identity, breaking Intuit-side abuse blocking. `.env` files leak. Keychain is encrypted + OS-ACL'd. The CLI prints the GUID on `register --reset` for backup.

**Alternatives rejected:** Hard-coded GUID (single identity for all operators), `.env` file (leakage surface), no GUID (the SDK requires one).

**Downstream:** C# Credential Manager wrapper via `CredentialManagement` NuGet or P/Invoke to `advapi32.dll`. CLI refuses to mint a new GUID if one exists unless `--reset` is passed.

**Source:** `docs/research/agents/02-auth-flow.md` §4.

---

## 2026-05-30 — Synthetic statusCode range: 9001–9099 for validation gates, 9100–9199 for auth

**Chosen:** Mirror QB MCP's 9001–9008 synthetic range, extend to two sub-ranges. **9001–9099** for validation/preflight gates (field access, length, missing context). **9100–9199** for auth-lifecycle (registration, 24h expiry, GUID missing). **9200–9299** reserved for COM bridge / runtime failures.

**Why:** Operators benefit from category-by-numeric-range when triaging errors. QB's 9001 (read-only), 9002 (idempotency), 9003 (edition gate) etc. work because each one is memorable in its problem domain. Lacerte has more pre-flight surface (per-field access map, TY-version gates, sub-client/state requirements), so we widen the range.

**Alternatives rejected:** Reusing QB's exact codes — collisions confuse operators who use both MCPs.

**Downstream:** `src/Errors/SyntheticStatusCodes.cs` enumerates all codes with `[Description]` attributes. `wrapToolHandler` decorator emits these consistently.

**Source:** `docs/research/agents/05-error-mapping.md` §6.

---

## 2026-05-30 — Flat-keyed sharded-JSON field access map; introspection is the only realistic path to full coverage

**Chosen:** Field access map is **flat string-keyed** (`"${module}|${table}|${field}"` → entry), **sharded one JSON file per module** under `src/Schema/FieldAccessMap/base/*.json`, with per-TY overlay files under `overlays/`. Default behavior on `Unknown` access: **refuse write** with a `force:true` escape hatch.

**Why:** Flat keying is ~30% smaller than nested, single lookup, trivially shardable. Sharded JSON avoids tsc-equivalent memory blowups, lazy-loadable per module, diffable in PRs. No public Intuit document ships a complete `C{table}_{n}` access map; honest coverage path is runtime introspection of the installed SDK (write-probe to detect `7 NotExposed` / `8 ExposedForRead` / `1 Success`). Hard-coded v1 ships only what the spec documents.

**Alternatives rejected:** Nested `Record<>`s (slower, balloons JSON), TypeScript const (build-cost), SQLite (overkill, native dep), YAML (10x parse cost), monolithic JSON (bad PR diffs).

**Downstream:**
- `LacertMCP.Server/Schema/FieldAccessMap/` shipped with hard-coded entries from spec.
- `scripts/dump-field-access.csx` (a `dotnet script` runner) for bulk introspection later.
- `lacerte-mcp field-access set ...` CLI for one-off discoveries.
- `assertWritable(...)` helper in every write tool runs before any COM call.

**Source:** `docs/research/agents/04-field-access-map.md`.

---

## 2026-05-30 — Simulation store: single canonical shape, query form branches on year

**Chosen:** The simulation store keeps **one canonical shape** for DATA2/3/4. The simulated query entry point branches on `(year >= 2021)` before formatting the result — pre-TY21 returns the global flat-table shape; TY21+ returns the per-client-prefixed shape. Both branches read from the same store.

**Why:** Lacerte's TY21+ change was a *query form* change, not a *data* change — the underlying DBF is the same. Pre-building two shapes doubles write paths and invites drift. One canonical store mirrors how the live ODBC driver works internally.

**Alternatives rejected:** Storing both shapes (double-write footgun); per-year stores (massive duplication).

**Downstream:** `SimulationStore.cs` exposes one `ProcessQuery(sql, ctx)` method that internally calls `QueryNewForm` or `QueryOldForm` based on context. Tests cover both forms against the same seed.

**Source:** `docs/research/agents/06-simulation-mode.md` §3.

---

## 2026-05-30 — Two-mode parity rule (non-negotiable)

**Chosen:** "**Simulation and live must be observationally identical** for the happy path of every tool. A tool can't tell which mode it's running in by reading the response — same status values, same row shape, same column names, same TY21+ branching, same access-map rejections."

**Why:** Adapted directly from QB MCP `ARCHITECTURE.md` Boundary Invariant #2. Proven across 90+ tasks. Lacerte has more surface (TY-version-aware) so the rule is more load-bearing, not less.

**Alternatives rejected:** Letting sim be a "good enough" shape with documented differences — drift accumulates within 3 sessions; the rule's value is operational, not aspirational.

**Downstream:** Parity tests (`bothModes(name, body)` harness) run every behavioral test twice — once against `SimulationStore`, once against a record-replay live adapter (until real Windows CI exists). Any divergence fails CI.

**Source:** `docs/research/agents/06-simulation-mode.md` §9–10 + QB MCP `ARCHITECTURE.md` Boundary Invariant #2.

---

## 2026-05-30 — Per-year access-map overlay semantic (full-replace, not cumulative)

**Chosen:** Each overlay file under `Schema/FieldAccessMap/overlays/` (e.g. `ty21-not-supported.json`, `ty22-not-supported.json`, …) is scoped to its **exact** tax year. `FieldAccessMapLoader.LoadOverlay(taxYear)` reads exactly one file — `ty{yy}-not-supported.json` — and does NOT walk prior years or merge them. Each TY ships its own complete list of fields not supported in that build, even if a field was already dropped in an earlier year.

**Why:** Matches the design in `docs/research/agents/04-field-access-map.md` §6: "Overlays are keyed by exact taxYear. A TY22 entry does not inherit from TY21 — each year's 'not supported' list is shipped explicitly." The alternative (cumulative deltas walked back to a baseline year) would require the loader to read N files per call, merge them in a deterministic precedence order, and add a manifest-managed baseline year — all to encode information that is already trivially expressible by checking in N independent files. The author-time cost of duplicating "C1_99 = NotSupported" across `ty21`, `ty22`, `ty23` files is small; the runtime cost of merging N overlays per write is paid on every write.

**Alternatives rejected:**
- **Cumulative deltas inherited from a baseline year** (walk `ty21` → `ty22` → `ty23` when asked for TY24): adds loader complexity and turns "field dropped in TY22 and re-supported in TY24" — a real Intuit scenario — into an awkward override-the-override file rather than just an explicit TY24 entry that says ReadWrite.
- **Single multi-year file**: harder to diff across releases; harder for the CLI helpers to upsert; loses the file-per-year audit trail that maps cleanly to Intuit's annual SDK build cycle.

**Downstream:**
- File naming and the loader behavior are now locked. Any test that pins this semantic should seed `ty{yy}-not-supported.json` with a NotSupported entry for year `yy` and assert that `GetFieldAccess(..., yy)` returns `NotSupported` while `GetFieldAccess(..., yy+1)` falls through to the base map.
- README at `Schema/FieldAccessMap/README.md` and the `LoadOverlay` xmldoc explicitly call out the per-exact-year semantic so future contributors don't add inheritance assuming it was an oversight.
- Authoring discipline: every year that drops fields ships its own full overlay file. The `manifest.json#overlays` array is the authoritative list of years that ship an overlay.

**Source:** `docs/research/agents/04-field-access-map.md` §6.

---

## 2026-05-30 — MCP SDK version pinning: `ModelContextProtocol` 0.3.0-preview.4 (not 1.x)

**Chosen:** Pin `ModelContextProtocol` NuGet to **`0.3.0-preview.4`** in `LacertMCP.Server.csproj`. The earlier research report (`docs/research/agents/01-stack-decision.md`) cited "v1.3.0 as of May 8, 2026" — that was incorrect.

**Why:** The highest version actually published on NuGet at Phase-0-implementation time was `0.3.0-preview.4`. The SDK has not yet reached 1.x stable. The 0.3.x API is feature-equivalent for our needs (`WithStdioServerTransport()`, `[McpServerTool]`, `WithToolsFromAssembly()`) — build verified end-to-end with 105 tests passing.

**Alternatives rejected:** Waiting for 1.x — uncertain timeline, blocks the build today. Pinning to a private fork — overkill.

**Downstream:**
- Carry follow-up F9 in `Todo.md` to bump to 1.x when it ships.
- Watch SDK release notes for breaking changes between 0.3.x and 1.x — minimal blast radius (a handful of attribute usages).
- The research report's claim of "feature-equivalent to TS SDK" holds; only the version number was wrong.

---

## 2026-05-30 — WireLog format + PII redaction policy

**Chosen:** Debug wire log is **one JSON object per line** with the shape `{ts, op, args, durationMs, result | error}`, written to `logs/lacerte-YYYYMMDD.log` (dir overrideable via `LACERTE_DEBUG_LOG_PATH`), activated only when `LACERTE_DEBUG_WIRE=1`. Before every line hits disk, a single compiled regex in `src/LacertMCP.Server/Logging/WireLog.cs` (line 62-64) redacts the values of four field names: **`SSN`, `EIN`, `BankAccountNumber`, `RoutingNumber`** — case-insensitive, covering string / numeric / boolean / null literal forms. Operationally needed but lower-sensitivity fields (`FirstName`, `LastName`, `Address`, `EmailID`, dollar amounts, ClientNumber, dataPath) are **intentionally NOT redacted** — they are required to triage a wire trace and the log file is local-only with a user-only ACL implied by `%CWD%/logs/`.

**Why:** Regex-over-serialized-JSON covers arbitrary nested object shapes (sim/live envelopes, error context dicts, idempotency snapshots) with one rule — a structured-tree redactor would have to walk every `Dictionary<string, object?>`, `JsonElement`, and anonymous-object shape the codebase emits. SSN/EIN are statutory PII; BankAccount/RoutingNumber are direct-deposit credentials. The four were picked because they appear in DATA1/DATA2 field-name conventions and (per the source spec §1) are the credential surface the operator can leak by tailing a log. Mirrors QB MCP's `QB_DEBUG_QBXML` pattern per agent 07 §11.

**Alternatives rejected:**
- **Structured-tree redactor:** Forces every emitter to declare which dict keys are sensitive — drifts as new fields are added. The regex catches new emit sites by default.
- **Encrypt the log file:** Defeats the operational point (operator needs to grep it during a hang).
- **Redact everything by default, opt-in to expose:** Operators can't debug a 9008 `LengthExceeded` if the offending value is `***` — debug context is the log's reason to exist.
- **Off-by-default + emit at INFO when on:** That's what `LACERTE_DEBUG_WIRE=1` already is. Default-off keeps log files out of the support-bundle attack surface.

**Downstream:**
- Any new tool or session-manager call that handles a PII-carrying field **MUST** be threaded through `WireLog.Record(...)` so the redaction regex sees it (currently every chokepoint in `LacerteSessionManager` + `LacerteDsnReader` + `Bridge/LacerteAccessClient`/`Writer` per follow-up F11).
- Adding a new sensitive field name (e.g. a future `TaxIdNumber`) requires editing the regex in `WireLog.cs` (line 62-64) AND adding a test in `tests/Logging/`. Do **not** widen the regex to be a free-form `(?i)(ssn|ein|bank|account|routing).*` — false positives on legitimate field names (e.g. `BankName` is not a number) would silently corrupt log records.
- The lazy `WireLogConfig` cache is read once at process start — a test that needs a different dir uses `WireLog.EnableForTests(dir)` + `DisableForTests()` rather than mutating env mid-process.
- `Redact(json)` is `public` for tests by design — callers that emit ad-hoc log lines outside `Record(...)` should still pass them through `Redact` first.

**Source:** `Todo.md` task #75 + follow-up F11; `src/LacertMCP.Server/Logging/WireLog.cs` XML doc lines 33-38.

---

## 2026-05-30 — AutoReconnect retry policy: 3 attempts at [250, 500, 1000]ms on whitelisted transient HRESULTs

**Chosen:** `src/LacertMCP.Server/Resilience/AutoReconnect.cs` retries a wire call **at most 3 times** with a fixed schedule of **[250, 500, 1000]ms** between attempts (4 calls total including the initial). The retry trigger is a tight whitelist of **four** HRESULTs:
- `0x800706BA RPC_S_SERVER_UNAVAILABLE` — "The RPC server is unavailable"
- `0x80010108 RPC_E_DISCONNECTED` — "The object invoked has disconnected from its clients"
- `0x8001010A RPC_E_SERVERCALL_RETRYLATER` — "The message filter indicated that the application is busy"
- `0x80010001 RPC_E_CALL_CANCELED` — "Call was canceled by the message filter"

Anything else propagates **immediately** without retry. Any `LacerteSDKError` (TAPIStatus-bearing exception like `NotSignedIn`, `ConsentDenied`, `SessionExpired`) is **NEVER** retried — those need re-auth, not retry (`AutoReconnect.cs` line 138). On exhaustion the original last exception is rethrown with `Exception.Data["AutoReconnect.Exhausted"]=true` and `Attempts=4` for diagnostics. Wired into the live branch of every `_bridge.<method>(...)` call site in `LacerteSessionManager` per follow-up F12; sim path is untouched (sim never throws transient COM).

**Why:** The four HRESULTs are the canonical Windows COM/RPC "the other side blinked" set — pulled from QB MCP's `isTransientLiveError` per agent 07 §11 and confirmed against Lacerte SDK community traces. The `[250, 500, 1000]ms` schedule is exponential-ish (matches the QB MCP pattern that's landed across 90+ tasks) and totals **1.75s** of worst-case retry latency — short enough to stay under any reasonable MCP client timeout, long enough to ride out a Lacerte UI repaint or a RPC reconnect. Live-only because (a) sim doesn't throw COM exceptions and (b) wrapping sim calls in retry would mask logic bugs in the test harness with sleeps.

**Alternatives rejected:**
- **Wider HRESULT whitelist (every `0x8001xxxx`):** Would retry permanent errors like `RPC_E_ACCESS_DENIED`, lengthening the failure path with no upside.
- **Retry `LacerteSDKError`:** Would retry `NotSignedIn` 3 times, then fail with the same code 4 seconds later — actively worse UX than failing fast and pointing the operator at `lacerte-mcp login`.
- **Longer / unbounded backoff:** MCP tools have a perceived-latency budget; >2s of silent retry feels like a hang to the agent.
- **Tunable from env:** Adds a foot-gun for operators who'd raise the count to "fix" a non-transient bug. The whitelist is small + tight on purpose.
- **Jittered delays:** Single operator, sequential tool calls, no thundering-herd surface — pure overhead.

**Downstream:**
- Do **not** tune `BackoffMs` or `MaxRetries` without a corresponding `REGRESSION_CHECKLIST.md` live regression item — these constants are observed-good on the QB MCP and changing them silently shifts perceived latency.
- Adding a new transient HRESULT requires (a) an entry in `TransientHResults` with the symbolic name comment, (b) a new test under `tests/Resilience/AutoReconnectTests.cs`, and (c) a brief justification in this DECISIONS entry (amend, don't overwrite).
- `WithRetry` and `WithRetrySync` are both wire-call API — never wrap pure-CPU code in them.
- When Phase 9 lands the live bridge, all `_bridge.X(...)` call sites in `LacerteSessionManager` are wrapped via F12; new bridge call sites added later MUST use `AutoReconnect.WithRetry` or they bypass the policy.

**Source:** `Todo.md` task #76 + follow-up F12; `src/LacertMCP.Server/Resilience/AutoReconnect.cs` XML doc lines 10-30.

---

## 2026-05-30 — Pagination envelope: offset-based with `{rows, total, limit, offset, hasMore, nextOffset}` plus additive legacy keys through v0.1.x

**Chosen:** All paginated tools (`lacerte_client_list`, `lacerte_table_read`, future list-shaped tools) emit a single envelope shape via `src/LacertMCP.Server/Pagination/Pagination.cs`:

```
{ "rows": [...], "total": N, "limit": L, "offset": O,
  "hasMore": bool, "nextOffset": int? }
```

Pagination is **offset-based** (not cursor-based). `DefaultLimit=100`, `MaxLimit=1000` (hard ceiling — anything larger clamps). Negative `offset` is normalized to 0; `offset > total` clamps to `total`. The page is materialized in-memory via `Enumerable.ToList<T>()` so `total` is exact. Each tool currently retains legacy sibling keys for additive backwards-compatibility — `lacerte_client_list` keeps `{count, clients}` (per `ClientsTools.cs:35-37`), `lacerte_table_read` keeps `{count}` (per `TablesTools.cs:37`). **Those legacy keys are removed in v0.2.**

**Why:** Lacerte data is **bounded and materializable** — even the largest practical client roster fits in process memory (single-firm, single-operator, no streaming need); a cursor protocol would add wire complexity (opaque tokens, server-side state, expiration semantics) with no concrete upside. Offset-based pages compose with `limit/offset` URL-param conventions every operator already knows. The `MaxLimit=1000` cap prevents a runaway tool from materializing an unbounded result while leaving room for the documented "give me the whole module" pattern. Legacy keys preserved because **pre-pagination consumers were already in the wild** when F13 landed (test fixtures + downstream agents reading `result.count` directly) — additive carries them forward without forcing a coordinated upgrade.

**Alternatives rejected:**
- **Cursor-based pagination:** No streaming requirement (operator queries are bounded), server-side cursor state collides with the stateless tool model.
- **Page-number + page-size (1-indexed):** Loses precision when `total` shifts mid-pagination; offset-based is unambiguous when callers care about a specific row range.
- **Hard-remove legacy keys at F13 landing:** Would have broken in-flight consumers; the additive approach lets v0.1.x ship and gives v0.2 a clean breaking-change window.
- **Per-tool envelope shapes:** Drift within 3 tools — `Pagination.Page<T>` is the single source of truth.

**Downstream:**
- **v0.2 removal target:** Drop the legacy `count`/`clients` keys from `ClientsTools.cs:46-49` and the legacy `count` key from `TablesTools.cs:50-52` when v0.2 ships. Tracked under follow-up **F15** in `Todo.md` so the deferred breakage isn't lost.
- New paginated tools MUST route through `Pagination.Page<T>(...)` rather than rolling their own envelope; tests for new tools assert the full key set (`rows/total/limit/offset/hasMore/nextOffset`).
- `Page<T>` is purposely a value-type-shaped class (immutable, readonly props) — do not mutate after construction; tools serialize directly to JSON.
- Tools whose `_session` returns an `IEnumerable<T>` should pass a generous server-side ceiling (e.g. `int.MaxValue`) into the session call so the in-tool `Pagination.Page` does the only truncation — splitting truncation across two layers causes confusing `total < expected` results (see `ClientsTools.cs:32`).
- `total` is exact-on-materialize; if a future tool sources from a streaming live SQL path, it MUST count separately or document that `total = rows.Count` (not full result size).

**Source:** `Todo.md` task #77 + follow-up F13; `src/LacertMCP.Server/Pagination/Pagination.cs`; `src/LacertMCP.Server/Tools/ClientsTools.cs:35-49`; `src/LacertMCP.Server/Tools/TablesTools.cs:34-52`.
