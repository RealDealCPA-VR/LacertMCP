# QB MCP Reusable Patterns — Reference for Lacerte MCP Build

**Source**: QuickBooks Desktop MCP (C:/Users/VR/Projects/Quickbooks MCP Desktop/)
**Target**: Lacerte MCP implementation  
**Date**: 2026-05-30

Extracts 14 core architectural patterns with file paths, line numbers, code snippets, and Lacerte adaptation notes. Use as implementation cheat-sheet for direct copy into Lacerte.

---

## 1. Tool Registration Pattern

**File**: src/index.ts lines 49–88, 170–199

QB MCP registers 40+ tools via egisterXxxTools(server, getSession) per domain (customers, vendors, accounts, invoices, bills, etc.). Central lazy getSession() defers session manager creation to first tool call.

**Key elements**: 
- Zod schemas for every tool input with .describe() docstrings
- Central instructions string as source of truth for tool documentation
- Dependency graph ordering (batch tools register after single-entity queries they depend on)
- MCP SDK: server.tool(name, description, zodSchema, asyncHandler)

**What to copy into Lacerte MCP**: Register all tools through egisterXxxTools(server, getSession) per domain. Maintain central instructions string documenting all tools. Use lazy getSession() construction to defer session manager (and ODBC connection pool) creation until first tool call. Same registration surface.

**What differs for Lacerte**: Lacerte uses SQL Server ODBC + COM/.NET instead of QBXMLRP2. The session manager will construct a LacerteSession with ODBC connection pool and COM bridge to Lacerte's object model. Tool registration surface (registerXxxTools signature, getSession() lazy construction, instructions string) stays identical.

---

## 2. Tool Handler Shape — Read and Write

**File**: src/tools/customers.ts lines 13–240

Every tool handler follows this pattern:

`	ypescript
server.tool(
  "qb_customer_list",
  "List or search customers...",
  {
    nameFilter: z.string().optional().describe("Filter by name..."),
    activeOnly: z.boolean().optional().describe("Only active..."),
    // ... more zod fields
  },
  async ({ nameFilter, activeOnly, ... }) => {
    const session = getSession();
    try {
      const result = await session.queryEntity("Customer", filters);
      return {
        content: [{
          type: "text" as const,
          text: JSON.stringify({ count: result.length, customers: result }, null, 2),
        }],
      };
    } catch (err) {
      return formatToolError(err, { fallbackMessage: "CustomerQueryRq failed" });
    }
  }
);
`

Writes follow same pattern but call ddEntity(), modifyEntity(), or deleteEntity(). Support dryRun: true for preview-without-persisting and idempotencyKey for replay protection on creates.

**What to copy into Lacerte MCP**: Every tool handler follows same shape. Call session methods (queryEntity, addEntity, modifyEntity, deleteEntity). Wrap errors with formatToolError(). Support dryRun and idempotencyKey. Response always { content: [{ type: "text", text: JSON.stringify(...) }] }.

**What differs for Lacerte**: Session calls change to querySQL()/executeSP() (SQL Server instead of QBXML entity names), but zod schema, error handling, response-wrapping shape stays identical.

---

## 3. Session Manager Pattern

**File**: src/session/manager.ts lines 1–250+

Session manager is the chokepoint between tools and the wire. Handles:
- Connection lifecycle (open, close, reconnect)
- Mode detection (live vs simulation)
- Chokepoint methods: queryEntity(), addEntity(), modifyEntity(), deleteEntity(), addEntityDryRun(), addEntityIdempotent()
- Read-only gate (statusCode 9001) — fires before any mutation
- Idempotency caching (statusCode 9002) — cached by (key, payload fingerprint)
- Dry-run snapshot/restore — for preview without persist

Lazy construction via getSession() (src/index.ts:196–199) defers session creation until first tool call:
\\\	ypescript
let session: QBSessionManager | null = null;
const getSession = (): QBSessionManager => {
  if (!session) session = new QBSessionManager(config);
  return session;
};
\\\

**What to copy into Lacerte MCP**: Implement LacerteSessionManager with identical interface. Lazy construction via getSession() essential for startup performance and deferring ODBC/COM initialization.

**What differs for Lacerte**: Lacerte session constructs connection pool to SQL Server (ODBC) and COM bridge to Lacerte's object model, not QBXMLRP2. Mode detection probes for SQL Server + Lacerte COM. Each chokepoint builds SQL or calls stored procedure instead of constructing QBXML. Error contract (statusCodes, exceptions) stays the same.

---

## 4. Simulation Store Pattern

**File**: src/session/simulation-store.ts lines 68–400

In-memory mock that seeds realistic QB data and implements CRUD:
- Storage: Map<entityType, Map<listId|txnId, StoredEntity>>
- Entities carry: ListID/TxnID, EditSequence (optimistic concurrency), TimeCreated, TimeModified
- Request processing: parse incoming QBXML, dispatch to specialized handler (handleQuery, handleAdd, handleMod, handleDel)
- Mutations: mutate entity maps, derive dependent fields (Invoice.Subtotal from line set), reverse AR/AP movements on delete
- Snapshot/restore: deep clone all stores + ID counter for dry-run rollback (lines 85–140)

**What to copy into Lacerte MCP**: Lacerte will have SimulationStore in-memory mock seeding realistic tax/accounting data (companies, tax years, forms, schedules, lines with nested structure). Implement snapshot()/restore() pattern for dry-run support. Filter application: flat Record<string,unknown> → JavaScript predicates (POST-FILTER in tool layer when wire has no server-side filter).

**What differs for Lacerte**: Instead of QBXML request parsing, Lacerte sim parses SQL or mocks COM method calls. Instead of per-entity ListID/TxnID keys, Lacerte uses database row IDs or composite keys like {companyId, taxYearId, formId, lineId}. Storage structure richer (nested schedules, form sections with multi-line grids) to match tax-filing domain.

---

## 5. Builder/Parser Separation

**File**: src/qbxml/builder.ts and src/qbxml/parser.ts

Separation contract:
- **Builder** (src/qbxml/builder.ts lines 40–127): Takes flat Record<string,unknown> and produces string (QBXML in QB; SQL in Lacerte)
- **Parser** (src/qbxml/parser.ts): Takes wire response (XML string in QB; ODBC rows in Lacerte) and produces JS objects
- **Tools**: Never construct wire requests directly; always go through session manager

Key parser pattern: Some XML elements can be arrays under certain conditions. Parser maintains pinned arrayElements set so multi-element responses (e.g., multiple CustomerRet) come back as arrays, not single objects.

**What to copy into Lacerte MCP**: Keep builder and parser as separate modules isolated from tool layer. Builder constructs parameterized SQL queries or COM method-call objects from flat input dict. Parser deserializes ODBC result sets or COM return values into JS objects. Tools call session manager; session manager uses builder+parser internally.

**What differs for Lacerte**: Instead of XML string building, Lacerte builder constructs parameterized SQL queries with placeholders. Instead of XML parsing, Lacerte parser deserializes ODBC DataReader rows or COM object properties. Separation discipline (builder isolated, parser isolated, session owns handoff) is identical.

---

## 6. Status Code Mapping — Table in util/qb-status-codes.ts

**File**: src/util/qb-status-codes.ts lines 19–90

Status code table maps codes to human-readable messages:
- QB server-side: 500, 3000, 3030, 3110, 3120, 3170, 3260 (missing field, stale record, invalid value, no permission, etc.)
- Synthetic client-side: 9001–9008 (gates that block before any wire call)

The table is the single source of truth. Lookup function:
\\\	ypescript
export function qbStatusCodeMessage(statusCode: number): string | undefined {
  return TABLE[statusCode];
}
\\\

**What to copy into Lacerte MCP**: Create identical table (Lacerte-specific codes) mapping status codes to human-readable messages. Include both real Lacerte error codes (SQL Server error codes, COM HRESULTs) and synthetic codes for client-side gates (9001–9008 range).

**What differs for Lacerte**: Codes/messages Lacerte-specific. QB uses 3xxx for most errors; Lacerte might use SQL Server native error codes (49xxx+). Synthetic range 9001–9008 portable.

---

## 7. Synthetic Status Codes — Complete Reference

All eight synthetic codes live in src/util/qb-status-codes.ts lines 33–85:

- **9001 (Read-Only Gate)**: Issued by QBReadOnlyError (manager.ts:99–110). Session opened with readOnly:true; mutation blocked client-side (not a wire call). Distinct from 3260 (real QB permission denial).
- **9002 (Idempotency Conflict)**: Issued by QBIdempotencyKeyConflictError (manager.ts:127–143). Previously-seen idempotency key replayed with DIFFERENT payload. Safe failure — retry with fresh key or replay with identical original payload.
- **9003 (Payroll Edition Gate)**: Issued by qb_w2_summary tool. Pro builds do not ship payroll data. Upgrade to Pro Plus, Premier Plus, or Enterprise.
- **9004 (Payroll Subscription Missing)**: Issued by qb_w2_summary. QB returns no payroll data (statusCode 1/empty). Operator must enable payroll subscription in QB Desktop.
- **9005 (Preferences Not Writable)**: Issued by qb_closing_date_set. QBXML SDK has no PreferencesModRq at any version. Set preferences manually in QB Desktop UI.
- **9007 (QB Desktop Launch Failed)**: Issued by QBLaunchError (manager.ts:162–177). Another company file open (swap needed), launch timed out, or QB exe not found.
- **9008 (Multi-User Lock)**: Issued by QBMultiUserLockError (manager.ts:185–194). Target .qbw held by another user. Wait for release or open in single-user mode.

**What to copy into Lacerte MCP**: Define 9001–9008 (or similar) range for synthetic codes. Each code maps to specific client-side gate (read-only, idempotency conflict, edition/subscription check, COM initialization, database connection, etc.). Document each in the status table. Issue from appropriate exception class in session manager.

**What differs for Lacerte**: Codes/conditions Lacerte-specific. Instead of QB payroll edition (9003), Lacerte might have tax-prep edition gates (Standard vs. Professional vs. Enterprise). Instead of QB launch failures (9007), Lacerte might have COM object registration failures or database connection pool exhaustion. Pattern (9001–9008 synthetic range, backed by specific exception classes) is portable.

---

## 8. Read-Only Mode Chokepoint

**File**: src/session/manager.ts lines 99–110

QBReadOnlyError exception:
\\\	ypescript
export class QBReadOnlyError extends Error {
  statusCode: number;
  constructor(operation: string) {
    super(
      \Read-only session: \ rejected. The session was opened with \ +
      \eadOnly: true. Reconnect with qb_session_connect({ readOnly: false }) \ +
      \	o re-enable mutations.\
    );
    this.name = "QBReadOnlyError";
    this.statusCode = 9001;
  }
}
\\\

Before every mutation (addEntity, modifyEntity, deleteEntity, executeBatchAdd), session manager checks:
\\\	ypescript
if (this.readOnly) {
  throw new QBReadOnlyError("CustomerAddRq");
}
\\\

This fires BEFORE any idempotency check, BEFORE snapshot/restore for dry-run, BEFORE the wire call. Read-only is a hard gate.

**What to copy into Lacerte MCP**: Define LacerteReadOnlyError exception. In session manager, check this.readOnly at START of every mutation method. Throw immediately with statusCode 9001 if true. This gate fires before dry-run, before idempotency, before any SQL/COM call.

**What differs for Lacerte**: Exception class name/message Lacerte-branded. Structure (synchronous gate, statusCode 9001, thrown before any side effect) is identical.

---

## 9. Idempotency Keys — Cache Design and State Machine

**File**: src/session/manager.ts lines 127–143; tests in tests/idempotency-keys.test.ts lines 67–100

Idempotency state machine (three cases):

1. **Cache miss** (first call with key K): Execute create, cache result by key K + payload fingerprint, return { entity, replayed: false }
2. **Cache hit + same payload** (replay with key K + original payload): Return cached entity, set replayed: true, make NO wire call
3. **Cache hit + different payload** (key K + NEW payload): Throw QBIdempotencyKeyConflictError (statusCode 9002)

Cache scope: Per company file (cleared on switchCompanyFile). Fingerprint canonicalization: order-insensitive for object keys, order-sensitive for arrays. Failed creates do NOT poison the cache (no cache entry if call threw).

Wire: Exposed at manager level (addEntityIdempotent), optional in tools:
\\\	ypescript
if (idempotencyKey) {
  const result = await session.addEntityIdempotent("Customer", data, idempotencyKey);
} else {
  const result = await session.addEntity("Customer", data);
}
\\\

**What to copy into Lacerte MCP**: Session manager maintains in-memory cache keyed by (idempotencyKey, entityType). On every idempotent add: compute canonical fingerprint of payload (sort keys, stringify), check cache, handle hit-with-match (return cached + replayed:true), hit-with-mismatch (throw 9002), miss (execute and cache). Cache cleared per company file (in Lacerte, per database/tax return context).

**What differs for Lacerte**: Fingerprint canonicalization might account for nested form structures, but state machine (miss/hit-match/hit-mismatch) is identical.

---

## 10. Dry-Run Mechanism

**File**: src/session/simulation-store.ts lines 85–140

Dry-run via snapshot/restore:

Manager's addEntityDryRun method:
\\\	ypescript
async addEntityDryRun(type: string, data: Record<string, unknown>) {
  const snap = this.simulationStore.snapshot();  // deep clone all stores
  try {
    const result = await this.addEntity(type, data);  // execute mutation
    return { entity: result.entity, dryRun: true };    // preview mode
  } finally {
    this.simulationStore.restore(snap);  // rollback in finally → always runs
  }
}
\\\

Snapshot captures:
\\\	ypescript
snapshot(): { stores: Map<string,EntityStore>; idCounter: number; auditTrail: AuditTrailEntry[] } {
  // Deep clone all stores using structuredClone(entity) for each entity
  // This ensures mutations (Customer.Balance increments, line array appends)
  // don't leak through restore
}

restore(snap: { stores; idCounter; auditTrail }): void {
  // Swap atomically — mutations discarded
}
\\\

Finally block is critical — runs even if mutation throws, so doomed-entity errors don't leave half-mutations behind.

**What to copy into Lacerte MCP**: Implement dry-run via snapshot/restore. Capture the mutable state of simulation store (all entity maps, ID counter, audit trail). Run mutation. Rollback via restore in finally block. Finally is essential — runs even on throw.

**What differs for Lacerte**: Instead of snapshotting Map of entities, Lacerte snapshots in-memory form/schedule/line hierarchy or transaction log. Structure changes, pattern (snapshot → execute → finally restore) identical.

---

## 11. Debug Wire Log — QB_DEBUG_QBXML Environment Variable

**File**: src/util/qbxml-logger.ts lines 1–263

Activation: QB_DEBUG_QBXML=1 (literal string "1")

\\\	ypescript
// qbxml-logger.ts:241–251
export function getQbxmlLogger(env: NodeJS.ProcessEnv = process.env): QbxmlLogger | null {
  if (cacheInitialized) return cachedLogger;
  cacheInitialized = true;
  if (env.QB_DEBUG_QBXML !== "1") return null;  // Only "1" activates
  const logDir = env.QB_DEBUG_LOG_PATH ?? resolve(process.cwd(), "logs");
  cachedLogger = new QbxmlLogger({ logDir });
  return cachedLogger;
}
\\\

Output: Rotating per-day files named qbxml-YYYYMMDD.log in log directory (default ./logs). Each entry:
\\\
=== ISO_TIMESTAMP mode=live|simulation seq=N op=request|response|error durationMs=X ===
<envelope or response body>

\\\

Redaction (lines 52–74): Sensitive tags (VendorTaxIdent, SSN, BankAccountNumber, CreditCardNumber) replaced with [REDACTED] before write.

Logging chokepoint at sendRequest():
\\\	ypescript
const logger = getQbxmlLogger();
const marker = logger?.logRequest(qbxmlString, "live");
try {
  const response = await this.qbxmlrp2.sendRequest(qbxmlString);
  logger?.logResponse(response, "live", marker);
} catch (err) {
  logger?.logError(err, "live", marker);
  throw err;
}
\\\

**What to copy into Lacerte MCP**: When LACERTE_DEBUG_SQL=1, capture every SQL statement sent to database and every result set returned. Log to rotating per-day files (debug-YYYYMMDD.log). Redact sensitive fields (SSN, TaxID, bank account, credit card). Use same format (timestamp, mode, seq, op, duration). Use try/catch/finally pattern so even failed requests logged before re-throw.

**What differs for Lacerte**: Instead of QBXML envelopes, Lacerte logs SQL queries (parameterized, placeholders for sensitive). Instead of XML parsing, Lacerte logs ODBC result sets or COM properties. Filename pattern (debug-YYYYMMDD.log), redaction discipline, logging chokepoint structure identical.

---

## 12. Doctor CLI — Diagnostic Probes with Exit Code Contract

**File**: src/cli/doctor.ts lines 1–435

Seven probes, each pure over DoctorDeps injection bag. Exit codes: 0 (all green), 1 (at least one fail), 2 (at least one skip/inconclusive).

Probes (src/cli/doctor.ts lines 112–305):
1. probeNodeVersion — Node 20.x required (winax ABI)
2. probePlatform — Windows-only (live mode)
3. probeQBInstall — QB Desktop exe resolution (env → registry → fallback)
4. probeComRegistration — QBXMLRP2 COM registered (via reg query)
5. probeCompanyFile — QB_COMPANY_FILE set and file exists (required)
6. probeCompanyRoot — QB_COMPANY_ROOT optional, but if set must exist
7. probeWinax — winax loadable (Windows-only, native addon)

Orchestration (lines 314–328):
\\\	ypescript
export function runDoctor(deps: DoctorDeps): DoctorReport {
  const probes: ProbeResult[] = [
    probeNodeVersion(deps),
    probePlatform(deps),
    probeQBInstall(deps),
    probeComRegistration(deps),
    probeCompanyFile(deps),
    probeCompanyRoot(deps),
    probeWinax(deps),
  ];
  const anyFail = probes.some((p) => p.status === "fail");
  const anySkip = probes.some((p) => p.status === "skip");
  const exitCode: 0 | 1 | 2 = anyFail ? 1 : anySkip ? 2 : 0;
  return { probes, exitCode };
}
\\\

**What to copy into Lacerte MCP**: Build quickbooks-lacerte-mcp-doctor CLI. Define 5–7 Lacerte-specific probes (Node version, platform, Lacerte install, Lacerte COM registration, database connectivity, env vars). Exit code contract: 0 (all pass), 1 (at least one fail), 2 (at least one skip). Per-probe remediation hints on fail. Inject environmental facts via DoctorDeps for testability.

**What differs for Lacerte**: Probes Lacerte-specific (Lacerte install path, Lacerte COM ProgID, database connectivity instead of file existence). Instead of QB launch failure (9007), might have COM object registration or connection pool failure. Structure (pure probes, exit code contract, remediation hints) identical.

---

## 13. MCP Prompts — Workflow Bundles via server.prompt()

**File**: src/prompts/workflows.ts lines 1–160+

Each prompt: Zod schema + callback returning single user-role message. Optional args with defaults. References exact qb_* tool names so agent's tool-use loop is deterministic.

Register via:
\\\	ypescript
export function registerWorkflowPrompts(server: McpServer): void {
  server.prompt(
    "month_end_close",
    "Month-end close workflow...",
    monthEndCloseArgs,  // Zod schema
    monthEndClosePrompt  // Callback
  );
}
\\\

Single prompt example (monthEndClosePrompt, lines 118–150):
\\\	ypescript
const monthEndCloseArgs = {
  fromDate: z.string().regex(...).optional()
    .describe("Start of month in ISO YYYY-MM-DD. Default: first day of prior month."),
  toDate: z.string().regex(...).optional()
    .describe("End of month in ISO YYYY-MM-DD. Default: last day of prior month."),
  bankAccountName: z.string().optional()
    .describe("Operating bank account FullName to reconcile..."),
};

export const monthEndClosePrompt: PromptCallback<typeof monthEndCloseArgs> = (args) => {
  // Compute defaults from priorCalendarMonth()
  // Build markdown text with step-by-step instructions
  // Reference exact qb_* tool names for agent to call
  return {
    description: "Full month-end close workflow...",
    messages: [{
      role: "user",
      content: { type: "text", text: markdown }
    }],
  };
};
\\\

**What to copy into Lacerte MCP**: Register Lacerte workflow prompts using same MCP server.prompt() API. Examples: tax_return_prep (walk through Schedule C / 1099 collection), estimated_quarterly_prepare (quarterly ES planning), tax_projection (year-end accrual/withholding). Each is Zod schema + callback returning single user-role message. Optional args with defaults.

**What differs for Lacerte**: Workflow steps and tool names change (lacerte_form_query, lacerte_schedule_detail, lacerte_projection_calc instead of qb_pnl_report, qb_ar_aging). Registration pattern (server.prompt, optional args with defaults, single user message, exact tool-name refs) identical.

---

## 14. Test Harness — Simulation-First Structure

**File**: 	ests/idempotency-keys.test.ts and 40+ test files

All tests run in simulation mode. Set companyFile: "simulation" to force sim. Exercise full stack: session manager → simulation store → tool handler → MCP server.

Test pattern (lines 22–57):
\\\	ypescript
import { describe, it, expect, beforeEach } from "vitest";
import { QBSessionManager } from "../src/session/manager.js";

type Handler = (args: unknown) => Promise<{ ... }>;
const handlers = new Map<string, Handler>();
const fakeServer = { tool: (name, ..., handler) => handlers.set(name, handler) };

function freshSession(): QBSessionManager {
  return new QBSessionManager({
    companyFile: "simulation",  // Forces sim mode (not live)
    appName: "vitest-idempotency",
    qbxmlVersion: "16.0",
    connectionMode: "optimistic",
  });
}

describe("addEntityIdempotent — cache miss/hit/conflict", () => {
  it("miss: first call creates record with replayed: false", async () => {
    const session = freshSession();
    await session.openSession();
    const { entity, replayed } = await session.addEntityIdempotent(
      "Customer",
      { Name: "Acme A" },
      "key-A",
    );
    expect(replayed).toBe(false);
    expect(entity.ListID).toBeDefined();
  });
});
\\\

Coverage layers (from test file docstrings):
1. **Manager layer** — idempotency state machine (miss/hit-same/hit-conflict), read-only gate, snapshot/restore
2. **Simulation layer** — seed data loads, CRUD works, filters apply, entity mutations
3. **Tool layer** — structured JSON response, error handling, status codes
4. **Integration** — end-to-end tool calls through MCP server

Tests fast, deterministic, no live QB needed. Run in CI.

**What to copy into Lacerte MCP**: Use vitest (or Jest). Create fresh sessions with companyFile: "simulation" to force sim mode. Test layered coverage: manager (idempotency, dry-run, read-only), sim (seed data, CRUD, filtering, mutations), tool (JSON shape, error handling), integration (end-to-end). No live tax software connection required.

**What differs for Lacerte**: Instead of simulating QB entity stores, Lacerte tests seed tax-year data (forms, schedules, lines). Instead of testing QBXML round-trip, Lacerte tests test SQL round-trip or COM object mapping. Test structure (simulation-first, layered coverage, fast+deterministic) identical.

---

## Summary — Implementation Checklist

1. **Tool registration** — registerXxxTools per domain, lazy getSession()
2. **Tool handlers** — server.tool(name, schema, asyncHandler), session methods, formatToolError
3. **Session manager** — queryEntity, addEntity, modifyEntity, deleteEntity, addEntityDryRun, addEntityIdempotent
4. **Simulation store** — Map<type,Map<id,entity>>, snapshot/restore, handle request dispatch
5. **Builder/parser** — builder constructs requests, parser deserializes, tools don't touch either
6. **Status codes** — table lookup, 9001–9008 synthetic codes documented
7. **Read-only gate** — check this.readOnly at mutation start, throw QBReadOnlyError (9001)
8. **Idempotency** — cache by (key,fingerprint), state machine (miss/hit-same/hit-diff)
9. **Dry-run** — snapshot/restore in try/finally, expose dryRun: true argument
10. **Debug logging** — QB_DEBUG_QBXML=1 (or equivalent), rotating logs, redact sensitive fields
11. **Doctor CLI** — 5–7 probes, exit codes 0/1/2, per-probe remediation
12. **MCP prompts** — server.prompt(), optional args with defaults, exact tool names
13. **Tests** — vitest, simulation-first (companyFile:"simulation"), layered coverage
14. **Operating system** — HANDOFF.md, todo.md, ARCHITECTURE.md, DECISIONS.md, REQUIREMENTS.md, REGRESSION_CHECKLIST.md, ACCEPTANCE_CRITERIA.md

---

**Document Version**: 2026-05-30  
**Extracted From**: QB MCP Phase 19 #91  
**Status**: Ready for implementation. Directly copy patterns into Lacerte MCP.