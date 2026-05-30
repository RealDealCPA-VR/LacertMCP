using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
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
        [Description("TY21+ explicit write entrypoint (UpdateTaxReturn under the hood, keyed by ClientNumber). Equivalent surface to lacerte_field_write — both go through the same session chokepoint which year-routes the backend call. Use this when you specifically want the TY21+ API in a TY21+ context. Returns the TAPIStatus integer (1=Success).")]
        public Task<string> LacerteUpdateReturn(
            [Description("DBF table number (1..7).")] int table,
            [Description("Lacerte ClientNumber (the C{t}_0 identifier).")] string clientNumber,
            [Description("Field name (e.g. C1_1).")] string field,
            [Description("Value to write. JSON-serializable scalar — string, number, or boolean.")] object? value,
            [Description("Required for DATA2 / DATA4 writes. Positive integer.")] int? subClientID = null,
            [Description("Required for DATA3 / DATA4 writes. 2-letter state postal code.")] string? stateAbbrev = null,
            [Description("If true, validate + roll back without persisting.")] bool dryRun = false,
            [Description("Optional idempotency key.")] string? idempotencyKey = null)
        {
            return WrapToolHandler.Run(async () =>
            {
                var status = await _session.WriteField(
                    table, clientNumber, field, value,
                    subClientID, stateAbbrev, dryRun, idempotencyKey);

                var info = StatusCodes.GetStatusInfo(status);
                return (object)new
                {
                    statusCode = status,
                    statusName = info.Name,
                    table,
                    clientNumber,
                    field,
                    dryRun,
                    api = "UpdateTaxReturn",
                };
            });
        }
    }
}
