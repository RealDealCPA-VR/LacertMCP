# Decisions

Log meaningful technical tradeoffs here. Every entry: **what was chosen**, **why**, **alternatives rejected**, **downstream tradeoffs**. Dated.

---

## 2026-05-30 — Cross-tool naming conventions (X_naming_xtool cluster, P16 preflight)

**Chosen:** Single canonical name for every tool parameter, response field, error-context key, and prompt name on the MCP wire surface. v0.1.0 pre-release breaking change (no installed user base to migrate).

Wire conventions:
- **Tax year** → `taxYear` on every input parameter, response field, and error-context key. Replaces `year` in MetadataTools.LacerteFieldAccessMap, FieldWriteBulkTools.LacerteFieldWriteBulk preflight, SessionTools.LacerteSessionStatus context + LacerteSetContext response + LacerteListDatapaths per-row shape. Error-context keys `expectedYear` / `actualYear` → `expectedTaxYear` / `actualTaxYear`.
- **State abbreviation** → `stateAbbrev` on every wire parameter. Replaces `stateAbbr` in DetailsTools.LacerteDetailWrite (model field `DetailRow.StateAbbr` stays — maps to the DBF column).
- **Field name** → single-write tools use `field`; bulk-write's `BulkWriteEntry` renames `FieldName` → `Field`. One canonical name across the write surface so an LLM refactoring single → bulk doesn't fail every row with empty-field errors.
- **Module character** → typed as `string` (length-1) on every tool that accepts it. MetadataTools.LacerteFieldAccessMap previously typed `char module` (JSON binding trap — clients send `"I"` not `'I'`); aligned with the existing SessionTools.SetContext + CanUseNewApis pattern.
- **Identity acronyms** → camelCase with `Id` mixed-case suffix (Microsoft Framework Design Guidelines): `clientId`, `emailId`, `subClientId`. Replaces ALL-CAPS suffix `clientID`, `emailID`, `subClientID` on every wire parameter, response envelope field, error-context key, and ripples to `CreateClientArgs` / `SimulateCreateClientArgs` / `BridgeInputValidation` / `ILacerteBridge` / `Writer` / `LacerteAccessClient` for internal consistency.

Response envelope conventions:
- **Canonical collection key** → `rows` for tabular reads (matches `Pagination.Page<T>().Rows`). Domain aliases (`clients`, `forms`, `subclients`, `states`, `stateSubclients`, `statuses`, `efStatuses`, `modules`, `datapaths`, `codes`) retained as v0.1 back-compat siblings; new consumers target `rows`. Removed in v0.2 per pagination cleanup window.
- **Inner `success` removed** → SchemaReloadTools.LacerteReloadSchema no longer returns `{success: true, ...}` inside `result`. WrapToolHandler's outer envelope already supplies `success`; the inner duplicate created a `response.success` / `response.result.success` ambiguity.
- **Error envelope `origin`** → wire-envelope error now surfaces `origin: "sdk"` or `"synthetic"` derived from `LacerteSDKError.Origin` (P15-037 was documented but the code never landed; P16-053 finishes the landing). Matches the `originGroup` column added to `lacerte_return_codes` so consumers can filter `originGroup == envelope.origin`.

Tool-method / prompt-name alignment:
- **`lacerte_bulk_field_update`** prompt renamed to **`lacerte_field_write_bulk`** to match the tool method name. The noun-first family (`lacerte_field_write` + `lacerte_field_write_bulk`) is canonical; verb-first names are legacy.

Documentation:
- **Remediation strings** referencing the phantom MCP tool `lacerte_register` replaced with `lacerte-mcp register` (CLI command, backticked). Affects SyntheticStatusCodes (AppNotRegistered/LoginExpired/ReAuthRequired) + StatusCodes (ConsentDenied/RegVersionBlocked/NotSignedIn). A regression test in `StatusCodesTests.Remediations_NeverNameNonexistentMcpTool_LacerteRegister_P16_011` pins the invariant.
- **`lacerte_client_read` description** corrected — it returns `{ clientNumber, found, client }` (envelope is never null, check `found`), not "null if no such client".

Optional-default surface:
- **`LacerteDetailWrite`** — the seven row-key segments (`prefix`, `suffix`, `stateAbbrev`, `cityAbbr`, `source`, `tsj`, `importSrc`) now default to `""`. Only `clientNumber`, `series`, `code`, `amount`, `description` are required. The W-2 happy path drops from 12 args to 5. Reorder is a breaking change for positional callers, but MCP wire calls are JSON-named so no wire callers break; in-repo test fixtures already used named arguments.

**Why:** Round-3 audit P16 surfaced the cross-tool drift as one of the dominant friction sources for the LLM-facing surface — an LLM learning `taxYear` from `lacerte_set_context` then guessing `year` for `lacerte_field_access_map` produces a binding-error retry loop with no actionable hint. Same pattern repeated across `stateAbbr/stateAbbrev`, `field/fieldName`, `clientID/clientId`. v0.1.0 has no installed user base — the cost of fixing now is just code churn; the cost of preserving drift is every future LLM interaction.

**Alternatives rejected:**
- **Add input aliases** (accept both `year` and `taxYear`): MCP tool schemas don't support param aliases without a custom binder; an LLM that hits "year not allowed; did you mean taxYear?" learns the right name on retry; the alias mechanism would add binding complexity for marginal LLM benefit.
- **Defer to v0.2**: nothing forces v0.2 to land sooner than now and the cluster is already cross-cutting — punting compounds the migration cost when more tools / consumers land in the interim.
- **Keep ALL-CAPS suffix for SDK-mirroring identifiers**: `ClientID` matches the Intuit C# SDK signature but the MCP wire is its own contract — every other camelCase param uses Microsoft's `Id` convention and the inconsistency was the bug.

**Downstream:**
- Regression suite gains `NamingConventionsTests` with reflection-driven sentinels — any future tool with `int year` / `int? year` / `string clientID` / `char module` / `string FieldName` will trip the build.
- README tool tables updated to surface the canonical param names + the new `lacerte_field_write_bulk` and `lacerte_reload_schema` rows; prompts table updated for `lacerte_field_write_bulk` rename.
- WireLog redaction keysets continue to scrub email-bearing values — comparer is case-insensitive so `emailId` matches the existing `EmailID` entry; no key list update needed.
- Internal sim helper `SimulationStore.SimulateFieldWrite(subClientID:)` keeps its parameter name for now — it's not on the wire and the renaming surface stops at the bridge boundary.

**Source:** `audit-findings-r3.json` cluster `X_naming_xtool` (P16-008, P16-011, P16-024, P16-025, P16-026, P16-027, P16-045, P16-052, P16-053, P16-054, P16-069, P16-070, P16-071, P16-079, P16-080).

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

> **Superseded by:** "2026-05-30 — WireLog redaction: HashSet walker, two scopes (P15-001/P15-006 supersedes prior single-regex / 4-field policy)" — see entry appended at the bottom of this file. The HOW-IT-WORKS prose below describes the pre-P15-001 design (single regex, four fields, FirstName/LastName/Address/EmailID NOT redacted). The current implementation is a `JsonNode` walker over two `HashSet<string>` keysets (disk + wire), and identity + address fields ARE redacted on disk scope. Do NOT edit `WireLog.cs:62-64` to add a sensitive field — those lines are XML doc comments now; the keysets live at `WireLog.cs:176` (`_diskRedactionKeys`) and `WireLog.cs:222` (`_wireRedactionKeys`).

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

---

## 2026-05-30 — `.env.example` does not include `LACERTE_APP_ID` (CredMan + file-fallback are the only sources of truth)

**Chosen:** `.env.example` documents `LACERTE_APP_NAME` but explicitly does **not** include a `LACERTE_APP_ID=...` line. The line previously present (`LACERTE_APP_ID=00000000-0000-0000-0000-000000000000`) was removed and replaced with a comment block that points to the real source of truth: Windows Credential Manager (target `lacerte-mcp`, account `app-id`) minted by `lacerte-mcp register`, with a file-store fallback at `%APPDATA%/lacerte-mcp/config.json` (auto-on off-Windows or when `LACERTE_MCP_FORCE_FILE_STORE=1`).

**Why:** The .env.example is the literal first-contact onboarding artifact and documenting `LACERTE_APP_ID` there falsely implies the env var is read at runtime — it is not. `AppIdStore.Get()` (LacertMCP.Cli/Storage/AppIdStore.cs) and `Registration.GetStoredAppId()` (LacertMCP.Server/Bridge/Registration.cs) read only from Credential Manager / file. Adding env-var support (env > CredMan > file precedence) was rejected because it would (i) break the "registration happens once from a single source of truth" boundary invariant, (ii) re-open an AppID-leak surface that the 2026-05-30 "AppID GUID in Windows Credential Manager" decision explicitly closed, and (iii) invent a new contract for a one-operator personal tool. Removing the zero-GUID example also prevents new operators from pasting it and wondering why `register` still complains.

**Alternatives rejected:**
- **Add env-var support with documented env > CredMan > file precedence:** Violates single-source-of-truth, re-opens leak surface, adds maintenance for zero operator benefit (single-operator personal tool).
- **Keep the existing zero-GUID line and add a warning comment:** Confuses new operators; the existing "do NOT change AppID after first registration" comment already misled them once.

**Downstream:**
- README.md:94 and ARCHITECTURE.md:174 already correctly label `LACERTE_APP_ID` as runtime read-only / Credential Manager source-of-truth — no edits needed there.
- If a future contributor proposes re-adding `LACERTE_APP_ID` to `.env.example` or to the env-read paths, reject and cite this entry.
- No code change, no test change. Pure docs alignment.

**Source:** Phase 15 audit finding P15-005; `.env.example` lines 4-10; README.md line 94; ARCHITECTURE.md line 174; cross-reference 2026-05-30 "AppID GUID in Windows Credential Manager" entry above.

---

## 2026-05-30 — Status messages are en-US only (no localization seam) — P15-068

**Chosen:** `StatusCodes.StatusTable`, `SyntheticStatusCodes.StatusTable`, and the
`GetStatusInfo` unknown-code fallback emit hardcoded English `UserMessage`
and `Remediation` strings. `StatusInfo` has no `CultureInfo` overload.

**Why:**
- Single-operator English-speaking US deployment per `CLAUDE.md` "Single operator".
- The wire envelope already mixes our en-US strings with whatever the SDK
  echoes into `context.reason` / `context.rawError`. Localizing only the
  MCP layer would not produce a fully-localized response; we would still
  emit mixed-language output. Either fully translate (out of scope for a
  personal tool) or accept en-US for both layers (what we do).
- LLMs handle en-US technical strings reliably; routing through a
  `CultureInfo` plumbing layer would add complexity for zero current
  operator benefit.

**Alternatives rejected:**
- **`StatusInfo.UserMessage(CultureInfo)` overload + per-culture StatusTable
  dictionaries:** Real refactor (every throw site stays numeric, but the
  table grows N× per supported culture) for a benefit the operator does
  not need. Over-engineering for v1.

**Downstream:**
- New `StatusInfo` entries continue to be authored in en-US.
- Revisit only if the operator deploys against a non-en-US Lacerte install
  and observes `context.reason` text the LLM mishandles.

**Source:** `audit-findings-r2.json` P15-068; `src/LacertMCP.Server/Errors/StatusCodes.cs`; `src/LacertMCP.Server/Errors/SyntheticStatusCodes.cs`; `REQUIREMENTS.md` Locale bullet.

---

## 2026-05-30 — Error envelope carries `origin: "sdk" | "synthetic"` discriminator — P15-037

**Chosen:** `LacerteSDKError` exposes an `Origin` property derived from
`StatusCode >= 9000` (`"synthetic"`) vs `< 9000` (`"sdk"`). The wire-envelope
`SerializeError` adds an `"origin"` field next to `statusCode` /
`statusName` so consumers don't have to memorize the 9000+ numeric
threshold to know whether a remediation lives MCP-side (`synthetic`,
pre-flight / auth / bridge runtime) or operator-side (`sdk`, real Lacerte
TAPIStatus / TPSDKStatus state — look it up in Intuit's SDK docs).

**Why:**
- The 9000+ threshold is a load-bearing convention. Surfacing it
  explicitly in the envelope avoids relying on consumers (LLM or human)
  knowing the range allocation defined under the 2026-05-30 "Synthetic
  statusCode range" entry above.
- Derived once at the `LacerteSDKError` boundary so the existing ~25
  throw sites and constructors need no change; the wire serializer just
  reads `err.Origin`.
- LLMs route to the right remediation strategy faster: `synthetic` =
  "fix the MCP call args / flip a config flag"; `sdk` = "talk to the
  Lacerte install / operator".

**Alternatives rejected:**
- **Per-code lookup tool (`lacerte_explain_status_code(code)`):** Already
  covered by `lacerte_return_codes` (which emits an `origin` discriminator
  in its catalog). An in-envelope field beats a round-trip mid-failure.
- **Encode origin into `statusName`:** Names like `"DBError"` /
  `"SimulationOnly"` are not range-uniform; pattern-matching would be
  fragile.

**Downstream:**
- `WrapToolHandler.SerializeError` reads `err.Origin` and emits it in the
  error envelope (wired in tandem with the A_logging cluster's owned file).
- Document the `origin` field in `README.md`'s Errors subsection so MCP
  consumers know to expect it.
- New synthetic codes MUST stay in the 9000–9299 range so the
  discriminator stays correct without per-code overrides.

**Source:** `audit-findings-r2.json` P15-037; `src/LacertMCP.Server/Errors/LacerteSDKError.cs`; `src/LacertMCP.Server/Errors/WrapToolHandler.cs:93-106`.

---

## 2026-05-30 — Detail-row + whole-row writes are outside the access-map gate — P15-003

**Chosen:** Two write paths — `LacerteSessionManager.WriteDetail` (~line 784) and `LacerteSessionManager.CreateClient` (~line 874) — deliberately skip the `assertWritable(module, table, field, year)` access-map check that every `WriteField` call goes through. Both still run `RejectIfReadOnly(op)` (the universal `C{t}_0` read-only fail-closed rule from ARCHITECTURE.md is preserved). Safety for these paths comes from purpose-built client-side gates instead: `DetailsTools.ValidateDetailWrite` (Series/Code/StateAbbr/TSJ shape checks) for detail rows, and `CreateClientTools` (8-char ClientID cap + `^[A-Z0-9]+$` regex + TaxType↔Module match) for whole-row creates.

**Why:** The access map is keyed by the triple `{module}|{table}|{field}` — a shape that fundamentally cannot address (a) detail-row writes, which target a multi-column row (Series/Code/Amount/Description/...) with no single field name to look up, or (b) whole-row creates, whose individual field set isn't enumerable from outside the SDK (the SDK assigns the row + populates derived fields atomically inside `IClient.CreateClient`). Forcing a synthetic key (e.g. `module|table|*`) would either (i) be permanently `Unknown`, hitting the default-refuse path with `force:true` as the only escape — degrading every legitimate detail write into a force-flagged call, or (ii) be permanently `ReadWrite`, providing zero gate. Neither is honest. The purpose-built validators do the real work for these shapes; the access map remains the canonical gate for per-field writes only.

**Alternatives rejected:**
- **Synthesize an access-map row-level key** (e.g. `{module}|{table}|__row__`): hides the shape mismatch behind an opaque convention; future contributor reading the map can't tell row-level from field-level entries.
- **Run `assertWritable` on every field of the row pre-CreateClient**: SDK doesn't expose the canonical field set until after creation; would either over-block (refuse fields that exist in the bound DBF but not in our map) or under-block (no signal for fields the SDK auto-derives).
- **Move the gate to the bridge layer**: bridge already runs `BridgeInputValidation` (defense-in-depth alphabet/length checks); the access map is a *semantic* gate (is this field write-supported in this TY?) that belongs at the session-manager layer, not the wire layer.

**Downstream:**
- The two carve-out sites in `LacerteSessionManager.cs` (lines ~792-797 and ~881-886) carry inline comments pointing at this entry — keep both pointer comments in sync with this title if the entry is ever renamed.
- `ARCHITECTURE.md` Invariant #7 is tightened to read "consulted before every per-field write (`WriteField`)" with an explicit pointer to this entry for the row-level carve-out.
- Any new write entry point that targets a row-level or multi-field shape MUST either (a) extend the explicit-gate pattern (a new `Validate*Write` helper in the tool layer, mirroring `DetailsTools.ValidateDetailWrite`), or (b) document why it can ride the per-field access-map gate. Silently adding a third carve-out without a validator is a regression.
- Future schema work (per-DBF write-supported flag) could one day cover row-level writes; until then, the per-field map is the right shape and the carve-outs are the right answer.

**Source:** `audit-findings-r2.json` P15-003 (P14-002 carve-out documentation); `src/LacertMCP.Server/Session/LacerteSessionManager.cs:792-797, 881-886`; `src/LacertMCP.Server/Tools/DetailsTools.cs` (`ValidateDetailWrite`); `src/LacertMCP.Server/Tools/CreateClientTools.cs`.

---

## 2026-05-30 — COM lifetime: try/finally + Marshal.ReleaseComObject on every dynamic dispatch — P15-003

**Chosen:** Every `dynamic` COM dispatch site in `Bridge/` follows the canonical lifetime pattern: allocate the late-bound COM instance inside a `try` block, perform the dispatch, and release the RCW (Runtime Callable Wrapper) in the `finally` block via `System.Runtime.InteropServices.Marshal.ReleaseComObject(...)` wrapped in a best-effort `try/catch`. Per-call allocation + per-call release is the *only* allowed pattern — no caching, no static singletons, no shared instance across dispatches.

**Why:** `LacerteSDK_Access` exposes only static utility methods through its late-bound COM interface; caching an instance has no performance benefit (SDK does its own internal warm-up across calls) but would change lifecycle semantics (an in-flight call holds the RCW past expected scope, defeating GC pressure measurement and making test-time Mode switching ambiguous). The default CLR finalizer eventually releases an RCW, but only after a GC cycle — which can be tens of seconds under low memory pressure, and `LacerteSDK_Access` holds OS-level handles (file locks on DBFs, Intuit session cookies) that need prompt release. Explicit `ReleaseComObject` decrements the RCW refcount immediately. The `try/catch` around release is for the rare case where the RCW has already been Disconnected by a prior RPC failure — release would throw `InvalidComObjectException`, which is non-fatal at that point.

**Alternatives rejected:**
- **Cached COM instance reused across dispatches**: hides per-call OS handle lifetime, makes Live↔Sim mode switches in tests dirty (the cached instance survives the manager's Dispose), and provides no measurable speedup.
- **`using` + IDisposable wrapper**: `LacerteSDK_Access` doesn't implement `IDisposable` on its dynamic facade — a wrapper would need to swallow `ReleaseComObject`'s rare exception inside `Dispose`, defeating the standard `using` contract.
- **Forget the release and let GC handle it**: 10-30s release latency unacceptable under DBF lock pressure; observable as file-busy errors on the next operator action.
- **Release without try/catch**: a Disconnected RCW throws on release; eats the surrounding business logic's exception and confuses diagnostics.

**Downstream:**
- The canonical template lives in `Bridge/CanUseNewApisProbe.cs:96-103` — copy this shape for every new dynamic dispatch site.
- Current call sites: `Bridge/Writer.cs` (3 sites: `WriteToDBFDatabase`, `UpdateTaxReturn`, `WriteDetail`), `Bridge/Reader.cs` (1 site: `ReadFromDBFData`), `Bridge/CanUseNewApisProbe.cs` (1 site). A future site missing the pattern is a CI-failable lint candidate (Todo F-followup).
- Do NOT relax this rule for "trivial" dispatches — even a single-line late-bound call needs the `try/finally` shape because the dispatch itself can throw mid-call leaving an allocated RCW unreferenced.
- The `Bridge/LacerteAccessClient.cs` wrapper centralizes the allocation entry point but each Writer/Reader/Probe call site still does its own RCW release — wrapper does NOT track instance ownership across boundaries (intentional — bridge methods are short-lived; cross-method ownership would invite leaks).

**Source:** `audit-findings-r2.json` P15-003 (P14-004 invariant documentation); `src/LacertMCP.Server/Bridge/CanUseNewApisProbe.cs:96-103` (canonical template); `src/LacertMCP.Server/Bridge/Writer.cs`; `src/LacertMCP.Server/Bridge/Reader.cs`.

---

## 2026-05-30 — Idempotency cache: per-key SemaphoreSlim + 24h TTL + 10k entry cap — P15-003

**Chosen:** `LacerteSessionManager` serializes concurrent same-key writes via `ExecuteWithIdempotencyLock<T>(key, body)` (line 1317) using a `ConcurrentDictionary<string, SemaphoreSlim>` (`_idempotencyLocks`, lines 105-106). Each unique idempotency key gets its own `SemaphoreSlim(1, 1)` created lazily via `GetOrAdd`. Null/empty key bypasses locking entirely (no idempotency contract, no serialization). Cache entries themselves live in `_idempotencyCache` with `IdempotencyTtl = 24h` (line 115) and `IdempotencyCacheMaxEntries = 10_000` (line 114). Eviction is lazy/opportunistic — expired entries are dropped on the next `TryReplay` hit; over-cap pruning runs only inside `StoreIdempotent` when `_idempotencyCache.Count > MaxEntries` (drops expired-first then the single oldest).

**Why:**
- **Per-key beats one global lock**: two concurrent calls with different keys remain fully concurrent. Only same-key callers serialize, which is the actual contract the lock exists to enforce (TryReplay → execute → StoreIdempotent must be atomic per key, or duplicate executions slip through the cache miss window).
- **Semaphores never removed on release**: removing the semaphore from `_idempotencyLocks` after the last waiter releases opens a classic recreate-race trap (Thread A removes after release; Thread B does `GetOrAdd` and gets a disposed/orphaned reference). Leaving them in is a cheap memory cost (one `SemaphoreSlim` per ever-used key).
- **TTL = 24h matches the Intuit auth window**: entries created before re-auth are semantically stale (different session context, different operator state), so the cache lifetime tracks the auth ticket lifetime rather than an arbitrary number.
- **10k entry cap is empirically generous**: an operator running 100 idempotent writes/minute for an 8-hour day produces 48k entries; the 10k cap forces eviction long before memory becomes a concern but is high enough to avoid eviction during normal interactive sessions.
- **Lazy eviction (no timer, no background thread)**: a background scrubber would need its own lifetime management vs. the session, would burn CPU during idle periods, and would surface as a stray task in diagnostics; opportunistic-on-store is zero-overhead until the cache is actually full.

**Alternatives rejected:**
- **One global lock for the cache**: serializes all writes regardless of key — pathological under concurrent write tools.
- **No eviction (unbounded cache)**: long-running sessions leak memory; an operator who runs a multi-day batch script could OOM the server.
- **Eager removal of semaphores on release**: introduces the recreate-race trap; the memory savings are not worth the correctness cost.
- **Tunable from env**: opens the constants to operator-side tuning that silently changes the idempotency contract (lower TTL → spurious replay misses; higher cap → memory pressure). Constants live in source.
- **Active background scrubber thread**: extra moving part, extra Dispose surface, no measurable win.

**Downstream:**
- Do NOT remove a semaphore from `_idempotencyLocks` after release — the recreate-race is the load-bearing reason it stays. If memory pressure ever becomes real (millions of unique keys over many days), add a periodic scan that removes only semaphores whose `CurrentCount == 1` AND whose corresponding cache entry has been evicted; do NOT change the release-time behavior.
- Adding a new write path that needs idempotency MUST route through `ExecuteWithIdempotencyLock` — rolling a new lock-and-cache pair would defeat both the contract and the eviction budget.
- Tuning `IdempotencyTtl` or `IdempotencyCacheMaxEntries` requires a corresponding `REGRESSION_CHECKLIST.md` item — these constants encode a contract observable from the wire (same key + same payload → replay; same key + different payload → conflict 9002).
- `StoreIdempotent`'s eviction loop is intentionally a single LINQ-shaped scan over ~10k entries — cold path on the over-cap branch, fine to be O(n).

**Source:** `audit-findings-r2.json` P15-003 (P14-005 invariant documentation); `src/LacertMCP.Server/Session/LacerteSessionManager.cs:105-115, 1317-1334, 1364-1395`.

---

## 2026-05-30 — Bridge input validation layer fires before WireLog + COM dispatch — P15-003

**Chosen:** Every late-bound `dynamic` COM dispatch in `Bridge/Writer.cs` and `Bridge/Reader.cs` runs `BridgeInputValidation.Validate*Args(...)` as the FIRST statement of the method, before WireLog records anything and before any RCW is allocated. Four call sites currently route through this layer: `Reader.ReadFromDBFData` (line 43), `Writer.WriteToDBFDatabase` (line 41), `Writer.UpdateTaxReturn` (line 94), `Writer.WriteDetail` (line 156). Validators throw `LacerteSDKError` with a specific synthetic / SDK status code BEFORE the dynamic invocation — `ValidateAppId`, `ValidateDataPath`, `ValidateDbfName`, `ValidateClientNumber`, `ValidateFieldName`, `ValidateStateAbbrev`, `ValidateValue`, `ValidateNumericString`, and the per-entrypoint composites `ValidateWriteToDBFDatabaseArgs` / `ValidateUpdateTaxReturnArgs` / `ValidateWriteDetailArgs` / `ValidateReadFromDBFDataArgs`.

**Why:** Three ordering rationales, in priority order:
- **No PII in the log file from rejected calls.** WireLog redacts SSN/EIN/BankAccountNumber/RoutingNumber but not other fields (FirstName, dataPath, ClientNumber). A rejected call that NEVER should have been logged (e.g. a `dataPath` containing control chars from a misconfigured environment, or an `appId` with embedded braces from a malformed config) would otherwise be persisted to disk with all its values. Bridge validation fires first so the log file only carries calls that got past the structural gate.
- **No COM RCW allocation for trivially-bad inputs.** `LacerteSDK_Access` allocation is a few-ms cold-start cost; allocating and then immediately releasing for a request that was always going to be rejected (NUL in a string, oversized field name, garbage DBF name) wastes both the allocation cycle and the operator's perceived latency on what should be a fast-fail.
- **The validation message is the operator-actionable surface.** A rejected call returns a structured error with `field`, `value` (where safe), `reason` — much more useful than a deep COM stack trace from the SDK rejecting a marshalled value 4 layers down.

Defense-in-depth: higher tool-layer gates (`DetailsTools.ValidateDetailWrite`, `FieldWriteTools` access-map, `ConnectionStringBuilder`, `SqlTemplates`) already validate the public surface. Bridge-layer validation catches a future caller that bypasses the tool layer entirely — a script, a regression harness, a refactor that calls `_bridge.X(...)` directly — and still cannot push a malicious string into the late-bound COM marshaller.

**Alternatives rejected:**
- **Validate inside WireLog**: would mix logging with validation; logging concerns (redaction, format) are orthogonal to structural validation and combining them couples otherwise-independent failure modes.
- **Validate AFTER WireLog records the call**: leaks would-be-rejected inputs into the log file (PII surface).
- **Validate inside the SDK call (let COM reject)**: deeper stack trace, harder operator triage, RCW allocation cost paid for invalid calls.
- **Validate only at the public tool boundary** (tools-only, no bridge layer): leaves the bridge exposed to direct callers and to any future code path that constructs `_bridge.X(...)` calls outside the tool layer.
- **Loose validation (regex-only, no length caps)**: caps + alphabet are independent gates; regex alone misses the oversize-string class of attacks.

**Downstream:**
- Every NEW dynamic bridge method MUST call its composite validator (`Validate{MethodName}Args`) as the FIRST statement of the method body — adding it later is a regression that bypasses the security gate silently.
- Adding a new validator (a new field shape, a new length cap) lives in `Bridge/BridgeInputValidation.cs` only — do NOT scatter validation logic across Writer/Reader. Length-cap constants (`AppIdMaxLength`, `DataPathMaxLength`, `ShortStringMaxLength`, `DescriptionMaxLength`, `StringValueMaxLength`) are intentionally `internal const` so tests can pin them; widening one without a test update is the canonical landmine.
- The four current call sites are the audit-confirmed coverage list. If a fifth `dynamic` dispatch is added to `Bridge/` without a matching `Validate*Args` call, the bridge has a silent hole — treat as a CI-failable lint target (Todo F-followup).
- BridgeInputValidation throws `LacerteSDKError` (the bridge boundary's normal error type) so the existing `WrapToolHandler` envelope path surfaces validation failures with the standard `{success: false, statusCode, ...}` shape — no special handling needed at the tool layer.

**Source:** `audit-findings-r2.json` P15-003 (P14-050 invariant documentation); `src/LacertMCP.Server/Bridge/BridgeInputValidation.cs`; `src/LacertMCP.Server/Bridge/Writer.cs:41, 94, 156`; `src/LacertMCP.Server/Bridge/Reader.cs:43`.

---

## 2026-05-30 — WireLog redaction: HashSet walker, two scopes (P15-001 / P15-006 supersedes prior single-regex / 4-field policy)

**Chosen:** WireLog redaction is **NOT** a single regex over four field names. It is a `JsonNode` walker (`RedactNode` / `ScrubFieldValuePairs` in `src/LacertMCP.Server/Logging/WireLog.cs`) over **two** `HashSet<string>` keysets selected by a `RedactionScope` enum:

- **`_diskRedactionKeys`** at `WireLog.cs:176` — BROAD keyset (~25 keys) used for the on-disk debug log via `WireLog.WriteRecord`. Covers true secrets PLUS identity (`FirstName`, `LastName`, `MiddleName`), address (`Address1`, `Address2`, `StreetAddress`, `City`, `Zip`, `ZipCode`), free-form notes (`Description`), and error-context echoes (`rawError`, `reason`, `odbcMessage`, `message`). Suitable for an archived log that may be attached to a bug report.
- **`_wireRedactionKeys`** at `WireLog.cs:222` — NARROW keyset (~17 keys) used by `WrapToolHandler` for the MCP wire envelope. Covers ONLY true secrets — `BankAccountNumber`, `RoutingNumber`, every `SSN`/`TIN`/`EIN` variant (`SSN`/`TaxpayerSSN`/`SpouseSSN`/`DependentSSN`/`TIN`/`ITIN`/`PTIN`/`EIN`/`EmployerEIN`/`FederalID`/`FedEIN`), `DateOfBirth`/`DOB`/`BirthDate`, `EmailID`/`Email`, all phone variants, and `odbcMessage`. `reason` / `rawError` / `Description` / `City` / `Zip` pass through on the wire so the LLM consumer still sees actionable diagnostics and operator-facing detail rows.
- **`_piiFieldCodes`** at `WireLog.cs:262` — Lacerte DATA1-Individual field codes (`C1_14`, `C1_15`, `C1_19`, `C1_21`, `C1_27`, `C1_28`, `C1_29`, `C1_32`, `C1_71`, `C1_72`). Consulted in BOTH passes: as a KEY name in `RedactNode` (catches the ODBC dict-row shape `{"C1_15":"111-22-3333"}`) and as the VALUE of a sibling `field` key in `ScrubFieldValuePairs` (catches the WriteField payload `{"field":"C1_15","value":"111-22-3333"}`).
- **Fallback regex** at `WireLog.cs:285` — fires ONLY when input fails `JsonNode.Parse` (malformed JSON). Mirrors the broad disk-scope keyset plus the PII field codes. Not the primary mechanism.

**Why:** A `HashSet`-keyed `JsonNode` walk covers nested objects, arrays, and dict-row shapes the regex couldn't reach without fragile pattern accretion (P14-018 / P14-019 / P15-018 / P15-019 series). Splitting wire from disk (P15-001) lets the LLM consumer see error prose without seeing taxpayer identity, while the on-disk log — which may leave the host — gets the wider scrub. Adding identity + address keys to the disk scope (P15-006) was the foundational round-2 fix; the prior 4-field policy was insufficient because Lacerte responses inline `FirstName`/`LastName`/`Address` in routine read paths.

**Alternatives rejected:**
- **Single keyset for both scopes** (the pre-P15-001 design): forces a choice between "scrub the LLM-facing error prose" and "leak taxpayer identity to disk." P14-018 / P14-019 collapsed to one set and immediately re-introduced the wire-side prose loss; P15-001 split them back out.
- **Regex-only redactor over serialized JSON** (the pre-P14-018 design): cannot reach the ODBC dict-row shape (`{"C1_15": ...}` keyed by raw column name) or the WriteField pattern (`{"field": "C1_15", "value": ...}`) where the secret is keyed by a name carrying no PII semantics. The walker handles those; regex remains only as a malformed-JSON fallback.

**Downstream:**
- **Adding a new sensitive field**: pick the correct scope (or both), append to `_diskRedactionKeys` (`WireLog.cs:176`) and/or `_wireRedactionKeys` (`WireLog.cs:222`), add the field name to the fallback `_redactRegex` alternation (`WireLog.cs:285`) if disk-scope, and add a test under `src/LacertMCP.Tests/Logging/WireLogRedactionTests.cs`. Do **NOT** add a new regex. Do **NOT** edit `WireLog.cs:62-64` — those lines are XML doc comments now (the line numbers in the superseded entry above are stale).
- **Adding a new PII Lacerte field code** (e.g. a future `C1_NN` that holds an SSN/DOB/etc.): append to `_piiFieldCodes` (`WireLog.cs:262`) and to the fallback regex alternation so BOTH the dict-row shape AND the WriteField shape are scrubbed.
- **Line-number drift policy**: this entry cites line ranges with a member-name anchor (`_diskRedactionKeys at line 176`) rather than a bare line range so future refactors that shift code by ±20 lines don't break the citation the way the superseded entry's bare `line 62-64` reference broke.

**Source:** `audit-findings-r3.json` P16-034; `src/LacertMCP.Server/Logging/WireLog.cs:176, 222, 262, 285`; supersedes the 2026-05-30 "WireLog format + PII redaction policy" entry above; round-2 work cross-references P14-018 / P14-019 / P15-001 / P15-006 / P15-017 / P15-018 / P15-019.
