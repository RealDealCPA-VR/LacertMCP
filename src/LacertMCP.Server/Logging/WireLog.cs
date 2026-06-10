using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace LacertMCP.Server.Logging
{
    /// <summary>
    /// P15-001 — selects which keyset <see cref="WireLog.Redact(string, RedactionScope)"/>
    /// applies to the input.
    ///
    /// <para>
    /// <see cref="Disk"/> uses the broad keyset (true secrets PLUS
    /// Description / City / Zip / identity names / rawError / reason) and
    /// matches the historical pre-P15-001 behavior — appropriate for
    /// archived debug logs that may leave the host.
    /// </para>
    ///
    /// <para>
    /// <see cref="Wire"/> uses a narrow keyset (true secrets only — bank
    /// numbers, every SSN/TIN/EIN variant, DOB, email, phone). Operator-
    /// facing diagnostics (reason / rawError / Description / City / Zip)
    /// survive so the LLM consumer sees actionable error messages and
    /// detail-row Descriptions on read paths.
    /// </para>
    /// </summary>
    public enum RedactionScope
    {
        /// <summary>Broad keyset — used for the on-disk debug log.</summary>
        Disk = 0,

        /// <summary>Narrow keyset — used for the MCP wire envelope.</summary>
        Wire = 1,
    }

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
    /// Redaction has two scopes (P15-001) — disk vs wire:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Disk scope</b> (default — used by
    ///   <see cref="WriteRecord"/>): broad PII keyset suitable for an
    ///   archived debug log the operator may share. Covers true secrets
    ///   plus Description / City / Zip / FirstName / LastName /
    ///   Address1+Address2 / rawError / reason — anything that could
    ///   transitively expose taxpayer identity in a log dump.</description></item>
    ///   <item><description><b>Wire scope</b> (used by
    ///   <c>WrapToolHandler</c> via
    ///   <see cref="Redact(string, RedactionScope)"/> with
    ///   <see cref="RedactionScope.Wire"/>): NARROW keyset of true secrets
    ///   only — BankAccountNumber / RoutingNumber, every SSN/TIN/EIN
    ///   variant, DateOfBirth / DOB / BirthDate, Email + phone numbers.
    ///   Description / City / Zip / reason / rawError pass through so the
    ///   LLM consuming an error envelope sees the actionable diagnostic
    ///   prefix and operator-facing detail rows surface their
    ///   Description.</description></item>
    /// </list>
    /// <para>
    /// Values are replaced with the literal <c>"***"</c> before the line
    /// hits its destination. JsonNode-walking redactor covers scalars,
    /// arrays, and nested objects; falls back to a regex for malformed-JSON
    /// inputs. The fallback regex covers the disk-scope keyset (broader
    /// alternation) because the regex pass only fires on inputs that failed
    /// JsonNode.Parse, which is the conservative path.
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
            // P16-010 — mirror WrapToolHandler's encoder so disk log lines
            // are operator-readable when opened in a text editor (accented
            // names, em-dashes, section signs render as glyphs not
            // `\u00XX`). HTML-sensitive chars still escape — see the
            // WrapToolHandler.JsonOptions comment for the rationale.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        // P15-052 — per-request correlation ID. Set by WrapToolHandler.Run at
        // tool entry and inherited by nested WireLog.Record/RecordSync calls
        // through async/await ExecutionContext flow. When non-null/non-empty,
        // WriteRecord stamps it on the JSON line as `"cid":"..."` so multi-
        // line forensics ("what wire calls did request abc12345 make?") can
        // grep one ID across the interleaved record stream of concurrent
        // tools. AsyncLocal vs ThreadLocal because tool handlers cross
        // await boundaries onto threadpool workers.
        //
        // No behavioral cost when callers don't set it: the value is null
        // for any thread that hasn't passed through WrapToolHandler, and
        // WriteRecord just omits the cid field in that case.
        public static AsyncLocal<string?> CurrentCorrelationId { get; } = new AsyncLocal<string?>();

        // P15-015 — log rotation observability seam. WrapToolHandler.Run does
        // not see rotation events, so the host (Program.cs) is the natural
        // place to wire this to its stderr ILogger. The callback fires once
        // per rotation OR prune cycle with a single human-readable line.
        // Never throws — callbacks are invoked under try/catch.
        public static Action<string>? OnRotation { get; set; }

        // P15-071 — sentinel to gate once-per-day retention pruning. Stored
        // as DateTime.UtcNow.Date so pruning piggybacks on the first write
        // of each calendar day rather than spinning up a background timer.
        // null = never pruned in this process lifetime. Guarded by
        // _writeLock.
        //
        // P18-018 follow-up — was previously `int _lastPrunedDayOfYear`
        // holding DateTime.UtcNow.DayOfYear (1..366). That key collides on
        // the year-anniversary: a process running from day-of-year 150 in
        // year N to day-of-year 150 in year N+1 saw the equality short-
        // circuit and skipped that day's prune. DateTime.Date is
        // monotonically distinguishable across years so the same gating
        // pattern survives a multi-year process lifetime.
        private static DateTime? _lastPrunedUtcDate = null;

        // P18-018 — test seam mirroring the TodayYearProvider pattern in
        // LacerteSessionManager. Lets a regression test step the clock
        // forward across a year boundary without an actual time delay.
        // Production callers must use the default (DateTime.UtcNow) — the
        // setter is `internal` and only invoked by WireLogRotationTests.
        internal static Func<DateTime> UtcNowProviderForTests { get; set; } = () => DateTime.UtcNow;

        // P15-001 — split keysets.
        //
        // _diskRedactionKeys is the BROAD keyset used for disk-log records
        // (WireLog.WriteRecord). It includes Description/City/Zip/identity
        // names/rawError/reason on top of true secrets because an archived
        // debug log may be attached to a bug report or shared off-host, and
        // any taxpayer-identifying prose should be scrubbed from that path.
        //
        // _wireRedactionKeys is the NARROW keyset used for the MCP wire
        // envelope (WrapToolHandler). It covers ONLY true secrets — bank
        // numbers, every SSN/TIN/EIN variant, DOB, email, and phone. Free
        // text (Description, reason, rawError) and address columns
        // (City/Zip) pass through so the LLM consumer sees actionable
        // diagnostics and operator-facing detail rows surface their
        // Description. This closes the P15-001 regression introduced when
        // P14-018/P14-019 widened a single keyset.
        private static readonly HashSet<string> _diskRedactionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
            // P15-019 — ODBC driver echo + internal-catch raw messages. The
            // Lacerte/FoxPro ODBC driver historically inlines the failing
            // value into its error text (e.g. "Field C1_15 value '123-45-6789'
            // rejected"); OdbcErrorMapper.Map stuffs that text under
            // "odbcMessage", and WireLog's own catch blocks (lines ~246, ~278)
            // put ex.Message under "message". Both are scrubbed wholesale at
            // disk scope.
            "odbcMessage", "message",
            // R6-12 — raw SQL text + name filters. LacerteDsnReader.Query logs
            // its full SQL under args.sql, and SqlTemplates.Data1TopWhereName
            // inlines the operator's name prefix as a quoted literal
            // (`... where C1_2 like 'Smith%'`) — taxpayer-identity prose that
            // must not land verbatim in an archived debug log. The session
            // layer likewise logs the operator's `lastNameFilter` /
            // `nameFilter` args on ListClients records. All three are
            // scrubbed wholesale at DISK scope only; the wire envelope keeps
            // them so the LLM can replay/refine diagnostic queries.
            "sql", "lastNameFilter", "nameFilter",
        };

        // P15-001 — narrow on-wire keyset. Anything not in this set
        // survives the MCP envelope unchanged so the LLM still sees
        // operator-facing diagnostics (reason / rawError / Description /
        // City / Zip).
        //
        // P15-017 follow-up — "message" was REMOVED from this set. P15-019
        // had added it on the same "ODBC driver echo" pretext as odbcMessage,
        // but "message" is a generic key that every legitimate tool result
        // uses (health probes, schema lookups, info-table responses, status
        // payloads). Keeping it here destroyed those values wholesale. The
        // narrow ODBC echo concern is still covered by "odbcMessage" (only
        // emitted by OdbcErrorMapper) and by RawError redaction on the disk
        // log. For SDK-echoed taxpayer data inside a generic Exception's
        // Message string, the InternalServerError context puts it under
        // "rawError" which is still narrow-keyset-scrubbed on disk.
        private static readonly HashSet<string> _wireRedactionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
            // P15-019 — substring leak via ODBC driver echo. The driver text
            // can carry the rejected value as a substring (e.g. "SSN 111-22-3333
            // invalid"); since the key-name redactor only matches whole values,
            // we wipe odbcMessage wholesale on the wire too. SQLSTATE remains
            // visible under the "sqlState" key so the LLM keeps its diagnostic
            // anchor.
            "odbcMessage",
        };

        // Lacerte DATA1-Individual field codes that hold PII. Consulted in
        // BOTH redaction passes:
        //
        // 1) As a KEY name in <see cref="RedactNode"/> — catches the ODBC dict-
        //    row shape `{"C1_15":"111-22-3333"}` where the row is keyed by
        //    raw column name (i.e. the Lacerte field code itself). LacerteDsnReader.Query
        //    materializes rows this way (Dictionary<string, object?> keyed by
        //    OdbcDataReader.GetName(i)), so without this branch a debug wire log
        //    of a `select * from [DATA1-Client Info]` lands SSN/DOB/bank values
        //    verbatim in the log. Per P15-018.
        //
        // 2) As the VALUE of a sibling `field` key in
        //    <see cref="ScrubFieldValuePairs"/> — catches the WriteField payload
        //    shape `{"field":"C1_15","value":"111-22-3333"}` where the secret
        //    is keyed by literal "value" and the field-code carries no
        //    semantic info to the redactor. Per P14-018.
        //
        // Sourced from the Lacerte SDK Structure Spec §DATA1-Individual.
        private static readonly HashSet<string> _piiFieldCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // DATA1 (module 1, Individual)
            "C1_3",   // Email (sim/seed convention — see SimulationStore.Seed)
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

        // R6-12 — DISK-scope-only coverage for raw-column-keyed dict rows.
        //
        // _piiFieldCodes above covers TRUE secrets (SSN/DOB/bank/email/phone)
        // and applies to BOTH scopes. But the ODBC dict-row shape
        // `{"C1_2":"Smith"}` also carries identity/address prose (first/last
        // name, entity name, address block, city, zip) under raw column
        // names — exactly the data the friendly-key disk set (FirstName /
        // LastName / Address1 / City / Zip) scrubs when keyed by friendly
        // name. There is no enumerable per-module catalog of which C{t}_N
        // codes hold names vs addresses vs statuses (the Schema shards are
        // unpopulated stubs and the spec defers the per-module catalog), so
        // we mirror the P15-058 denylist posture for the write shape: at
        // DISK scope, over-redact EVERY C{t}_N-shaped key — names, address
        // blocks, and entity-module equivalents included — EXCEPT the
        // C{t}_0 client-number column, which is the row's diagnostic anchor
        // (join key, not identity prose). Wire scope is untouched: the LLM
        // consumer still sees full rows (minus true secrets) so read tools
        // stay useful.
        //
        // Production cost is zero on the wire and over-redaction on an
        // archived debug log is strictly safer — the operator can re-derive
        // row contents by re-running the logged op.
        private static readonly Regex _rawColumnCodeRegex = new Regex(
            "^C[1-8]_[A-Z0-9]+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _rawColumnClientNumberRegex = new Regex(
            "^C[1-8]_0$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool IsDiskScrubbedRawColumnCode(string name)
            => _rawColumnCodeRegex.IsMatch(name)
               && !_rawColumnClientNumberRegex.IsMatch(name);

        // Fallback regex: used only when the input is not parseable JSON.
        // Matches "Key":"value"|number|true|false|null for the scalar-only
        // shapes that legitimate JSON serializers emit; the JsonNode walker
        // handles arrays + nested objects above. Mirrors the broad disk-scope
        // keyset PLUS the PII field codes (P15-018, generalized to every
        // C{t}_N code by R6-12 — including C{t}_0, which is acceptable
        // over-redaction on the malformed-input path) and the ODBC
        // driver-echo message keys (P15-019). R6-12 also adds sql /
        // lastNameFilter / nameFilter. The regex pass only fires on
        // malformed input, which is the conservative path — covering the
        // broader alternation here is intentional.
        private static readonly Regex _redactRegex = new Regex(
            "\"(?<key>SSN|TaxpayerSSN|SpouseSSN|DependentSSN|TIN|ITIN|PTIN|EIN|EmployerEIN|FederalID|FedEIN|BankAccountNumber|RoutingNumber|DateOfBirth|DOB|BirthDate|EmailID|Email|HomePhone|WorkPhone|CellPhone|PhoneNumber|FirstName|LastName|MiddleName|Address1|Address2|StreetAddress|City|ZipCode|Zip|Description|rawError|reason|odbcMessage|message|sql|lastNameFilter|nameFilter|C[1-8]_[A-Z0-9]+)\"\\s*:\\s*(?:\"[^\"]*\"|-?\\d+(?:\\.\\d+)?|true|false|null)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Run <paramref name="work"/> with a wire-log entry written on completion
        /// or failure. When the disk log is disabled, this still pushes the
        /// (Disk-redacted) line into the in-process <see cref="WireLogRing"/>
        /// so <c>lacerte_recent_log</c> can surface the recent tail even when
        /// <c>LACERTE_DEBUG_WIRE</c> is off — P18-026. Ring push is bounded
        /// (~50 KB at default capacity 256) and never throws.
        /// </summary>
        public static async Task<T> Record<T>(string op, object? args, Func<Task<T>> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var cfg = _testForceConfig ?? _config.Value;

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await work().ConfigureAwait(false);
                sw.Stop();
                EmitRecord(cfg, op, args, result, error: null, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                EmitRecord(cfg, op, args, result: null, error: BuildErrorRecord(ex), sw.ElapsedMilliseconds);
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

            var sw = Stopwatch.StartNew();
            try
            {
                var result = work();
                sw.Stop();
                EmitRecord(cfg, op, args, result, error: null, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                EmitRecord(cfg, op, args, result: null, error: BuildErrorRecord(ex), sw.ElapsedMilliseconds);
                throw;
            }
        }

        // P18-026 — build the redacted line once, push it to the in-process
        // ring (always), then hand off to the disk-write path only when the
        // env-gated config is non-null. Decoupling the ring from the
        // LACERTE_DEBUG_WIRE gate is the whole point of lacerte_recent_log:
        // it makes the LLM's self-diagnostic surface useful in the default
        // ops posture (debug-wire OFF) instead of only when an operator
        // pre-enabled disk logging.
        private static void EmitRecord(
            WireLogConfig? cfg, string op, object? args, object? result,
            IReadOnlyDictionary<string, object?>? error, long durationMs)
        {
            string line = BuildRedactedLine(
                op, args, result, error, durationMs,
                resultSampleLimit: cfg?.ResultSampleLimit ?? WireLogConfig.DefaultResultSampleLimit,
                fullResults: cfg?.FullResults ?? false);

            // Ring push is always-on (independent of cfg). Never throws.
            try { WireLogRing.Push(line); }
            catch { /* defensive */ }

            // Disk write only when the env-gated config is live.
            if (cfg != null)
            {
                WriteRecord(cfg, line);
            }
        }

        // P15-014 — capture ex.Data alongside type + message so the
        // AutoReconnect attempt-count stamps (Attempts / Exhausted) and any
        // other diagnostic data the catch chain accreted survive on the same
        // log line as the failed op. Without this, an exhausted-retry failure
        // was observationally indistinguishable from a first-attempt failure
        // in the wire log stream. The downstream Redact pass scrubs PII from
        // the data dict via the existing keyset walker.
        private static Dictionary<string, object?> BuildErrorRecord(Exception ex)
        {
            var err = new Dictionary<string, object?>
            {
                ["type"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            };
            if (ex.Data.Count > 0)
            {
                var data = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry de in ex.Data)
                {
                    if (de.Key is string k)
                    {
                        data[k] = de.Value;
                    }
                }
                if (data.Count > 0)
                {
                    err["data"] = data;
                }
            }
            return err;
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
        /// P16-066 — the path WireLog WOULD write to today, computed unconditionally
        /// of <c>LACERTE_DEBUG_WIRE</c>. When logging is active, this matches
        /// <see cref="CurrentLogPath"/>. When logging is disabled, this returns the
        /// path the operator would see if they set <c>LACERTE_DEBUG_WIRE=1</c> and
        /// restarted — applying the same default-dir / <c>LACERTE_DEBUG_LOG_PATH</c>
        /// override / <c>PathValidation</c> chain as <see cref="ResolveConfig"/>.
        ///
        /// <para>
        /// Surfaced via <c>lacerte_session_status</c> so an operator hitting the
        /// 9201/9203 remediation ("inspect logs/lacerte-YYYYMMDD.log") can see the
        /// exact path the file lives at without having to enable logging first.
        /// </para>
        /// </summary>
        public static string ResolvedOrWouldBeLogPath()
        {
            // If logging is active, return the actual active path.
            var active = _testForceConfig ?? _config.Value;
            if (active != null)
            {
                return Path.Combine(active.LogDir, FilenameForToday());
            }

            // Logging disabled — replay the same dir-resolution logic as
            // ResolveConfig so the operator sees where the file WOULD land.
            var defaultDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            var dirOverride = Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_PATH");
            string dir = defaultDir;
            if (!string.IsNullOrEmpty(dirOverride))
            {
                var validated = LacertMCP.Server.Session.PathValidation.ValidateLogDir(dirOverride);
                dir = validated ?? defaultDir;
            }
            return Path.Combine(dir, FilenameForToday());
        }

        /// <summary>
        /// Test-only: force-enable logging to <paramref name="logDir"/> so the
        /// Record/RecordSync paths write to disk without depending on the lazy
        /// env-resolved config. Pair every call with <see cref="DisableForTests"/>
        /// in a finally block.
        ///
        /// <para>
        /// Optional rotation/retention overrides (P15-015 / P15-071): when a
        /// test wants to exercise the size cap or per-day prune branch it can
        /// pass small values (e.g. 1 KB / 4 KB / 1 day) without producing
        /// megabytes of test data. Null defaults to the production caps.
        /// </para>
        /// </summary>
        public static void EnableForTests(
            string logDir,
            long? maxBytes = null,
            long? maxDirBytes = null,
            int? retentionDays = null,
            int? resultSampleLimit = null,
            bool fullResults = false)
        {
            if (string.IsNullOrEmpty(logDir)) throw new ArgumentNullException(nameof(logDir));
            _testForceConfig = new WireLogConfig(
                logDir, maxBytes, maxDirBytes, retentionDays, resultSampleLimit, fullResults);
            // Reset the prune sentinel so tests can exercise the prune branch
            // deterministically without depending on the process-lifetime
            // memo from a previous test.
            lock (_writeLock)
            {
                _lastPrunedUtcDate = null;
            }
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
        ///
        /// <para>
        /// Uses the <b>disk-scope</b> keyset by default for backwards
        /// compatibility with pre-P15-001 callers. The MCP wire path
        /// (<c>WrapToolHandler</c>) must opt into
        /// <see cref="RedactionScope.Wire"/> explicitly so operator-facing
        /// diagnostics (reason / rawError / Description / City / Zip)
        /// survive the envelope.
        /// </para>
        /// </summary>
        public static string Redact(string json, RedactionScope scope = RedactionScope.Disk)
        {
            if (string.IsNullOrEmpty(json)) return json;

            var keys = scope == RedactionScope.Wire ? _wireRedactionKeys : _diskRedactionKeys;
            var diskScope = scope == RedactionScope.Disk;

            // First try the JsonNode walker — covers arrays, nested objects,
            // and whitespace-padded shapes that the regex can't reach.
            try
            {
                var node = JsonNode.Parse(json);
                if (node != null)
                {
                    RedactNode(node, keys, diskScope);
                    // The field/value sibling-pair pass targets WriteField
                    // payloads carrying PII field-codes; the relevant codes
                    // are true secrets (SSN/DOB/email/bank) so the pass
                    // applies to BOTH scopes — wire-scope error envelopes
                    // for a WriteField rejection would otherwise echo the
                    // raw SSN back at the LLM.
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
            // walker is the primary control path. The regex covers the
            // broader disk-scope alternation regardless of caller scope —
            // it only fires when JsonNode.Parse failed (malformed input),
            // where the conservative path is preferred.
            return _redactRegex.Replace(json, m => "\"" + m.Groups["key"].Value + "\":\"***\"");
        }

        // -----------------------------------------------------------------
        // P15-072 — large-result summarization
        // -----------------------------------------------------------------

        /// <summary>
        /// P15-072 — when the serialized record's <c>result</c> is a JSON
        /// array longer than <paramref name="sampleLimit"/>, replace it with
        /// <c>{"kind":"collection","count":N,"sample":[first sampleLimit items]}</c>.
        /// Operationally useful fields (op, args, durationMs, count, sample) survive
        /// while the bulk-payload write is suppressed. Returns the input
        /// unchanged when the result isn't a JSON array OR when length is
        /// at or below the threshold.
        ///
        /// <para>Exposed at internal scope for direct unit-test coverage —
        /// the summarization happens in the JSON-string layer (after the
        /// JsonSerializer pass), so a unit test can feed a record line in
        /// and assert the resulting JSON without driving the full WriteRecord
        /// pipeline.</para>
        /// </summary>
        internal static string SummarizeLargeResultArray(string line, int sampleLimit)
        {
            if (string.IsNullOrEmpty(line) || sampleLimit < 0) return line;
            try
            {
                var node = JsonNode.Parse(line);
                if (node is not JsonObject obj) return line;
                if (!obj.TryGetPropertyValue("result", out var resultNode)
                    || resultNode is not JsonArray arr)
                {
                    return line;
                }
                int count = arr.Count;
                if (count <= sampleLimit) return line;

                // Move sampleLimit items into a fresh array (detach from
                // original so reassignment doesn't fight the parent slot).
                var sample = new JsonArray();
                for (int i = 0; i < sampleLimit; i++)
                {
                    var child = arr[i];
                    if (child == null)
                    {
                        sample.Add(null);
                    }
                    else
                    {
                        // Re-parse the child via its JSON text so the
                        // node is freshly rooted under `sample` without
                        // disturbing the original's tree.
                        sample.Add(JsonNode.Parse(child.ToJsonString()));
                    }
                }
                var summary = new JsonObject
                {
                    ["kind"] = "collection",
                    ["count"] = count,
                    ["sample"] = sample,
                };
                obj["result"] = summary;
                return obj.ToJsonString(_jsonOptions);
            }
            catch
            {
                // Defensive — summarization must never poison the log line.
                return line;
            }
        }

        // -----------------------------------------------------------------
        // Redaction helpers
        // -----------------------------------------------------------------

        private static void RedactNode(JsonNode node, HashSet<string> keys, bool diskScope)
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
                        if (keys.Contains(name))
                        {
                            // Replace the slot with the *** sentinel. JsonObject
                            // detaches the prior child automatically on slot reassignment.
                            obj[name] = JsonValue.Create("***");
                            continue;
                        }
                        // P15-018 — ODBC dict-row shape `{"C1_15":"111-22-3333"}`
                        // is keyed by raw Lacerte column name (which IS the PII
                        // field code). Apply to BOTH scopes — these are true-secret
                        // slots (SpouseSSN/TaxpayerSSN/DOB/Bank/Email/Phone) so
                        // the wire-scope LLM consumer should never see them either.
                        if (_piiFieldCodes.Contains(name))
                        {
                            obj[name] = JsonValue.Create("***");
                            continue;
                        }
                        // R6-12 — at DISK scope only, scrub every other
                        // C{t}_N-shaped key (names, address block, entity-
                        // module equivalents) except the C{t}_0 client-number
                        // anchor. See the _rawColumnCodeRegex comment for the
                        // P15-058-mirroring rationale. Wire scope passes these
                        // through so read tools stay useful to the LLM.
                        if (diskScope && IsDiskScrubbedRawColumnCode(name))
                        {
                            obj[name] = JsonValue.Create("***");
                            continue;
                        }
                        var child = obj[name];
                        if (child is JsonObject || child is JsonArray)
                        {
                            // Recurse in-place — no detach/reattach needed.
                            RedactNode(child!, keys, diskScope);
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
                            RedactNode(child, keys, diskScope);
                        }
                    }
                    return;
                }
                default:
                    return;
            }
        }

        // Second pass: when we see a sibling pair {"field":"<code>", "value": <anything>}
        // within the same object, redact the sibling "value" regardless of
        // the field code or module (P15-058 — denylist semantics).
        //
        // Rationale: the curated _piiFieldCodes set only covers DATA1-Individual
        // (C1_14/15/19/etc.). Entity-module equivalents (Corp officer SSNs,
        // Partnership K-1 recipient SSNs, Fiduciary beneficiary SSNs, bank-
        // account columns across all modules) have no spec source yet — the
        // Schema/FieldAccessMap/base/*.json shards are unpopulated stubs and
        // Lacerte_MCP_Structure_Spec.md explicitly defers the per-module
        // catalog. For a debug log, over-redacting WriteField values is
        // strictly safer than guessing per module: the field code + module +
        // dryRun + result remain visible for diagnosis, and the operator can
        // re-derive the original value from their input payload.
        //
        // Production cost is zero (LACERTE_DEBUG_WIRE is off by default).
        //
        // Applies to BOTH disk and wire scopes — value-shape PII slipping
        // through a WriteField envelope is the same leak in either path.
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
                        && obj.ContainsKey("value"))
                    {
                        // P15-058 — denylist semantics: every WriteField {field, value}
                        // pair has its `value` slot scrubbed regardless of which Lacerte
                        // module/field code is involved. Pre-P15-058 this was gated on
                        // _piiFieldCodes.Contains(fieldCode), which only knew about
                        // DATA1-Individual codes and silently leaked entity-module PII.
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

        // P18-026 — Build the canonical wire-log line ONCE. The output is
        // already Disk-scope-redacted (broad keyset) and has the large-result
        // summarization applied when warranted. Same line is then pushed
        // into WireLogRing AND written to disk (when enabled) so the
        // in-process tail surfaced by lacerte_recent_log is byte-identical
        // to what landed on disk in the corresponding session.
        internal static string BuildRedactedLine(
            string op, object? args, object? result,
            IReadOnlyDictionary<string, object?>? error, long durationMs,
            int resultSampleLimit, bool fullResults)
        {
            var record = new Dictionary<string, object?>
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["op"] = op,
                ["args"] = args,
                ["durationMs"] = durationMs,
            };
            // P15-052 — stamp the per-request correlation ID when set so
            // nested log lines (CanUseNewApis, AutoReconnect, bridge sub-ops)
            // are recoverable as one causal chain by `grep "cid":"abc12345"`.
            // Field is omitted entirely when there's no active scope to keep
            // the disabled-path JSON shape unchanged.
            var cid = CurrentCorrelationId.Value;
            if (!string.IsNullOrEmpty(cid))
            {
                record["cid"] = cid;
            }
            if (error != null) record["error"] = error;
            else record["result"] = result;

            string line;
            try
            {
                line = JsonSerializer.Serialize(record, _jsonOptions);
                // P15-072 — large-result summarization. Read paths like
                // ReadTable / ReadEFStatus / ListClients ship the FULL
                // payload back through WireLog by default; on a 5000-row
                // table this writes ~2 MB per log line, balloons the log
                // file, and gives the operator no quick "this returned N
                // rows in M ms" summary. Post-process the parsed line to
                // replace any top-level result JSON array longer than
                // resultSampleLimit with a {kind, count, sample} object.
                // Opt out with LACERTE_DEBUG_LOG_FULL_RESULTS=1.
                //
                // P18-026 — applies to the ring entry too so a 5000-row
                // read doesn't blow the ring's ~50 KB budget on one entry.
                if (!fullResults && error == null)
                {
                    line = SummarizeLargeResultArray(line, resultSampleLimit);
                }
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

            // CRITICAL: redact BEFORE write. Disk scope = broad keyset
            // (includes Description / City / Zip / identity names /
            // rawError / reason) because an archived debug log may leave
            // the host.
            return Redact(line, RedactionScope.Disk);
        }

        private static void WriteRecord(WireLogConfig cfg, string line)
        {
            lock (_writeLock)
            {
                try
                {
                    Directory.CreateDirectory(cfg.LogDir);
                    var path = Path.Combine(cfg.LogDir, FilenameForToday());

                    // P15-071 — once-per-UTC-day retention prune piggybacks
                    // on a write. Cheap (single Directory.GetFiles + parse),
                    // gated by _lastPrunedUtcDate so we don't run it for
                    // every record. P18-018 — keyed on DateTime.UtcNow.Date
                    // (not DayOfYear) so year-anniversary collisions don't
                    // short-circuit the prune in a multi-year process
                    // lifetime.
                    var today = UtcNowProviderForTests().Date;
                    if (_lastPrunedUtcDate != today)
                    {
                        PruneOldLogsLocked(cfg.LogDir, cfg.RetentionDays);
                        _lastPrunedUtcDate = today;
                    }

                    // P15-015 — check size cap BEFORE acquiring the writer
                    // so a freshly-rolled-over file is the one we write to.
                    // Approximate the post-write size with line.Length + 2
                    // (CRLF terminator); over-rotating once per gigabyte is
                    // fine, and the alternative (querying FileInfo on every
                    // write) is much more expensive in the hot path.
                    var writeLen = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    RotateIfNeededLocked(cfg, path, writeLen);

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

        // P15-015 — size-cap-driven rotation. Must be called under _writeLock.
        // If the existing file at `path` would exceed cfg.MaxBytes after
        // writing `pendingLen` more bytes, close the cached writer, rename
        // the file to lacerte-YYYYMMDD-HHmmss.log, and let the next
        // GetOrOpenWriterLocked() call reopen a fresh path. Then run the
        // dir-size prune so retention stays honest after a rotation event.
        private static void RotateIfNeededLocked(WireLogConfig cfg, string path, int pendingLen)
        {
            long existingLen = 0;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) existingLen = fi.Length;
            }
            catch
            {
                // If we can't stat the file, skip rotation — the WriteLine below
                // will surface the failure (or not) via the outer swallow.
                return;
            }

            if (existingLen + pendingLen <= cfg.MaxBytes) return;

            // Close the cached writer so the file is unlocked for the rename.
            if (_openWriters.TryGetValue(path, out var existing))
            {
                try { existing.Dispose(); } catch { /* swallow */ }
                _openWriters.Remove(path);
            }

            // New target name: lacerte-YYYYMMDD-HHmmss.log (UTC). The hyphen
            // separator distinguishes a rotated file from the canonical
            // per-day file, which is lacerte-YYYYMMDD.log (no time suffix).
            var rotated = "lacerte-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log";
            var rotatedPath = Path.Combine(cfg.LogDir, rotated);

            try
            {
                if (File.Exists(path))
                {
                    // Defensive against collisions if the test/harness rotates
                    // twice in the same second (improbable but cheap to handle).
                    int salt = 0;
                    var attemptPath = rotatedPath;
                    while (File.Exists(attemptPath) && salt < 64)
                    {
                        salt++;
                        attemptPath = Path.Combine(cfg.LogDir,
                            "lacerte-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + salt + ".log");
                    }
                    File.Move(path, attemptPath);
                    rotatedPath = attemptPath;
                }
            }
            catch
            {
                // Swallow — rotation must never poison the caller. If the move
                // failed (file held open by AV/tail viewer), the next write
                // will just append to the original file.
                return;
            }

            // Dir-size prune always runs after a rotation so MaxDirBytes
            // is honored even when retention-days alone wouldn't catch.
            long pruned = PruneToMaxDirBytesLocked(cfg.LogDir, cfg.MaxDirBytes, exclude: path);

            // Observability — single line per rotation event. Swallow any
            // callback failure to honor the never-throw policy.
            try
            {
                var cb = OnRotation;
                if (cb != null)
                {
                    var msg = string.Format(
                        "WireLog rotated {0} → {1} (size cap {2} bytes){3}",
                        path, rotatedPath, cfg.MaxBytes,
                        pruned > 0 ? string.Format(", pruned {0} bytes from dir to fit MaxDirBytes={1}", pruned, cfg.MaxDirBytes) : "");
                    cb(msg);
                }
            }
            catch { /* swallow */ }
        }

        // P15-071 — delete lacerte-YYYYMMDD*.log files whose embedded date is
        // older than UtcNow.Date - retentionDays. Must be called under
        // _writeLock. Tolerates and skips files with unrecognized names.
        private static void PruneOldLogsLocked(string logDir, int retentionDays)
        {
            string[] files;
            try
            {
                files = Directory.Exists(logDir)
                    ? Directory.GetFiles(logDir, "lacerte-*.log")
                    : Array.Empty<string>();
            }
            catch { return; }

            var cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
            int deleted = 0;
            foreach (var f in files)
            {
                if (!TryParseLogFileDate(f, out var fileDate)) continue;
                if (fileDate >= cutoff) continue;
                try
                {
                    File.Delete(f);
                    deleted++;
                }
                catch { /* swallow — file may be held open by a tail viewer */ }
            }

            if (deleted > 0)
            {
                try
                {
                    var cb = OnRotation;
                    if (cb != null)
                    {
                        cb(string.Format(
                            "WireLog pruned {0} stale files (older than {1} days) from {2}",
                            deleted, retentionDays, logDir));
                    }
                }
                catch { /* swallow */ }
            }
        }

        // P15-015 — delete oldest files until total dir size fits under
        // maxDirBytes. Returns bytes freed. The `exclude` path (typically the
        // file we just rotated INTO, which is brand-new and empty) is never
        // pruned even if it would otherwise be selected. Must be called under
        // _writeLock.
        private static long PruneToMaxDirBytesLocked(string logDir, long maxDirBytes, string? exclude = null)
        {
            string[] files;
            try
            {
                files = Directory.Exists(logDir)
                    ? Directory.GetFiles(logDir, "lacerte-*.log")
                    : Array.Empty<string>();
            }
            catch { return 0; }

            var infos = new List<FileInfo>(files.Length);
            long totalBytes = 0;
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    infos.Add(fi);
                    totalBytes += fi.Length;
                }
                catch { /* skip */ }
            }

            if (totalBytes <= maxDirBytes) return 0;

            // Sort oldest first so we delete the least-useful entries.
            infos.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            long freed = 0;
            foreach (var fi in infos)
            {
                if (totalBytes <= maxDirBytes) break;
                if (exclude != null && string.Equals(fi.FullName, exclude, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var len = fi.Length;
                    File.Delete(fi.FullName);
                    totalBytes -= len;
                    freed += len;
                }
                catch { /* swallow */ }
            }
            return freed;
        }

        // P15-071 — parse the YYYYMMDD or YYYYMMDD-HHmmss segment out of a
        // lacerte-YYYYMMDD*.log filename. Returns true and the parsed date
        // when the filename matches; false otherwise (caller should skip).
        private static bool TryParseLogFileDate(string fullPath, out DateTime date)
        {
            date = default;
            try
            {
                var name = Path.GetFileNameWithoutExtension(fullPath); // strips ".log"
                if (string.IsNullOrEmpty(name) || !name.StartsWith("lacerte-", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var tail = name.Substring("lacerte-".Length);
                // tail is "YYYYMMDD" or "YYYYMMDD-HHmmss" or "YYYYMMDD-HHmmss-<salt>"
                if (tail.Length < 8) return false;
                var datePart = tail.Substring(0, 8);
                return DateTime.TryParseExact(
                    datePart,
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out date);
            }
            catch
            {
                return false;
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

            // P15-015 + P15-071 — read optional rotation/retention env vars.
            // Invalid values silently fall back to defaults (matches the
            // never-throw-from-debug-logging policy below).
            long? maxBytes = TryParseLong(Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_MAX_BYTES"));
            long? maxDirBytes = TryParseLong(Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_MAX_DIR_BYTES"));
            int? retentionDays = TryParseInt(Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_RETENTION_DAYS"));
            // P15-072 — result-shape summarization env vars. Default
            // sample limit = 5 keeps log lines bounded on multi-thousand-
            // row reads while preserving operationally useful signal.
            int? resultSampleLimit = TryParseInt(Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_RESULT_SAMPLE"));
            bool fullResults = Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_FULL_RESULTS") == "1";

            var dirOverride = Environment.GetEnvironmentVariable("LACERTE_DEBUG_LOG_PATH");
            if (string.IsNullOrEmpty(dirOverride))
            {
                return new WireLogConfig(defaultDir, maxBytes, maxDirBytes, retentionDays, resultSampleLimit, fullResults);
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
            return new WireLogConfig(validated ?? defaultDir, maxBytes, maxDirBytes, retentionDays, resultSampleLimit, fullResults);
        }

        private static long? TryParseLong(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? v : (long?)null;
        }

        private static int? TryParseInt(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? v : (int?)null;
        }

        /// <summary>
        /// Test-only: force a one-shot write that bypasses the lazy
        /// enabled-flag — used in unit tests that point LACERTE_DEBUG_LOG_PATH
        /// at a temp dir.
        ///
        /// <para>
        /// P15-015 — if an active <see cref="EnableForTests"/> config exists
        /// AND its LogDir matches <paramref name="logDir"/>, reuse the test
        /// config so size-cap/retention overrides drive the rotation branch
        /// for the probe write. Otherwise (legacy callers + cross-dir tests)
        /// build a fresh default-cap config.
        /// </para>
        /// </summary>
        internal static void WriteForTest(string logDir, string op, object? args, object? result, long durationMs)
        {
            WireLogConfig cfg;
            var existing = _testForceConfig;
            if (existing != null && string.Equals(
                Path.GetFullPath(existing.LogDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(logDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                cfg = existing;
            }
            else
            {
                cfg = new WireLogConfig(logDir);
            }
            // P18-026 — same BuildRedactedLine path as production so the
            // test seam exercises the now-shared rendering helper.
            var line = BuildRedactedLine(
                op, args, result, error: null, durationMs,
                resultSampleLimit: cfg.ResultSampleLimit,
                fullResults: cfg.FullResults);
            try { WireLogRing.Push(line); } catch { /* defensive */ }
            WriteRecord(cfg, line);
            // Ensure the file handle isn't held open past the call boundary
            // for one-shot probe writes — keeps WriteForTest semantics
            // identical to the prior File.AppendAllText behavior so the
            // existing test harness can read the file immediately after.
            FlushAndCloseWritersUnder(logDir);
        }

        private sealed class WireLogConfig
        {
            public string LogDir { get; }

            /// <summary>
            /// P15-015 — per-file size cap in bytes. When the next WriteLine
            /// would push the file past this, the writer is closed, the file
            /// is renamed to lacerte-YYYYMMDD-HHmmss.log, and a fresh file is
            /// opened. Default 100 MB. Configurable via
            /// LACERTE_DEBUG_LOG_MAX_BYTES (positive integer; non-numeric /
            /// non-positive falls back to default).
            /// </summary>
            public long MaxBytes { get; }

            /// <summary>
            /// P15-015 — total directory size cap in bytes. After each
            /// rotation event, oldest lacerte-*.log files are deleted until
            /// total dir size sits below this. Default 500 MB. Configurable
            /// via LACERTE_DEBUG_LOG_MAX_DIR_BYTES with the same parse
            /// fallback rule.
            /// </summary>
            public long MaxDirBytes { get; }

            /// <summary>
            /// P15-071 — retention window in days. On the first write of each
            /// UTC day (gated by <see cref="_lastPrunedUtcDate"/>), files
            /// named lacerte-YYYYMMDD*.log whose embedded date is older than
            /// UtcNow.Date - RetentionDays are deleted. Default 14.
            /// Configurable via LACERTE_DEBUG_LOG_RETENTION_DAYS (1..365;
            /// invalid values fall back to default).
            /// </summary>
            public int RetentionDays { get; }

            /// <summary>
            /// P15-072 — sample size used when summarizing large result
            /// arrays. Records whose <c>result</c> is a JSON array longer
            /// than this threshold are rewritten to
            /// <c>{kind:"collection", count:N, sample:[…]}</c>. Default 5.
            /// Configurable via <c>LACERTE_DEBUG_LOG_RESULT_SAMPLE</c>
            /// (positive integer; non-positive falls back to default).
            /// </summary>
            public int ResultSampleLimit { get; }

            /// <summary>
            /// P15-072 — opt-out for the large-result summarizer. When
            /// <see cref="FullResults"/> is true, the legacy verbose-payload
            /// behavior is restored — every read-path result is dumped to
            /// disk in full. Enabled via
            /// <c>LACERTE_DEBUG_LOG_FULL_RESULTS=1</c> for the rare
            /// deep-debugging session that needs the full payload.
            /// </summary>
            public bool FullResults { get; }

            public WireLogConfig(
                string logDir,
                long? maxBytes = null,
                long? maxDirBytes = null,
                int? retentionDays = null,
                int? resultSampleLimit = null,
                bool fullResults = false)
            {
                LogDir = logDir;
                MaxBytes = maxBytes.HasValue && maxBytes.Value > 0 ? maxBytes.Value : DefaultMaxBytes;
                MaxDirBytes = maxDirBytes.HasValue && maxDirBytes.Value > 0 ? maxDirBytes.Value : DefaultMaxDirBytes;
                RetentionDays = retentionDays.HasValue && retentionDays.Value >= 1 && retentionDays.Value <= 365
                    ? retentionDays.Value
                    : DefaultRetentionDays;
                ResultSampleLimit = resultSampleLimit.HasValue && resultSampleLimit.Value > 0
                    ? resultSampleLimit.Value
                    : DefaultResultSampleLimit;
                FullResults = fullResults;
            }

            public const long DefaultMaxBytes = 100L * 1024L * 1024L;       // 100 MB
            public const long DefaultMaxDirBytes = 500L * 1024L * 1024L;    // 500 MB
            public const int DefaultRetentionDays = 14;

            /// <summary>P15-072 — default for <see cref="ResultSampleLimit"/>.</summary>
            public const int DefaultResultSampleLimit = 5;
        }
    }
}
