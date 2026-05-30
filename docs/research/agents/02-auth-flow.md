# Lacerte MCP — Auth & Session-Lifecycle Design

Status: research / proposal
Scope: Layer B (Session & auth) of `Lacerte_MCP_Structure_Spec.md`
Companion to: `01-bridge-runtime.md` (planned)

The Lacerte SDK's `ITPRegistration.Register(AppName, AppID, TaxYear)` is the riskiest
moment in the build: it spawns **two interactive GUI windows** (a one-time-per-user
consent dialog, then an Intuit sign-in window) and the resulting authorization expires
every **24 hours**. Both behaviors are confirmed by the SDK Developer Instructions and
by Intuit's own support copy ("You will need to enter your Lacerte sign-in credentials
again when your session expires in 24 hours"). MCP servers run under headless clients
(Claude Desktop, Cursor), so neither dialog can be allowed to surface mid-tool-call.
This document picks one design across the seven dimensions the spec calls out.

---

## 1. Registration trigger — **one-time CLI setup, not lazy first-call**

Use a dedicated CLI subcommand:

```
npx lacerte-mcp register --app-name "Operator Lacerte MCP" --tax-year 2024
```

Mint and persist the GUID on first run; subsequent invocations reuse it. The MCP
server process itself never invokes `Register()` unsolicited.

Reasoning:

- **Lazy first-call** would block the first tool the agent calls behind two modal
  dialogs the agent's host can't see, can't dismiss, and can't time out. The agent
  appears hung; the operator has no clear remediation path.
- The MCP transport is stdio under a parent client. Surfacing a GUI from a child
  process owned by a GUI host is racy — the dialog can end up Z-ordered behind
  Claude Desktop with no taskbar entry.
- A CLI run is explicitly *interactive*: the operator opens a terminal, runs the
  command, watches the consent + sign-in dialogs pop, clicks through, and gets a
  written confirmation. This is the same operator experience as the QuickBooks
  MCP's initial Application Certificate approval — which is also "drive it once in
  an interactive shell, then the MCP just rides the trust."
- One-time-per-user/per-system means the consent dialog is a setup step, not a
  runtime concern. Treating it as runtime conflates two different ceremonies.

The same CLI binary also gets a `npx lacerte-mcp login` subcommand for the 24-hour
re-sign-in (which DOES happen at runtime — see §3).

---

## 2. Surfacing GUI dialogs — operator runs the CLI in an interactive shell

Of the three options, pick **(a)**: require the operator to run `register` and
`login` from an interactive PowerShell. Rejecting the others:

- **(b) MCP launches a helper window** — every "small standalone helper" idea ends
  up reimplementing a window manager. The Intuit sign-in window is rendered by
  Lacerte's own process; we don't own its event loop and can't reparent it.
- **(c) Require Lacerte already open and warm** — Lacerte being open does NOT
  bypass `Register()`. The SDK consent + sign-in are SDK-scoped, not app-scoped.
  This conflates the spec's prerequisite ("a successful login through the Lacerte
  app must have already occurred") with the SDK's own auth gate.

The MCP server process, when started by Claude Desktop, will:

1. Read the persisted AppID GUID and AppName from config.
2. Attempt `Register()` ONCE at startup (the SDK treats a Register call within the
   24-hour window as a no-op session refresh — confirmed by the spec's
   `AccessGranted` semantics).
3. If it returns `NotSignedIn`, the MCP does **not** open a dialog. It marks every
   tool call's response with the structured error in §3 and instructs the operator
   to run `npx lacerte-mcp login`.

Concrete file structure:

```
src/cli/
  register.ts   ← interactive setup; mints GUID; persists; runs Register()
  login.ts      ← re-runs Register() inside the 24h window; surfaces sign-in
  status.ts     ← prints whether the cache is fresh / expired / unregistered
src/index.ts    ← MCP entry; refuses to start if no GUID configured
```

---

## 3. 24-hour re-login UX — detect via `NotSignedIn`, surface structured error

The SDK has no exposed "TTL remaining" call. Detection is reactive: any wire call
that returns `TPSDKStatus.NotSignedIn` (or registration returning the same code on
a refresh attempt) means the operator must re-sign in. The session manager catches
this and converts it. Tool error shape:

```json
{
  "isError": true,
  "statusCode": "NotSignedIn",
  "statusCodeNumeric": 9101,
  "category": "auth",
  "humanReadable": "Lacerte SDK session expired (24-hour Intuit sign-in window elapsed).",
  "remediation": "Run `npx lacerte-mcp login` in a terminal on this machine. An Intuit sign-in window will appear — enter the same credentials you use to sign in to Lacerte itself. The MCP will resume the next tool call automatically.",
  "details": {
    "lastSuccessfulRegisterAt": "2026-05-29T14:22:11Z",
    "approxExpiryAt": "2026-05-30T14:22:11Z",
    "appName": "Operator Lacerte MCP",
    "taxYear": 2024
  }
}
```

Notes:

- `statusCodeNumeric` lives in the synthetic-sentinel range (9100–9199) to match
  the QB MCP convention (`9001` read-only, `9007` launch). Lacerte's own
  `TPSDKStatus` is an enum without stable integers across versions, so the string
  is canonical and the numeric is a stable MCP-side mapping.
- `lastSuccessfulRegisterAt` is tracked in `LacerteSessionManager` (in-memory) and
  optionally persisted to `%APPDATA%/lacerte-mcp/session.json` so a process
  restart doesn't lose the timestamp.
- The MCP **does not** try to auto-re-Register on the failed tool call — the
  sign-in window would steal focus mid-tool-call (the failure mode in §1). Always
  push the operator through the CLI ceremony.

To reduce surprise expiry mid-conversation, ship `lacerte_session_status` (§6)
that returns `expiresAt` and recommend agents call it at conversation start. A
soft warning at T-30min ("session expires soon, run `lacerte-mcp login` to extend")
can be added later.

---

## 4. AppID GUID — one per install, stored in OS keychain with config fallback

Recommendation: **one GUID per install, minted on first `register` run, persisted
to the Windows Credential Manager** (via the `keytar` npm package) under service
`lacerte-mcp` / account `app-id`. Fall back to `%APPDATA%/lacerte-mcp/config.json`
if keytar is unavailable.

Reasoning:

- **One per install** (not per operator) — the SDK identifies the third-party app,
  not the user. Per-operator would force a fresh consent dialog every time a
  different OS user runs the MCP, which is annoying without buying anything.
- **Not hard-coded** — the spec example GUID `9C3EF09C-DA54-4531-9A82-8F1EEA922AE0`
  is Intuit's sample and should never be reused in production. If we shipped a
  hard-coded GUID, every operator's install would share an identity, which makes
  Intuit-side abuse blocking impossible to scope.
- **Not `.env`** — env files leak into shell histories, accidentally checked-in
  dotfiles, and process listings. A GUID isn't exactly a secret (the consent
  dialog already shows the app identity to the operator), but it's installation-
  identifying and belongs in the same trust store as the QB MCP's certificate.
- **Keychain over file** — `keytar` gives encrypted storage with OS-level ACL;
  the JSON fallback is read-only to the owning user. Both are addressable via
  the `getAppId()` accessor on `LacerteSessionManager` (same shape as
  `QBSessionManager.getAppId()`).

Note that the spec is firm that **the GUID is permanent** — once registered with
a given operator and approved by their consent dialog, changing the GUID forces a
fresh consent ceremony. The CLI must refuse to mint a new GUID if one already
exists; explicit `--reset` flag required to override.

---

## 5. Session caching — yes, the SDK keeps the session alive within 24h

The spec and Intuit's support copy together imply: after `Register()` returns
`AccessGranted`, subsequent SDK calls (ODBC queries, `ILacerteAccess` reads/
writes, `IClient.CreateClient`) ride the same session **without re-prompting**
until the 24-hour window elapses. The session is process-bound for ODBC but
machine-bound for the Intuit auth ticket — meaning a restart of the MCP process
within 24 hours can re-call `Register()` and get a silent `AccessGranted` (no
dialogs) because the auth ticket is still cached at the OS/Lacerte level.

Practical caching strategy in `LacerteSessionManager`:

- Hold the `ITPRegistration` instance in memory between tool calls. Don't
  re-register on every call (that would cost a round trip even when silent).
- On startup, call `Register()` once. If it returns `AccessGranted`, mark the
  manager as `registered+logged-in` and stamp `registeredAt = now()`.
- Track `expiresAt = registeredAt + 24h`. Compare on each tool call entry; if
  expired, short-circuit to the §3 error WITHOUT calling the SDK (avoid the
  Intuit dialog firing in the wrong process).
- On a `NotSignedIn` from a wire call (clock skew, server-side revoke), transition
  immediately to expired and return the §3 error.

This mirrors the QB MCP's `idempotencyCache` / `hostInfoCache` pattern — session
state survives `openSession`/`closeSession` and is only cleared on explicit
reset.

---

## 6. State machine

```
                       ┌─────────────────────┐
                       │   UNREGISTERED      │  (no GUID in keychain)
                       │   no GUID minted    │
                       └──────────┬──────────┘
                                  │ `npx lacerte-mcp register`
                                  │  (mints GUID, calls Register,
                                  │   consent + Intuit dialogs)
                                  ▼
        ┌──────────────────────────────────────────────────┐
        │            REGISTERED + LOGGED-IN                │ ◄─────────┐
        │   registeredAt set; expiresAt = +24h             │           │
        │   tool calls return real data                    │           │
        └──┬───────────────────────┬────────────────┬──────┘           │
           │                       │                │                  │
           │ MCP process exit      │ 24h elapses    │ explicit         │
           │ (state persists in    │ OR NotSignedIn │ logout (rare)    │
           │  session.json)        ▼                ▼                  │
           │           ┌─────────────────────────────────┐             │
           │           │   REGISTERED + EXPIRED          │             │
           │           │   tool calls return §3 error    │             │
           │           │   with NotSignedIn + remediation│             │
           │           └─────────────┬───────────────────┘             │
           │                         │  `npx lacerte-mcp login`        │
           │                         │   (re-calls Register inside     │
           │                         │    24h cache → silent OR        │
           │                         │    Intuit sign-in window)       │
           │                         └─────────────────────────────────┘
           │
           ▼
        ┌──────────────────────────────────────────────────┐
        │      REGISTERED + LOGGED-OUT (process restart)   │
        │   GUID present; no in-memory registration        │
        │   first tool call triggers silent Register()     │
        │   → REGISTERED + LOGGED-IN if within 24h cache   │
        │   → REGISTERED + EXPIRED otherwise (§3 error)    │
        └──────────────────────────────────────────────────┘
```

Tool surface that drives transitions:

| Tool | Purpose | Where it lives |
|---|---|---|
| `lacerte_register` | Mint GUID + first-time `Register()`. Refuses if GUID already exists unless `force:true`. Used inside the `register` CLI; also callable from agent for "I've reset, redo." | `src/tools/session.ts` |
| `lacerte_session_status` | Snapshot: `{ state, appName, appId, taxYear, registeredAt, expiresAt, minutesUntilExpiry, simulationMode }`. No wire I/O. Mirrors `qb_session_status`. | `src/tools/session.ts` |
| `lacerte_session_refresh` | NEW. Re-call `Register()` from within the MCP process. Returns success silently if 24h cache is warm; returns the §3 error if expired (does NOT pop a dialog). Used by agents to probe "am I still good?" without making a real wire call. | `src/tools/session.ts` |
| `lacerte_session_logout` | NEW (low-priority). Drops the in-memory `ITPRegistration` reference. Mostly for testing the expired→logged-in transition. | `src/tools/session.ts` |

No `lacerte_login` tool — login is the CLI ceremony, not an MCP tool, for the
same dialog-surfacing reason as `register`.

---

## 7. What to copy from the QuickBooks MCP — and what to do differently

Copy:

- **Synthetic sentinel statusCode range** (`9001`, `9007`, `9008` in QB → `9101`+
  in Lacerte for auth errors). Tools never expose raw enum integers; the error
  mapping layer (`src/errors/status-codes.ts`) owns the mapping.
- **Two-mode discipline** — simulation mode skips the entire Register flow and
  jumps to `REGISTERED + LOGGED-IN` with synthetic `registeredAt = now`. Same
  observational shape as live mode so tests are mode-agnostic.
- **`get*` accessor pattern** (`getSession`, `getAppId`, `getAppName`,
  `peekHostInfoCache`) — keeps `LacerteSessionManager` testable.
- **Lazy-initialized singleton** in `src/index.ts` — one `LacerteSessionManager`
  per MCP process, passed to every tool registrar via `getSession()`.
- **`*_session_status` shape** — fail-soft, no wire I/O on the default path, all
  the identity fields in one snapshot.

Differ:

- **QB Desktop has no expiry.** The QB Application Certificate is approved once
  per (machine, appName) tuple and lasts until the operator revokes it in QB's
  Integrated Applications panel. Lacerte has a *hard* 24-hour expiry that an
  operator cannot disable. The QB MCP's auto-reconnect retry loop
  (`RECONNECT_BACKOFF_MS`, `isTransientLiveError`) is the wrong shape here —
  retrying through a NotSignedIn is pointless, it just delays the §3 error.
- **No `lacerte_session_disconnect` analog** with the same semantics. QB's
  `qb_session_disconnect` releases the QBXMLRP2 connection so a different
  process can take it. Lacerte's COM/ODBC bridge is multi-process friendly;
  disconnect is more about clearing the in-memory `ITPRegistration` ref. We
  ship `lacerte_session_logout` instead and label it as test-only.
- **GUI dialog ownership.** QB's Application Certificate dialog is owned by the
  QB Desktop process and pops over whatever the operator is doing. The Lacerte
  consent + sign-in dialogs are SDK-scoped and pop from the calling process —
  which is why we must call `Register()` from the CLI in an interactive shell,
  not from the MCP child of Claude Desktop.
- **Connection identity.** QB's identity is just `appName` (no GUID). Lacerte
  requires the permanent GUID. Operators who reinstall Lacerte or the MCP need
  the GUID rescued from keychain or backed up to a file — the CLI should print
  the GUID on every `register --reset` for safekeeping.

---

## Sources

- `Lacerte_MCP_Structure_Spec.md` §1, §3 (layer B), §5, §6 — registration
  prerequisites, status enum, 24-hour rule, AppID note.
- Intuit Accountants Community: *Authorization to Lacerte SDK expires every 24
  hours?* — confirms the 24-hour expiry from a third-party developer asking
  the same question.
- Intuit Accountants Community: *When use Lacerte SDK Library to Register, I
  encounter to LacerteTaxSetupRequired error* — confirms `LacerteTaxSetupRequired`
  fires when the local Lacerte install has not completed its own setup; operator
  must launch Lacerte once and complete onboarding before any MCP `Register()`
  attempt succeeds. Worth adding as a pre-flight check in the `register` CLI.
- Intuit support: *Signing in to My Account for Lacerte* — restates the 24-hour
  re-sign-in window in operator-facing copy, useful verbatim in the remediation
  message.
- QuickBooks MCP Desktop reference:
  - `C:/Users/VR/Projects/Quickbooks MCP Desktop/src/session/manager.ts`
    (singleton pattern, two-mode resolver, accessor surface, synthetic status
    codes, `lastSwitchLaunchInfo` metadata pattern).
  - `C:/Users/VR/Projects/Quickbooks MCP Desktop/src/tools/reports.ts` lines
    3160–3345 (`qb_session_connect` / `qb_session_disconnect` /
    `qb_session_status` — the shape `lacerte_session_status` should mirror).
