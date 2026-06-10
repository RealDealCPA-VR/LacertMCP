using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using LacertMCP.Cli.Storage;
using LacertMCP.Server.Diagnostics;
using LacertMCP.Server.Schema;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp doctor [--json]</c>
    ///
    /// <para>
    /// Pre-flight probe for the operator install — mirrors the QB MCP <c>cli/doctor.ts</c> contract:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Exit 0 — every probe is green.</description></item>
    ///   <item><description>Exit 1 — at least one red (real failure).</description></item>
    ///   <item><description>Exit 2 — PARTIAL: at least one warn (advisory) or skipped (probe inapplicable on this OS), and no reds. (R6-29: Warn now lands in the PARTIAL bucket so a warn-only run no longer claims "all probes green".)</description></item>
    /// </list>
    ///
    /// <para>
    /// Probes (P15-031 + P15-032 expanded): Build version, Windows version, x64 process,
    /// App.config shim (useLegacyV2RuntimeActivationPolicy + supportedRuntime),
    /// .NET 4.8, ODBC DSN <c>LacerteDSII</c>, Lacerte SDK install dir, Lacerte COM
    /// ProgID resolves, operating-mode resolution, <c>LACERTE_DATA_PATH</c> env +
    /// directory-exists, <c>LACERTE_DATA_ROOT</c> env + directory-exists, FieldAccessMap
    /// base shards present, AppID GUID parses. Every red gets a one-line remediation pointer.
    /// </para>
    ///
    /// <para>
    /// Output is the human table by default; pass <c>--json</c> for a machine-parseable payload
    /// (<c>exitCode</c> + <c>probes[]</c>) the way <c>tools</c> and <c>status</c> already work.
    /// (P15-027 harmonized the three diagnostic verbs on a single JSON contract.)
    /// </para>
    /// </summary>
    public static class DoctorCommand
    {
        public enum ProbeStatus { Pass, Warn, Fail, Skipped }

        public sealed class ProbeResult
        {
            public string Name { get; }
            public ProbeStatus Status { get; }
            public string Detail { get; }
            public string? Remediation { get; }

            public ProbeResult(string name, ProbeStatus status, string detail, string? remediation = null)
            {
                Name = name;
                Status = status;
                Detail = detail;
                Remediation = remediation;
            }

            /// <summary>
            /// P15-065: returns a status badge that's safe on the active console encoding.
            /// On UTF-8 terminals (Windows Terminal, PowerShell 7, modern Linux) we emit the
            /// pretty Unicode glyphs; on legacy OEM code pages (cmd.exe CP437/CP850) we fall
            /// back to ASCII tags so the operator's first-touch diagnostic surface doesn't
            /// render as mojibake `?` boxes. Type changed from <c>char</c> to <c>string</c>
            /// because <c>[OK]</c> et al. don't fit in one char.
            /// </summary>
            public string Glyph
            {
                get
                {
                    if (SupportsUnicode())
                    {
                        return Status switch
                        {
                            ProbeStatus.Pass => "✓",     // U+2713
                            ProbeStatus.Warn => "⚠",     // U+26A0
                            ProbeStatus.Fail => "✗",     // U+2717
                            ProbeStatus.Skipped => "-",
                            _ => "?",
                        };
                    }
                    return Status switch
                    {
                        ProbeStatus.Pass => "[OK]",
                        ProbeStatus.Warn => "[WARN]",
                        ProbeStatus.Fail => "[FAIL]",
                        ProbeStatus.Skipped => "[--]",
                        _ => "[?]",
                    };
                }
            }

            /// <summary>
            /// P15-065: testable seam — when the active console encoding is UTF-8 (CP 65001)
            /// or self-identifies as utf-8, emit the pretty glyphs; otherwise ASCII fallback.
            /// </summary>
            private static bool SupportsUnicode()
            {
                try
                {
                    var enc = Console.OutputEncoding;
                    if (enc == null) return false;
                    if (enc.CodePage == 65001) return true;
                    if (string.Equals(enc.WebName, "utf-8", StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }
                catch { return false; }
            }
        }

        public static Command Build()
        {
            var cmd = new Command("doctor",
                "Probe the install for prerequisites; exit 0 (green) / 1 (red) / 2 (partial: warnings, or probes skipped on this OS). " +
                "Human table by default; pass --json for machine-parseable output.");

            // P15-027: machine-parseable shape so CI smoke can fail on specific probes
            // instead of swallowing exit 1 silently.
            var jsonOpt = new Option<bool>("--json", () => false,
                "Emit machine-parseable JSON (exitCode + probes[]) instead of the human table.");
            cmd.AddOption(jsonOpt);

            cmd.SetHandler((context) =>
            {
                var asJson = context.ParseResult.GetValueForOption(jsonOpt);
                context.ExitCode = Run(Console.Out, asJson);
            });
            return cmd;
        }

        /// <summary>
        /// Back-compat overload — preserved for older test call sites that pre-date the
        /// P15-027 <c>asJson</c> parameter. Defaults to the human table.
        /// </summary>
        public static int Run(TextWriter writer) => Run(writer, asJson: false);

        public static int Run(TextWriter writer, bool asJson)
            => Run(writer, asJson, RunAllProbes());

        /// <summary>
        /// R6-29: results-injectable seam so tests can pin the Warn rendering
        /// (remediation line + PARTIAL summary + exit 2) with synthetic probe
        /// lists instead of depending on the host's real environment.
        /// </summary>
        public static int Run(TextWriter writer, bool asJson, IList<ProbeResult> results)
        {
            var exit = ComputeExitCode(results);

            if (asJson)
            {
                var payload = new
                {
                    exitCode = exit,
                    probes = results.Select(r => new
                    {
                        name = r.Name,
                        status = r.Status,
                        detail = r.Detail,
                        remediation = r.Remediation,
                    }),
                };
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    // P16-068: load-bearing — the App.config remediation literally
                    // contains `<startup>` and `<supportedRuntime>` XML, which the
                    // default HTML-safe encoder turns into `<` noise that is
                    // unreadable when an operator pastes it into a bug report.
                    // Doctor JSON is shell/terminal/jq-bound, never embedded in HTML.
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                // CRITICAL: PropertyNamingPolicy does NOT apply to enum values; without
                // this converter ProbeStatus would serialize as the int 0/1/2/3.
                options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                writer.WriteLine(JsonSerializer.Serialize(payload, options));
                return exit;
            }

            writer.WriteLine("lacerte-mcp doctor");
            writer.WriteLine(new string('-', 60));
            foreach (var r in results)
            {
                // P15-065: Glyph is now a string (may be ASCII tag like "[OK]" on legacy
                // consoles or Unicode glyph "✓" on UTF-8); keep one space between glyph
                // and name regardless of width.
                writer.WriteLine($" {r.Glyph} {r.Name}: {r.Detail}");
                // R6-29(a): Warn remediation is now printed too — previously only
                // Fail rows surfaced their `→` hint, so the operator saw a ⚠ row
                // with no guidance on what to do about it.
                if ((r.Status == ProbeStatus.Fail || r.Status == ProbeStatus.Warn)
                    && !string.IsNullOrEmpty(r.Remediation))
                {
                    writer.WriteLine($"      → {r.Remediation}");
                }
            }
            writer.WriteLine(new string('-', 60));

            // R6-29(b): summary must not claim "all probes green" when warns
            // exist — warn-only runs are PARTIAL (exit 2), same bucket as skips.
            var warnCount = results.Count(r => r.Status == ProbeStatus.Warn);
            var skipCount = results.Count(r => r.Status == ProbeStatus.Skipped);
            switch (exit)
            {
                case 1:
                    writer.WriteLine("Result: FAIL (one or more probes red). Exit 1.");
                    break;
                case 2:
                    if (warnCount > 0 && skipCount > 0)
                        writer.WriteLine($"Result: PARTIAL ({warnCount} warning(s), {skipCount} probe(s) skipped). Exit 2.");
                    else if (warnCount > 0)
                        writer.WriteLine($"Result: PARTIAL ({warnCount} warning(s)). Exit 2.");
                    else
                        writer.WriteLine("Result: PARTIAL (one or more probes skipped — not on Windows). Exit 2.");
                    break;
                default:
                    writer.WriteLine("Result: OK (all probes green). Exit 0.");
                    break;
            }

            // P16-073: append an exit-code-keyed "Next:" block to human output so
            // doctor doesn't end as a probe table the operator has to context-switch
            // to the README to act on. Mirrors the self-curing discipline status
            // command adopted in P16-041. Skip on --json (JSON consumers have the
            // exitCode + per-probe remediation already and the schema is contract-pinned
            // by P15-027).
            writer.WriteLine();
            switch (exit)
            {
                case 0:
                    writer.WriteLine("Next: `lacerte-mcp tools` lists what the server exposes. To go live, set LACERTE_LIVE=1 in your MCP client config (Claude Desktop / Cursor / Windsurf) and restart the host.");
                    break;
                case 1:
                    // If AppID-missing is the only fail, push register; otherwise generic.
                    var appIdFail = results.FirstOrDefault(r => r.Name == "AppID GUID" && r.Status == ProbeStatus.Fail);
                    var failCount = results.Count(r => r.Status == ProbeStatus.Fail);
                    if (appIdFail != null && failCount == 1)
                    {
                        writer.WriteLine("Next: run `lacerte-mcp register --app-name \"Operator Lacerte MCP\" --tax-year <year>` from an interactive PowerShell (the Intuit consent dialog needs a desktop).");
                    }
                    else
                    {
                        writer.WriteLine("Next: address the red probe(s) above (each has a `→` remediation). Re-run `lacerte-mcp doctor` to confirm green. See README.md Troubleshooting for context.");
                    }
                    break;
                case 2:
                    // R6-29: warn-only (or warn+skip) PARTIAL gets actionable
                    // guidance; skip-only keeps the original off-Windows note.
                    if (warnCount > 0)
                    {
                        writer.WriteLine("Next: review the warn probe(s) above (advisory — each prints a `→` remediation when one applies). Skipped probes are normal off-Windows. Re-run `lacerte-mcp doctor` after addressing warnings to confirm green.");
                    }
                    else
                    {
                        writer.WriteLine("Next: PARTIAL is normal off-Windows — Windows-only probes are skipped. For sim/dev work: `lacerte-mcp tools` lists the surface and `$env:LACERTE_SIMULATION = \"1\"` keeps the server in in-memory fixture mode. See README.md Quick Start.");
                    }
                    break;
            }
            return exit;
        }

        /// <summary>
        /// P14-071: pure, side-effect-free exit-code decision so the
        /// <c>0 green / 1 red / 2 partial</c> contract (Fail trumps everything)
        /// is directly testable without I/O. Public so xUnit can pin it
        /// with synthetic <see cref="ProbeResult"/> lists.
        ///
        /// <para>
        /// R6-29: Warn now lands in the PARTIAL bucket (exit 2) alongside
        /// Skipped — previously a warn-only box exited 0 "all probes green"
        /// while showing visible ⚠ rows, so scripts gating on the exit code
        /// could never see advisory problems.
        /// </para>
        /// </summary>
        public static int ComputeExitCode(IList<ProbeResult> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            var hasFail = false;
            var hasPartial = false;
            foreach (var r in results)
            {
                if (r.Status == ProbeStatus.Fail) hasFail = true;
                if (r.Status == ProbeStatus.Skipped || r.Status == ProbeStatus.Warn) hasPartial = true;
            }
            // Fail trumps Warn/Skip — a red probe always wins over a partial one.
            if (hasFail) return 1;
            if (hasPartial) return 2;
            return 0;
        }

        public static IList<ProbeResult> RunAllProbes()
            => RunAllProbes(envOverrides: null);

        /// <summary>
        /// Test seam: <paramref name="envOverrides"/> lets a test pin the value
        /// of LACERTE_DATA_PATH / LACERTE_DATA_ROOT (null value = treat as
        /// unset) WITHOUT mutating process-wide environment variables. Mutating
        /// the real env races concurrently-running test collections whose
        /// SetContext validation reads LACERTE_DATA_ROOT at call time.
        /// </summary>
        public static IList<ProbeResult> RunAllProbes(
            IDictionary<string, string?>? envOverrides)
        {
            string? Env(string name) =>
                envOverrides != null && envOverrides.TryGetValue(name, out var v)
                    ? v
                    : Environment.GetEnvironmentVariable(name);

            var results = new List<ProbeResult>();
            var onWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            // 0. Build version — always Pass; exists so the operator can copy
            //    the build stamp into bug reports (P14-040). Sourced from
            //    Directory.Build.props -> AssemblyInformationalVersion.
            results.Add(new ProbeResult("Build version", ProbeStatus.Pass, BuildInfo.Version));

            // 1. OS / Windows version
            if (onWindows)
            {
                var v = Environment.OSVersion.Version;
                results.Add(new ProbeResult("Windows version",
                    ProbeStatus.Pass,
                    $"Windows NT {v.Major}.{v.Minor}.{v.Build}"));
            }
            else
            {
                results.Add(new ProbeResult("Windows version",
                    ProbeStatus.Skipped,
                    $"skipped: not on Windows (running {Environment.OSVersion.Platform})"));
            }

            // 2. x64 process
            if (Environment.Is64BitProcess)
            {
                results.Add(new ProbeResult("x64 process", ProbeStatus.Pass, "process is 64-bit"));
            }
            else
            {
                results.Add(new ProbeResult("x64 process",
                    ProbeStatus.Fail,
                    "process is 32-bit",
                    "Rebuild with <PlatformTarget>x64</PlatformTarget> and use the 64-bit Lacerte SDK MSI."));
            }

            // 2b. P15-032: App.config runtime shim. The mixed-mode LacerteSDK_Access
            //     CLR requires the legacy v2 runtime activation policy + supportedRuntime
            //     declaration in LacertMCP.Server.exe.config. Without it,
            //     `new LacerteSDK_Access.LacerteAccess()` throws on construction and
            //     the operator sees a raw COM HRESULT via BridgeUnavailable — NOT
            //     "App.config missing." Probe ships with the .exe regardless of OS
            //     (it's a build-output check, not Windows-only), so we always run it.
            results.Add(ProbeAppConfigShim());

            // 3. .NET Framework 4.8 installed (Windows-only check)
            if (onWindows)
            {
                results.Add(ProbeNetFx48());
            }
            else
            {
                results.Add(new ProbeResult(".NET Framework 4.8",
                    ProbeStatus.Skipped,
                    "skipped: not on Windows (net48 is the deployment target)"));
            }

            // 4. ODBC DSN LacerteDSII present
            if (onWindows)
            {
                results.Add(ProbeOdbcDsn());
            }
            else
            {
                results.Add(new ProbeResult("ODBC DSN LacerteDSII",
                    ProbeStatus.Skipped,
                    "skipped: not on Windows (ODBC DSN check uses Win registry)"));
            }

            // 5. Lacerte SDK install location
            if (onWindows)
            {
                results.Add(ProbeLacerteSdkInstall());
            }
            else
            {
                results.Add(new ProbeResult("Lacerte SDK install",
                    ProbeStatus.Skipped,
                    "skipped: not on Windows (Lacerte is a Windows-only product)"));
            }

            // 5b. P15-031: Lacerte COM ProgID resolves — catches the "MSI files present
            //     but regsvr32 not applied" half-install where (5) passes but tools die
            //     at the first COM call.
            if (onWindows)
            {
                results.Add(ProbeLacerteComProgId());
            }
            else
            {
                results.Add(new ProbeResult("Lacerte COM ProgID",
                    ProbeStatus.Skipped,
                    "skipped: not on Windows (COM ProgID lookup is Win-only)"));
            }

            // 5c. P15-031: Operating mode resolution — informational probe so the
            //     operator can see which mode (sim/live) the server WOULD start in
            //     given the current env vars and OS. Warn on conflict (both env
            //     flags set; or LACERTE_LIVE=1 off-Windows).
            results.Add(ProbeOperatingMode(onWindows));

            // 6. LACERTE_DATA_PATH env (mode-independent — useful even off-Windows for sim)
            //    P15-031: when set, also probe the directory exists on disk.
            //    P16-023: per-env-var remediation + bonus tier-down so an operator
            //    with LACERTE_DATA_ROOT correctly configured isn't told (falsely)
            //    that DATA_PATH must be set via `lacerte_set_context`.
            var dataRootSet = !string.IsNullOrEmpty(Env("LACERTE_DATA_ROOT"));
            results.AddRange(ProbeDataPathEnv(
                "LACERTE_DATA_PATH",
                Env("LACERTE_DATA_PATH"),
                unsetDetail: dataRootSet
                    ? "not set; per-call dataPath will resolve from LACERTE_DATA_ROOT (current discovery covers this)"
                    : "not set (call the `lacerte_set_context` tool per-session, or set LACERTE_DATA_PATH in env for a persistent default)",
                unsetRemediation: null,
                downgradeUnsetToPass: dataRootSet));

            // 6b. P15-031: LACERTE_DATA_ROOT env + directory-exists.
            //    P16-023: distinct remediation — DATA_ROOT is NOT settable via
            //    lacerte_set_context (that tool only writes DATA_PATH); the prior
            //    copy-pasted message sent operators on a wild-goose chase.
            results.AddRange(ProbeDataPathEnv(
                "LACERTE_DATA_ROOT",
                Env("LACERTE_DATA_ROOT"),
                unsetDetail: "not set; `lacerte_list_datapaths` will fall back to C:\\Lacerte",
                unsetRemediation: "Set LACERTE_DATA_ROOT in the MCP client config (or Windows env) to your install root, e.g. C:\\Lacerte. NOTE: this is NOT settable via lacerte_set_context — that tool only writes the current per-session DATA_PATH.",
                downgradeUnsetToPass: false));

            // 6c. P15-031: FieldAccessMap base shards present — without them every
            //     write returns Unknown access; an operator with a corrupt build dir
            //     deserves a direct signal.
            results.Add(ProbeFieldAccessMapShards());

            // 7. AppID persisted — P15-031: now distinguishes "absent" from
            //    "persisted but malformed" via AppIdStore.GetDiagnostic().
            results.Add(ProbeAppId());

            return results;
        }

        // ---- Individual probes ------------------------------------------------------

        /// <summary>
        /// P15-032: locate the App.config shim that ships next to
        /// <c>LacertMCP.Server.exe</c> and validate the load-bearing runtime
        /// keys. Wrapper that resolves the canonical file path and delegates
        /// to <see cref="ProbeAppConfigShimAt(string)"/>; the helper is pure
        /// so it can be exercised off-Windows with synthetic paths.
        ///
        /// <para>
        /// Resolution: <c>{AppContext.BaseDirectory}/LacertMCP.Server.exe.config</c>
        /// — operator install layout (and dev bin layout) places the CLI and
        /// Server .exe + .config side-by-side in one bin dir.
        /// </para>
        /// </summary>
        private static ProbeResult ProbeAppConfigShim()
        {
            try
            {
                var configPath = ResolveServerConfigPath();
                return ProbeAppConfigShimAt(configPath);
            }
            catch (Exception ex)
            {
                return new ProbeResult("App.config shim",
                    ProbeStatus.Fail,
                    "probe threw: " + ex.Message,
                    "Verify LacertMCP.Server.exe.config is readable next to LacertMCP.Server.exe (see ARCHITECTURE.md \"Build & Runtime\").");
            }
        }

        /// <summary>
        /// P15-032: best-effort path to <c>LacertMCP.Server.exe.config</c>. Public
        /// for tests; returns the AppContext.BaseDirectory-relative path so the
        /// probe finds the file in both the dev bin layout AND the operator's
        /// installed layout (the .exe + .config get copied side-by-side at
        /// build/publish time).
        /// </summary>
        public static string ResolveServerConfigPath()
        {
            var baseDir = AppContext.BaseDirectory ?? string.Empty;
            return Path.Combine(baseDir, "LacertMCP.Server.exe.config");
        }

        /// <summary>
        /// P15-032: pure probe — given a path to a candidate App.config, return
        /// a ProbeResult. Public + path-injectable so xUnit can pin all four
        /// outcomes (missing / malformed XML / missing keys / valid) off-Windows
        /// without touching the real build output.
        ///
        /// <para>Pass criteria — ALL must hold:</para>
        /// <list type="bullet">
        ///   <item><description>File exists at <paramref name="configPath"/>.</description></item>
        ///   <item><description>XML parses without throwing.</description></item>
        ///   <item><description><c>/configuration/startup</c> exists with attribute <c>useLegacyV2RuntimeActivationPolicy="true"</c>.</description></item>
        ///   <item><description><c>/configuration/startup/supportedRuntime</c> exists with attribute <c>version="v4.0"</c>.</description></item>
        /// </list>
        ///
        /// <para>
        /// Remediation always points at the canonical 14-line file shape in
        /// <c>src/LacertMCP.Server/App.config</c> (ARCHITECTURE.md "Build &amp;
        /// Runtime"). The mixed-mode LacerteSDK_Access CLR will throw on
        /// construction without the shim — and the existing
        /// <c>LacerteAccessClient</c> catch path reduces that to a raw COM
        /// HRESULT, so doctor is the only surface that names the actual cause.
        /// </para>
        /// </summary>
        public static ProbeResult ProbeAppConfigShimAt(string configPath)
        {
            const string Name = "App.config shim";
            const string CanonicalRemediation =
                "Restore App.config per ARCHITECTURE.md \"Build & Runtime\" — the file " +
                "is load-bearing for the mixed-mode CLR shim and must ship next to " +
                "LacertMCP.Server.exe with <startup useLegacyV2RuntimeActivationPolicy=\"true\"> " +
                "and <supportedRuntime version=\"v4.0\" />. See src/LacertMCP.Server/App.config " +
                "for the canonical 14-line file.";

            if (string.IsNullOrEmpty(configPath))
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    "config path could not be resolved",
                    CanonicalRemediation);
            }

            if (!File.Exists(configPath))
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    $"LacertMCP.Server.exe.config not found next to LacertMCP.Server.exe (looked at {configPath})",
                    CanonicalRemediation);
            }

            XmlDocument doc;
            try
            {
                doc = new XmlDocument();
                doc.Load(configPath);
            }
            catch (XmlException ex)
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    $"App.config XML is malformed: {ex.Message}",
                    CanonicalRemediation);
            }

            // /configuration/startup[@useLegacyV2RuntimeActivationPolicy='true']
            var startup = doc.SelectSingleNode("/configuration/startup") as XmlElement;
            if (startup == null)
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    "App.config is missing the <startup> element",
                    CanonicalRemediation);
            }

            var legacyAttr = startup.GetAttribute("useLegacyV2RuntimeActivationPolicy");
            if (!string.Equals(legacyAttr, "true", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    $"App.config <startup> is missing useLegacyV2RuntimeActivationPolicy=\"true\" (found: \"{legacyAttr}\")",
                    CanonicalRemediation);
            }

            // /configuration/startup/supportedRuntime[@version='v4.0']
            var supportedRuntime = startup.SelectSingleNode("supportedRuntime") as XmlElement;
            if (supportedRuntime == null)
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    "App.config is missing <supportedRuntime> under <startup>",
                    CanonicalRemediation);
            }

            var versionAttr = supportedRuntime.GetAttribute("version");
            if (!string.Equals(versionAttr, "v4.0", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeResult(Name,
                    ProbeStatus.Fail,
                    $"App.config <supportedRuntime> version is \"{versionAttr}\", expected \"v4.0\"",
                    CanonicalRemediation);
            }

            return new ProbeResult(Name,
                ProbeStatus.Pass,
                "runtime shim present (useLegacyV2RuntimeActivationPolicy=true, supportedRuntime v4.0)");
        }

        private static ProbeResult ProbeNetFx48()
        {
            try
            {
                // The canonical .NET 4.8 detection: HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\Release >= 528040
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key == null)
                    {
                        return new ProbeResult(".NET Framework 4.8",
                            ProbeStatus.Fail,
                            "registry key missing",
                            "Install .NET Framework 4.8 from https://dotnet.microsoft.com/download/dotnet-framework/net48.");
                    }
                    var release = key.GetValue("Release") as int?;
                    if (release.HasValue && release.Value >= 528040)
                    {
                        return new ProbeResult(".NET Framework 4.8",
                            ProbeStatus.Pass,
                            $"Release={release.Value} (>= 528040)");
                    }
                    return new ProbeResult(".NET Framework 4.8",
                        ProbeStatus.Fail,
                        $"Release={release?.ToString() ?? "(missing)"} (< 528040)",
                        "Install .NET Framework 4.8 from https://dotnet.microsoft.com/download/dotnet-framework/net48.");
                }
            }
            catch (Exception ex)
            {
                return new ProbeResult(".NET Framework 4.8",
                    ProbeStatus.Fail,
                    "probe threw: " + ex.Message,
                    "Verify .NET Framework 4.8 is installed and the registry hive is readable.");
            }
        }

        private static ProbeResult ProbeOdbcDsn()
        {
            // ODBC DSNs live under HKLM\SOFTWARE\ODBC\ODBC.INI for System DSNs
            // and HKCU\SOFTWARE\ODBC\ODBC.INI for User DSNs. Probe both.
            try
            {
                if (HasOdbcDsn(Microsoft.Win32.Registry.LocalMachine, "LacerteDSII") ||
                    HasOdbcDsn(Microsoft.Win32.Registry.CurrentUser, "LacerteDSII"))
                {
                    return new ProbeResult("ODBC DSN LacerteDSII",
                        ProbeStatus.Pass,
                        "DSN registered");
                }
                return new ProbeResult("ODBC DSN LacerteDSII",
                    ProbeStatus.Warn,
                    "DSN not found (DSN-less connection string is still usable)",
                    null);
            }
            catch (Exception ex)
            {
                return new ProbeResult("ODBC DSN LacerteDSII",
                    ProbeStatus.Fail,
                    "probe threw: " + ex.Message,
                    "Open ODBC Data Sources (64-bit) and add a System DSN named LacerteDSII.");
            }
        }

        private static bool HasOdbcDsn(Microsoft.Win32.RegistryKey hive, string dsnName)
        {
            using (var ini = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI"))
            {
                if (ini == null) return false;
                foreach (var sub in ini.GetSubKeyNames())
                {
                    if (string.Equals(sub, dsnName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
        }

        private static ProbeResult ProbeLacerteSdkInstall()
        {
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            var candidates = new[]
            {
                Path.Combine(pf, "Common Files", "Lacerte Shared", "LacerteSDK"),
                Path.Combine(pfx86, "Common Files", "Lacerte Shared", "LacerteSDK"),
            };
            foreach (var path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    return new ProbeResult("Lacerte SDK install",
                        ProbeStatus.Pass,
                        path);
                }
            }
            return new ProbeResult("Lacerte SDK install",
                ProbeStatus.Fail,
                "not found in Program Files\\Common Files\\Lacerte Shared\\LacerteSDK",
                "Install the 64-bit Lacerte SDK MSI from Intuit's developer portal.");
        }

        /// <summary>
        /// P15-031: Catches the "MSI files present but COM not registered" half-install
        /// where ProbeLacerteSdkInstall passes but tools die at the first COM call.
        /// </summary>
        private static ProbeResult ProbeLacerteComProgId()
        {
            try
            {
                // Both ProgIDs from Bridge/LacerteAccessClient.cs + Bridge/Registration.cs.
                var t1 = Type.GetTypeFromProgID("LacerteSDK_Access.TPRegistration");
                var t2 = Type.GetTypeFromProgID("LacerteSDK_Access.LacerteAccess");
                if (t1 != null && t2 != null)
                {
                    return new ProbeResult("Lacerte COM ProgID",
                        ProbeStatus.Pass,
                        "TPRegistration + LacerteAccess ProgIDs both resolve");
                }

                var missing = new List<string>();
                if (t1 == null) missing.Add("LacerteSDK_Access.TPRegistration");
                if (t2 == null) missing.Add("LacerteSDK_Access.LacerteAccess");
                return new ProbeResult("Lacerte COM ProgID",
                    ProbeStatus.Fail,
                    "missing: " + string.Join(", ", missing),
                    "SDK files present but COM not registered; re-run the 64-bit Lacerte SDK MSI as Administrator " +
                    "(or `regsvr32 LacerteSDK_Access.dll` from the install dir).");
            }
            catch (Exception ex)
            {
                return new ProbeResult("Lacerte COM ProgID",
                    ProbeStatus.Fail,
                    "probe threw: " + ex.Message,
                    "Investigate COM registration of LacerteSDK_Access ProgIDs.");
            }
        }

        /// <summary>
        /// P15-031: Mirror the resolution logic in LacerteSessionManager.cs:131-135 so
        /// the doctor probe shows the operator which mode the server WOULD start in.
        /// Always Pass (informational) unless the env vars are in a conflicting state
        /// (both set, or LIVE=1 off-Windows) — those get Warn.
        /// </summary>
        private static ProbeResult ProbeOperatingMode(bool onWindows)
        {
            var sim = Environment.GetEnvironmentVariable("LACERTE_SIMULATION") == "1";
            var live = Environment.GetEnvironmentVariable("LACERTE_LIVE") == "1";
            // Mirrors LacerteSessionManager: Live only when (!sim && live && onWindows).
            var resolved = (!sim && live && onWindows) ? "Live" : "Simulation";

            // Warn cases.
            if (sim && live)
            {
                return new ProbeResult("Operating mode",
                    ProbeStatus.Warn,
                    $"resolved: {resolved} — BOTH LACERTE_LIVE=1 AND LACERTE_SIMULATION=1 set; simulation wins silently. Clear one to make intent explicit.");
            }
            if (live && !onWindows)
            {
                return new ProbeResult("Operating mode",
                    ProbeStatus.Warn,
                    $"resolved: {resolved} — LACERTE_LIVE=1 but not on Windows; falling back to Simulation.");
            }
            // Normal cases.
            string detail = resolved switch
            {
                "Live" => "resolved: Live (LACERTE_LIVE=1 on Windows)",
                _ when !onWindows => $"resolved: Simulation (off-Windows default; platform={Environment.OSVersion.Platform})",
                _ when sim => "resolved: Simulation (LACERTE_SIMULATION=1)",
                _ => "resolved: Simulation (default; set LACERTE_LIVE=1 on Windows to enable Live)",
            };
            return new ProbeResult("Operating mode", ProbeStatus.Pass, detail);
        }

        /// <summary>
        /// P15-031: env-presence + directory-exists pair. Yields ONE probe when the env
        /// var is unset (Warn) or TWO probes (presence Pass + directory-exists Pass/Fail)
        /// when set, so the operator can see both layers in the table.
        ///
        /// <para>
        /// P16-023: per-env-var <paramref name="unsetDetail"/> + <paramref name="unsetRemediation"/>
        /// so the DATA_PATH and DATA_ROOT warns can carry distinct guidance (the prior
        /// copy-pasted message told operators DATA_ROOT was settable via
        /// <c>lacerte_set_context</c>, which is false). When <paramref name="downgradeUnsetToPass"/>
        /// is true, the unset state is reported as <see cref="ProbeStatus.Pass"/> instead
        /// of <see cref="ProbeStatus.Warn"/> — used for DATA_PATH when DATA_ROOT is wired
        /// (discovery already covers the gap, so the unset warn is a false positive).
        /// </para>
        /// </summary>
        private static IEnumerable<ProbeResult> ProbeDataPathEnv(
            string envVar,
            string? val,
            string unsetDetail,
            string? unsetRemediation,
            bool downgradeUnsetToPass)
        {
            if (string.IsNullOrEmpty(val))
            {
                yield return new ProbeResult($"{envVar} env",
                    downgradeUnsetToPass ? ProbeStatus.Pass : ProbeStatus.Warn,
                    unsetDetail,
                    unsetRemediation);
                yield break;
            }

            yield return new ProbeResult($"{envVar} env",
                ProbeStatus.Pass,
                val!);

            // Compute the directory probe in a try/catch (C# disallows yield inside
            // try-with-catch — so build the result first, then yield it).
            ProbeResult dirResult;
            try
            {
                if (Directory.Exists(val))
                {
                    dirResult = new ProbeResult($"{envVar} directory",
                        ProbeStatus.Pass,
                        "exists on disk");
                }
                else
                {
                    dirResult = new ProbeResult($"{envVar} directory",
                        ProbeStatus.Fail,
                        $"path does not exist on disk: {val}",
                        $"Correct the {envVar} env var, or run `lacerte_set_context` with a valid path.");
                }
            }
            catch (Exception ex)
            {
                dirResult = new ProbeResult($"{envVar} directory",
                    ProbeStatus.Fail,
                    "probe threw: " + ex.Message,
                    "Verify the path is accessible and the user has read permission.");
            }
            yield return dirResult;
        }

        /// <summary>
        /// P15-031 / P15-034: enumerate FieldAccessMap base shards + per-year overlays
        /// next to the running assembly. Without base shards every write returns Unknown
        /// access (the loader silently degrades to "no entry" — the operator deserves a
        /// direct signal).
        ///
        /// <para>
        /// P15-034 wired this probe through the schema-side helpers
        /// (<see cref="FieldAccessMapLoader.GetShardCounts"/> +
        /// <see cref="FieldAccessMapLoader.GetSchemaRoot"/>) so doctor and the loader
        /// look at the exact same directory and use the exact same enumeration filter —
        /// no more drift between "what doctor counts" and "what the loader can find."
        /// </para>
        ///
        /// <para>
        /// Severity is <b>Warn</b> (not Fail) at zero modules: the v1 base shards ship
        /// empty by design (Phase-9 introspection pending per Todo.md), so Fail would
        /// break the green doctor today. When Phase-9 lands and shards actually populate,
        /// upgrade this to Fail at zero. The detail reports both module + overlay counts
        /// AND the resolved root path so an operator can paste it into a bug report and
        /// see at a glance whether the lookup directory is where they expect.
        /// </para>
        /// </summary>
        private static ProbeResult ProbeFieldAccessMapShards()
        {
            try
            {
                var root = FieldAccessMapLoader.GetSchemaRoot();
                var (modules, overlays) = FieldAccessMapLoader.GetShardCounts();

                if (modules == 0)
                {
                    // Warn (not Fail) — see XML doc above. Surface the root path so the
                    // operator can confirm the loader is looking where they expect.
                    return new ProbeResult("FieldAccessMap shards",
                        ProbeStatus.Warn,
                        $"0 module shards under {root}\\base (every WriteField returns 9012 UnknownFieldAccess until populated); overlays={overlays}",
                        null);
                }
                return new ProbeResult("FieldAccessMap shards",
                    ProbeStatus.Pass,
                    $"{modules} module(s), {overlays} overlay(s) under {root}");
            }
            catch (Exception ex)
            {
                return new ProbeResult("FieldAccessMap shards",
                    ProbeStatus.Fail,
                    "probe threw: " + ex.Message,
                    "Investigate the Schema/FieldAccessMap/ directory accessibility.");
            }
        }

        /// <summary>
        /// P15-031: distinguish "no AppID persisted" from "AppID stored but the value
        /// is not a parseable GUID". The plain AppIdStore.Get() collapses both into
        /// null, which would mislead an operator who DID run register but hit a
        /// corruption — they'd see "not persisted" and re-mint, masking the bug.
        ///
        /// <para>
        /// R6-05: the P16-058 Win32NT-Live "register is a Phase 9 stub" branches are
        /// deleted per their own PHASE-9-CLEANUP note — <c>lacerte-mcp register</c>
        /// now actually runs <c>ITPRegistration.Register()</c> (via
        /// <c>Bridge/Registration.cs</c>), so the canonical Absent remediation is
        /// simply "run register" and the Ok branch is a plain Pass again.
        /// </para>
        /// </summary>
        private static ProbeResult ProbeAppId()
        {
            var diag = AppIdStore.GetDiagnostic(out var id, out var raw);

            switch (diag)
            {
                case AppIdStore.AppIdDiagnostic.Ok:
                    return new ProbeResult("AppID GUID",
                        ProbeStatus.Pass,
                        $"persisted ({AppIdStore.Mask(id)}) via {(AppIdStore.UsesCredentialManager ? "Credential Manager" : "file")}");
                case AppIdStore.AppIdDiagnostic.ParseFailed:
                    return new ProbeResult("AppID GUID",
                        ProbeStatus.Fail,
                        $"persisted value is not a valid GUID (length={raw?.Length ?? 0})",
                        "Run `lacerte-mcp unregister --confirm` followed by `lacerte-mcp register` to re-mint a fresh AppID.");
                case AppIdStore.AppIdDiagnostic.Absent:
                default:
                    return new ProbeResult("AppID GUID",
                        ProbeStatus.Fail,
                        "not persisted",
                        "Run `lacerte-mcp register` to mint and store one (the Intuit consent ceremony runs as part of it on Windows + Lacerte).");
            }
        }
    }
}
