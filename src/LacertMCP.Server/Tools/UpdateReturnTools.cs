using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools.Internal;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Phase 6 task #52: <c>lacerte_update_return</c> — the TY21+
    /// <c>UpdateTaxReturn</c> entrypoint, keyed by ClientNumber (not row
    /// number, which was the pre-TY21 model).
    ///
    /// <para>
    /// Architectural note: both <see cref="FieldWriteTools.LacerteFieldWrite"/>
    /// and this tool funnel through <see cref="LacerteSessionManager.WriteField"/>,
    /// which year-routes internally per boundary invariant #5. The two tools
    /// exist as separate MCP entrypoints because the SDK developer docs treat
    /// them as separate APIs; the model gets a clearer surface this way. The
    /// session manager guarantees the right backend call lands in either case.
    /// </para>
    ///
    /// <para>
    /// Today, when the active context's year is &lt; 2021, calling this tool
    /// will still produce the pre-TY21 wire effect (because the session
    /// router is year-aware). That's intentional — we choose convenience for
    /// the operator over strict per-tool year gating. Callers who need an
    /// explicit pre-TY21 path should use <c>lacerte_field_write</c>.
    /// </para>
    ///
    /// <para>
    /// The response intentionally does NOT carry an <c>api</c> label — the
    /// tool name signals operator intent, but the live wire choice belongs to
    /// the session manager (Boundary Invariant #5). A previous version
    /// hard-coded <c>api = "UpdateTaxReturn"</c> which was provably wrong for
    /// pre-TY21 contexts (finding P14-026). Body lives in
    /// <see cref="FieldWriteCore"/> so both write tools cannot drift.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class UpdateReturnTools
    {
        private readonly LacerteSessionManager _session;

        public UpdateReturnTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Alias for lacerte_field_write kept to mirror Intuit's TY21+ UpdateTaxReturn developer-doc surface. Behavior is identical: both funnel through the same session chokepoint and the session year-routes the backend call. Prefer lacerte_field_write in new code — only use this tool when a user prompt or workflow explicitly references the UpdateTaxReturn SDK API by name. Returns the TAPIStatus integer (1=Success).")]
        public Task<string> LacerteUpdateReturn(
            [Description("DBF table number. 1..5 are writable; 6 (Display) and 7 (Forms) are read-only per spec §2.")] int table,
            [Description("Lacerte ClientNumber (the C{t}_0 identifier).")] string clientNumber,
            [Description("Field name (e.g. C1_1).")] string field,
            [Description("Value to write. JSON-serializable scalar — string, number, or boolean.")] object? value,
            // P16-054/P16-080 — canonical wire casing `subClientId`.
            [Description("Required for DATA2 / DATA4 writes. Positive integer.")] int? subClientId = null,
            [Description("Required for DATA3 / DATA4 writes. 2-letter state postal code.")] string? stateAbbrev = null,
            [Description("If true, validate + roll back without persisting.")] bool dryRun = false,
            [Description("STRONGLY RECOMMENDED for any write that could be retried. Pass a stable string keyed on op+target+intent (e.g. 'update_return-{clientNumber}-{field}') so the server replays the prior result for matching key+payload and rejects with 9102 IdempotencyConflict on conflict. Without this key, an MCP transport timeout + LLM auto-retry re-executes the write — safe at the cell level for lacerte_update_return but the LLM cannot verify whether the first call landed. Same key + same payload: cached replay (idempotent for 24h). Same key + different payload: 9102 IdempotencyConflict.")] string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            return FieldWriteCore.Run(
                _session, table, clientNumber, field, value,
                subClientId, stateAbbrev, dryRun, idempotencyKey, cancellationToken);
        }
    }
}
