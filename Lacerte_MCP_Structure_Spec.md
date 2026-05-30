# Lacerte MCP — Required Structure (Confirmed from the SDK Developer Instructions)

This document confirms the full structure an MCP server needs in order to drive the Lacerte tax software, based on the *Lacerte Software Development Kit Developer Instructions* (rev V1.0.0.497, 2025-12-18). It maps every SDK capability the doc exposes to the layers an MCP server must implement, and flags the constraints that shape the design.

---

## 1. The single most important architectural fact

The Lacerte SDK is **not a cloud or REST API**. It is a local Windows integration that talks to on-disk DBF and detail files through one of two installed components:

1. **Lacerte ODBC driver** (`LacerteDSIIDriver` / DSN `LacerteDSII`) — full **read** of tables 1–7 plus **read** of tax detail files, via SQL over ADO/ADO.Net. **Writes go through the .NET / COM library** (`WriteToDBFDatabase` pre-TY21, `UpdateTaxReturn` TY21+, `IClient.CreateClient`).[^odbc-readonly]
2. **.NET / COM library** (`LacerteSDK_Access`, `LacerteSDK_Interfaces`, `LacerteSDK_SharedInterfaces`) — class/interface access returning XML; the **only** path that can create new clients.

[^odbc-readonly]: ODBC was originally documented as read/write but in practice INSERT/UPDATE through the 32-bit driver crashes the process; Intuit has confirmed writes are unsupported on the ODBC path. See `DECISIONS.md` 2026-05-30 "ODBC is reads-only" for the full evidence trail.

Consequence for the MCP: **the MCP server must run on the same Windows machine where Lacerte and the SDK are installed.** It cannot be a remote/hosted server. This mirrors the existing QuickBooks Desktop MCP in this environment, which bridges to a local Windows SDK — the Lacerte MCP should follow the same "local bridge" pattern.

Hard environment requirements from the doc:
- Windows 7 64-bit or later; 64-bit OS specifically required for authentication.
- Lacerte TY18+ installed, and a successful login through the Lacerte app must have already occurred.
- SDK installed (32-bit → `C:\Program Files (x86)\Common Files\Lacerte Shared\LacerteSDK`; 64-bit → `C:\Program Files\Common Files\Lacerte Shared\LacerteSDK`).
- The host process needs an `<exe>.config` with `useLegacyV2RuntimeActivationPolicy="true"` and `supportedRuntime v4.0 / .NETFramework v4.6`.

---

## 2. Data model the MCP must represent

### Modules (the `?` / module character in `DATA1?YY.DBF`)

| Module | Char | ReturnType (connection string) |
|---|---|---|
| Individual | I | Individual |
| Corporate | C | Corporate |
| Partnership | P | Partnership |
| S-Corporation | S | SCorporate |
| Fiduciary | F | Fiduciary |
| Exempt | R | Exempt |
| Estate | T | Estate |
| Gift | N | Gift |
| Benefit | B | Benefit |

DBF filename pattern: `DATA{n}{moduleChar}{YY}.DBF` (e.g. `DATA1I18.DBF` = table 1, Individual, TY2018). `YY` = two-digit tax year.

### The 8 DBF tables (per module, per year)

| Table | DBF | Contents | Access in MCP |
|---|---|---|---|
| 1 | DATA1 | Client Info (contact/demographic) | Read all; write per field map |
| 2 | DATA2 | Subclient Info (dependents, partners, shareholders…) | Read/write; needs SubClientID |
| 3 | DATA3 | State Client Info | Read/write; needs StateAbbrev |
| 4 | DATA4 | State Subclient Info | Read/write; needs SubClientID + StateAbbrev |
| 5 | DATA5 | Status Info (F4 status) | Read only (non-global); some globals R/W |
| 6 | DATA6 | Display Info (client-list display) | Read only |
| 7 | DATA7 | Form Info (forms in return) | Read only |
| 8 | DATA8 | RESERVED | n/a |

### Detail files (per client tax input)

Each client has its own detail file (e.g. `JOHNS.ID8`), treated as its own table. Columns: `Series, Prefix, Code, Suffix, StateAbbr, CityAbbr, Source, TSJ, ImportSrc, Amount, Description`. Series ≈ Lacerte input screen, Code ≈ input field. **No validation on writes** — the MCP must validate before writing or it can corrupt returns.

### EF status tables (TY19+)

`EFilings` (Id, ClientId, EFId, InstanceId) and `EFStatuses` (EFilingId, EFStatus, EFStatusDate). `EFilings.ClientId` ↔ `DATA1` ClientNum; `EFStatuses.EFilingId` is a FK to `EFilings.Id`. ~16 status values (PendingEFE, Succeeded, Rejected, Transmitted, Complete, etc.).

### TY21+ schema shift (critical branching logic)

Tables 2/3/4 changed query form. The MCP must call `CanUseNewApis(year, moduleChar)` and branch:
- Old: `select * from [DATA2-Subclient Info] where C2_0 = 'TEST'`
- New: `select * from [TEST-SubClientInfo]` (client number becomes the table prefix)
- Writes for TY21+ use `UpdateTaxReturn(...)` keyed by **ClientNumber** instead of `WriteToDBFDatabase(...)` keyed by **row number**.

### Field naming & access model

Fields are `C{table}_{n}` (non-global) and `C{table}_G{n}` (global). `C{table}_0` is always the Client Number (ReadOnly). Access per field is **ReadOnly / ReadWrite / Hidden** (see doc's "Database Field Access" and "Not Supported Fields in TY21" tables). The MCP must ship this access map as metadata and reject writes to ReadOnly/Hidden/NotExposed fields up front.

---

## 3. Required MCP server layers

```
┌─────────────────────────────────────────────────────────────┐
│ MCP Server (stdio) — runs locally on the Lacerte machine      │
├─────────────────────────────────────────────────────────────┤
│ A. Bridge/runtime layer                                       │
│    .NET host wrapping LacerteSDK COM/.NET libs (+ ODBC for     │
│    detail files & bulk SQL). Needs the .config runtime shim.   │
├─────────────────────────────────────────────────────────────┤
│ B. Session & auth layer                                       │
│    Register(AppName, AppID GUID, TaxYear) → consent dialog;    │
│    Intuit login UI; 24-hour re-login; session status cache.    │
├─────────────────────────────────────────────────────────────┤
│ C. Connection/context layer                                    │
│    Datapath + TaxYear + ReturnType/module; CanUseNewApis()     │
│    detection; multi-year/multi-module routing.                 │
├─────────────────────────────────────────────────────────────┤
│ D. Tool surface (MCP tools — see §4)                          │
├─────────────────────────────────────────────────────────────┤
│ E. Schema/metadata layer                                       │
│    Module chars, table map, field-access map, return-code map. │
├─────────────────────────────────────────────────────────────┤
│ F. Error mapping layer                                         │
│    TAPIStatus/TPSDKStatus enum → structured MCP errors.        │
└─────────────────────────────────────────────────────────────┘
```

### Layer B — auth is the riskiest part

The SDK requires interactive consent and login:
- `Register()` shows a one-time-per-user/per-system consent dialog and an Intuit sign-in window.
- Login re-prompts **every 24 hours**.
- These are **GUI dialogs**, which an MCP server normally runs headless. The MCP must either run in a session that can surface these dialogs, or pre-establish the session out of band. This needs a decision (see §6).

The `Register` call needs a unique **AppID GUID** that identifies this MCP as the registered third party (the doc's sample uses `9C3EF09C-DA54-4531-9A82-8F1EEA922AE0`). You will mint and hard-code your own.

---

## 4. Proposed MCP tool surface

Mapped directly from the SDK interfaces (`ILacerteAccess`, `ITPRegistration`, `IDBFReader`, `IDBFWriter`, `IClient`, `IClientList`).

**Session / context**
- `lacerte_register` — Register(appName, appId, taxYear) → consent + login. Returns TPSDKStatus.
- `lacerte_session_status` — is registration/login still valid (24h window)?
- `lacerte_set_context` — set Datapath, TaxYear, ReturnType/module.
- `lacerte_can_use_new_apis` — CanUseNewApis(year, moduleChar) → drives query/write branching.
- `lacerte_list_modules` / `lacerte_list_datapaths` — discover available modules/years on disk.

**Read**
- `lacerte_client_list` — read DATA1 (all clients) → rows.
- `lacerte_client_read` — one client by ClientNumber.
- `lacerte_table_read` — generic read of tables 1–7 (with TY21+ query branching for 2/3/4).
- `lacerte_subclient_read` — DATA2 (dependents/partners/shareholders).
- `lacerte_state_read` — DATA3/DATA4.
- `lacerte_status_read` — DATA5 (F4 status).
- `lacerte_forms_read` — DATA7.
- `lacerte_detail_read` — read a client's detail file (series/code/amount…).
- `lacerte_ef_status` — EFilings + EFStatuses (TY19+).

**Write**
- `lacerte_field_write` — pre-TY21: WriteToDBFDatabase(appId, dataPath, dbf, rowNum, field, value).
- `lacerte_update_return` — TY21+: UpdateTaxReturn(appId, dataPath, dbf, clientNumber, field, value, subClientID, stateAbbrev).
- `lacerte_detail_write` — write a detail-file field (validate first; SDK does not).

**Create**
- `lacerte_client_create` — IClientList.SetDataPath → IClient (TaxType, ClientID ≤8 chars, EmailID, FirstName, LastName) → CreateClient.

**Metadata (read-only helpers)**
- `lacerte_field_access_map` — return ReadOnly/ReadWrite/Hidden per field for a module/table.
- `lacerte_return_codes` — human-readable map of TAPIStatus values.

---

## 5. Return-code → error mapping (must be built in)

The SDK returns a `TAPIStatus` / `TPSDKStatus` enum on every call. The MCP must translate these into structured errors rather than passing integers through:

`0 Error`, `1 Success`, `2 DBFNotAccessable`, `3 CodeBaseError`, `4 OutOfIndex`, `5 DBNotSupported`, `6 NotImplemented`, `7 NotExposed`, `8 ExposedForRead`, `9 ExposedForWrite`, `ExposedForReadWrite`, `VersionBlocked`, `DuplicateClient`, `LengthExceeded`, `TaxModuleMismatch`, `DetailCreationError`, `DB1Error`–`DB8Error`, `InValidClientName`, `InValidTaxYear`, `FieldNotSupported`, `InValidSubClientID`, `InValidStateAbbrev`.

Registration-specific: `ConsentDenied`, `AccessGranted`, `VersionBlocked`, `NotSignedIn`, `LacerteTaxSetupRequired`.

Key validation rules the MCP should enforce client-side: ClientID ≤ 8 chars and no special characters; TaxYear ≥ 2018; SubClientID > 0 only for DATA2/DATA4 writes; StateAbbrev required for DATA3/DATA4 writes; never write ReadOnly/Hidden/NotExposed fields.

---

## 6. Open decisions / blockers before build

1. **Bridge language/runtime.** The create-client and registration paths are COM/.NET only, so the bridge almost certainly needs a .NET host (C#) exposing an MCP stdio interface — with the ODBC driver used for detail files and bulk SQL reads. Confirm the MCP server itself is .NET, or a thin non-.NET MCP shelling out to a .NET helper.
2. **Headless auth.** The consent dialog and 24-hour Intuit login are interactive GUI prompts. Decide how the MCP surfaces or pre-satisfies these (e.g. a one-time interactive setup step, then session reuse within 24h).
3. **AppID GUID.** Generate and register a permanent GUID for this integration.
4. **Scope of v1.** Suggest: read-only across all tables + EF status + detail read first (lowest risk, no data corruption), then field writes, then client create.
5. **Year/module coverage.** TY21 had a documented schema change and a list of unsupported fields; multi-tax-year in one session arrived only in V1.0.0.495 (2025-12). Confirm target tax years.
6. **Field-access data.** The doc gives the access map for Individual/Partnership table 1 & 5 globals and a partial field list. A complete per-module field catalog (from GL Bridge docs or Lacerte blank input sheets, referenced but not included in this PDF) is needed for full write coverage.

---

## 7. Bottom line

The document fully specifies a **local Windows bridge MCP**: a .NET/COM + ODBC host, gated by an interactive register/login session, exposing read/write/create tools over a DBF table model (1–7 + detail files + EF status), with TY21+ query branching, a per-field access map, and a ~30-value status enum to map to errors. Nothing in the doc requires anything the QuickBooks Desktop MCP pattern can't accommodate — the main non-code unknowns are the interactive-auth handling and obtaining the complete field catalog.
