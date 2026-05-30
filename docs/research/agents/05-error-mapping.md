# Agent 05 — Error Mapping Layer (`src/errors/status-codes.ts`)

Translates Lacerte SDK `TAPIStatus` / `TPSDKStatus` enum returns into structured
MCP errors, with synthetic 9xxx codes for MCP-injected validation/preflight
failures. Modeled on `C:/Users/VR/Projects/Quickbooks MCP Desktop/src/util/qb-status-codes.ts`
and `format-tool-error.ts` (proven over 90+ landed tasks).

---

## 1. Numeric value table

The spec (§5) gives integer values only for `0..9`. Everything from `ExposedForReadWrite`
onward is name-only. Below is the reconstructed mapping. Integer assignments past
`9` are inferred from the SDK's declaration order in the spec text — they MUST be
verified against the actual SDK headers (`LacerteSDK_Interfaces.dll` /
`LacerteSDK_SharedInterfaces.dll`, decompile or check the `.tlb`) before code
ships. Anything uncertain is marked `?`.

`TAPIStatus` (general SDK calls) and `TPSDKStatus` (registration) appear to be
two **separate enums** in the doc but share `VersionBlocked` by name — they may
collide on integer value. The TS layer treats them as one flat namespace and
prefixes the registration-only codes (200+) to avoid the clash. Verify whether
SDK actually returns distinct integers or whether they alias.

| Code   | Name                     | Source       | Notes                                                                                  |
|--------|--------------------------|--------------|----------------------------------------------------------------------------------------|
| 0      | Error                    | spec literal | Generic failure — SDK returned 0 with no further info.                                 |
| 1      | Success                  | spec literal | The only non-error status. AccessGranted (registration) is also success.               |
| 2      | DBFNotAccessable         | spec literal | DBF file missing / locked / on a path the host process can't see. (sic: "Accessable") |
| 3      | CodeBaseError            | spec literal | Underlying CodeBase DBF engine failure — usually corrupt index.                       |
| 4      | OutOfIndex               | spec literal | Row number out of range (pre-TY21 WriteToDBFDatabase path).                            |
| 5      | DBNotSupported           | spec literal | Requested DBF table not supported for this module/year.                                |
| 6      | NotImplemented           | spec literal | SDK method exists but the requested op isn't implemented for this combo.               |
| 7      | NotExposed               | spec literal | Field exists in DBF but SDK refuses to expose it.                                      |
| 8      | ExposedForRead           | spec literal | Field is readable only — write attempted.                                              |
| 9      | ExposedForWrite          | spec literal | Field is writable only — read attempted (rare; possible for some globals).             |
| 10 ?   | ExposedForReadWrite      | inferred     | Returned as informational on access-map queries; NOT an error.                          |
| 11 ?   | VersionBlocked           | inferred     | SDK build older than required for this op (TAPIStatus variant).                        |
| 12 ?   | DuplicateClient          | inferred     | CreateClient with an existing ClientID.                                                |
| 13 ?   | LengthExceeded           | inferred     | ClientID > 8 chars, or a string field exceeded its declared length.                    |
| 14 ?   | TaxModuleMismatch        | inferred     | ReturnType in connection string doesn't match the module char on the DBF.              |
| 15 ?   | DetailCreationError      | inferred     | Failed to create a `.IDx` detail file for a new client.                                |
| 16 ?   | DB1Error                 | inferred     | Table-specific DBF error — DATA1.                                                      |
| 17 ?   | DB2Error                 | inferred     | DATA2.                                                                                 |
| 18 ?   | DB3Error                 | inferred     | DATA3.                                                                                 |
| 19 ?   | DB4Error                 | inferred     | DATA4.                                                                                 |
| 20 ?   | DB5Error                 | inferred     | DATA5.                                                                                 |
| 21 ?   | DB6Error                 | inferred     | DATA6.                                                                                 |
| 22 ?   | DB7Error                 | inferred     | DATA7.                                                                                 |
| 23 ?   | DB8Error                 | inferred     | DATA8 (reserved — should never fire; if it does, treat as SDK bug).                    |
| 24 ?   | InValidClientName        | inferred     | Bad chars or wrong shape in client name fields. (sic: "InValid")                       |
| 25 ?   | InValidTaxYear           | inferred     | Year < 2018 or not installed on this machine.                                          |
| 26 ?   | FieldNotSupported        | inferred     | Field name passed doesn't exist in this module/year's schema.                          |
| 27 ?   | InValidSubClientID       | inferred     | SubClientID ≤ 0 or out of range for DATA2/DATA4 write.                                 |
| 28 ?   | InValidStateAbbrev       | inferred     | StateAbbrev empty/unknown for DATA3/DATA4 write.                                       |
| 200 ?  | ConsentDenied            | TPSDKStatus  | User declined the consent dialog during `Register()`.                                  |
| 201 ?  | AccessGranted            | TPSDKStatus  | Registration succeeded — treat as success.                                             |
| 202 ?  | VersionBlocked           | TPSDKStatus  | Registration variant — may collide with TAPIStatus #11; verify.                        |
| 203 ?  | NotSignedIn              | TPSDKStatus  | No Intuit login active, or 24h window expired.                                          |
| 204 ?  | LacerteTaxSetupRequired  | TPSDKStatus  | Lacerte itself not installed/initialized on this box.                                  |

`?` rows MUST be verified against the SDK headers before release. Suggested
verification: `tlbimp LacerteSDK_Interfaces.tlb` in a Developer PowerShell,
inspect the generated managed assembly with ILSpy, copy the literal enum
values into this table, then drop the `?` markers.

---

## 2. TypeScript shape

```ts
// src/errors/status-codes.ts
export const LacerteStatus = {
  Error: 0,
  Success: 1,
  DBFNotAccessable: 2,
  CodeBaseError: 3,
  OutOfIndex: 4,
  DBNotSupported: 5,
  NotImplemented: 6,
  NotExposed: 7,
  ExposedForRead: 8,
  ExposedForWrite: 9,
  ExposedForReadWrite: 10,        // ? verify
  VersionBlocked: 11,             // ? verify
  DuplicateClient: 12,            // ?
  LengthExceeded: 13,             // ?
  TaxModuleMismatch: 14,          // ?
  DetailCreationError: 15,        // ?
  DB1Error: 16, DB2Error: 17, DB3Error: 18, DB4Error: 19,
  DB5Error: 20, DB6Error: 21, DB7Error: 22, DB8Error: 23,
  InValidClientName: 24,          // ?
  InValidTaxYear: 25,             // ?
  FieldNotSupported: 26,          // ?
  InValidSubClientID: 27,         // ?
  InValidStateAbbrev: 28,         // ?
  // Registration (TPSDKStatus) — namespaced 200+ to avoid collision with TAPIStatus
  ConsentDenied: 200,             // ?
  AccessGranted: 201,             // ?
  RegVersionBlocked: 202,         // ?
  NotSignedIn: 203,               // ?
  LacerteTaxSetupRequired: 204,   // ?
} as const;

export type LacerteStatusCode = (typeof LacerteStatus)[keyof typeof LacerteStatus];
export type Severity = "success" | "warning" | "error";

export interface StatusInfo {
  name: string;
  severity: Severity;
  userMessage: string;
  remediation?: string;
}

export function getStatusInfo(code: number): StatusInfo {
  return STATUS_TABLE[code] ?? {
    name: `Unknown(${code})`,
    severity: "error",
    userMessage: `Lacerte SDK returned an unrecognized status code (${code}).`,
    remediation:
      "Capture the SDK call + arguments and file an issue; verify against the SDK enum headers.",
  };
}

export class LacerteSDKError extends Error {
  readonly statusCode: number;
  readonly statusName: string;
  readonly severity: Severity;
  readonly userMessage: string;
  readonly remediation?: string;
  readonly context: Record<string, unknown>;

  constructor(code: number, context: Record<string, unknown> = {}) {
    const info = getStatusInfo(code);
    super(`[${info.name}] ${info.userMessage}`);
    this.name = "LacerteSDKError";
    this.statusCode = code;
    this.statusName = info.name;
    this.severity = info.severity;
    this.userMessage = info.userMessage;
    this.remediation = info.remediation;
    this.context = context;
  }
}
```

---

## 3. Severity classification + 4. userMessage + 5. remediation

Justifications:

* **success** — `Success` (1) and `AccessGranted` (201) are the two non-error
  terminals.
* **warning** — `ExposedForRead`, `ExposedForWrite`, `ExposedForReadWrite`,
  `NotExposed` are returned by access-map queries (the caller asked "is this
  field writable?"). When they show up on a *data* call they mean the caller
  attempted the wrong operation and are escalated to **error** by the caller
  context; the table itself classifies them as warning so the access-map tool
  doesn't surface `isError: true` on a perfectly normal answer.
* **error** — everything else.

| Code | Name                     | Severity | userMessage                                                                                       | remediation                                                                                                  |
|------|--------------------------|----------|---------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------|
| 0    | Error                    | error    | Lacerte returned a generic error with no further detail.                                          | Re-run with `LACERTE_DEBUG=1` to capture the bridge call args; check Windows Event Viewer for SDK traces.    |
| 1    | Success                  | success  | OK.                                                                                                | —                                                                                                            |
| 2    | DBFNotAccessable         | error    | The Lacerte data file is missing, locked, or on a path this MCP can't reach.                       | Confirm `LACERTE_DATAPATH` exists and the user account has read/write on it; close the file in Lacerte if open exclusively. |
| 3    | CodeBaseError            | error    | The Lacerte DBF index is corrupt or unreadable.                                                    | Open Lacerte and run File → Repair Indexes for the affected module/year, then retry.                         |
| 4    | OutOfIndex               | error    | The row number passed to a pre-TY21 write doesn't exist in this table.                            | Re-query the table to refresh row numbers, or switch to the TY21+ `UpdateTaxReturn` path (keyed by ClientNumber). |
| 5    | DBNotSupported           | error    | This DBF table isn't supported for the requested module/year combo.                                | Check `lacerte_list_modules` for which tables/modules are actually installed.                                |
| 6    | NotImplemented           | error    | The SDK doesn't implement this operation yet.                                                      | No workaround — file with Intuit; the MCP cannot fix this.                                                   |
| 7    | NotExposed               | error    | This field exists in the DBF but the SDK refuses to expose it.                                     | Don't reference this field — remove it from the request payload.                                             |
| 8    | ExposedForRead           | warning  | This field is read-only.                                                                           | Use `lacerte_table_read` instead of a write tool for this field.                                             |
| 9    | ExposedForWrite          | warning  | This field is write-only.                                                                          | Don't try to read this field; it's input-only.                                                               |
| 10   | ExposedForReadWrite      | warning  | This field supports both read and write.                                                           | —                                                                                                            |
| 11   | VersionBlocked           | error    | Your installed Lacerte/SDK build is too old for this operation.                                    | Upgrade Lacerte, or set `LACERTE_DEFAULT_TAX_YEAR` to a year your build supports.                            |
| 12   | DuplicateClient          | error    | A client with that ClientID already exists for this tax year/module.                               | Pick a different ClientID, or call `lacerte_client_read` to load the existing record.                        |
| 13   | LengthExceeded           | error    | A field value is longer than Lacerte allows (ClientID > 8 chars is the most common cause).         | Trim the value; ClientIDs must be ≤ 8 alphanumeric characters with no special chars.                         |
| 14   | TaxModuleMismatch        | error    | The connection's ReturnType doesn't match the module character of the DBF being addressed.        | Call `lacerte_set_context` again with the correct ReturnType for the module char in the DBF filename.        |
| 15   | DetailCreationError      | error    | Failed to create the per-client detail file (`.IDx`) for a new client.                            | Verify disk space and write permission on the datapath; retry `lacerte_client_create`.                       |
| 16-23| DB1Error..DB8Error       | error    | A table-specific DBF error occurred (DATA{n}).                                                     | Check the table file on disk; run Lacerte's index-repair on the affected module/year.                        |
| 24   | InValidClientName        | error    | The client name contains characters Lacerte rejects.                                               | Use letters, numbers, spaces, hyphen/apostrophe only; avoid `< > " ' & / \\ | ?`.                            |
| 25   | InValidTaxYear           | error    | The requested tax year is < 2018 or not installed on this machine.                                 | Set `LACERTE_DEFAULT_TAX_YEAR` ≥ 2018, or install the Lacerte module for that year.                          |
| 26   | FieldNotSupported        | error    | The field name doesn't exist in this module/year's schema.                                         | Cross-check `lacerte_field_access_map` for the valid field list at this year/module.                         |
| 27   | InValidSubClientID       | error    | SubClientID must be > 0 for DATA2/DATA4 writes.                                                    | Pass a positive integer SubClientID identifying the dependent/partner/shareholder row.                       |
| 28   | InValidStateAbbrev       | error    | StateAbbrev is required and must be a recognized 2-letter code for DATA3/DATA4 writes.            | Pass the 2-letter postal code (e.g. `CA`, `NY`); see `lacerte_list_modules` for supported states.            |
| 200  | ConsentDenied            | error    | You declined consent in the Lacerte registration dialog.                                           | Re-run `lacerte_register` and click Accept on the consent dialog.                                            |
| 201  | AccessGranted            | success  | Lacerte registration succeeded.                                                                    | —                                                                                                            |
| 202  | RegVersionBlocked        | error    | Registration is blocked because Lacerte/SDK is below the minimum version.                          | Upgrade Lacerte to the minimum supported build, then re-run `lacerte_register`.                              |
| 203  | NotSignedIn              | error    | No active Intuit login, or the 24-hour login window has expired.                                   | Run `lacerte_register` (or open Lacerte and sign in) to refresh the login token.                             |
| 204  | LacerteTaxSetupRequired  | error    | Lacerte itself isn't installed or fully initialized on this machine.                              | Install Lacerte TY18+ and complete its first-run setup before retrying MCP registration.                     |

---

## 6. Synthetic 9xxx codes (MCP-injected)

These are emitted **before** any SDK call lands — pre-flight validation and
client-side gates. Mirrors the QB MCP's 9001-9008 pattern.

| Code | Name                  | Emitted When                                                                                       | Remediation                                                                                                    |
|------|-----------------------|----------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------|
| 9001 | FieldIsReadOnly       | Caller tried to write a field marked ReadOnly/Hidden/NotExposed in the access map.                | Remove the field from the write payload, or use the corresponding read tool.                                  |
| 9002 | ClientIDTooLong       | `ClientID` > 8 chars or contains a disallowed char before the SDK ever sees it.                   | Trim to ≤ 8 alphanumeric chars; no special chars.                                                              |
| 9003 | TaxYearTooOld         | TaxYear < 2018 (SDK floor) caught client-side.                                                     | Pass a TaxYear ≥ 2018.                                                                                         |
| 9004 | StateAbbrevRequired   | DATA3 or DATA4 write attempted without `stateAbbrev`.                                              | Pass a 2-letter `stateAbbrev` (e.g. `CA`).                                                                     |
| 9005 | SubClientIDRequired   | DATA2 or DATA4 write attempted without `subClientID`.                                              | Pass a positive integer `subClientID`.                                                                         |
| 9006 | AppNotRegistered      | Any tool other than `lacerte_register` called before a successful Register() landed.              | Run `lacerte_register` once to grant consent and sign in.                                                      |
| 9007 | LoginExpired          | Cached session manager detects the 24h Intuit login window has lapsed.                            | Run `lacerte_register` to re-authenticate (consent is one-time; only the sign-in needs refresh).               |
| 9008 | DetailWriteInvalid    | `lacerte_detail_write` failed client-side validation (Series/Code/Amount shape) — SDK doesn't validate, so the MCP must. | Fix the failed field per the validation message; the SDK would silently corrupt the return otherwise.          |
| 9009 | ContextNotSet         | A read/write tool called before `lacerte_set_context` provided Datapath + TaxYear + ReturnType.   | Call `lacerte_set_context` first.                                                                              |
| 9010 | BridgeUnavailable     | Live mode requested but the .NET/COM bridge process didn't start or crashed.                       | Confirm running on Windows with the SDK installed; check `LACERTE_BRIDGE_LOG` for the bridge stderr.           |
| 9011 | SimulationOnly        | Live-only op (e.g. `lacerte_client_create`) called while running in simulation mode.               | Set `LACERTE_LIVE=1` on a Windows + Lacerte machine, or stub the op in the simulation store.                   |

---

## 7. Tool wrapper helper

```ts
// src/errors/wrap.ts
import { LacerteSDKError, getStatusInfo } from "./status-codes.js";

type ToolHandler<I, O> = (input: I) => Promise<O>;
type McpToolResponse = {
  content: { type: "text"; text: string }[];
  isError?: boolean;
  [k: string]: unknown;
};

export function wrapToolHandler<I>(
  handler: ToolHandler<I, McpToolResponse | Record<string, unknown>>,
): ToolHandler<I, McpToolResponse> {
  return async (input: I) => {
    try {
      const result = await handler(input);
      // Pass through already-formed MCP responses.
      if (result && typeof result === "object" && "content" in result) {
        return result as McpToolResponse;
      }
      return {
        content: [{ type: "text", text: JSON.stringify({ success: true, ...result }) }],
      };
    } catch (err) {
      if (err instanceof LacerteSDKError) {
        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              success: false,
              statusCode: err.statusCode,
              statusName: err.statusName,
              severity: err.severity,
              userMessage: err.userMessage,
              remediation: err.remediation,
              context: err.context,
            }),
          }],
          isError: true,
        };
      }
      // Unknown throw — surface as generic Error(0) so the shape is consistent.
      const info = getStatusInfo(0);
      return {
        content: [{
          type: "text",
          text: JSON.stringify({
            success: false,
            statusCode: 0,
            statusName: info.name,
            severity: "error" as const,
            userMessage: info.userMessage,
            remediation: info.remediation,
            rawError: err instanceof Error ? err.message : String(err),
          }),
        }],
        isError: true,
      };
    }
  };
}
```

Tool callsite pattern (matching the sibling QB MCP):

```ts
server.tool("lacerte_client_read", "...", schema, wrapToolHandler(async (input) => {
  const session = getSession();
  const row = await session.readClient(input.clientNumber);  // throws LacerteSDKError
  return { client: row };
}));
```

---

## Open follow-ups for the next session

1. **Verify integer values** for codes 10-28 and 200-204 against the actual SDK
   `.tlb` / decompiled headers; drop the `?` markers.
2. **Decide on collision strategy** for `VersionBlocked` (TAPIStatus 11) vs
   `RegVersionBlocked` (TPSDKStatus 202) — if the SDK actually emits the same
   integer from both paths, collapse to one code and disambiguate via call
   context.
3. **Add a `lacerte_return_codes` tool** that returns `STATUS_TABLE` as a
   discoverable resource so agents can ask "what does code 14 mean?" without a
   round trip to the docs.
4. **Hook the access map** (Agent 04 output) into the 9001 emission path so the
   write tools can name the specific field that was ReadOnly.
