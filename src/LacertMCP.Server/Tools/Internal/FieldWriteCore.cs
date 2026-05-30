using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Tools.Internal
{
    /// <summary>
    /// Shared implementation for the two write entrypoints
    /// (<see cref="FieldWriteTools.LacerteFieldWrite"/> and
    /// <see cref="UpdateReturnTools.LacerteUpdateReturn"/>). Both surfaces
    /// funnel through <see cref="LacerteSessionManager.WriteField"/> with the
    /// same parameter list and the same response shape — keeping the body
    /// here prevents silent drift if/when fields are added to the response
    /// envelope (e.g. severity, changedAt, idempotencyReplay).
    ///
    /// <para>
    /// Each public tool keeps its own <c>[McpServerTool]</c> attribute and
    /// per-parameter <c>[Description]</c> wording (model-facing surface
    /// intentionally differs); only the body delegates here. See findings
    /// P14-026 (drop the misleading <c>api</c> field) and P14-051
    /// (collapse the two write tools).
    /// </para>
    /// </summary>
    internal static class FieldWriteCore
    {
        /// <summary>
        /// Runs the canonical write envelope. Response shape is exactly
        /// <c>{statusCode, statusName, table, clientNumber, field, dryRun}</c>
        /// — locked by <c>UpdateReturnToolsTests</c> /
        /// <c>FieldWriteToolsTests</c>. Add new fields only if both tools
        /// should surface them.
        /// </summary>
        public static Task<string> Run(
            LacerteSessionManager session,
            int table,
            string clientNumber,
            string field,
            object? value,
            int? subClientID,
            string? stateAbbrev,
            bool dryRun,
            string? idempotencyKey)
        {
            return WrapToolHandler.Run(async () =>
            {
                var status = await session.WriteField(
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
                };
            });
        }
    }
}
