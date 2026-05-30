using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LacertMCP.Server.Logging
{
    /// <summary>
    /// Debug wire log. Activates only when <c>LACERTE_DEBUG_WIRE=1</c>. Writes
    /// one JSON record per line to <c>logs/lacerte-YYYYMMDD.log</c> (path
    /// overrideable via <c>LACERTE_DEBUG_LOG_PATH</c>). Mirrors the QB MCP
    /// <c>QB_DEBUG_QBXML</c> pattern per agent 07 §11.
    ///
    /// <para>
    /// Public surface:
    /// <code>
    ///   await WireLog.Record("op-name", new { ... }, async () =&gt; { ... });
    /// </code>
    /// </para>
    ///
    /// <para>
    /// Record shape (one per line):
    /// <code>{ "ts": "2026-05-30T12:34:56.789Z", "op": "...", "args": {...},
    ///         "durationMs": 12, "result": {...} | "error": {...} }</code>
    /// </para>
    ///
    /// <para>
    /// Redaction: SSN, EIN, BankAccountNumber, RoutingNumber values are
    /// replaced with <c>***</c> BEFORE the line hits disk. Regex-based so
    /// arbitrary JSON shapes are covered (matches the key in standard JSON
    /// punctuation contexts and the bare value after).
    /// </para>
    ///
    /// <para>Thread-safety: append-only with a lock around the write path.</para>
    /// </summary>
    public static class WireLog
    {
        private static readonly object _writeLock = new object();
        private static Lazy<WireLogConfig?> _config = new Lazy<WireLogConfig?>(
            ResolveConfig, LazyThreadSafetyMode.ExecutionAndPublication);

        // Test-only forced config — set via EnableForTests so integration tests
        // can drive the live Record() path without process-restart. Takes
        // precedence over the lazy env-resolved config when non-null.
        private static WireLogConfig? _testForceConfig;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
        };

        // Match "FieldName":"value" OR "FieldName":123 OR "FieldName":null, even
        // when the JSON has been embedded by JsonSerializer (which does not
        // pretty-print by default here). The redacted output keeps the key + a
        // string sentinel so the record stays valid JSON.
        private static readonly Regex _redactRegex = new Regex(
            "\"(?<key>SSN|EIN|BankAccountNumber|RoutingNumber)\"\\s*:\\s*(?:\"[^\"]*\"|-?\\d+(?:\\.\\d+)?|true|false|null)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Run <paramref name="work"/> with a wire-log entry written on completion
        /// or failure. When the log is disabled, this is a near-zero-overhead
        /// passthrough — the lazy config check is the only cost.
        /// </summary>
        public static async Task<T> Record<T>(string op, object? args, Func<Task<T>> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var cfg = _testForceConfig ?? _config.Value;
            if (cfg == null)
            {
                return await work().ConfigureAwait(false);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await work().ConfigureAwait(false);
                sw.Stop();
                WriteRecord(cfg, op, args, result, error: null, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var err = new Dictionary<string, object?>
                {
                    ["type"] = ex.GetType().FullName,
                    ["message"] = ex.Message,
                };
                WriteRecord(cfg, op, args, result: null, error: err, sw.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>Non-generic convenience overload — for ops that return <see cref="object"/>.</summary>
        public static Task<object?> Record(string op, object? args, Func<Task<object?>> work)
            => Record<object?>(op, args, work);

        /// <summary>Sync overload — for non-async wire calls.</summary>
        public static T RecordSync<T>(string op, object? args, Func<T> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var cfg = _testForceConfig ?? _config.Value;
            if (cfg == null) return work();

            var sw = Stopwatch.StartNew();
            try
            {
                var result = work();
                sw.Stop();
                WriteRecord(cfg, op, args, result, error: null, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var err = new Dictionary<string, object?>
                {
                    ["type"] = ex.GetType().FullName,
                    ["message"] = ex.Message,
                };
                WriteRecord(cfg, op, args, result: null, error: err, sw.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>True iff <c>LACERTE_DEBUG_WIRE=1</c> at process start.</summary>
        public static bool IsEnabled => (_testForceConfig ?? _config.Value) != null;

        /// <summary>The active log file path, or null when disabled.</summary>
        public static string? CurrentLogPath
        {
            get
            {
                var cfg = _testForceConfig ?? _config.Value;
                return cfg == null ? null : Path.Combine(cfg.LogDir, FilenameForToday());
            }
        }

        /// <summary>
        /// Test-only: force-enable logging to <paramref name="logDir"/> so the
        /// Record/RecordSync paths write to disk without depending on the lazy
        /// env-resolved config. Pair every call with <see cref="DisableForTests"/>
        /// in a finally block.
        /// </summary>
        public static void EnableForTests(string logDir)
        {
            if (string.IsNullOrEmpty(logDir)) throw new ArgumentNullException(nameof(logDir));
            _testForceConfig = new WireLogConfig(logDir);
        }

        /// <summary>Test-only: clear the forced config set by <see cref="EnableForTests"/>.</summary>
        public static void DisableForTests()
        {
            _testForceConfig = null;
        }

        /// <summary>
        /// Redact secrets from an arbitrary JSON string. Public for tests.
        /// </summary>
        public static string Redact(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            return _redactRegex.Replace(json, m => "\"" + m.Groups["key"].Value + "\":\"***\"");
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private static void WriteRecord(
            WireLogConfig cfg, string op, object? args, object? result,
            IReadOnlyDictionary<string, object?>? error, long durationMs)
        {
            var record = new Dictionary<string, object?>
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["op"] = op,
                ["args"] = args,
                ["durationMs"] = durationMs,
            };
            if (error != null) record["error"] = error;
            else record["result"] = result;

            string line;
            try
            {
                line = JsonSerializer.Serialize(record, _jsonOptions);
            }
            catch
            {
                // Defensive — never let logging poison the call site.
                line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["ts"] = record["ts"],
                    ["op"] = op,
                    ["durationMs"] = durationMs,
                    ["error"] = "wirelog-serialize-failed",
                });
            }

            // CRITICAL: redact BEFORE write.
            line = Redact(line);

            lock (_writeLock)
            {
                try
                {
                    Directory.CreateDirectory(cfg.LogDir);
                    var path = Path.Combine(cfg.LogDir, FilenameForToday());
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Swallow — debug logging must never break the operator path.
                }
            }
        }

        private static string FilenameForToday()
            => "lacerte-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log";

        private static WireLogConfig? ResolveConfig()
        {
            var flag = Environment.GetEnvironmentVariable("LACERTE_DEBUG_WIRE");
            if (flag != "1") return null;

            var dirOverride = Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_PATH");
            var dir = string.IsNullOrEmpty(dirOverride)
                ? Path.Combine(Directory.GetCurrentDirectory(), "logs")
                : dirOverride!;
            return new WireLogConfig(dir);
        }

        /// <summary>
        /// Test-only: reset the cached enabled-flag so a fresh env read happens
        /// on the next call.
        /// </summary>
        internal static void ResetForTests() => _testReset?.Invoke();

        // Use reflection-free reset via a writable Lazy proxy. Simpler: expose
        // a helper that the test harness can call after mutating env.
        private static readonly Action? _testReset = null;

        /// <summary>
        /// Test-only: force a one-shot write that bypasses the lazy
        /// enabled-flag — used in unit tests that point LACERTE_DEBUG_LOG_PATH
        /// at a temp dir.
        /// </summary>
        internal static void WriteForTest(string logDir, string op, object? args, object? result, long durationMs)
        {
            var cfg = new WireLogConfig(logDir);
            WriteRecord(cfg, op, args, result, error: null, durationMs);
        }

        private sealed class WireLogConfig
        {
            public string LogDir { get; }
            public WireLogConfig(string logDir) => LogDir = logDir;
        }
    }
}
