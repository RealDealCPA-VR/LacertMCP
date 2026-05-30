using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using LacertMCP.Cli.Storage;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp doctor</c>
    ///
    /// <para>
    /// Pre-flight probe for the operator install — mirrors the QB MCP <c>cli/doctor.ts</c> contract:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Exit 0 — every probe is green.</description></item>
    ///   <item><description>Exit 1 — at least one red (real failure).</description></item>
    ///   <item><description>Exit 2 — at least one skipped (probe inapplicable on this OS) and no reds.</description></item>
    /// </list>
    ///
    /// <para>
    /// Probes: Windows version, x64 process, .NET 4.8 installed, ODBC DSN <c>LacerteDSII</c> present
    /// (via <c>reg query</c>), Lacerte SDK install location, <c>LACERTE_DATA_PATH</c> env, AppID
    /// persisted. Every red gets a one-line remediation pointer.
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

            public char Glyph => Status switch
            {
                ProbeStatus.Pass => '✓',     // ✓
                ProbeStatus.Warn => '⚠',     // ⚠
                ProbeStatus.Fail => '✗',     // ✗
                ProbeStatus.Skipped => '-',
                _ => '?',
            };
        }

        public static Command Build()
        {
            var cmd = new Command("doctor",
                "Probe the install for prerequisites; exit 0 (green) / 1 (red) / 2 (skipped on this OS).");
            cmd.SetHandler((context) =>
            {
                context.ExitCode = Run(Console.Out);
            });
            return cmd;
        }

        public static int Run(TextWriter writer)
        {
            var results = RunAllProbes();

            writer.WriteLine("lacerte-mcp doctor");
            writer.WriteLine(new string('-', 60));
            foreach (var r in results)
            {
                writer.WriteLine($" {r.Glyph}  {r.Name}: {r.Detail}");
                if (r.Status == ProbeStatus.Fail && !string.IsNullOrEmpty(r.Remediation))
                {
                    writer.WriteLine($"      → {r.Remediation}");
                }
            }
            writer.WriteLine(new string('-', 60));

            var hasFail = false;
            var hasSkip = false;
            foreach (var r in results)
            {
                if (r.Status == ProbeStatus.Fail) hasFail = true;
                if (r.Status == ProbeStatus.Skipped) hasSkip = true;
            }

            if (hasFail)
            {
                writer.WriteLine("Result: FAIL (one or more probes red). Exit 1.");
                return 1;
            }
            if (hasSkip)
            {
                writer.WriteLine("Result: PARTIAL (one or more probes skipped — not on Windows). Exit 2.");
                return 2;
            }
            writer.WriteLine("Result: OK (all probes green). Exit 0.");
            return 0;
        }

        public static IList<ProbeResult> RunAllProbes()
        {
            var results = new List<ProbeResult>();
            var onWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

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

            // 6. LACERTE_DATA_PATH env (mode-independent — useful even off-Windows for sim)
            var dataPath = Environment.GetEnvironmentVariable("LACERTE_DATA_PATH");
            if (string.IsNullOrEmpty(dataPath))
            {
                results.Add(new ProbeResult("LACERTE_DATA_PATH env",
                    ProbeStatus.Warn,
                    "not set (will need to call lacerte_set_context before any tool that reads/writes data)",
                    null));
            }
            else
            {
                results.Add(new ProbeResult("LACERTE_DATA_PATH env",
                    ProbeStatus.Pass,
                    dataPath!));
            }

            // 7. AppID persisted
            var appId = AppIdStore.Get();
            if (appId.HasValue)
            {
                var first8 = appId.Value.ToString("D").Substring(0, 8);
                results.Add(new ProbeResult("AppID GUID",
                    ProbeStatus.Pass,
                    $"persisted ({first8}-****) via {(AppIdStore.UsesCredentialManager ? "Credential Manager" : "file")}"));
            }
            else
            {
                results.Add(new ProbeResult("AppID GUID",
                    ProbeStatus.Fail,
                    "not persisted",
                    "Run `lacerte-mcp register` to mint and store one."));
            }

            return results;
        }

        // ---- Individual probes ------------------------------------------------------

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
    }
}
