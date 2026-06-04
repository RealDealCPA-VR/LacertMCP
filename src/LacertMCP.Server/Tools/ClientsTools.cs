using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA1 (Client Info) read tools. Per <c>Todo.md</c> Phase 5 task #42.
    /// Delegates to <see cref="LacerteSessionManager.ListClients"/> and
    /// <see cref="LacerteSessionManager.ReadClient"/>.
    /// </summary>
    [McpServerToolType]
    public sealed class ClientsTools
    {
        private readonly LacerteSessionManager _session;

        public ClientsTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("List clients in the active (datapath, year, module) context. Optional case-insensitive prefix filter on last-name (individual modules) or entity name (corporate/partnership/etc.). Supports limit/offset pagination — limit semantics: omit (null) for the default page size (100); pass a positive int up to Pagination.MaxLimit (1000) for a custom page; pass 0 for a count-only response (rows=[], total=N); negative values are rejected.")]
        public Task<string> LacerteClientList(
            [Description("Optional case-insensitive prefix to match against the conventional name field (C1_2 for individual modules, C1_1 for entity modules).")] string? nameFilter = null,
            [Description("Maximum rows to return. Omit (null) for the default page size (100). Pass a positive int up to Pagination.MaxLimit (1000) for a custom page. Pass 0 to get a count-only response (rows=[], total=N). Negative values are rejected.")] int? limit = null,
            [Description("Zero-based row offset into the filtered result set. Omit (null) for offset=0.")] int? offset = null,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                // P16-075 — surface a safe, four-state limit contract on the wire:
                //   null     -> default page (Pagination.DefaultLimit = 100)
                //   0        -> count-only (rows=[], total=N) — no MaxLimit bypass
                //   positive -> clamped to Pagination.MaxLimit (handled by helper)
                //   negative -> ArgumentOutOfRangeException
                //
                // We normalize BEFORE calling Pagination.Page so the helper's
                // current "non-positive = unbounded" branch (which bypasses
                // MaxLimit) is never reached from this tool. The four-state
                // contract is locked in here; an LLM that hallucinates
                // `limit: 0` gets a safe empty-rows envelope with the total,
                // not the full firm-history.
                if (limit.HasValue && limit.Value < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(limit),
                        "limit must be null (default), 0 (count-only), or a positive int up to Pagination.MaxLimit (1000).");
                }
                int effectiveLimit = !limit.HasValue
                    ? Pagination.Pagination.DefaultLimit
                    : limit.Value; // 0 or positive — helper handles positive; we special-case 0 below.

                // Pull the full filtered set from the session, then page in-tool via
                // Pagination.Page<T> so 'total' is exact.
                //
                // PAGINATION LIMITATION (audit P14-014, cluster N_sql): the Lacerte
                // ODBC driver (FoxPro-style) does NOT support OFFSET / FETCH NEXT /
                // ROW_NUMBER(), so true server-side OFFSET-based paging is not
                // available. The live-mode push-down templates SqlTemplates.Data1Top
                // and Data1TopWhereName cap rows via TOP n but cannot skip; until the
                // session exposes a (rows, total) overload that does an in-memory
                // slice while preserving an exact total, this tool overfetches via
                // int.MaxValue and slices here. Sim is in-memory so the overfetch is
                // free; live mode will eat the full-table scan cost per paged request
                // — slated for a session-level fix in a future phase.
                var rows = await _session.ListClients(nameFilter, int.MaxValue, cancellationToken).ConfigureAwait(false);

                if (limit.HasValue && limit.Value == 0)
                {
                    // Count-only: surface total without materializing rows on the
                    // wire. Page through the helper with limit=1 just to get exact
                    // total + correct hasMore/nextOffset semantics relative to the
                    // requested offset, then blank the rows.
                    var probe = Pagination.Pagination.Page(rows, 1, offset);
                    return new
                    {
                        rows = System.Array.Empty<object>(),
                        total = probe.Total,
                        limit = 0,
                        offset = probe.Offset,
                        hasMore = probe.Total > probe.Offset,
                        nextOffset = probe.Total > probe.Offset ? (int?)probe.Offset : null,
                        // legacy (v0.1) — remove in v0.2:
                        count = 0,
                        clients = System.Array.Empty<object>(),
                    };
                }

                var page = Pagination.Pagination.Page(rows, effectiveLimit, offset);

                // NOTE: `count` and `clients` are legacy fields kept for additive
                // backwards-compat with pre-pagination consumers. They will be
                // removed in v0.2 — new code should read `rows` + `total`.
                return new
                {
                    rows = page.Rows,
                    total = page.Total,
                    limit = page.Limit,
                    offset = page.Offset,
                    hasMore = page.HasMore,
                    nextOffset = page.NextOffset,
                    // legacy (v0.1) — remove in v0.2:
                    count = page.Rows.Count,
                    clients = page.Rows,
                };
            });

        [McpServerTool]
        [Description("Read one client by ClientNumber from DATA1 in the active context. Returns an envelope { clientNumber, found, client, hint? } — when no such client exists in the active context, `found` is false, `client` is null, and `hint` describes the recovery path (call lacerte_client_list to enumerate available ClientNumbers). The envelope itself is never null; check `found` (or `client != null`), not the top-level result.")]
        public Task<string> LacerteClientRead(
            [Description("The ClientNumber (sim-assigned monotonic identifier; e.g. \"1001\"). NOTE: ClientNumber is the SDK-minted identifier (sim's monotonic counter), distinct from the operator-chosen ClientID — these are not interchangeable.")] string clientNumber,
            CancellationToken cancellationToken = default)
            => WrapToolHandler.Run(async () =>
            {
                var row = await _session.ReadClient(clientNumber, cancellationToken).ConfigureAwait(false);
                if (row == null)
                {
                    // P16-081 — add a `hint` field so an LLM scanning the envelope
                    // cannot mis-summarize `client: null` as "the record exists but
                    // is empty". The hint also disambiguates the ClientNumber vs
                    // ClientID confusion, which is the second most common cause of
                    // this failure.
                    return (object)new
                    {
                        clientNumber,
                        found = false,
                        client = (object?)null,
                        hint = $"ClientNumber '{clientNumber}' not present in the active (datapath, year, module). Call lacerte_client_list to enumerate available ClientNumbers, or lacerte_create_client to create one. Note: ClientNumber is the SDK-minted identifier (sim's monotonic counter, e.g. \"1001\"), distinct from the operator-chosen ClientID (e.g. \"BROW\") — these are not interchangeable.",
                    };
                }
                return new { clientNumber, found = true, client = row };
            });
    }
}
