# Handoff State

## Last Session Summary (2026-11-11) — live path wired, HTTP mode + control page

Branch `feat/live-reads-verified-writes` (not merged). Built and tested on the Linux tower: `mcr.microsoft.com/dotnet/sdk:8.0` builds the net48 solution; tests run under Mono in a local image `lacerte-test-mono` (dotnet sdk 8 + mono-complete). **1545 passed, 11 skipped, 5 failed.** The 5 failures are Linux-only Windows-path tests (`SessionToolsTests.ValidateDataPath_*`/`ValidateLogDir_*`, `ObservabilityToolsTests.ResourceUsage_EmitsCanonicalShape`); they fail identically on the untouched baseline and pass on Windows.

### What was wrong before this session (found by running the real binary)

1. **The server never called `Register()`.** Every data tool returned 203 NotSignedIn — in simulation too — when driven by a real MCP client. Only the tests registered. Fixed: `Startup/StartupRegistration.cs`.
2. **Live reads were never wired.** Every read threw 9201 BridgeUnavailable; `_odbc` was never assigned. Fixed: `Session/LacerteSessionManager.Live.cs`.
3. **Live write failures were returned as values.** The bulk tools counted e.g. DBFNotAccessable as success. Now they throw (sim parity).
4. **Writes were never verified.** Now every field write is read back over a fresh ODBC connection; mismatch → new **9022 WriteNotVerified** (`Session/LacerteSessionManager.Verify.cs`).
5. The writable-field access map ships **empty**, so every live field write is refused (9012) until FieldAccessDumper is run on the Lacerte PC. Fail-closed is correct; the control page now says so.

### Added

- `--http` / `LACERTE_HTTP=1`: Streamable HTTP MCP at `/mcp` + control page at `/` (`Web/`). Loopback + Tailscale IP only, `tailscale whois` identity, per-device None/Read/Write (`%APPDATA%\lacerte-mcp\device-access.json`), per-connection context (`Session/SessionScope.cs`), idle reaping, activity timeline (`activity.jsonl`), `/healthz`.
- `scripts/install-http.ps1` (in the release zip): copies to `%LOCALAPPDATA%\LacertMCP`, urlacl `http://+:8767/`, firewall 8767 from 100.64/10, Startup-folder VBS restart loop running live `--http`. `-Uninstall` reverses.
- Command Center: Lacerte tile → "Open control page" (`/mcp/lacerte/control`), host from `LACERTE_MCP_HOST` in `~/command-center/.env` (not set yet).

## Verify Before Continuing

- [ ] Build: `docker run --rm -v $PWD:/src -v lacerte-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build LacertMCP.sln -c Debug -p:Platform=x64` → 0 warnings, 0 errors. (Windows: `scripts/build.ps1`.)
- [ ] Tests: same with image `lacerte-test-mono` and `dotnet test src/LacertMCP.Tests/LacertMCP.Tests.csproj -c Debug -p:Platform=x64` → only the 5 Linux-only failures above.
- [ ] HTTP smoke under Mono: start `mono .../LacertMCP.Server.exe --http` with `LACERTE_SIMULATION=1`, use `http://127.0.0.1:8767` (Mono's HttpListener rejects `Host: localhost` when both prefixes are bound — Windows http.sys does not).

## Next Task — live verification on the Lacerte PC (nothing above is proven against real Lacerte yet)

1. Install: release zip → `install-http.ps1`. `doctor` should be green for SDK/ODBC/AppID.
2. `lacerte-mcp register` (first time) — record the real AccessGranted/NotSignedIn integers (F1/F7).
3. `dumper\LacertMCP.FieldAccessDumper.exe --live --year <yyyy> --out %LOCALAPPDATA%\LacertMCP\server\Schema\FieldAccessMap\base` for each year in use (without `--out` it writes next to itself and the server never sees it); restart; control page "Writable fields" goes green. Re-running install-http.ps1 keeps a built map.
4. Control page → Data folders → **Test read** on each folder. Confirms connection string, ReturnType, DATA1 columns, client-number format.
5. Read a known client with `lacerte_client_read`, `lacerte_table_read` 2/3/5/7, `lacerte_detail_read`, `lacerte_ef_status`; compare against the Lacerte UI. **Check:** DATA2/DATA4 subclient id column name (code looks for `SubClientID`/`SubClientNumber`), TY21+ per-client table names (`[CLIENT-SubClientInfo]` etc.).
6. On a TEST client only: `lacerte_field_write` a harmless field with Lacerte closed → expect `verified:true`; with the return open in Lacerte → expect a DBFNotAccessable error, not success. Confirm the fresh-connection read-back sees the COM write.
7. Capture `Tests/Parity/fixtures/live-ty24-individual.json`; merge the branch; tag.

## Context Notes

- Lacerte PC candidates on the tailnet: `vr` (100.114.92.33), `desktop-0ngsgb4` (100.124.82.41, SSH open as of 2026-11-11 but key not yet accepted), `desktop-6fhu255` (100.80.126.71). Tower key for Windows: `~/.ssh/id_ed25519_lacerte`.
- Per-connection context: every read of `LacerteSessionManager.Context` resolves through `SessionScope.Current`; under stdio it is null and the old global applies. Don't cache `Context` across awaits in new code.
- `ToolActivity` records tool name, device, outcome and for writes only clientNumber/field/table — never values.
- MCP SDK 1.4.0's `StreamableHttpServerTransport` is in ModelContextProtocol.Core (netstandard2.0); the AspNetCore package is net8+ only, hence the HttpListener host.
