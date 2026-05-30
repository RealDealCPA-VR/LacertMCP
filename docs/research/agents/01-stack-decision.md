# Stack Decision — Language / Runtime for the Lacerte MCP Server

Research agent report — 2026-05-30. Phase 0 of `Todo.md`. Supersedes the tentative entry in `DECISIONS.md` (2026-05-30) that proposed mirroring the QuickBooks Desktop MCP stack 1:1.

---

## i. Recommendation

**Adopt Option 2: a pure C# / .NET 8 MCP server using the official `ModelContextProtocol` NuGet package (Microsoft + Anthropic, GA Feb 2026, v1.3.0 current).**

The Lacerte SDK is fundamentally different from QBXMLRP2 in a way that breaks the QB MCP analogy: every write path (`Register`, `Login`, `CreateClient`, TY21+ `UpdateTaxReturn`) lives in the `LacerteSDK_Access` / `LacerteSDK_Interfaces` .NET assemblies, **not** classic COM, and the host process **must** carry an `<exe>.config` with `useLegacyV2RuntimeActivationPolicy="true"` plus `supportedRuntime v4.0 / .NETFramework v4.6` to load the mixed-mode CLR shim. `node.exe` does not naturally honor that config (no `node.exe.config` ships with Node, and `winax` is a classic-COM IDispatch wrapper with no documented CLR-hosting story), so a TS-first design fights the runtime on its single most load-bearing requirement from day one. .NET 8 gives us native COM/.NET interop, free `<exe>.config` honoring, an officially-supported MCP SDK with stdio transport, and direct ODBC access via `System.Data.Odbc` — every Lacerte-specific friction point evaporates. We give up some iteration speed and the QB-mirror pattern, but the QB mirror was only ever a means to an end, and the end (a working bridge to a COM/.NET-heavy Windows-local SDK) is much closer in .NET.

---

## ii. Evidence

**Lacerte SDK shape forces .NET hosting.**
- `Lacerte_MCP_Structure_Spec.md` §1 and §2 confirm: the .NET/COM libraries (`LacerteSDK_Access`, `LacerteSDK_Interfaces`, `LacerteSDK_SharedInterfaces`) are the **only** path for `Register()`, `Login()`, `CreateClient`, and TY21+ writes; the host needs an `<exe>.config` runtime shim ([repo path: `C:\Users\VR\Projects\LacertMCP\Lacerte_MCP_Structure_Spec.md` lines 12, 20, 165]).
- Intuit's own SDK docs frame the integration as ".Net Library (COM Component)" plus ODBC, with no JS/TS path acknowledged ([Intuit Developer — Lacerte SDK Getting Started](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started); [Intuit Accountants Community — Lacerte SDK group](https://accountants.intuit.com/community/lacerte-sdk-group/gp-p/501)).

**`winax` is classic-COM only and undocumented for .NET CCWs.**
- `winax` README and source focus on IDispatch wrappers for ADO/Excel/WScript-style ActiveX objects; there is no documented support for .NET-assembly-via-COM-CCW or mixed-mode CLR activation ([winax — npm](https://www.npmjs.com/package/winax); [durs/node-activex](https://github.com/durs/node-activex)).
- The `useLegacyV2RuntimeActivationPolicy` shim is read from the **host executable's** config file, and enabling it disables CLR side-by-side loading in that process — a constraint that's invisible to `winax` and that there's no clean way to assert from a Node host ([Microsoft Learn — `<startup>` element](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/startup/startup-element); [Reed Copsey — Setting useLegacyV2RuntimeActivationPolicy At Runtime](http://reedcopsey.com/2011/09/15/setting-uselegacyv2runtimeactivationpolicy-at-runtime/)).
- No blog post, GitHub issue, or community thread surfaces a worked example of driving the Lacerte SDK from Node; the only adjacent Node-CLR bridge that **does** host the runtime is `edge-js`, and embedding `edge-js` to talk to Lacerte gets us all the C# we'd write in Option 2 *plus* a Node host and a marshalling layer we'd otherwise not need ([edge-js — npm](https://www.npmjs.com/package/edge-js); [tjanczuk/edge](https://github.com/tjanczuk/edge)).

**.NET MCP SDK is production-ready in 2026.**
- Microsoft + Anthropic co-maintain the official `ModelContextProtocol` C# SDK; it left preview on Feb 25, 2026 (v1.0.0) and is at **v1.3.0** as of May 8, 2026 ([modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk); [NuGet — ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol); [Microsoft DevBlogs — Build an MCP server in C#](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/); [InfoQ coverage of the partnership](https://www.infoq.com/news/2025/04/microsoft-csharp-sdk-mcp/)).
- The SDK ships `WithStdioServerTransport()` and `[McpServerTool]` attribute-based tool registration — feature-equivalent to the TS SDK pattern we already use in the QB MCP ([Microsoft Learn — Build an MCP server quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server); [MCP C# SDK Getting Started](https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html)).

**QB MCP precedent does not transfer.**
- The QB MCP (`C:\Users\VR\Projects\Quickbooks MCP Desktop\package.json`) declares `winax` as an **optional** dependency and `ARCHITECTURE.md` lines 36-39 confirm the live COM path is currently **stubbed**. The TS+winax pattern has therefore not been live-exercised even on QBXMLRP2 — which is itself a classic COM out-of-process server, the easiest case for `winax`. Lacerte is strictly harder, so the QB stack is unproven for *easy* COM and silent on *harder* .NET-assembly COM.

---

## iii. Risks of the Recommended Option

1. **No neighboring codebase to mirror.** We lose the line-for-line QB MCP template (tool layout, session manager pattern, zod-style validation). Mitigation: port the *operating system* (HANDOFF/DECISIONS/ARCHITECTURE files, simulation-vs-live mode, one-tool-per-file convention) wholesale; only the language differs. The C# SDK's attribute-routing makes per-domain tool files cleanly equivalent to `src/tools/*.ts`.
2. **AI-pair-programming velocity is lower in C# than TS** for this codebase's authors. Mitigation: scope is bounded (the Lacerte SDK surface is finite); offset is real but not catastrophic. Use `dotnet watch run` for the same hot-reload feel as `tsx`.
3. **.NET 8 vs .NET Framework 4.6 ambiguity.** Lacerte's `<exe>.config` requirement names .NET Framework 4.6. Modern .NET 8 still loads COM-registered assemblies on Windows via `System.Runtime.InteropServices`, but mixed-mode CLR loading is a .NET Framework concept. Mitigation: target **.NET Framework 4.8** for the bridge `.exe` (which trivially satisfies the `<exe>.config` shim Lacerte expects) and use the C# MCP SDK's `netstandard2.0`-compatible surface — confirm support in a Phase 0 spike before committing the project file. If the SDK requires .NET 8, fall back to Option 3 with a tiny .NET Framework 4.8 helper for the COM calls only and a .NET 8 host for the MCP transport.
4. **ODBC story is slightly less mature in .NET than in Node.** `System.Data.Odbc` is in `runtime`, works fine for the `LacerteDSII` DSN, but lacks the connection-pool ergonomics of the `odbc` npm package. Acceptable.
5. **Simulation-mode fidelity.** We lose the QB MCP's elegant in-memory `SimulationStore`. Mitigation: build the C# equivalent — a `LacerteSimulationSession` with seeded `ConcurrentDictionary` stores — as a first-class Phase 0 deliverable, mirroring the QB pattern.

---

## iv. Concrete Next Steps If Adopted

**Update `DECISIONS.md` (replace the tentative 2026-05-30 entry):**
- Chosen: C# / .NET, official `ModelContextProtocol` SDK, target `net48` for the bridge `.exe`.
- Alternatives rejected: TS+winax (no documented CLR shim path), Hybrid (extra IPC surface for marginal gain).

**Replace the empty `package.json`/`tsconfig.json` scaffolding with a .NET solution.** Current state: every directory under `src/` (`bridge`, `cli`, `errors`, `odbc`, `prompts`, `schema`, `session`, `tools`, `types`) is empty; no `package.json` exists yet (confirmed via Glob).

Proposed file structure:
```
LacertMCP.sln
src/
  LacertMCP.Server/                 # entry point, stdio MCP host
    LacertMCP.Server.csproj         # <TargetFramework>net48</TargetFramework>
    Program.cs                       # Host.CreateApplicationBuilder + AddMcpServer + WithStdioServerTransport
    app.config                       # useLegacyV2RuntimeActivationPolicy + supportedRuntime v4.0
    Tools/                           # one file per domain — Clients.cs, Returns.cs, Reports.cs, Session.cs ...
    Session/
      LacerteSessionManager.cs       # mode switch (live | simulation), wraps LacerteSDK_Access
      SimulationStore.cs             # in-memory analogue to QB MCP's simulation-store.ts
    Bridge/
      LacerteAccessClient.cs         # thin wrapper around LacerteSDK_Access XML returns
    Odbc/
      LacerteDsnReader.cs            # System.Data.Odbc against LacerteDSII DSN
    Schema/                          # TY21+ branching (CanUseNewApis)
    Errors/
  LacertMCP.Tests/
    LacertMCP.Tests.csproj           # xUnit
tests/                                # delete; folded into LacertMCP.Tests
```

**NuGet packages to install** (in `LacertMCP.Server`):
- `ModelContextProtocol` (1.3.0) — MCP server + DI + hosting
- `Microsoft.Extensions.Hosting` (latest 8.x compatible with net48 via netstandard2.0)
- `Microsoft.Extensions.Logging.Console`
- `System.Data.Odbc` (only if not in BCL on net48 — usually already available)
- COM references to `LacerteSDK_Access.dll`, `LacerteSDK_Interfaces.dll`, `LacerteSDK_SharedInterfaces.dll` via `<Reference Include="..." />` with `<EmbedInteropTypes>false</EmbedInteropTypes>`

**Update `ARCHITECTURE.md`** to drop the TS-specific layer naming (`src/bridge/*.ts`) and replace with the C# namespace layout above; the layer *responsibilities* (Tool → Session → Bridge/Odbc) port directly.

**First Phase 0 spike (before any tool work):** build a 30-line `Program.cs` that boots the MCP stdio server, registers a single `[McpServerTool] static string LacerteHealth()` that calls `new LacerteSDK_Access.LacerteAccess()` and returns `"reachable"` or the COM HRESULT. If that runs from `dotnet run`, the stack is locked.

---

## v. When to Revisit

Revisit this decision if any of:
- The `LacerteSDK_Access` COM type cannot be instantiated from .NET 4.8 in the Phase 0 spike (would force a deeper investigation into in-proc registration, possibly Option 3 hybrid).
- The official `ModelContextProtocol` C# SDK drops support for `net48` / `netstandard2.0` in a future major version, *and* Lacerte still mandates the legacy CLR shim (would force Option 3: .NET 8 host + net48 COM helper).
- More than ~20% of the tool surface ends up being ODBC-only reads with no COM dependency (would *not* change the recommendation but would shift the internal split — keep an eye on whether a separate .NET 8 read-only daemon becomes worthwhile).
- A second similar local-Windows MCP becomes necessary and a shared TS+winax pattern is independently validated against a CCW-exposed .NET assembly (would justify reconsidering Option 1 across both projects).

---

## Sources

- [Intuit Developer — Lacerte SDK Getting Started](https://developer.intuit.com/app/developer/lacerte-sdk/docs/lacerte-get-started)
- [Intuit Accountants Community — Lacerte SDK group](https://accountants.intuit.com/community/lacerte-sdk-group/gp-p/501)
- [Intuit Support — Lacerte Software Developer's Kit](https://accountants.intuit.com/support/en-us/help-article/license-information/lacerte-software-developer-kit-sdk/L3pbQA7gF_US_en_US)
- [winax — npm](https://www.npmjs.com/package/winax)
- [durs/node-activex (winax upstream)](https://github.com/durs/node-activex)
- [edge-js — npm](https://www.npmjs.com/package/edge-js)
- [tjanczuk/edge — Run .NET and Node.js in-process](https://github.com/tjanczuk/edge)
- [Microsoft Learn — `<startup>` element](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/startup/startup-element)
- [Microsoft Learn — `<supportedRuntime>` element](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/startup/supportedruntime-element)
- [Reed Copsey — Setting useLegacyV2RuntimeActivationPolicy At Runtime](http://reedcopsey.com/2011/09/15/setting-uselegacyv2runtimeactivationpolicy-at-runtime/)
- [modelcontextprotocol/csharp-sdk on GitHub](https://github.com/modelcontextprotocol/csharp-sdk)
- [NuGet — ModelContextProtocol package](https://www.nuget.org/packages/ModelContextProtocol)
- [Microsoft DevBlogs — Build an MCP server in C#](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/)
- [Microsoft Learn — Quickstart: build an MCP server](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server)
- [MCP C# SDK — Getting Started](https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html)
- [Microsoft for Developers — Microsoft partners with Anthropic on official C# MCP SDK](https://developer.microsoft.com/blog/microsoft-partners-with-anthropic-to-create-official-c-sdk-for-model-context-protocol)
- [InfoQ — Microsoft + Anthropic launch C# SDK for MCP](https://www.infoq.com/news/2025/04/microsoft-csharp-sdk-mcp/)
- Local files: `C:\Users\VR\Projects\LacertMCP\Lacerte_MCP_Structure_Spec.md`, `C:\Users\VR\Projects\LacertMCP\DECISIONS.md`, `C:\Users\VR\Projects\Quickbooks MCP Desktop\package.json`, `C:\Users\VR\Projects\Quickbooks MCP Desktop\ARCHITECTURE.md`
