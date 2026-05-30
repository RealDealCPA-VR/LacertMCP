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

## 2026-05-30 — MCP SDK version pinning: `ModelContextProtocol` 0.3.0-preview.4 (not 1.x)

**Chosen:** Pin `ModelContextProtocol` NuGet to **`0.3.0-preview.4`** in `LacertMCP.Server.csproj`. The earlier research report (`docs/research/agents/01-stack-decision.md`) cited "v1.3.0 as of May 8, 2026" — that was incorrect.

**Why:** The highest version actually published on NuGet at Phase-0-implementation time was `0.3.0-preview.4`. The SDK has not yet reached 1.x stable. The 0.3.x API is feature-equivalent for our needs (`WithStdioServerTransport()`, `[McpServerTool]`, `WithToolsFromAssembly()`) — build verified end-to-end with 105 tests passing.

**Alternatives rejected:** Waiting for 1.x — uncertain timeline, blocks the build today. Pinning to a private fork — overkill.

**Downstream:**
- Carry follow-up F9 in `Todo.md` to bump to 1.x when it ships.
- Watch SDK release notes for breaking changes between 0.3.x and 1.x — minimal blast radius (a handful of attribute usages).
- The research report's claim of "feature-equivalent to TS SDK" holds; only the version number was wrong.
