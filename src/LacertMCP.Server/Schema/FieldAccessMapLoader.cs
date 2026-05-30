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
    ///   <item><description>Universal rule: <c>field == "C{table}_0"</c> → <see cref="FieldAccess.ReadOnly"/>.</description></item>
    ///   <item><description>Overlay (e.g. <c>ty21-not-supported.json</c>) — wins over base.</description></item>
    ///   <item><description>Base module map.</description></item>
    ///   <item><description><see cref="FieldAccess.Unknown"/> fallback (makes the lookup total).</description></item>
    /// </list>
    ///
    /// <para>
    /// Module + overlay maps are cached per-process in a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>; a missing file
    /// caches an empty map so we don't re-stat the disk on every call.
    /// </para>
    /// </summary>
    public static class FieldAccessMapLoader
    {
        private static readonly ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>> _moduleCache
            = new ConcurrentDictionary<char, IReadOnlyDictionary<string, FieldEntry>>();

        private static readonly ConcurrentDictionary<int, TaxYearOverlay> _overlayCache
            = new ConcurrentDictionary<int, TaxYearOverlay>();

        private static readonly JsonSerializerOptions _jsonOptions = BuildJsonOptions();

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

        // Module char → filename basename (matches Schema/FieldAccessMap/base/*.json).
        private static readonly IReadOnlyDictionary<char, string> _moduleFileName = new Dictionary<char, string>
        {
            { 'I', "individual" },
            { 'C', "corporate" },
            { 'P', "partnership" },
            { 'S', "scorp" },
            { 'F', "fiduciary" },
            { 'R', "exempt" },
            { 'T', "estate" },
            { 'N', "gift" },
            { 'B', "benefit" },
        };

        /// <summary>
        /// Total function: returns the field's access for the (module, table,
        /// field, taxYear) tuple. Falls back to <see cref="FieldAccess.Unknown"/>
        /// for fields not in the catalog.
        /// </summary>
        public static FieldAccess GetFieldAccess(char module, int table, string field, int taxYear)
        {
            if (field == null) return FieldAccess.Unknown;

            // 1. Universal rule — cheaper than a map hit, applies to every module/table.
            if (field == "C" + table.ToString() + "_0") return FieldAccess.ReadOnly;

            var key = module + "|" + table.ToString() + "|" + field;

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

            switch (access)
            {
                case FieldAccess.ReadWrite:
                    return;

                case FieldAccess.ReadOnly:
                    context["reason"] = $"Field {field} on {module}/DATA{table} is read-only (e.g. computed or client number).";
                    throw new LacerteSDKError(StatusCodes.ExposedForRead, context);

                case FieldAccess.Hidden:
                case FieldAccess.NotExposed:
                    context["reason"] = $"Field {field} is not exposed for write on {module}/DATA{table}.";
                    throw new LacerteSDKError(StatusCodes.NotExposed, context);

                case FieldAccess.NotSupported:
                    context["reason"] = $"Field {field} is not supported in TY{taxYear} (overlay).";
                    throw new LacerteSDKError(StatusCodes.FieldNotSupported, context);

                case FieldAccess.Unknown:
                    if (force) return;
                    context["reason"] =
                        $"Field {field} on {module}/DATA{table} is not in the access map. " +
                        "Refusing to write. Pass force:true to override, or run " +
                        "'lacerte-mcp field-access discover' to populate the map.";
                    throw new LacerteSDKError(SyntheticStatusCodes.UnknownFieldAccess, context);

                default:
                    // Defensive — every enum member is handled above.
                    context["reason"] = $"Field {field} on {module}/DATA{table} returned an unrecognized access value: {access}.";
                    throw new LacerteSDKError(StatusCodes.Error, context);
            }
        }

        // ---- Internals ----

        private static IReadOnlyDictionary<string, FieldEntry> LoadModule(char module)
        {
            return _moduleCache.GetOrAdd(module, m =>
            {
                if (!_moduleFileName.TryGetValue(m, out var basename))
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

        /// <summary>Test-only: forget all cached module + overlay JSON.</summary>
        internal static void ResetCacheForTests()
        {
            _moduleCache.Clear();
            _overlayCache.Clear();
        }

        // JSON-shape mirror for overlay files: { "taxYear": int, "overrides": {...} }
        private sealed class OverlayFile
        {
            public int TaxYear { get; set; }
            public Dictionary<string, OverlayEntry>? Overrides { get; set; }
        }
    }
}
