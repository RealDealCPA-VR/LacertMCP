using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Schema
{
    /// <summary>
    /// Lazy per-module loader for the sharded JSON access map.
    ///
    /// <para>
    /// Files are read from disk relative to the running assembly at
    /// <c>{assemblyDir}/Schema/FieldAccessMap/base/{module}.json</c> and
    /// <c>{assemblyDir}/Schema/FieldAccessMap/overlays/ty{yy}-not-supported.json</c>.
    /// The csproj copies these JSON files to the build output via
    /// <c>&lt;Content Include="Schema\FieldAccessMap\**\*.json" CopyToOutputDirectory="PreserveNewest" /&gt;</c>.
    /// </para>
    ///
    /// <para>
    /// Lookup order (per agent 04 §6):
    /// </para>
    /// <list type="number">
    ///   <item><description>Universal rule: <c>field == "C{table}_0"</c> (case-insensitive) → <see cref="FieldAccess.ReadOnly"/>.</description></item>
    ///   <item><description>Overlay (e.g. <c>ty21-not-supported.json</c>) — wins over base.</description></item>
    ///   <item><description>Base module map.</description></item>
    ///   <item><description><see cref="FieldAccess.Unknown"/> fallback (makes the lookup total).</description></item>
    /// </list>
    ///
    /// <para>
    /// Module + overlay maps are cached per-process in a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>; a missing or
    /// malformed file caches an empty map so we don't re-stat the disk on
    /// every call AND so a single typo in a shipped overlay doesn't poison
    /// every subsequent write attempt for the lifetime of the process.
    /// </para>
    ///
    /// <para>
    /// Per-year overlay semantic (per agent 04 §6 + DECISIONS.md 2026-05-30):
    /// overlay files are <b>full-replace per exact tax year</b>, not
    /// cumulative deltas. A TY22 overlay does NOT inherit from TY21; each
    /// year's "not supported" list must ship explicitly. The loader does
    /// not walk prior years and will not silently chain them.
    /// </para>
    /// </summary>
    public static class FieldAccessMapLoader
    {
        private static readonly ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>> _moduleCache
            = new ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>>();

        private static readonly ConcurrentDictionary<int, TaxYearOverlay> _overlayCache
            = new ConcurrentDictionary<int, TaxYearOverlay>();

        private static readonly JsonSerializerOptions _jsonOptions = BuildJsonOptions();

        // Pre-interned table strings for the SDK's 1..7 range plus 0 and a
        // small buffer — used to skip the per-call `table.ToString()` boxing on
        // the access-map hot path (every write goes through GetFieldAccess).
        // Cache covers tables 0..15; outside that range we fall back to
        // table.ToString(). Indices 0..15 are conservative — Lacerte's DBF
        // tables only run 1..7 today but giving headroom costs nothing.
        private static readonly string[] _tableStrings = BuildTableStrings(16);

        private static string[] BuildTableStrings(int max)
        {
            var arr = new string[max];
            for (var i = 0; i < max; i++) arr[i] = i.ToString();
            return arr;
        }

        private static string TableString(int table)
        {
            return (table >= 0 && table < _tableStrings.Length)
                ? _tableStrings[table]
                : table.ToString();
        }

        private static JsonSerializerOptions BuildJsonOptions()
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };
            // FieldAccess values ship in JSON as strings ("ReadWrite", "ReadOnly", ...) — not ints.
            opts.Converters.Add(new JsonStringEnumConverter());
            return opts;
        }

        // Module char → filename basename now lives on Modules.FileBasenameFor
        // (single source of truth — formerly duplicated here, in MetadataTools,
        // and in the CLI FieldAccessCommand).

        /// <summary>
        /// Allocation-free check for the universal <c>C{table}_0</c> primary-key
        /// rule. Equivalent to <c>field.ToUpperInvariant() == "C" + table + "_0"</c>
        /// but without the transient strings on the hot write path. Case
        /// insensitive so a lowercased field name (e.g. round-tripped through
        /// JSON) cannot bypass the universal ReadOnly rule.
        /// </summary>
        private static bool IsClientNumberField(string field, int table)
        {
            if (field == null) return false;

            // Build the expected literal in our head: 'C' + decimal(table) + "_0".
            // Length = 1 + digits(table) + 2.
            int digits = table < 10 ? 1 : (table < 100 ? 2 : table.ToString().Length);
            if (field.Length != 1 + digits + 2) return false;

            if (field[0] != 'C' && field[0] != 'c') return false;
            if (field[field.Length - 2] != '_') return false;
            if (field[field.Length - 1] != '0') return false;

            // Middle slice must equal the table number as decimal.
            // For the common single-digit case avoid any allocation.
            if (digits == 1)
            {
                return field[1] == (char)('0' + table);
            }

            // Multi-digit: compare against the cached/computed table string.
            var ts = TableString(table);
            for (var i = 0; i < digits; i++)
            {
                if (field[1 + i] != ts[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Total function: returns the field's access for the (module, table,
        /// field, taxYear) tuple. Falls back to <see cref="FieldAccess.Unknown"/>
        /// for fields not in the catalog. Field name matching is
        /// case-insensitive so the universal <c>C{table}_0</c> rule and base /
        /// overlay lookups cannot be bypassed by a lowercased field name.
        /// </summary>
        public static FieldAccess GetFieldAccess(char module, int table, string field, int taxYear)
        {
            if (field == null) return FieldAccess.Unknown;

            // 1. Universal rule — allocation-free, case-insensitive, applies
            //    to every module/table. Must run BEFORE the map lookup so the
            //    rule stays unconditional even when the base shards populate.
            if (IsClientNumberField(field, table)) return FieldAccess.ReadOnly;

            // Normalize field once so the universal rule (above) and the
            // base/overlay map keys (below) agree on case. Base shards are
            // authored uppercase; without this a lowercase payload would miss
            // entries even when the universal rule didn't fire.
            var normField = field.ToUpperInvariant();
            var key = module + "|" + TableString(table) + "|" + normField;

            // 2. Overlay (TY-version delta) wins over base.
            var overlay = LoadOverlay(taxYear);
            if (overlay != null && overlay.Overrides.TryGetValue(key, out var o))
            {
                return o.Access;
            }

            // 3. Base map.
            var baseMap = LoadModule(module);
            if (baseMap.TryGetValue(key, out var entry))
            {
                return entry.Access;
            }

            // 4. Unknown fallback.
            return FieldAccess.Unknown;
        }

        /// <summary>
        /// Throws a structured <see cref="LacerteSDKError"/> mapped to the
        /// right <c>TAPIStatus</c> code per agent 04 §7 when the field is
        /// not safe to write. Returns silently when the field is
        /// <see cref="FieldAccess.ReadWrite"/> (or when <paramref name="force"/>
        /// is set and the field is <see cref="FieldAccess.Unknown"/>).
        ///
        /// Status codes emitted:
        /// <list type="bullet">
        ///   <item><description><see cref="FieldAccess.ReadOnly"/> → <c>ExposedForRead (8)</c></description></item>
        ///   <item><description><see cref="FieldAccess.Hidden"/> / <see cref="FieldAccess.NotExposed"/> → <c>NotExposed (7)</c></description></item>
        ///   <item><description><see cref="FieldAccess.NotSupported"/> → <c>FieldNotSupported (26)</c></description></item>
        ///   <item><description><see cref="FieldAccess.Unknown"/> without <paramref name="force"/> → synthetic <c>UnknownFieldAccess (9012)</c> with the "not in access map" message — distinct from 9001 so callers can disambiguate a missing map entry from a known-non-writable field</description></item>
        /// </list>
        /// </summary>
        public static void AssertWritable(char module, int table, string field, int taxYear, bool force = false)
        {
            var access = GetFieldAccess(module, table, field, taxYear);
            var context = new Dictionary<string, object?>
            {
                { "module", module.ToString() },
                { "table", table },
                { "field", field },
                { "taxYear", taxYear },
            };

            // P15-069: replace the duplicated prose "reason" string with
            // structured, machine-readable keys. The StatusInfo for each code
            // already carries the canonical remediation; the throw-site only
            // needs to surface typed handles the LLM can use to either retry
            // the same call with force:true or run the suggested CLI. After
            // the round-1 WireLog.Redact pass turns any "reason" string into
            // "***" anyway, so the standalone remediation is the only useful
            // text path — duplicating it inside context just created a
            // maintenance trap. Keep the structured keys lean.
            switch (access)
            {
                case FieldAccess.ReadWrite:
                    return;

                case FieldAccess.ReadOnly:
                    context["access"] = "ReadOnly";
                    throw new LacerteSDKError(StatusCodes.ExposedForRead, context);

                case FieldAccess.Hidden:
                case FieldAccess.NotExposed:
                    context["access"] = access.ToString();
                    throw new LacerteSDKError(StatusCodes.NotExposed, context);

                case FieldAccess.NotSupported:
                    context["access"] = "NotSupported";
                    context["overlayYear"] = taxYear;
                    throw new LacerteSDKError(StatusCodes.FieldNotSupported, context);

                case FieldAccess.Unknown:
                    if (force) return;
                    context["access"] = "Unknown";
                    context["suggestedRetryArgs"] = new Dictionary<string, object?> { ["force"] = true };
                    context["suggestedCli"] = "lacerte-mcp field-access discover";
                    throw new LacerteSDKError(SyntheticStatusCodes.UnknownFieldAccess, context);

                default:
                    // Defensive — every enum member is handled above.
                    context["access"] = access.ToString();
                    throw new LacerteSDKError(StatusCodes.Error, context);
            }
        }

        // ---- Internals ----

        private static IReadOnlyDictionary<string, FieldEntry> LoadModule(char module)
        {
            return _moduleCache.GetOrAdd(module, m =>
            {
                var basename = Modules.FileBasenameFor(m);
                if (basename == null)
                {
                    // Unknown module char — return an empty map so callers get Unknown.
                    return new Dictionary<string, FieldEntry>();
                }

                var path = Path.Combine(SchemaRoot(), "base", basename + ".json");
                if (!File.Exists(path))
                {
                    return new Dictionary<string, FieldEntry>();
                }

                using (var stream = File.OpenRead(path))
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, FieldEntry>>(stream, _jsonOptions);
                    return (IReadOnlyDictionary<string, FieldEntry>)(map ?? new Dictionary<string, FieldEntry>());
                }
            });
        }

        /// <summary>
        /// Loads (or returns cached) the per-year "not supported" overlay.
        ///
        /// <para>
        /// <b>Per-exact-year, full-replace semantic</b> — the loader reads
        /// <c>ty{yy}-not-supported.json</c> for the requested year only and
        /// does NOT walk prior years or merge them. Each TY ships its own
        /// complete list of fields not supported in that build, even if a
        /// field was already dropped in an earlier year. See agent 04 §6 and
        /// the dated entry in <c>DECISIONS.md</c> (2026-05-30 — "Per-year
        /// access-map overlay semantic").
        /// </para>
        ///
        /// <para>
        /// Missing OR malformed files degrade to an empty overlay sentinel so
        /// neither a missing-by-design shard nor a shipping typo poisons the
        /// write path. A missing-by-mistake shard for a year that should ship
        /// an overlay would silently fall through to base-map behavior — that
        /// is the explicit downside of per-year full-replace and is mitigated
        /// by manifest authoring discipline, not by the loader.
        /// </para>
        /// </summary>
        private static TaxYearOverlay? LoadOverlay(int taxYear)
        {
            return _overlayCache.GetOrAdd(taxYear, year =>
            {
                var yy = (year % 100).ToString("D2");
                var path = Path.Combine(SchemaRoot(), "overlays", "ty" + yy + "-not-supported.json");
                if (!File.Exists(path))
                {
                    // Cache a sentinel empty overlay so we don't re-stat every call.
                    return new TaxYearOverlay(year, new Dictionary<string, OverlayEntry>());
                }

                // Be paranoid about malformed/corrupted overlay files. Without
                // the try/catch a JsonException thrown inside the GetOrAdd
                // factory would propagate every call (because the entry never
                // gets cached) — effectively poisoning every subsequent write
                // attempt for the lifetime of the process. Mirror LoadModule's
                // missing-file degrade-to-empty behavior: bad JSON becomes "no
                // overrides for this year." A shipping typo in an overlay
                // should fail open to base-map behavior, not nuke the write
                // path. Other failure modes (FileNotFound shouldn't reach
                // here, IOException would still propagate) deliberately are
                // not swallowed so genuine I/O issues are still observable.
                try
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var raw = JsonSerializer.Deserialize<OverlayFile>(stream, _jsonOptions);
                        if (raw == null)
                        {
                            return new TaxYearOverlay(year, new Dictionary<string, OverlayEntry>());
                        }
                        var overrides = raw.Overrides ?? new Dictionary<string, OverlayEntry>();
                        return new TaxYearOverlay(raw.TaxYear == 0 ? year : raw.TaxYear, overrides);
                    }
                }
                catch (JsonException)
                {
                    // Cache an empty sentinel so we don't re-parse-and-throw on
                    // every call. The shard is effectively unreadable; degrade
                    // to "no overrides" rather than gating every write.
                    return new TaxYearOverlay(year, new Dictionary<string, OverlayEntry>());
                }
            });
        }

        private static string SchemaRoot()
        {
            // Schema/FieldAccessMap lives next to the running assembly. The
            // csproj copies the JSON tree to the build output.
            var asmPath = typeof(FieldAccessMapLoader).Assembly.Location;
            var asmDir = Path.GetDirectoryName(asmPath) ?? Directory.GetCurrentDirectory();
            return Path.Combine(asmDir, "Schema", "FieldAccessMap");
        }

        /// <summary>
        /// P15-044 — Forget all cached module + overlay JSON. Returns the
        /// counts evicted so callers (e.g. the <c>lacerte_reload_schema</c>
        /// MCP tool) can report what they cleared.
        ///
        /// <para>
        /// Intended use: an operator runs <c>LacertMCP.FieldAccessDumper.exe</c>
        /// against a long-lived MCP server, writes a fresh shard to disk,
        /// then calls a reload tool that delegates here so the next
        /// <c>AssertWritable</c> picks up the new map without restarting the
        /// server (a restart would invalidate the 24h Intuit auth ticket).
        /// </para>
        ///
        /// <para>
        /// Single-load semantics remain the default — this is an EXPLICIT
        /// reload, not a watcher. No surprise reloads mid-tool.
        /// </para>
        /// </summary>
        public static (int modulesEvicted, int overlaysEvicted) ReloadCaches()
        {
            var modules = _moduleCache.Count;
            var overlays = _overlayCache.Count;
            _moduleCache.Clear();
            _overlayCache.Clear();
            return (modules, overlays);
        }

        /// <summary>Test-only: forget all cached module + overlay JSON.</summary>
        internal static void ResetCacheForTests()
        {
            ReloadCaches();
        }

        /// <summary>
        /// P15-034 — Count shards present on disk under <see cref="SchemaRoot"/>
        /// WITHOUT loading or parsing them. Pure file enumeration; safe to
        /// call from the hot path / startup / a doctor probe.
        ///
        /// <para>
        /// Returns <c>(0, 0)</c> when the schema root does not exist (the
        /// most common diagnostic gap: an operator running from a partially
        /// published bin/ where the <c>Schema/FieldAccessMap/**/*.json</c>
        /// CopyToOutputDirectory failed). Surface this via a doctor probe or
        /// <c>lacerte_session_status</c> so the symptom "every write returns
        /// 9012 UnknownFieldAccess" can be traced to "the access-map
        /// directory is empty" without spelunking through the source.
        /// </para>
        /// </summary>
        public static (int modules, int overlays) GetShardCounts()
        {
            var root = SchemaRoot();
            var baseDir = Path.Combine(root, "base");
            var overlayDir = Path.Combine(root, "overlays");

            var modules = Directory.Exists(baseDir)
                ? Directory.GetFiles(baseDir, "*.json", SearchOption.TopDirectoryOnly).Length
                : 0;

            var overlays = Directory.Exists(overlayDir)
                ? Directory.GetFiles(overlayDir, "ty*-not-supported.json", SearchOption.TopDirectoryOnly).Length
                : 0;

            return (modules, overlays);
        }

        /// <summary>
        /// P15-034 — Return the schema root directory (absolute, next to the
        /// running assembly). Exposed so doctor / session-status surfaces can
        /// report where the loader is looking when shard counts are zero.
        /// </summary>
        public static string GetSchemaRoot() => SchemaRoot();

        // JSON-shape mirror for overlay files: { "taxYear": int, "overrides": {...} }
        private sealed class OverlayFile
        {
            public int TaxYear { get; set; }
            public Dictionary<string, OverlayEntry>? Overrides { get; set; }
        }
    }
}
