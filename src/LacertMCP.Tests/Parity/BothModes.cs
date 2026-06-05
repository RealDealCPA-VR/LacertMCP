using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using Xunit;
using Xunit.Sdk;

namespace LacertMCP.Tests.Parity
{
    /// <summary>
    /// xUnit data attribute that fans out every <c>[Theory]</c> across both
    /// session modes. Used by the Phase 12 parity harness (task #80) so
    /// behavioral tests run identically against:
    /// <list type="bullet">
    ///   <item><description><see cref="SessionMode.Simulation"/> — in-memory store, always available.</description></item>
    ///   <item><description><see cref="SessionMode.Live"/> — real COM/ODBC, only meaningful on a
    ///     Windows + Lacerte CI box. Until a recorded-replay fixture lands
    ///     (see <see cref="ParitySessionFactory.RecordingEnvVar"/>), the Live
    ///     leg is GATED OFF — the attribute yields only the Simulation row
    ///     and <see cref="LiveLegParityPlaceholder"/> emits a single Skipped
    ///     test so the gap surfaces honestly in test runner output instead
    ///     of silently running sim twice and reporting it as parity coverage.
    ///     The harness STRUCTURE is here so adding the recorded-replay leg
    ///     is a one-file change.</description></item>
    /// </list>
    ///
    /// <para>
    /// P17-015 — Gate the Live leg behind <see cref="HasLiveLegFixture"/> so
    /// dev/CI runs stop double-paying for ~58 redundant sim invocations and
    /// the test-runner output stops falsely advertising dual-mode coverage.
    /// When the env var <c>LACERTE_PARITY_RECORDING</c> is set OR a non-example
    /// fixture appears in <c>Parity/fixtures/</c>, both legs are emitted and
    /// the Live leg actually runs against the recording bridge.
    /// </para>
    ///
    /// <para>
    /// Usage:
    /// <code>
    /// [Theory]
    /// [BothModesData]
    /// public async Task ListClients_ReturnsRows(SessionMode mode)
    /// {
    ///     using var mgr = ParitySessionFactory.For(mode);
    ///     ...
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public sealed class BothModesDataAttribute : DataAttribute
    {
        public override IEnumerable<object[]> GetData(MethodInfo testMethod)
        {
            yield return new object[] { SessionMode.Simulation };

            // P17-015 — Only emit the Live leg when a recorded-replay fixture
            // is actually wired up. Without it the Live leg short-circuits to
            // Simulation (see ParitySessionFactory.For) and we'd just run the
            // same assertions twice, padding the test count and falsely
            // reporting dual-mode parity coverage. When this gate flips
            // (env var set, or a real fixture committed), the second leg
            // starts executing automatically — no theory-method changes
            // needed. The single Skipped placeholder fact in
            // LiveLegParityPlaceholder makes the gap visible in the test
            // runner output instead of silently disappearing.
            if (HasLiveLegFixture())
            {
                yield return new object[] { SessionMode.Live };
            }
        }

        /// <summary>
        /// True when the Live leg should actually fan out. The Live leg is
        /// gated behind either an explicit <c>LACERTE_PARITY_RECORDING</c>
        /// env var (the canonical knob, also honored by
        /// <see cref="ParitySessionFactory.For(SessionMode)"/>) or the
        /// presence of any committed fixture under
        /// <c>src/LacertMCP.Tests/Parity/fixtures/</c> other than the
        /// <c>example-recording.json</c> sample (which is only used by
        /// <c>RecordingHarnessTests</c> via <c>ForRecording</c>, not by the
        /// BothModesData fan-out).
        /// </summary>
        internal static bool HasLiveLegFixture()
        {
            var recording = Environment.GetEnvironmentVariable(ParitySessionFactory.RecordingEnvVar);
            if (!string.IsNullOrEmpty(recording)) return true;

            // Recorded-replay fixtures live next to BothModes.cs in the
            // source tree. Probe the standard candidate roots so the gate
            // works whether the test runner reports AppContext.BaseDirectory,
            // the assembly location, or the cwd as the starting point.
            foreach (var root in CandidateRoots())
            {
                var fixturesDir = FindFixturesDir(root);
                if (fixturesDir == null) continue;
                var files = Directory.GetFiles(fixturesDir, "*.json", SearchOption.TopDirectoryOnly);
                if (files.Any(f => !string.Equals(
                    Path.GetFileName(f), "example-recording.json", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            var roots = new List<string?>
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(BothModesDataAttribute).Assembly.Location),
                Directory.GetCurrentDirectory(),
            };
            return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))!;
        }

        private static string? FindFixturesDir(string root)
        {
            var direct = Path.Combine(root, "Parity", "fixtures");
            if (Directory.Exists(direct)) return direct;

            var probe = new DirectoryInfo(root);
            for (int hop = 0; hop < 8 && probe != null; hop++)
            {
                var src = Path.Combine(probe.FullName, "src", "LacertMCP.Tests",
                    "Parity", "fixtures");
                if (Directory.Exists(src)) return src;
                probe = probe.Parent;
            }
            return null;
        }
    }

    /// <summary>
    /// P17-015 — single class-level placeholder that surfaces the Live-leg
    /// parity gap in the test runner output. When no recorded-replay fixture
    /// is configured this fact reports as <c>Skipped</c> with an explicit
    /// reason instead of silently running the sim leg twice and falsely
    /// reporting dual-mode parity coverage. When the gate flips (env var set
    /// or a real fixture committed) the placeholder runs as a no-op green Fact
    /// and the BothModesData fan-out picks up the second leg automatically.
    /// </summary>
    public class LiveLegParityPlaceholder
    {
        [LiveLegGatedFact]
        public void LiveLeg_AwaitsRecordedReplayFixture()
        {
            // No-op. When this method actually runs (instead of being skipped
            // via LiveLegGatedFactAttribute's Skip property) it means the
            // recorded-replay fixture is wired up and the BothModesData
            // fan-out is already exercising the Live leg — there is nothing
            // additional to assert here.
        }
    }

    /// <summary>
    /// xUnit <c>[Fact]</c> variant that sets its <see cref="FactAttribute.Skip"/>
    /// reason to a sentinel string when
    /// <see cref="BothModesDataAttribute.HasLiveLegFixture"/> returns false at
    /// attribute construction time. xUnit honors the Skip property by reporting
    /// the test as Skipped, which is exactly what we want — the parity gap
    /// surfaces honestly in the test runner output, and flipping the env var
    /// (or committing a real fixture) re-enables the fact without a code change.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class LiveLegGatedFactAttribute : FactAttribute
    {
        public LiveLegGatedFactAttribute()
        {
            if (!BothModesDataAttribute.HasLiveLegFixture())
            {
                Skip = "Live-leg parity is gated until a recorded-replay fixture lands. " +
                       "Set LACERTE_PARITY_RECORDING=<path> or commit a non-example " +
                       "fixture under src/LacertMCP.Tests/Parity/fixtures/ to enable " +
                       "the second leg across all [Theory, BothModesData] cases. " +
                       "See Parity/BothModes.cs (P17-015) and HANDOFF.md.";
            }
        }
    }

    /// <summary>
    /// Factory for parity-test session managers. Centralizes the env-bag a test
    /// passes in so all parity tests get identical mode resolution.
    ///
    /// <para>
    /// F10 — RecordingBridge adapter. When the env var
    /// <c>LACERTE_PARITY_RECORDING</c> is set to a path, the
    /// <see cref="SessionMode.Live"/> branch constructs a real live-mode
    /// session manager backed by a <see cref="RecordingBridge"/> that
    /// replays canned <see cref="ILacerteBridge"/> responses from the JSON
    /// recording. The Simulation branch is unaffected. Without the env var
    /// both branches still fall back to sim per the legacy behavior so
    /// existing tests stay green.
    /// </para>
    /// </summary>
    public static class ParitySessionFactory
    {
        /// <summary>
        /// Env var the harness checks for a recording-file path. When set,
        /// the Live leg constructs a recording-bridge-backed session manager.
        /// </summary>
        public const string RecordingEnvVar = "LACERTE_PARITY_RECORDING";

        /// <summary>
        /// Build a <see cref="LacerteSessionManager"/> targeting
        /// <paramref name="mode"/>.
        ///
        /// <list type="bullet">
        ///   <item><description>Simulation: always returns a sim-backed manager.</description></item>
        ///   <item><description>Live + no recording env var: falls back to a sim-backed manager
        ///     with <c>LACERTE_SIMULATION=1</c> pinned (legacy behavior — keeps
        ///     existing parity tests passing on dev/CI without a real Lacerte
        ///     install).</description></item>
        ///   <item><description>Live + <c>LACERTE_PARITY_RECORDING=path</c>: constructs a real
        ///     live-mode manager via the internal test-only ctor and wires it
        ///     to a <see cref="RecordingBridge"/> loaded from the given file.</description></item>
        /// </list>
        /// </summary>
        public static LacerteSessionManager For(SessionMode mode)
        {
            if (mode == SessionMode.Live)
            {
                var recording = Environment.GetEnvironmentVariable(RecordingEnvVar);
                if (!string.IsNullOrEmpty(recording))
                {
                    return ForRecording(recording!);
                }
            }

            // Default (Simulation, OR Live without a recording): pin sim mode
            // so the manager doesn't try to talk to a real COM bridge on dev/CI.
            var env = new Dictionary<string, string?>
            {
                ["LACERTE_SIMULATION"] = "1",
            };
            _ = mode; // both legs resolve to sim in the legacy path.
            return new LacerteSessionManager(env);
        }

        /// <summary>
        /// Public hook used by F10's playback test: build a live-mode session
        /// manager backed by the recording file at <paramref name="recordingPath"/>.
        /// Resolves the path relative to the running test assembly if not
        /// absolute.
        /// </summary>
        public static LacerteSessionManager ForRecording(string recordingPath)
        {
            var resolved = ResolveRecordingPath(recordingPath);
            var bridge = RecordingBridge.LoadFromFile(resolved);
            // Internal ctor pins Mode=Live and skips env resolution; this is
            // exactly what we want — the recording bridge is the live wire.
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) },
                modifiers: null);
            if (ctor == null)
            {
                throw new InvalidOperationException(
                    "Could not find internal LacerteSessionManager(ILacerteBridge, IDictionary<string, string?>) ctor. " +
                    "The recording-bridge harness relies on this internal hook (InternalsVisibleTo LacertMCP.Tests).");
            }
            return (LacerteSessionManager)ctor.Invoke(new object?[] { bridge, null });
        }

        private static string ResolveRecordingPath(string recordingPath)
        {
            if (Path.IsPathRooted(recordingPath) && File.Exists(recordingPath)) return recordingPath;
            if (File.Exists(recordingPath)) return Path.GetFullPath(recordingPath);

            // Walk a list of candidate roots — xunit's test runner sometimes
            // reports Assembly.Location as a shadow-copy temp path, so we
            // also check AppContext.BaseDirectory + the cwd and (as a
            // fallback) walk up from any of those to find the test project's
            // Parity/fixtures/ folder.
            var roots = new List<string>();
            void AddRoot(string? r) { if (!string.IsNullOrEmpty(r) && Directory.Exists(r)) roots.Add(r!); }

            AddRoot(AppContext.BaseDirectory);
            AddRoot(Path.GetDirectoryName(typeof(ParitySessionFactory).Assembly.Location));
            AddRoot(Directory.GetCurrentDirectory());

            var name = Path.GetFileName(recordingPath);
            foreach (var root in roots)
            {
                var nextTo = Path.Combine(root, recordingPath);
                if (File.Exists(nextTo)) return nextTo;

                var fixturesPath = Path.Combine(root, "Parity", "fixtures", name);
                if (File.Exists(fixturesPath)) return fixturesPath;

                // Walk up a few hops looking for the source-tree fixtures dir
                // (handy when the test runner shadow-copies the assembly into
                // a temp dir that doesn't have the Content payload).
                var probe = new DirectoryInfo(root);
                for (int hop = 0; hop < 8 && probe != null; hop++)
                {
                    var srcFixtures = Path.Combine(probe.FullName, "src", "LacertMCP.Tests",
                        "Parity", "fixtures", name);
                    if (File.Exists(srcFixtures)) return srcFixtures;
                    probe = probe.Parent;
                }
            }

            throw new FileNotFoundException(
                $"Parity recording not found at {recordingPath}. Searched: " +
                string.Join("; ", roots),
                recordingPath);
        }
    }

    // =====================================================================
    // Recording file shape — top-level { "calls": [ { op, args, result } ] }
    // =====================================================================

    /// <summary>
    /// Recording file shape: a flat list of canned bridge calls. Each entry
    /// pins one <see cref="ILacerteBridge"/> operation (by <see cref="Op"/>
    /// name + canonical-JSON of <see cref="Args"/>) to a <see cref="Result"/>
    /// the playback bridge returns when the session manager calls in.
    ///
    /// <para>
    /// Argument-matching is intentionally exact (canonical-JSON of the args
    /// object). Hand-author the args block to match what the session manager
    /// will actually send — e.g. for <c>CanUseNewApis</c> the args are
    /// <c>{ appId, year, module }</c>. Unmatched calls raise an explicit
    /// "no matching recording" error so missing fixtures surface loudly
    /// instead of silently returning zero/null.
    /// </para>
    /// </summary>
    public sealed class RecordingFile
    {
        [JsonPropertyName("calls")]
        public List<RecordedCall> Calls { get; set; } = new List<RecordedCall>();
    }

    /// <summary>
    /// One canned (op, args, result) row. Matched by the playback bridge
    /// when the session manager invokes the corresponding
    /// <see cref="ILacerteBridge"/> method.
    /// </summary>
    public sealed class RecordedCall
    {
        [JsonPropertyName("op")]
        public string Op { get; set; } = "";

        [JsonPropertyName("args")]
        public JsonElement Args { get; set; }

        [JsonPropertyName("result")]
        public JsonElement Result { get; set; }
    }

    /// <summary>
    /// <see cref="ILacerteBridge"/> implementation that replays canned
    /// responses from a <see cref="RecordingFile"/>. Used by the F10 parity
    /// harness to drive Live-mode session-manager chokepoints without a real
    /// Lacerte SDK install.
    ///
    /// <para>
    /// Lookup: for each method, the bridge canonicalizes the call's args into
    /// a JSON object with stable key order, then scans the recording for a
    /// matching <see cref="RecordedCall.Op"/> with a deep-equal
    /// <see cref="RecordedCall.Args"/>. On match the typed result is parsed
    /// and returned; on miss a <see cref="LacerteSDKError"/> is thrown so the
    /// test surfaces the missing fixture immediately.
    /// </para>
    /// </summary>
    public sealed class RecordingBridge : ILacerteBridge
    {
        private readonly RecordingFile _recording;

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public RecordingBridge(RecordingFile recording)
        {
            _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        }

        public static RecordingBridge LoadFromFile(string path)
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<RecordingFile>(json, _jsonOpts)
                ?? new RecordingFile();
            return new RecordingBridge(parsed);
        }

        // =================================================================
        // ILacerteBridge — every method funnels through Match<T>
        // =================================================================

        public int Register(string appName, string appId, int taxYear)
            => Match<int>("Register", new Dictionary<string, object?>
            {
                ["appName"] = appName,
                ["appId"] = appId,
                ["taxYear"] = taxYear,
            });

        public bool CanUseNewApis(string appId, int year, char module)
            => Match<bool>("CanUseNewApis", new Dictionary<string, object?>
            {
                ["appId"] = appId,
                ["year"] = year,
                ["module"] = module.ToString(),
            });

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(
            string dataPath, int year, char module)
            => Match<List<Dictionary<string, object?>>>("ListClients", new Dictionary<string, object?>
            {
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
            }).Cast<IReadOnlyDictionary<string, object?>>().ToList();

        public IReadOnlyDictionary<string, object?>? ReadClient(
            string dataPath, int year, char module, string clientNumber)
            => MatchOrNull<Dictionary<string, object?>>("ReadClient", new Dictionary<string, object?>
            {
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
                ["clientNumber"] = clientNumber,
            });

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(
            string dataPath, int year, char module, int table,
            string? clientNumber = null, string? stateAbbrev = null)
            => Match<List<Dictionary<string, object?>>>("ReadTable", new Dictionary<string, object?>
            {
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
                ["table"] = table,
                ["clientNumber"] = clientNumber,
                ["stateAbbrev"] = stateAbbrev,
            }).Cast<IReadOnlyDictionary<string, object?>>().ToList();

        public int WriteField(
            string appId, string dataPath, int year, char module,
            int table, int rowNum, string field, object? value)
            => Match<int>("WriteField", new Dictionary<string, object?>
            {
                ["appId"] = appId,
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
                ["table"] = table,
                ["rowNum"] = rowNum,
                ["field"] = field,
                ["value"] = value,
            });

        public int UpdateTaxReturn(
            string appId, string dataPath, int year, char module,
            int table, string clientNumber, string field, object? value,
            int? subClientID, string? stateAbbrev)
            => Match<int>("UpdateTaxReturn", new Dictionary<string, object?>
            {
                ["appId"] = appId,
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
                ["table"] = table,
                ["clientNumber"] = clientNumber,
                ["field"] = field,
                ["value"] = value,
                ["subClientID"] = subClientID,
                ["stateAbbrev"] = stateAbbrev,
            });

        public int WriteDetail(
            string appId, string dataPath, int year, char module,
            string clientNumber, DetailRow row, int? replaceIndex)
            => Match<int>("WriteDetail", new Dictionary<string, object?>
            {
                ["appId"] = appId,
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
                ["clientNumber"] = clientNumber,
                ["row"] = row,
                // P17-001 — replaceIndex threads through the interface;
                // recordings can pin distinct expected results per index
                // (and per null, for the legacy append path).
                ["replaceIndex"] = replaceIndex,
            });

        public BridgeCreateClientResult CreateClient(
            string appId, string dataPath, int year, char module,
            string clientID, string taxType,
            string? firstName, string? lastName, string? emailID)
        {
            var packed = Match<Dictionary<string, object?>>("CreateClient", new Dictionary<string, object?>
            {
                ["appId"] = appId,
                ["dataPath"] = dataPath,
                ["year"] = year,
                ["module"] = module.ToString(),
                ["clientID"] = clientID,
                ["taxType"] = taxType,
                ["firstName"] = firstName,
                ["lastName"] = lastName,
                ["emailID"] = emailID,
            });
            int status = packed.TryGetValue("status", out var s) && s is JsonElement se && se.ValueKind == JsonValueKind.Number
                ? se.GetInt32()
                : Convert.ToInt32(s);
            string? clientNumber = packed.TryGetValue("clientNumber", out var c) && c is JsonElement ce
                ? (ce.ValueKind == JsonValueKind.String ? ce.GetString() : null)
                : c as string;
            return new BridgeCreateClientResult(status, clientNumber);
        }

        public void Dispose()
        {
            // Recording is in-memory only — nothing to release.
        }

        // =================================================================
        // Internals — match + canonicalization
        // =================================================================

        private T Match<T>(string op, IDictionary<string, object?> args)
        {
            var hit = FindMatch(op, args);
            if (hit == null)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                    new Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["args"] = args,
                        ["reason"] = $"No matching recording entry for op={op}. " +
                                     "Add a { op, args, result } row to the recording JSON.",
                    });
            }
            // Result deserialization: JsonElement → T using the same options.
            return JsonSerializer.Deserialize<T>(hit.Result.GetRawText(), _jsonOpts)!;
        }

        private T? MatchOrNull<T>(string op, IDictionary<string, object?> args) where T : class
        {
            var hit = FindMatch(op, args);
            if (hit == null) return null;
            if (hit.Result.ValueKind == JsonValueKind.Null) return null;
            return JsonSerializer.Deserialize<T>(hit.Result.GetRawText(), _jsonOpts);
        }

        private RecordedCall? FindMatch(string op, IDictionary<string, object?> args)
        {
            var actual = CanonicalizeArgs(args);
            foreach (var call in _recording.Calls)
            {
                if (!string.Equals(call.Op, op, StringComparison.Ordinal)) continue;
                var expected = CanonicalizeJsonElement(call.Args);
                if (expected == actual) return call;
            }
            return null;
        }

        /// <summary>
        /// Stable canonical-JSON for arg matching. Keys sorted, nulls preserved.
        /// </summary>
        private static string CanonicalizeArgs(IDictionary<string, object?> args)
        {
            var sorted = args.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return JsonSerializer.Serialize(sorted, _jsonOpts);
        }

        /// <summary>
        /// Round-trip a JsonElement through deserialize → re-sort → serialize
        /// so we can compare it byte-for-byte to <see cref="CanonicalizeArgs"/>.
        /// </summary>
        private static string CanonicalizeJsonElement(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null)
            {
                return JsonSerializer.Serialize(new Dictionary<string, object?>(), _jsonOpts);
            }
            // Treat the recorded args as a string-keyed dict; sort and re-emit.
            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    bag[prop.Name] = JsonElementToValue(prop.Value);
                }
            }
            var sorted = bag.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return JsonSerializer.Serialize(sorted, _jsonOpts);
        }

        /// <summary>
        /// Strip a JsonElement back to BCL primitives so it serializes the
        /// same way the manager-supplied args dict does. Mirrors the shape
        /// JsonSerializer would emit for the corresponding C# value.
        /// </summary>
        private static object? JsonElementToValue(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt32(out var i)) return i;
                    if (el.TryGetInt64(out var l)) return l;
                    return el.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                case JsonValueKind.Object:
                    var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var p in el.EnumerateObject()) bag[p.Name] = JsonElementToValue(p.Value);
                    return bag;
                case JsonValueKind.Array:
                    var list = new List<object?>();
                    foreach (var e in el.EnumerateArray()) list.Add(JsonElementToValue(e));
                    return list;
                default: return null;
            }
        }
    }
}
