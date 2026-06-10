using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Startup
{
    /// <summary>
    /// P15-033 — startup-time validator for <c>LACERTE_*</c> environment
    /// variables. Walks each known variable, emits one warning line per
    /// invalid value (boolean-shaped vars accept only <c>"1"</c>/<c>"0"</c>/empty
    /// per project convention; year vars require a parseable int in
    /// [2018, current+1]; path vars run through
    /// <see cref="PathValidation"/>), then prints a single startup banner
    /// summarising the resolved configuration. Never throws and never
    /// fail-exits — Boundary Invariant #4 (simulation works off-Windows) means
    /// startup must not block on missing platform features. All output goes
    /// to the caller-supplied <see cref="TextWriter"/> (stderr in production),
    /// not stdout — stdout is the MCP JSON-RPC transport.
    /// </summary>
    /// <remarks>
    /// Existence of this validator is a UX safety net: today an operator who
    /// typo'd <c>LACERTE_LIVE=true</c> instead of <c>LACERTE_LIVE=1</c> sees
    /// no signal until the first tool call returns sim-mode data. After
    /// running through <see cref="Validate"/> the operator sees a stderr
    /// warning identifying the typo and the resolved mode banner.
    /// </remarks>
    public static class EnvValidator
    {
        // Strict-on/off env flags — project convention is exact-string "1"
        // means on; everything else means off. We warn when the operator
        // wrote a non-empty value that doesn't match "1" or "0" so common
        // typos like "true"/"yes"/"on" / " 1" / "1 " surface at startup.
        private static readonly string[] BooleanFlags = new[]
        {
            "LACERTE_LIVE",
            "LACERTE_SIMULATION",
            "LACERTE_DEBUG_WIRE",
            "LACERTE_ALLOW_UNC",
            "LACERTE_MCP_FORCE_FILE_STORE",
            "LACERTE_SIM_EXPIRED",
            // R6-30(b): LACERTE_DEBUG_LOG_FULL_RESULTS is a strict-"1" flag
            // (WireLog.cs reads it with == "1"). LACERTE_PARITY_RECORDING was
            // REMOVED from this list — it's a file path, not a boolean
            // (validated separately in CheckParityRecording below).
            "LACERTE_DEBUG_LOG_FULL_RESULTS",
        };

        // R6-30(b): default-ON flags — the feature is enabled unless the value
        // is the exact string "0". Distinct from BooleanFlags because the
        // "treated as disabled" warning text would be wrong here (a garbage
        // value leaves the feature ENABLED). LACERTE_IDEMPOTENCY_PERSIST
        // adopted default-ON/disable-with-0 semantics (see
        // LacerteSessionManager idempotency persistence doc block).
        private static readonly string[] DefaultOnFlags = new[]
        {
            "LACERTE_IDEMPOTENCY_PERSIST",
        };

        // R6-30(c): numeric env vars — when set, the value must be a parseable
        // non-negative integer or the code falls back to its built-in default
        // silently; warn so the typo surfaces at startup. long-ranged because
        // the *_BYTES vars can legitimately exceed Int32.
        private static readonly string[] NonNegativeIntVars = new[]
        {
            "LACERTE_HEARTBEAT_SECONDS",
            "LACERTE_BULK_WRITE_MAX",
            "LACERTE_BULK_CREATE_MAX",
            "LACERTE_RECENT_LOG_CAPACITY",
            "LACERTE_DEBUG_LOG_MAX_BYTES",
            "LACERTE_DEBUG_LOG_MAX_DIR_BYTES",
            "LACERTE_DEBUG_LOG_RETENTION_DAYS",
            "LACERTE_DEBUG_LOG_RESULT_SAMPLE",
        };

        /// <summary>
        /// Validate the process env (or the supplied dictionary) and write a
        /// startup banner. Returns the resolved <see cref="SessionMode"/> so
        /// the caller can correlate with the host build.
        /// </summary>
        /// <param name="stderr">Where to write warnings and the banner. Pass
        /// <see cref="Console.Error"/> in production.</param>
        /// <param name="env">Env-var lookup. Null reads the process env.</param>
        public static SessionMode Validate(TextWriter stderr, IDictionary<string, string?>? env = null)
        {
            if (stderr == null) throw new ArgumentNullException(nameof(stderr));
            var resolved = env ?? ReadProcessEnv();

            foreach (var flag in BooleanFlags)
            {
                CheckBooleanFlag(stderr, resolved, flag);
            }
            foreach (var flag in DefaultOnFlags)
            {
                CheckDefaultOnFlag(stderr, resolved, flag);
            }
            foreach (var name in NonNegativeIntVars)
            {
                CheckNonNegativeInt(stderr, resolved, name);
            }

            CheckDefaultTaxYear(stderr, resolved);
            CheckDataRoot(stderr, resolved);
            CheckDebugLogPath(stderr, resolved);
            CheckParityRecording(stderr, resolved);

            return WriteBanner(stderr, resolved);
        }

        private static void CheckBooleanFlag(TextWriter stderr, IDictionary<string, string?> env, string name)
        {
            var raw = Get(env, name);
            if (raw == null) return;            // unset is fine
            if (raw.Length == 0) return;        // explicit empty is fine
            if (raw == "1" || raw == "0") return; // canonical
            // Anything else (incl. whitespace-padded "1 " / lower-cased "true")
            // is "treated as disabled" per the project's strict-equality
            // ReadEnvFlag in LacerteSessionManager.
            stderr.WriteLine(
                $"[startup] {name}='{raw}' is not '1' — treated as disabled (this server uses strict '1'-means-on semantics; common alternatives like 'true'/'yes'/'on' do NOT enable it).");
        }

        /// <summary>
        /// R6-30(b): default-ON flag check. The feature is enabled unless the
        /// value is the exact string <c>"0"</c>; unset/empty/<c>"1"</c> are all
        /// fine. Anything else (e.g. <c>"false"</c>/<c>"off"</c>) does NOT
        /// disable the feature, which is the opposite of what the operator
        /// likely intended — warn loudly.
        /// </summary>
        private static void CheckDefaultOnFlag(TextWriter stderr, IDictionary<string, string?> env, string name)
        {
            var raw = Get(env, name);
            if (raw == null) return;            // unset = default ON
            if (raw.Length == 0) return;        // explicit empty = default ON
            if (raw == "1" || raw == "0") return; // canonical
            stderr.WriteLine(
                $"[startup] {name}='{raw}' is not '0' or '1' — this flag defaults to ON and is disabled only by the exact string '0'; the value '{raw}' leaves it ENABLED (common alternatives like 'false'/'off'/'no' do NOT disable it).");
        }

        /// <summary>
        /// R6-30(c): numeric env var check — when set but not a parseable
        /// non-negative integer, the reading code silently falls back to its
        /// built-in default; warn so the typo is visible at startup.
        /// </summary>
        private static void CheckNonNegativeInt(TextWriter stderr, IDictionary<string, string?> env, string name)
        {
            var raw = Get(env, name);
            if (string.IsNullOrEmpty(raw)) return;
            if (!long.TryParse(raw, out var value) || value < 0)
            {
                stderr.WriteLine(
                    $"[startup] {name}='{raw}' is not a non-negative integer — the value will not be honored as written (the reading code falls back to its built-in default or disables the feature).");
            }
        }

        /// <summary>
        /// R6-30(a): <c>LACERTE_PARITY_RECORDING</c> is a PATH to a parity
        /// recording file (see Tests/Parity/BothModes.cs), not a boolean —
        /// the old boolean check warned "'&lt;path&gt;' is not '1'" on every
        /// documented usage. Validate instead that the file exists when set.
        /// </summary>
        private static void CheckParityRecording(TextWriter stderr, IDictionary<string, string?> env)
        {
            var raw = Get(env, "LACERTE_PARITY_RECORDING");
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                if (!File.Exists(raw))
                {
                    stderr.WriteLine(
                        $"[startup] LACERTE_PARITY_RECORDING='{raw}' does not exist on disk — parity replay tests will not find the recording (this variable is a file path, not a 0/1 flag).");
                }
            }
            catch (Exception ex)
            {
                stderr.WriteLine(
                    $"[startup] LACERTE_PARITY_RECORDING='{raw}' is malformed ({ex.GetType().Name}) — ignored.");
            }
        }

        private static void CheckDefaultTaxYear(TextWriter stderr, IDictionary<string, string?> env)
        {
            var raw = Get(env, "LACERTE_DEFAULT_TAX_YEAR");
            if (string.IsNullOrEmpty(raw)) return;
            if (!int.TryParse(raw, out var year))
            {
                stderr.WriteLine($"[startup] LACERTE_DEFAULT_TAX_YEAR='{raw}' is not an integer — ignored.");
                return;
            }
            var maxYear = DateTime.UtcNow.Year + 1;
            if (year < 2018 || year > maxYear)
            {
                stderr.WriteLine($"[startup] LACERTE_DEFAULT_TAX_YEAR={year} is outside [2018, {maxYear}] — ignored.");
            }
        }

        private static void CheckDataRoot(TextWriter stderr, IDictionary<string, string?> env)
        {
            var raw = Get(env, "LACERTE_DATA_ROOT");
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                // Reuse PathValidation's canonicalization rules — same control-
                // char/UNC/'..'/rooted gates apply.
                var canonical = Path.GetFullPath(raw!);
                if (!Directory.Exists(canonical))
                {
                    stderr.WriteLine(
                        $"[startup] LACERTE_DATA_ROOT='{raw}' resolves to '{canonical}' which does not exist on disk — set-context calls will fail until the path is corrected.");
                }
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"[startup] LACERTE_DATA_ROOT='{raw}' is malformed ({ex.GetType().Name}) — ignored.");
            }
        }

        private static void CheckDebugLogPath(TextWriter stderr, IDictionary<string, string?> env)
        {
            var raw = Get(env, "LACERTE_DEBUG_LOG_PATH");
            if (string.IsNullOrEmpty(raw)) return;
            // Only warn when debug-wire is actually enabled — otherwise the
            // path is moot and surfacing a warning would be noise.
            var wireEnabled = Get(env, "LACERTE_DEBUG_WIRE") == "1";
            if (!wireEnabled) return;
            var canonical = PathValidation.ValidateLogDir(raw, env);
            if (canonical == null)
            {
                stderr.WriteLine(
                    $"[startup] LACERTE_DEBUG_LOG_PATH='{raw}' rejected (not under USERPROFILE/TEMP/LACERTE_DATA_ROOT, malformed, or UNC without LACERTE_ALLOW_UNC=1) — wire log will write to the default location.");
            }
        }

        private static SessionMode WriteBanner(TextWriter stderr, IDictionary<string, string?> env)
        {
            var sim = Get(env, "LACERTE_SIMULATION") == "1";
            var live = Get(env, "LACERTE_LIVE") == "1";
            var onWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var mode = (!sim && live && onWindows) ? SessionMode.Live : SessionMode.Simulation;

            var dataRoot = Get(env, "LACERTE_DATA_ROOT");
            var defaultTaxYear = Get(env, "LACERTE_DEFAULT_TAX_YEAR");
            var debugWire = Get(env, "LACERTE_DEBUG_WIRE") == "1";
            var logPath = Get(env, "LACERTE_DEBUG_LOG_PATH");
            var asmVersion = SafeAssemblyVersion();

            stderr.WriteLine(
                $"[startup] LacertMCP {asmVersion} mode={mode.ToString().ToLowerInvariant()} dataRoot={Nz(dataRoot)} defaultTaxYear={Nz(defaultTaxYear)} debugWire={(debugWire ? "on" : "off")} debugLogPath={Nz(logPath)}");
            return mode;
        }

        private static string Nz(string? s) => string.IsNullOrEmpty(s) ? "<unset>" : s!;

        private static string SafeAssemblyVersion()
        {
            try
            {
                var asm = typeof(EnvValidator).Assembly;
                // InformationalVersion captures the SemVer + commit suffix set
                // by Directory.Build.props (P14-040). Fall back to FileVersion
                // then "unknown" if attributes aren't populated (test builds).
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(info)) return info!;
                var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrEmpty(file)) return file!;
                return "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string? Get(IDictionary<string, string?> env, string name)
            => env.TryGetValue(name, out var v) ? v : null;

        private static IDictionary<string, string?> ReadProcessEnv()
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var raw = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry kv in raw)
            {
                if (kv.Key is string k)
                {
                    dict[k] = kv.Value?.ToString();
                }
            }
            return dict;
        }
    }
}
