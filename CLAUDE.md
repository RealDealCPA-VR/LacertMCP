# Project Operating System — Lacerte MCP

## Purpose

This file is the operating system for AI-assisted development on the Lacerte MCP server. It mirrors the discipline used on the sibling Quickbooks Desktop MCP project — proven across 90+ landed tasks — adapted to the Lacerte SDK's specifics (COM/.NET + ODBC + interactive Intuit auth).

The goal is not raw speed. The goal is code that stays coherent, verifiable, maintainable, and transferable across many AI sessions.

> Treat each AI session like a replaceable contractor that must read the brief, verify prior work, complete one bounded task, and leave clean state for the next contractor.

---

## Project snapshot

* **What it is** — MCP server that exposes Intuit Lacerte tax software (TY18+) as tools an LLM can call.
* **Wire path** — JSON-RPC/stdio for the MCP, COM/.NET (`LacerteSDK_Access`) for registration / create-client / TY21+ writes, ODBC (`LacerteDSII` DSN) for SQL reads + detail files.
* **Two modes** — `live` (Windows-only, real SDK) and `simulation` (in-memory DBF fixtures, default off-Windows).
* **Transport** — stdio MCP server bound in `src/index.ts`.
* **Single operator** — Personal tool. Bar is "works flawlessly," not "ships to customers."

---

## Required project files

* **`HANDOFF.md`** — short-term session continuity. Read this first.
* **`Todo.md`** — ordered task queue. Phases laid out so simulation work comes before live so most tasks can be verified off-Windows.
* **`ARCHITECTURE.md`** — stable structural rules; update sparingly.
* **`DECISIONS.md`** — dated entries for meaningful tradeoffs.
* **`REQUIREMENTS.md`** — product truth (what the operator needs the MCP to do).
* **`REGRESSION_CHECKLIST.md`** — pre-completion verification list.
* **`ACCEPTANCE_CRITERIA.md`** — what "done" means per task.
* **`Lacerte_MCP_Structure_Spec.md`** — source spec mined from the Lacerte SDK Developer Instructions PDF.
* **`docs/research/`** — fan-out research outputs that inform decisions.

---

## Agent session lifecycle

### 1. PICKUP — Read state first

Before writing any code, read in this order:

1. `HANDOFF.md`
2. `Todo.md`
3. `ARCHITECTURE.md` if the task touches session lifecycle, mode switching, bridge boundaries, or tool registration.
4. `DECISIONS.md` if the task touches an area that has been previously debated (COM vs ODBC, simulation fidelity, etc.).
5. `REQUIREMENTS.md` if the task affects what an exposed tool must do for the operator.

If `HANDOFF.md` has a **Verify Before Continuing** section, those checks are top priority — fix any failures before new work.

### 2. WORK — Implement one bounded task

* Work sequentially within the current phase of `Todo.md`.
* Reuse existing patterns; look at neighboring tool files first.
* Adding a tool means: register in `src/index.ts`, handle the request in the simulation store, update the `instructions` blurb, and add it to the README tool table.
* Test continuously — `npm run build` after each meaningful change.

### 3. VERIFY — Confirm the work functions

Run the items in `REGRESSION_CHECKLIST.md` relevant to your changes. Live-mode work cannot be marked complete until exercised on a Windows + Lacerte box; if you can't do that in-session, leave the task partial in `HANDOFF.md` with explicit verification steps.

### 4. HANDOFF — Write state before stopping

* Update `Todo.md` — check off only what is fully working.
* Write a fresh `HANDOFF.md` using the template the `/handoff` skill enforces.
* Log major design changes in `DECISIONS.md`.

---

## Stable code conventions (initial)

These will firm up as the architecture lands. Default assumptions:

* TypeScript strict mode, ES2022, ESM modules (`"type": "module"`), `Node16` module resolution.
* Always use `.js` extensions on relative imports.
* MCP tools registered via `server.tool(name, description, zodSchema, handler)`.
* Zod schemas for every tool input — `.describe()` every field.
* Tool handlers return `{ content: [{ type: "text" as const, text: JSON.stringify(...) }] }`; `isError: true` on failure.
* One file per tool domain in `src/tools/` (e.g. `clients.ts`, `tables.ts`, `details.ts`, `efile.ts`).
* Session lifecycle through `LacerteSessionManager` only; tools never construct COM calls or ODBC queries directly.
* COM interop in `src/bridge/`; ODBC in `src/odbc/`; simulation in `src/session/simulation-store.ts`.
* Schema metadata (modules, tables, fields, access map, status enum) in `src/schema/` and treated as read-only.
* TAPIStatus mapping in `src/errors/status-codes.ts`; tools never expose raw integers.

---

## Two-mode discipline

Like Quickbooks MCP Desktop, this server runs in `simulation` mode by default off-Windows and in `live` mode on Windows when `LACERTE_LIVE=1` is set. The non-negotiable rule:

> **Simulation and live must be observationally identical for the happy path.** A tool can't tell which mode it's running in by reading the response shape.

When you change a live behavior, change the simulation too (or stub it) so the two stay in sync.

---

## What this operating system prevents

Cross-session context loss, silent divergence between live and simulation, ad-hoc architectural drift, checkbox inflation without working behavior, undocumented design choices, and codebase entropy.

What it does not do: replace human architectural judgment, verify live mode against a real Lacerte install, or substitute for actual engineering. Those still need you.
