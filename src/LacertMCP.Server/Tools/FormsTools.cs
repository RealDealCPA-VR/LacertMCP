using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA7 (Form Info) read tool. Per <c>Todo.md</c> Phase 5 task #47.
    /// Returns the form list in the return for one client. Read-only.
    ///
    /// <para>
    /// Audit P16-047 — paginated envelope (limit/offset/total/hasMore/
    /// nextOffset) so every DATA-row read tool shares one shape the LLM can
    /// memorize. Audit P16-076 — accepts CancellationToken so a slow read
    /// on a complex return can be aborted.
    /// </para>
    /// </summary>
    [McpServerToolType]
    public sealed class FormsTools
    {
        private readonly LacerteSessionManager _session;

        public FormsTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA7 form rows for one client. Returns the list of forms attached to the return. Read-only. Supports limit/offset pagination.")]
        public Task<string> LacerteFormsRead(
            [Description("The ClientNumber whose form rows to read.")] string clientNumber,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the result set. Default 0.")] int offset = 0,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadForms(clientNumber, cancellationToken).ConfigureAwait(false);
                var page = Pagination.Pagination.Page(rows, limit, offset);
                // P16-024 — canonical `rows` key for cross-tool consistency.
                // `forms` retained as a v0.1 alias (removed in v0.2).
                // P16-047 — paginated envelope.
                return new
                {
                    clientNumber,
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    forms = page.Rows,
                };
            });
    }
}
