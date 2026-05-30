using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// Redaction: covers SSN/EIN/BankAccountNumber/RoutingNumber and a
    /// broader PII keyset (TaxpayerSSN, SpouseSSN, DependentSSN, TIN, ITIN,
    /// PTIN, EmployerEIN, FederalID, FedEIN, DateOfBirth/DOB/BirthDate,
    /// EmailID/Email, HomePhone/WorkPhone/CellPhone/PhoneNumber,
    /// FirstName/LastName/MiddleName, Address1/Address2/StreetAddress, City,
    /// ZipCode/Zip, Description, rawError, reason). Values are replaced with
    /// the literal <c>"***"</c> BEFORE the line hits disk. JsonNode-walking
    /// redactor covers scalars, arrays, and nested objects; falls back to a
    /// regex for malformed-JSON inputs.
    /// </para>
    ///
    /// <para>
    /// A second pass targets the WriteField pattern
    /// <c>{ "field": "&lt;name&gt;", "value": &lt;secret&gt; }</c>: when
    /// <c>field</c> is one of the known PII field codes (Lacerte
    /// DATA1-Individual fields that hold SSN/DOB/email/etc.), the sibling
    /// <c>value</c> is redacted regardless of its JSON shape.
    /// </para>
    ///
    /// <para>Thread-safety: a cached StreamWriter per logfile path keeps the
    /// file open across records (perf — avoids the per-call open/close
    /// syscall cycle of File.AppendAllText). All write paths take
    /// <c>_writeLock</c>.</para>
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

        // Cached StreamWriter per logfile path. The path includes the UTC
        // date so a new path naturally appears at midnight rollover and a new
        // writer is opened then. Writers stay open across records to avoid
        // the per-call open/seek/write/close cycle of File.AppendAllText.
        // Guarded by _writeLock. Disposed on ProcessExit / DisableForTests.
        private static readonly Dictionary<string, StreamWriter> _openWriters
            = new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);

        private static int _processExitHookInstalled = 0;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
        };

        // The set of JSON property names whose values are PII or otherwise
        // sensitive and must be replaced wholesale (scalar, array, or
        // nested-object subtree).
        private static readonly HashSet<string> _sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Bank
            "BankAccountNumber", "RoutingNumber",
            // SSN/TIN/EIN variants
            "SSN", "TaxpayerSSN", "SpouseSSN", "DependentSSN",
            "TIN", "ITIN", "PTIN",
            "EIN", "EmployerEIN", "FederalID", "FedEIN",
            // DOB
            "DateOfBirth", "DOB", "BirthDate",
            // Contact
            "EmailID", "Email",
            "HomePhone", "WorkPhone", "CellPhone", "PhoneNumber",
            // Identity
            "FirstName", "LastName", "MiddleName",
            // Address
            "Address1", "Address2", "StreetAddress", "City", "ZipCode", "Zip",
            // Free-form notes (often contain SSN-bearing prose)
            "Description",
            // Exception/error contexts that may carry SDK-echoed taxpayer data
            "rawError", "reason",
        };

        // Lacerte DATA1-Individual field codes that hold PII. When a record
        // logs `{"field":"C1_15","value":"111-22-3333"}` the key-name-based
        // redactor cannot catch it (the secret is keyed by literal "value"),
        // so we explicitly scrub the sibling value when `field` ∈ this set.
        // Sourced from the Lacerte SDK Structure Spec §DATA1-Individual.
        private static readonly HashSet<string> _piiFieldCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // DATA1 (module 1, Individual)
            "C1_14",  // SpouseSSN
            "C1_15",  // TaxpayerSSN
            "C1_19",  // DateOfBirth
            "C1_21",  // SpouseDateOfBirth
            "C1_27",  // HomePhone
            "C1_28",  // WorkPhone
            "C1_29",  // EmailID
            "C1_32",  // CellPhone
            "C1_71",  // BankAccountNumber
            "C1_72",  // RoutingNumber
        };

        // Fallback regex: used only when the input is not parseable JSON.
        // Matches "Key":"value"|number|true|false|null for the scalar-only
        // shapes that legitimate JSON serializers emit; the JsonNode walker
        // handles arrays + nested objects above.
        private static readonly Regex _redactRegex = new Regex(
            "\"(?<key>SSN|TaxpayerSSN|SpouseSSN|DependentSSN|TIN|ITIN|PTIN|EIN|EmployerEIN|FederalID|FedEIN|BankAccountNumber|RoutingNumber|DateOfBirth|DOB|BirthDate|EmailID|Email|HomePhone|WorkPhone|CellPhone|PhoneNumber|FirstName|LastName|MiddleName|Address1|Address2|StreetAddress|City|ZipCode|Zip|Description|rawError|reason)\"\\s*:\\s*(?:\"[^\"]*\"|-?\\d+(?:\\.\\d+)?|true|false|null)",
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

        /// <summary>
        /// Test-only: clear the forced config set by <see cref="EnableForTests"/>
        /// and flush+dispose any cached writer rooted under that dir so the
        /// test can delete the temp directory without a sharing violation.
        /// </summary>
        public static void DisableForTests()
        {
            var cfg = _testForceConfig;
            _testForceConfig = null;
            if (cfg != null)
            {
                FlushAndCloseWritersUnder(cfg.LogDir);
            }
        }

        /// <summary>
        /// Test-only: flush + close any cached StreamWriter rooted under
        /// <paramref name="logDir"/> so a test can call
        /// <c>File.ReadAllLines</c> on the logfile WITHOUT a Windows
        /// sharing violation (the writer holds the handle open with
        /// FileShare.ReadWrite, but <c>File.ReadAllLines</c> opens with
        /// the more restrictive FileShare.Read). Production callers do not
        /// read their own log file so this is a test-only seam.
        /// </summary>
        public static void FlushForTests(string logDir)
        {
            if (string.IsNullOrEmpty(logDir)) return;
            FlushAndCloseWritersUnder(logDir);
        }

        /// <summary>
        /// Redact secrets from an arbitrary JSON string. Public for tests.
        /// Parses the input as JSON and recursively replaces sensitive
        /// values (scalar, array, or nested object) with <c>"***"</c>.
        /// Falls back to a regex pass when the input is not valid JSON.
        /// </summary>
        public static string Redact(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;

            // First try the JsonNode walker — covers arrays, nested objects,
            // and whitespace-padded shapes that the regex can't reach.
            try
            {
                var node = JsonNode.Parse(json);
                if (node != null)
                {
                    RedactNode(node);
                    ScrubFieldValuePairs(node);
                    return node.ToJsonString(_jsonOptions);
                }
            }
            catch (JsonException)
            {
                // Fall through to the regex pass below.
            }
            catch (InvalidOperationException)
            {
                // Defensive — JsonNode can throw on malformed numerics in some shapes.
            }

            // Fallback: regex pass on the raw text. Only catches scalar
            // values; this is intentionally limited because the structured
            // walker is the primary control path.
            return _redactRegex.Replace(json, m => "\"" + m.Groups["key"].Value + "\":\"***\"");
        }

        // -----------------------------------------------------------------
        // Redaction helpers
        // -----------------------------------------------------------------

        private static void RedactNode(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    // Materialize the property names first to avoid mutating
                    // the collection while iterating.
                    var names = obj.Select(kv => kv.Key).ToList();
                    foreach (var name in names)
                    {
                        if (_sensitiveKeys.Contains(name))
                        {
                            // Replace the slot with the *** sentinel. JsonObject
                            // detaches the prior child automatically on slot reassignment.
                            obj[name] = JsonValue.Create("***");
                            continue;
                        }
                        var child = obj[name];
                        if (child is JsonObject || child is JsonArray)
                        {
                            // Recurse in-place — no detach/reattach needed.
                            RedactNode(child!);
                        }
                    }
                    return;
                }
                case JsonArray arr:
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var child = arr[i];
                        if (child is JsonObject || child is JsonArray)
                        {
                            RedactNode(child);
                        }
                    }
                    return;
                }
                default:
                    return;
            }
        }

        // Second pass: when we see a sibling pair {"field":"<known-PII-code>", "value": <anything>}
        // within the same object, redact the sibling "value". Handles the
        // WriteField shape where the PII is keyed by the literal "value"
        // and the field-code carries no semantic info to the regex.
        private static void ScrubFieldValuePairs(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    if (obj.TryGetPropertyValue("field", out var fieldNode)
                        && fieldNode is JsonValue fieldVal
                        && fieldVal.TryGetValue<string>(out var fieldCode)
                        && !string.IsNullOrEmpty(fieldCode)
                        && _piiFieldCodes.Contains(fieldCode!)
                        && obj.ContainsKey("value"))
                    {
                        obj["value"] = JsonValue.Create("***");
                    }
                    var names = obj.Select(kv => kv.Key).ToList();
                    foreach (var name in names)
                    {
                        var child = obj[name];
                        if (child is JsonObject || child is JsonArray)
                        {
                            ScrubFieldValuePairs(child);
                        }
                    }
                    return;
                }
                case JsonArray arr:
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var child = arr[i];
                        if (child is JsonObject || child is JsonArray)
                        {
                            ScrubFieldValuePairs(child);
                        }
                    }
                    return;
                }
                default:
                    return;
            }
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
                    var writer = GetOrOpenWriterLocked(path);
                    writer.WriteLine(line);
                    // AutoFlush=true on the writer guarantees the line is
                    // pushed through the StreamWriter buffer to the FileStream
                    // on every WriteLine; the FileStream itself flushes to OS
                    // on every write since we don't buffer it.
                }
                catch
                {
                    // Swallow — debug logging must never break the operator path.
                    // Drop any cached writer for the target path so the next
                    // call re-opens cleanly (covers transient AV/lock churn).
                    try
                    {
                        var path = Path.Combine(cfg.LogDir, FilenameForToday());
                        if (_openWriters.TryGetValue(path, out var stale))
                        {
                            try { stale.Dispose(); } catch { /* swallow */ }
                            _openWriters.Remove(path);
                        }
                    }
                    catch { /* swallow */ }
                }
            }
        }

        // Must be called under _writeLock.
        private static StreamWriter GetOrOpenWriterLocked(string path)
        {
            if (_openWriters.TryGetValue(path, out var existing))
            {
                return existing;
            }

            // New path — close any stale writers for OTHER paths (date
            // rollover) so we don't leak FDs across midnight on a long-lived
            // process. Cheap: dict typically has 0 or 1 entries.
            if (_openWriters.Count > 0)
            {
                foreach (var stale in _openWriters.Values.ToList())
                {
                    try { stale.Dispose(); } catch { /* swallow */ }
                }
                _openWriters.Clear();
            }

            // FileShare.ReadWrite (not just Read): a concurrent reader using
            // File.ReadAllLines opens with FileAccess.Read + FileShare.Read,
            // and Windows requires the existing handle's share to include
            // the reader's requested access. Without ReadWrite, the read
            // throws IOException 'in use by another process'.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            _openWriters[path] = writer;

            EnsureProcessExitHook();
            return writer;
        }

        private static void EnsureProcessExitHook()
        {
            if (Interlocked.Exchange(ref _processExitHookInstalled, 1) == 0)
            {
                try
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                    {
                        lock (_writeLock)
                        {
                            foreach (var w in _openWriters.Values.ToList())
                            {
                                try { w.Dispose(); } catch { /* swallow */ }
                            }
                            _openWriters.Clear();
                        }
                    };
                }
                catch
                {
                    // If hook install fails, reset the flag so a future write
                    // path can try again — but don't break the write itself.
                    Interlocked.Exchange(ref _processExitHookInstalled, 0);
                }
            }
        }

        private static void FlushAndCloseWritersUnder(string rootDir)
        {
            string? normalizedRoot = null;
            try
            {
                normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                // If the path can't be normalized, fall back to substring match.
                normalizedRoot = rootDir;
            }

            lock (_writeLock)
            {
                var toRemove = new List<string>();
                foreach (var kv in _openWriters)
                {
                    string fullPath;
                    try { fullPath = Path.GetFullPath(kv.Key); }
                    catch { fullPath = kv.Key; }

                    if (fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        try { kv.Value.Dispose(); } catch { /* swallow */ }
                        toRemove.Add(kv.Key);
                    }
                }
                foreach (var k in toRemove) _openWriters.Remove(k);
            }
        }

        private static string FilenameForToday()
            => "lacerte-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log";

        private static WireLogConfig? ResolveConfig()
        {
            var flag = Environment.GetEnvironmentVariable("LACERTE_DEBUG_WIRE");
            if (flag != "1") return null;

            // Default location — ./logs. Always considered safe.
            var defaultDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");

            var dirOverride = Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_PATH");
            if (string.IsNullOrEmpty(dirOverride))
            {
                return new WireLogConfig(defaultDir);
            }

            // P14-049 — defensive validation of LACERTE_DEBUG_LOG_PATH. Same
            // threat model as the dataPath gate: a hostile env value could
            // coerce Directory.CreateDirectory into provisioning a directory
            // anywhere the process can write. ValidateLogDir applies the
            // control-char / '..' / UNC / rooting checks and confirms the
            // canonical path is under USERPROFILE, TEMP, or LACERTE_DATA_ROOT.
            // On rejection we silently fall back to the default ./logs dir —
            // logging policy (see the swallow comment in WriteRecord) is that
            // debug logging must never break the operator path, so a throw
            // here is forbidden.
            var validated = LacertMCP.Server.Session.PathValidation.ValidateLogDir(dirOverride);
            return new WireLogConfig(validated ?? defaultDir);
        }

        /// <summary>
        /// Test-only: force a one-shot write that bypasses the lazy
        /// enabled-flag — used in unit tests that point LACERTE_DEBUG_LOG_PATH
        /// at a temp dir.
        /// </summary>
        internal static void WriteForTest(string logDir, string op, object? args, object? result, long durationMs)
        {
            var cfg = new WireLogConfig(logDir);
            WriteRecord(cfg, op, args, result, error: null, durationMs);
            // Ensure the file handle isn't held open past the call boundary
            // for one-shot probe writes — keeps WriteForTest semantics
            // identical to the prior File.AppendAllText behavior so the
            // existing test harness can read the file immediately after.
            FlushAndCloseWritersUnder(logDir);
        }

        private sealed class WireLogConfig
        {
            public string LogDir { get; }
            public WireLogConfig(string logDir) => LogDir = logDir;
        }
    }
}
