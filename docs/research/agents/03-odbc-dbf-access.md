# 03 — ODBC / DBF Access Plan for Lacerte MCP

Concrete plan for the ODBC layer of the Lacerte MCP server. Source spec:
`Lacerte_MCP_Structure_Spec.md` §1 (ODBC vs COM split), §2 (DBF tables,
detail files, field naming, TY21+ schema shift). Web research notes inline;
sources listed at the bottom.

---

## 1. Driver bitness — there are two MSIs, both should be installed

Intuit ships two installers from the developer portal: an x86 MSI and an x64
MSI. The 32-bit driver lands under `C:\Program Files (x86)\Common Files\
Lacerte Shared\LacerteSDK\`; the 64-bit driver under `C:\Program Files\
Common Files\Lacerte Shared\LacerteSDK\`. ODBC drivers are bitness-bound to
the host process — a 32-bit driver can only be loaded by a 32-bit process and
vice versa. The two DSNs are also kept in separate registry hives and managed
by separate ODBC Data Source Administrator binaries:

- 32-bit: `C:\Windows\SysWOW64\odbcad32.exe`
- 64-bit: `C:\Windows\System32\odbcad32.exe`

Community evidence indicates the **64-bit driver is the modern path** and what
most SDK consumers run against, but several third-party Lacerte integrators
(TaxCaddy, SurePrep) instruct users to install **both** so that 32-bit COM
hosts and 64-bit query tools can each find a driver. There is also a known
bad behavior — calling `INSERT` through the 32-bit `LacerteDSIIDriver` kills
the host process. That alone is reason to prefer 64-bit for our MCP.

**Recommendation for the MCP**

1. Require the 64-bit Lacerte SDK MSI on the host machine.
2. Run the MCP host process (Node or .NET) as **64-bit** so it binds the
   64-bit `LacerteDSIIDriver`.
3. Document in `REQUIREMENTS.md` that both MSIs should be installed for
   maximum tool compatibility, but the MCP itself only needs x64.
4. The COM half (`LacerteSDK_Access`) is a separate decision (§6, spec).
   If we end up forced to 32-bit for COM, the bridge process and the ODBC
   process must share the same bitness or we run them as two processes.

---

## 2. Connection string shape

The driver accepts both a DSN reference and a DSN-less connection string.
The Lacerte SDK Developer Instructions document the following keys:

| Key          | Required? | Meaning                                                              |
|--------------|-----------|----------------------------------------------------------------------|
| `Driver`     | Required when DSN-less | Always `{LacerteDSIIDriver}` (braces matter).             |
| `DSN`        | Required when not using Driver | Default is `LacerteDSII`; custom DSNs allowed.    |
| `Datapath`   | Required  | Absolute path to the year-specific data directory (e.g. `C:\Lacerte\24data`). |
| `ReturnType` | Conditionally required | One of `Individual, Corporate, Partnership, SCorporate, Fiduciary, Exempt, Estate, Gift, Benefit`. Required when the datapath contains multiple modules in the same directory (the common case for `C:\Lacerte\YYdata\`). |
| `TaxYear`    | Conditionally required | Two-digit year (`24`) when datapath contains multiple years (unusual but legal). |

Per Intuit community docs: "Return Type and Tax Year are optional and should
be used if the Datapath contains data for multiple tax modules within the
same directory." In practice we should **always send them** because the
standard install layout (`C:\Lacerte\24data\`) does mix modules.

**DSN-less example (recommended for the MCP — avoids requiring DSN config):**

```text
Driver={LacerteDSIIDriver};Datapath=C:\Lacerte\24data;ReturnType=Individual;TaxYear=24
```

**DSN-based example (when an admin has pre-configured the DSN):**

```text
DSN=LacerteDSII;Datapath=C:\Lacerte\24data;ReturnType=Individual;TaxYear=24
```

A single DSN cannot disambiguate module + year — Access users have hit the
"only one `LacerteDSII` shows up" wall — so the MCP must open one connection
per `(datapath, module, year)` triple. Pool by that key in
`src/odbc/connection-pool.ts`.

The host process also needs an `<exe>.config` (or App.config) with
`useLegacyV2RuntimeActivationPolicy="true"` and supportedRuntime v4.0 /
.NETFramework v4.6. This is a runtime-activation shim for the .NET pieces the
driver loads internally — it applies even when only ODBC is used.

---

## 3. Library choice

### Node host (recommended if we keep stack parity with the QBD MCP)

| Option        | Latest | Node 22+ | Windows binaries | Notes |
|---------------|--------|----------|------------------|-------|
| `odbc` (npm)  | 2.5.x (2026) | Yes (N-API, ABI-stable) | Requires build-from-source on Windows unless prebuilds are present for the exact Node ABI. node-gyp + Python + VS Build Tools needed on the build machine. | Maintained, Promise-first API, supports raw connection strings via `odbc.connect(connStr)`. |
| `node-odbc` (wankdanker) | Stale; no prebuilt Windows binaries | Partial | None for Windows — must compile | Effectively abandoned compared to `odbc`. |
| `msnodesqlv8` | Active | Yes | Yes (Windows-only) | SQL Server-specific, not a general ODBC driver host — **not usable** here. |

**Recommendation: `odbc` (npm).** It's the only actively maintained,
N-API-based, Promise-first Node ODBC library and it explicitly supports DSN-
less connection strings. Plan for the build dependency:

- Pin the `odbc` version in `package.json`.
- Document VS Build Tools + Python install in the MCP's setup script.
- For dev/CI off-Windows, install with `--ignore-scripts` or skip — the
  simulation mode never loads the native module (see §8).
- Wrap the import in `src/odbc/driver.ts` with a try/catch that flips to
  simulation mode if the native binding is missing.

Minimal usage:

```ts
import odbc from "odbc";
const connStr =
  "Driver={LacerteDSIIDriver};" +
  `Datapath=${dataPath};` +
  `ReturnType=${returnType};` +
  `TaxYear=${twoDigitYear}`;
const conn = await odbc.connect(connStr);
const rows = await conn.query("select * from [DATA1-Client Info]");
await conn.close();
```

### .NET host

`System.Data.Odbc.OdbcConnection` is built into the framework and is the
canonical path. Quirks worth flagging for DBF-style drivers:

- Quote bracket-named tables exactly as the driver expects:
  `[DATA1-Client Info]`, never `\"DATA1-Client Info\"`.
- `OdbcParameter` positional parameters (`?`) work, but field names with
  underscores (`C2_0`) confuse some legacy DBF drivers when used in
  identifiers — always wrap in brackets if filtering by them inline.
- Connection-string parsing collapses braces around values containing
  semicolons; `Datapath` typically has none so plain `Datapath=C:\...` is
  fine, but if a path ever contains `;` wrap with `{}`.
- The .NET ODBC provider needs the **same bitness** as the driver — build
  with `Prefer 32-bit` off and target `x64` explicitly to bind the 64-bit
  driver.
- Pool with the connection string verbatim — the ODBC provider keys its
  pool on the literal connection string, so consistent casing/ordering
  matters.

If we end up writing a .NET bridge process (likely, because COM-based
registration and TY21+ writes are .NET-only — see spec §6), folding ODBC into
the same process is the cleanest answer. The MCP can stay Node and shell out
to one .NET helper for everything.

---

## 4. SQL templates by table

Two-form branching keyed on `CanUseNewApis(year, moduleChar)`. The MCP picks
the form at query time. Templates below use placeholders: `{client}` =
client number / table-prefix in the new form, `{state}` = 2-letter state.

### Pre-TY21 form (legacy)

```sql
-- DATA1: full client list
select * from [DATA1-Client Info];

-- DATA1: one client
select * from [DATA1-Client Info] where C1_0 = '{client}';

-- DATA2: subclients (dependents/partners/shareholders) for one client
select * from [DATA2-Subclient Info] where C2_0 = '{client}';

-- DATA3: state client info
select * from [DATA3-State Client Info] where C3_0 = '{client}';

-- DATA3: state client info filtered by state
select * from [DATA3-State Client Info] where C3_0 = '{client}' and C3_G1 = '{state}';

-- DATA4: state subclient info
select * from [DATA4-State Subclient Info] where C4_0 = '{client}' and C4_G1 = '{state}';

-- DATA5: status info
select * from [DATA5-Status Info] where C5_0 = '{client}';

-- DATA6: display info
select * from [DATA6-Display Info] where C6_0 = '{client}';

-- DATA7: form info (forms present in return)
select * from [DATA7-Form Info] where C7_0 = '{client}';
```

### TY21+ form (per-client table prefix)

DATA2/3/4 became per-client tables prefixed by client number. DATA1/5/6/7
appear to keep the legacy global form (the spec only flags 2/3/4 as
changed); confirm against the SDK PDF before shipping but treat 1/5/6/7 as
legacy until proven otherwise.

```sql
-- DATA1 unchanged (global)
select * from [DATA1-Client Info];

-- DATA2 per client
select * from [{client}-SubClientInfo];

-- DATA3 per client
select * from [{client}-StateClientInfo];

-- DATA4 per client
select * from [{client}-StateSubClientInfo];

-- DATA5/6/7 still global in the spec
select * from [DATA5-Status Info] where C5_0 = '{client}';
select * from [DATA6-Display Info] where C6_0 = '{client}';
select * from [DATA7-Form Info]   where C7_0 = '{client}';
```

The MCP's `lacerte_table_read` tool must:

1. Look up `canUseNewApis` for `(year, module)` from the session cache.
2. Pick the matching template above.
3. Substitute the client/state placeholders **outside** the SQL string when
   possible (parameter binding); fall back to safe identifier interpolation
   when the placeholder is a table name (which can't be parameterized) — in
   that case validate `{client}` against `^[A-Z0-9]{1,8}$` first.

### EF status tables (TY19+)

```sql
-- Latest EF status per client (community-validated template)
select ci.C1_0, ci.C1_2, ci.C1_3, s.EFStatus, s.EFStatusDate
from   [DATA1-Client Info] as ci
left   outer join [EFilings] as ef on ci.C1_0 = ef.ClientId
left   outer join [EFStatuses] as s on ef.Id = s.EFilingId;
```

---

## 5. Detail file reads (`JOHNS.ID8` etc.)

Per-client detail files live on disk as `<CLIENT>.ID<n>` (where `<n>` is the
two-digit year for TY18-19; the format evolved later) but the driver exposes
them as **logical ODBC tables named `[<ClientNumber>-Detail]`**. This is
confirmed by community discussion ("Client-specific data accessed by
substituting the client number, e.g. `[02SAMPLE-Detail]`").

```sql
-- Read the entire detail file for client JOHNS
select Series, Prefix, Code, Suffix, StateAbbr, CityAbbr,
       Source, TSJ, ImportSrc, Amount, Description
from   [JOHNS-Detail];

-- Filter by series (11 = W-2, 12 = Interest)
select * from [JOHNS-Detail] where Series = 11;

-- Pull description labels (Code 800 holds labels per community guidance)
select * from [JOHNS-Detail] where Code = 800;
```

Field list straight from spec §2: `Series, Prefix, Code, Suffix, StateAbbr,
CityAbbr, Source, TSJ, ImportSrc, Amount, Description`. There is **no
validation on writes**, so the MCP must validate Series/Code combinations
against a curated catalog before any write.

INSERTs through ODBC are **explicitly unsupported** for detail tables and
will hang or crash the 32-bit driver per the Intuit community thread.
Detail creation must go through the COM `IDBFWriter` / `UpdateTaxReturn`
paths instead.

---

## 6. Write paths — confirming the split

The spec §1 line "full read/write of tables 1–7 plus read/write of tax
detail files, via SQL over ADO/ADO.Net" is **aspirational / partially
wrong** based on what's actually shipping:

- **Pre-TY21 writes**: Go through COM `WriteToDBFDatabase(appId, dataPath,
  dbf, rowNum, field, value)`. Some `UPDATE` statements may work via ODBC
  for a subset of fields, but the SDK's supported path is COM.
- **TY21+ writes**: Go through COM `UpdateTaxReturn(appId, dataPath, dbf,
  clientNumber, field, value, subClientID, stateAbbrev)`.
- **INSERTs (any year, any table)**: Not supported via ODBC. Confirmed by
  Intuit's own moderator: "we can't create a client using the ODBC driver."

**MCP rule:** ODBC is for reads only. All writes go through the COM bridge.
`src/odbc/` exposes no `executeUpdate` / `executeInsert` surface. If we
ever discover a working UPDATE path for a hot field, treat it as an
optimization layered on top of the COM path, not a replacement.

Update `Lacerte_MCP_Structure_Spec.md` §1 to read "full **read** of tables
1–7 plus read of tax detail files" — log this as a decision in
`DECISIONS.md`.

---

## 7. Error handling — normalize to the COM bridge shape

The COM bridge returns `{ success, statusCode, statusName, error }`. ODBC
errors come back as `OdbcError` (Node) or `OdbcException` (.NET) carrying a
`state` (SQLSTATE), a driver-specific error code, and a message string.
Mapping table:

| ODBC SQLSTATE prefix | Meaning                          | statusCode (synthetic) | statusName        |
|----------------------|----------------------------------|------------------------|-------------------|
| `08*`                | Connection failure               | `2`                    | `DBFNotAccessable`|
| `IM002`              | DSN/Driver not found             | `2`                    | `DBFNotAccessable`|
| `42S02`              | Table not found                  | `5`                    | `DBNotSupported`  |
| `42S22`              | Column not found                 | `7`                    | `NotExposed`      |
| `42000`              | Syntax error                     | `0`                    | `Error`           |
| `HY*`                | General driver error             | `3`                    | `CodeBaseError`   |
| `S1*` (legacy HY)    | General driver error             | `3`                    | `CodeBaseError`   |
| (other)              | Unknown                          | `0`                    | `Error`           |

The Node wrapper in `src/odbc/driver.ts`:

```ts
function normalizeOdbcError(e: any) {
  const sqlState = e?.odbcErrors?.[0]?.state ?? "";
  const mapped = mapSqlStateToTAPI(sqlState);
  return {
    success: false,
    statusCode: mapped.code,
    statusName: mapped.name,
    error: e?.odbcErrors?.[0]?.message ?? String(e),
    sqlState,
  };
}
```

Keep the original `sqlState` in the response so we don't lose information
on the way through — the COM bridge has no equivalent of SQLSTATE so this
field is ODBC-only and consumers can ignore it.

Use `src/errors/status-codes.ts` for the canonical TAPIStatus enum so the
mapping table lives next to the COM mappings.

---

## 8. Simulation strategy

Two viable options, recommendation below:

### Option A — JS fixture map keyed by `(module, year, table)`

```ts
const fixtures = {
  "I:24:DATA1": [{ C1_0: "JOHNS", C1_2: "John", C1_3: "Smith", ... }],
  "I:24:JOHNS-Detail": [{ Series: 11, Code: 200, Amount: 50000, ... }],
};
```

Pros: zero dependencies, fast, trivially diffable in PRs, easy to seed from
real exported rows. Cons: doesn't exercise SQL parsing; `WHERE` filters
must be re-implemented as JS predicates in the simulation store.

### Option B — sqlite3 / better-sqlite3 shim with the same logical schema

Pros: exercises real SQL, supports JOINs (useful for the EFStatus query),
catches malformed SQL early. Cons: native dep on the dev side, schema-
mirror maintenance, doesn't perfectly emulate the bracketed `[NAME-Form]`
syntax (sqlite uses different identifier quoting), and we'd need a
translation step between the Lacerte SQL dialect and sqlite's.

### Recommendation: **Option A with a thin query interpreter**

Lacerte's query surface is tiny — handful of templates from §4 above. Don't
parse arbitrary SQL; the simulation store should expose a typed API
(`getTable(module, year, table)`, `getDetail(client)`, etc.) and the
`src/odbc/driver.ts` wrapper calls **either** the real `odbc.connect`
(live mode) **or** the fixture-backed store (simulation mode), routing on
the same `mode` flag that gates COM. This keeps the rule from CLAUDE.md
("simulation and live observationally identical for the happy path") true:
both modes produce the same `{ rows: [...] }` payload at the tool boundary.

Layout:

```
src/odbc/
  driver.ts            # mode-aware wrapper, connection pooling
  connection-string.ts # build connStr from (datapath, module, year)
  errors.ts            # SQLSTATE -> TAPIStatus mapping
  templates.ts         # the §4 SQL templates, both old and new forms
src/session/
  simulation-store.ts  # already exists per CLAUDE.md; add detail/EF fixtures
```

Fixture seeding: ship a handful of representative clients (5-10 across
Individual + Corporate + Partnership), 2 tax years (one pre-TY21, one
TY21+), and one or two detail rows per client per common Series. That's
enough to exercise both query forms and the per-client detail path
without a real Lacerte install.

---

## Sources

- [Lacerte SDK Developer Instructions PDF](https://static.developer.intuit.com/resources/Lacerte_SDK_Developer_Instructions.pdf)
- [Intuit: Install Lacerte SDK (ODBC Driver) — SurePrep guide](https://www.thomsonreuters.com/en-us/help/sureprep/integration-with-lacerte/installing-lacerte-sdk-odbc-driver)
- [Intuit Community: ODBC INSERT unsupported (CLIENT-Detail)](https://accountants.intuit.com/community/lacerte-sdk-group/lacerte-tax-sdk-odbc-how-to-insert-new-records-into-lt-client/gpm-p/337659)
- [Intuit Community: SQL to JOIN EFStatus to Client Info](https://accountants.intuit.com/community/lacerte-sdk-group/sql-to-join-efstatus-to-client-info/gpm-p/187155)
- [Intuit Community: Linking tables in Access (per-module DSN gotcha)](https://accountants.intuit.com/community/lacerte-sdk-group/linking-tables-in-access/gpm-p/1476)
- [Intuit Community: Viewing Data with Lacerte SDK (Detail/Series guidance)](https://accountants.intuit.com/community/lacerte-sdk-group/viewing-data-with-lacerte-sdk/gpm-p/338557)
- [Intuit Community: ODBC Connection (performance / multi-DSN limits)](https://accountants.intuit.com/community/lacerte-sdk-group/odbc-connection/gpm-p/967)
- [npm: odbc package](https://www.npmjs.com/package/odbc)
- [GitHub: wankdanker/node-odbc (stale alt)](https://github.com/wankdanker/node-odbc)
- [ConnectionStrings.com: .NET Framework Data Provider for ODBC](https://www.connectionstrings.com/net-framework-data-provider-for-odbc/)
- [Microsoft Learn: OdbcConnection.ConnectionString](https://learn.microsoft.com/en-us/dotnet/api/system.data.odbc.odbcconnection.connectionstring)
