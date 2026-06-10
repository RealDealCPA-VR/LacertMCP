using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// P18-025 (cluster X_feat_resource) — exposes the active-context client
    /// row as a portable, self-documenting export envelope keyed by the
    /// canonical <c>lacerte://client/{clientNumber}</c> URI.
    ///
    /// <para>
    /// The cluster hint allowed two implementations: ship a true MCP
    /// <c>[McpServerResourceType]</c> (which the 0.3.0-preview.4 SDK does
    /// support — reflection confirms <c>WithResourcesFromAssembly</c> and
    /// <c>McpServerResourceAttribute</c> exist) OR ship a tool returning the
    /// equivalent JSON shape with an explicit <c>uri</c> string field. We
    /// take the TOOL path here for three reasons that match the cluster
    /// instructions verbatim:
    /// </para>
    /// <list type="number">
    ///   <item>The cluster's owned file is <c>Tools/ClientResourceTools.cs</c>
    ///     — the file name + folder layout already declares this as a
    ///     <c>[McpServerToolType]</c>, not a <c>Resources/</c> sibling.</item>
    ///   <item>Step 3 of the cluster instructions says "Wire via
    ///     <c>[McpServerToolType]</c> attribute (assembly scan picks them up;
    ///     no Program.cs change)." The resource-attribute path would force a
    ///     <c>WithResourcesFromAssembly()</c> call in <c>Program.cs</c> —
    ///     out of scope per that rule.</item>
    ///   <item>Tool-shape parity means the export envelope reuses the same
    ///     <c>WrapToolHandler.Run</c> success/error envelope as every other
    ///     tool, the same <c>cid</c> correlation id, and the same redaction
    ///     pass — observationally identical with the rest of the surface.</item>
    /// </list>
    ///
    /// <para>
    /// The single tool, <c>lacerte_export_client</c>, accepts a
    /// <c>clientNumber</c>, delegates to
    /// <see cref="LacerteSessionManager.ReadClient"/> through the active
    /// (datapath, year, module) context, and returns:
    /// </para>
    /// <code>
    /// {
    ///   uri: "lacerte://client/1001",
    ///   clientNumber: "1001",
    ///   found: true,
    ///   context: { dataPath, taxYear, module },
    ///   client: { ...row... }
    /// }
    /// </code>
    /// <para>
    /// The <c>context</c> echo is the key differentiator from
    /// <c>lacerte_client_read</c>: an LLM pinning the export across a context
    /// switch can still tell which (datapath, year, module) the row was
    /// resolved against, preventing the "stale client snapshot" failure mode
    /// the verdict calls out as the main reason to ship a resource-shaped
    /// envelope at all.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class ClientResourceTools
    {
        private readonly LacerteSessionManager _session;

        public ClientResourceTools(LacerteSessionManager session) => _session = session;

        /// <summary>
        /// Per-call cap on clientNumber validation — DBF rows mint numeric
        /// strings (sim seeds use "1001", "1002"; the SDK's monotonic counter
        /// is bounded by the FoxPro column width). Reject anything that
        /// can't possibly be a Lacerte ClientNumber BEFORE delegating to
        /// the session, mirroring the bulk-write pre-flight pattern.
        /// </summary>
        public const int MaxClientNumberLength = 16;

        // Compiled once so the per-call validation stays cheap (well under a
        // microsecond for the typical 4-char ClientNumber). Pattern matches
        // the sim store's monotonic emitter and the SDK's documented
        // ClientNumber shape: digits only, length >= 1.
        private static readonly Regex ClientNumberPattern =
            new Regex("^[0-9]+$", RegexOptions.Compiled);

        [McpServerTool]
        [Description(
            "Export one client as a portable, self-documenting envelope keyed by the canonical "
            + "lacerte://client/{clientNumber} URI. Returns { uri, clientNumber, found, context: "
            + "{ dataPath, taxYear, module }, client: { ...row... }, hint? } — the context echo "
            + "pins the (datapath, year, module) the row was resolved against, so an LLM that pins "
            + "the export across a context switch still has the original resolution context to "
            + "compare against. When the client is missing, `found` is false, `client` is null, "
            + "and `hint` describes the recovery path. Mirrors the JSON shape of an MCP Resource "
            + "read against the same URI template; the explicit `uri` field lets host-side caches "
            + "key the envelope without needing the resources channel.")]
        public Task<string> LacerteExportClient(
            [Description(
                "The ClientNumber (sim-assigned monotonic identifier; e.g. \"1001\"). Digits only, "
                + "1-16 chars. NOTE: ClientNumber is the SDK-minted identifier (sim's monotonic "
                + "counter), distinct from the operator-chosen ClientID — these are not interchangeable.")]
            string clientNumber,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // Defensive pre-flight — reject obvious garbage before touching
                // the access map / idempotency cache / wire path, the same
                // pattern the bulk-write tools follow. Empty/whitespace and
                // non-digit inputs surface as ArgumentException → wrapped
                // 9203 by WrapToolHandler, preserving the structured-error
                // contract every other tool emits.
                if (string.IsNullOrWhiteSpace(clientNumber))
                {
                    throw new ArgumentException(
                        "clientNumber must be a non-empty digit string (e.g. \"1001\"). "
                        + "To enumerate available ClientNumbers in the active context, call "
                        + "lacerte_client_list. Note: ClientNumber is the SDK-minted identifier "
                        + "(sim's monotonic counter), distinct from the operator-chosen ClientID.",
                        nameof(clientNumber));
                }
                if (clientNumber.Length > MaxClientNumberLength
                    || !ClientNumberPattern.IsMatch(clientNumber))
                {
                    throw new ArgumentException(
                        $"clientNumber '{clientNumber}' is not a valid ClientNumber shape — "
                        + "expected digits only, 1-16 chars. Note: ClientNumber is the SDK-minted "
                        + "identifier (sim's monotonic counter), distinct from the operator-chosen "
                        + "ClientID (e.g. \"BROW\"). Call lacerte_client_find to resolve a name "
                        + "or ClientID to a ClientNumber.",
                        nameof(clientNumber));
                }

                // Delegate through the session — preserves the
                // RequireRegistered / RequireContext / ReadOnly gates so the
                // export tool can never observe a row the rest of the
                // surface couldn't. The session call also handles the
                // sim/live split, so we get observational identity for
                // free per the two-mode discipline.
                var row = await _session.ReadClient(clientNumber, cancellationToken)
                    .ConfigureAwait(false);

                // Snapshot the active context AFTER ReadClient so we observe
                // the same triple ReadClient resolved against — if a parallel
                // SetContext landed between RequireContext and here it would
                // mis-echo the resolution context, defeating the whole point
                // of the export envelope. ReadClient holds a brief read on
                // the session; the post-call snapshot is the narrowest
                // window we can take without exposing internals.
                var ctx = _session.Context;
                var contextEcho = ctx == null
                    ? null
                    : (object)new
                    {
                        dataPath = ctx.DataPath,
                        // P16-069/P16-079 — canonical wire name `taxYear`.
                        taxYear = ctx.Year,
                        // R6-18 — the `module` wire key carries the canonical
                        // single-character DBF code ("I"), matching every
                        // other tool so cross-tool joins work. The lowercase
                        // file basename ('I' -> "individual", 'S' -> "scorp")
                        // still carries rendering info, so it moves to its
                        // own key instead of overloading `module`.
                        module = ctx.Module.ToString(),
                        moduleFileBasename = Modules.FileBasenameFor(ctx.Module),
                    };

                var uri = $"lacerte://client/{clientNumber}";

                if (row == null)
                {
                    // Match LacerteClientRead's not-found envelope shape so
                    // an LLM that already knows how to parse the read tool's
                    // {found:false, hint} contract doesn't need a new code
                    // path for the export tool. The `uri` field is still
                    // emitted on the miss path so an LLM that built the
                    // envelope for caching can still key it.
                    return (object)new
                    {
                        uri,
                        clientNumber,
                        found = false,
                        context = contextEcho,
                        client = (object?)null,
                        hint = $"ClientNumber '{clientNumber}' not present in the active "
                            + "(datapath, year, module). Call lacerte_client_list to enumerate "
                            + "available ClientNumbers, or lacerte_create_client to create one. "
                            + "Note: ClientNumber is the SDK-minted identifier (sim's monotonic "
                            + "counter, e.g. \"1001\"), distinct from the operator-chosen "
                            + "ClientID (e.g. \"BROW\") — these are not interchangeable.",
                    };
                }

                return new
                {
                    uri,
                    clientNumber,
                    found = true,
                    context = contextEcho,
                    client = row,
                };
            });
    }
}
