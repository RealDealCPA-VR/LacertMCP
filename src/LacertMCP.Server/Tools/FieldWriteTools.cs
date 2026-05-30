using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools.Internal;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Phase 6 task #51: <c>lacerte_field_write</c> — the pre-TY21
    /// <c>WriteToDBFDatabase</c> entrypoint.
    ///
    /// <para>
    /// The session manager's <see cref="LacerteSessionManager.WriteField"/>
    /// routes internally by year (see boundary invariant #5 in
    /// <c>ARCHITECTURE.md</c>): pre-TY21 goes through <c>WriteToDBFDatabase</c>
    /// in live mode, TY21+ goes through <c>UpdateTaxReturn</c>. We expose two
    /// MCP tools (this one + <see cref="UpdateReturnTools"/>) for clarity to
    /// the model — both end up at the same chokepoint, but the surface naming
    /// matches the Lacerte SDK developer instructions.
    /// </para>
    ///
    /// <para>
    /// Read-only / dry-run / idempotency are handled by the session manager;
    /// the tool just plumbs the parameters through. Body lives in
    /// <see cref="FieldWriteCore"/> so both write tools cannot drift.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public class FieldWriteTools
    {
        private readonly LacerteSessionManager _session;

        public FieldWriteTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Write a single field value to a DATA1..DATA5 row for the active context (DATA6/DATA7 are read-only per spec). Pre-TY21 path uses WriteToDBFDatabase under the hood; TY21+ silently routes to UpdateTaxReturn (use lacerte_update_return for an explicit entrypoint). Returns the TAPIStatus integer (1=Success).")]
        public Task<string> LacerteFieldWrite(
            [Description("DBF table number. 1=Client, 2=SubClient, 3=State, 4=StateSubClient, 5=Status are writable; 6=Display and 7=Forms are read-only per spec §2 and will be rejected with ExposedForRead.")] int table,
            [Description("Lacerte ClientNumber (the C{t}_0 identifier, not the operator-chosen ClientID).")] string clientNumber,
            [Description("Field name (e.g. C1_1, C2_15, C4_G3). Must be a writable field in the access map.")] string field,
            [Description("Value to write. JSON-serializable scalar — string, number, or boolean.")] object? value,
            [Description("Required for DATA2 / DATA4 writes. Positive integer identifying the dependent/partner/shareholder row.")] int? subClientID = null,
            [Description("Required for DATA3 / DATA4 writes. 2-letter state postal code (e.g. CA, NY).")] string? stateAbbrev = null,
            [Description("If true, run the full validation + access-map gate then roll back without persisting. Returns the would-be TAPIStatus.")] bool dryRun = false,
            [Description("Optional idempotency key. Same key + same payload returns the cached result; same key + different payload throws 9102 IdempotencyConflict.")] string? idempotencyKey = null)
        {
            return FieldWriteCore.Run(
                _session, table, clientNumber, field, value,
                subClientID, stateAbbrev, dryRun, idempotencyKey);
        }
    }
}
