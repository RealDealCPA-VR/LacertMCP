using System.Threading;
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
        /// Reads the written field back and throws 9022 WriteNotVerified when the
        /// stored value differs. Shared by the single and bulk field-write tools.
        /// </summary>
        public static async Task<FieldWriteVerification> VerifyOrThrow(
            LacerteSessionManager session, string op, int table, string clientNumber, string field,
            object? value, int? subClientId, string? stateAbbrev, CancellationToken cancellationToken)
        {
            var v = await session.VerifyFieldWrite(table, clientNumber, field, value,
                subClientId, stateAbbrev, cancellationToken).ConfigureAwait(false);
            if (v.Verified == false)
            {
                throw new LacerteSDKError(SyntheticStatusCodes.WriteNotVerified,
                    new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["op"] = op,
                        ["table"] = table,
                        ["clientNumber"] = clientNumber,
                        ["field"] = field,
                        ["written"] = value?.ToString(),
                        ["readBack"] = v.ReadBack?.ToString(),
                        ["reason"] = v.Note,
                    });
            }
            return v;
        }

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
            // P16-054/P16-080 — canonical C# casing matches the wire form.
            int? subClientId,
            string? stateAbbrev,
            bool dryRun,
            string? idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                var status = await session.WriteField(
                    table, clientNumber, field, value,
                    subClientId, stateAbbrev, dryRun, idempotencyKey, cancellationToken);

                var verification = dryRun
                    ? null
                    : await VerifyOrThrow(session, "WriteField", table, clientNumber, field, value,
                        subClientId, stateAbbrev, cancellationToken).ConfigureAwait(false);

                var info = StatusCodes.GetStatusInfo(status);
                return (object)new
                {
                    statusCode = status,
                    statusName = info.Name,
                    table,
                    clientNumber,
                    field,
                    dryRun,
                    // true = read back from Lacerte and matched; null = dry run,
                    // or the row couldn't be identified (see verificationNote).
                    verified = verification?.Verified,
                    verificationNote = verification?.Note,
                };
            });
        }
    }
}
