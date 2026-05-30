# Requirements

Product truth: what the Lacerte MCP must do for the operator. Implementation work lives in `Todo.md`; this file says **why** that work exists.

---

## Operator profile

A tax professional running Lacerte locally on Windows who wants to drive Lacerte from an LLM-powered assistant (Claude Desktop, Cursor, etc.) for:

1. Reading client information without opening Lacerte and clicking through screens.
2. Bulk-updating fields across many clients (mailing addresses, EF flags, preparer assignments).
3. Bootstrapping new clients from a CSV or an upstream system (Tax Organizer responses).
4. Watching e-file status across the firm.
5. Pulling tax-detail context into review chats without manual lookup.

The bar is "works flawlessly on the operator's box," not "ships to thousands of firms."

## Capabilities the MCP must expose (product-level, not implementation-level)

### Read

- Enumerate clients (DATA1) for any installed module + tax year (TY18+).
- Read a single client's contact / demographic fields.
- Read dependents/partners/shareholders (DATA2).
- Read state info (DATA3) and state subclient info (DATA4).
- Read F4 return status (DATA5) and form list (DATA7).
- Read a client's tax-detail file (per-field amounts/descriptions).
- Read e-file status history (TY19+).

### Write

- Update a single field on a single client safely (never corrupt a return).
- Reject writes to fields that are ReadOnly / Hidden / NotExposed in the SDK access map.
- Reject writes that violate SDK constraints (TaxYear < 2018, bad SubClientID, missing StateAbbrev).
- Branch to the TY21+ `UpdateTaxReturn` write path automatically when the active context is TY21+.

### Create

- Create a new client (Individual, Corporate, etc.) with the minimum required fields (TaxType, ClientID ≤ 8 chars, EmailID, FirstName/LastName).

### Session

- One-time interactive registration + login through Lacerte's Intuit dialog.
- Detect when the 24-hour login window has expired and surface a clear re-auth instruction.
- Surface session state (registered? logged in? expires when?) as a tool.

### Discovery / metadata

- List available modules + tax years for the current data path.
- Return the per-field access map (ReadOnly / ReadWrite / Hidden) for a given module/table.
- Return a human-readable map of TAPIStatus error codes.

## Non-functional requirements

- **Safety first.** Read-only mode (`readOnly: true` session flag) must hard-block every write call site. Validation must run client-side before any SDK call — the SDK does not validate detail-file writes.
- **Two-mode parity.** Simulation mode (off-Windows or `LACERTE_SIMULATION=1`) must return responses that are observationally identical to live mode for the happy path of every read tool.
- **Auditability.** All write calls must be loggable via a debug flag (`LACERTE_DEBUG_WIRE=1` mirrors QB MCP's `QB_DEBUG_QBXML=1`).
- **No silent failures.** Every SDK return code must be mapped to a structured MCP error — never pass an enum integer back to the client.

## Explicitly out of scope (for v1)

- Hosted / remote operation — Lacerte SDK is local-only and the spec confirms this.
- Multi-tax-year-in-one-session support across modules where the SDK lacks it (Lacerte v1.0.0.495+ only).
- Driving Lacerte UI screens — only data-layer access.
- Form imaging / PDF export.

These can be revisited once v1 is solid.
