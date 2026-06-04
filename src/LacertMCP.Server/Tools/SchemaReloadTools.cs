using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P15-044 — operator-facing MCP tool that drops the in-process
    /// <see cref="FieldAccessMapLoader"/> caches so a freshly written
    /// shard on disk (e.g. produced by <c>LacertMCP.FieldAccessDumper.exe</c>)
    /// takes effect without restarting the server.
    ///
    /// <para>
    /// Why this exists: the loader's module + overlay caches are populated
    /// lazily via <c>ConcurrentDictionary.GetOrAdd</c> and never invalidated.
    /// The Phase-13 dumper workflow has an operator run
    /// <c>LacertMCP.FieldAccessDumper.exe</c> against a long-lived MCP
    /// server, writes a fresh <c>Schema/FieldAccessMap/base/{module}.json</c>,
    /// then exercises tools. Without an explicit reload path, the cached
    /// empty / stale shard from the first <c>AssertWritable</c> pins every
    /// subsequent write to synthetic 9012 <c>UnknownFieldAccess</c> until
    /// process restart — and a restart invalidates the Intuit 24h auth
    /// ticket and forces a re-register dance.
    /// </para>
    ///
    /// <para>
    /// Single-load semantics remain the default — this is an EXPLICIT
    /// operator-triggered reload, not a <c>FileSystemWatcher</c>. No
    /// surprise reloads mid-tool; no fights with the dumper's multi-file
    /// rewrite; no non-deterministic test behavior.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class SchemaReloadTools
    {
        [McpServerTool]
        [Description("Drop the in-process FieldAccessMap caches so freshly written shards on disk take effect. Call this after running LacertMCP.FieldAccessDumper.exe so the new shards take effect without restarting the server (which would invalidate the Intuit 24h auth ticket). Returns { modulesReloaded, overlaysReloaded, schemaRoot } — the counts are how many cached entries were evicted (NOT how many shards exist on disk); the next field-write or lookup will lazily repopulate the cache from disk. The outer envelope's `success: true` already indicates the reload itself succeeded.")]
        public Task<string> LacerteReloadSchema()
        {
            return WrapToolHandler.Run(() =>
            {
                var (modules, overlays) = FieldAccessMapLoader.ReloadCaches();
                return Task.FromResult<object>(new
                {
                    modulesReloaded = modules,
                    overlaysReloaded = overlays,
                    schemaRoot = FieldAccessMapLoader.GetSchemaRoot(),
                });
            });
        }
    }
}
