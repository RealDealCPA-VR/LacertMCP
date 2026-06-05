using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// Phase 5 task #48 + Phase 6 task #53: per-client detail-file read +
    /// write. The detail file is the per-client <c>{CLIENT}.{Mod}{YY}</c>
    /// row-ordered store with no primary key.
    ///
    /// <para>
    /// <b>CRITICAL — the write path runs load-bearing client-side validation
    /// BEFORE calling the session.</b> The SDK does NOT validate detail-row
    /// shape (per agent 06 §5); a malformed row silently corrupts the
    /// return. We enforce here:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>Series</c> numeric (matches <c>^\d+$</c>) — Lacerte screen identifier per spec §2 (11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners). Kept typed as <c>string</c> on the wire to preserve <see cref="DetailRow"/> shape, but the numeric-string invariant guarantees a parseable int so <see cref="LacertMCP.Server.Odbc.SqlTemplates.DetailBySeries"/> agrees with sim predicates (audit task P14-055).</description></item>
    ///   <item><description><c>Code</c> numeric (matches <c>^-?\d+$</c>) — leading minus allowed for negative codes. Same string-on-the-wire / numeric-string-invariant contract as Series.</description></item>
    ///   <item><description><c>StateAbbr</c> matches <c>^[A-Z]{2}$</c> when non-empty.</description></item>
    ///   <item><description><c>Amount</c> numeric (already typed as <c>double?</c> — the JSON-binding stage will throw before reaching us if a non-number lands).</description></item>
    /// </list>
    /// </summary>
    [McpServerToolType]
    public class DetailsTools
    {
        // 2-letter state postal-code shape. Mirrors Odbc/SqlTemplates.cs and
        // agent 06 §5 detail validation table.
        // P16-025/P16-045 — operator-facing wire parameter is `stateAbbrev`;
        // DetailRow.StateAbbr (model property) keeps its name because it maps
        // to the DBF column.
        private static readonly Regex _rxStateAbbrev = new Regex(
            @"^[A-Z]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Numeric Code shape — Lacerte detail Code fields are integer-valued
        // per the detail-file spec. Allow leading minus for negative codes;
        // do NOT allow decimals (use Amount for those).
        private static readonly Regex _rxNumeric = new Regex(
            @"^-?\d+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Numeric Series shape — Lacerte Series IDs are non-negative screen
        // identifiers per spec §2 (no negative Series exists). Kept narrower
        // than _rxNumeric so a `-5` value is rejected with a Series-specific
        // error rather than slipping through. Audit task P14-055 (lighter-fix
        // path): DetailRow.Series stays `string` on the wire — the regex is
        // the invariant that makes the string safe to interpolate into the
        // unquoted-integer SQL emitted by SqlTemplates.DetailBySeries.
        private static readonly Regex _rxNumericSeries = new Regex(
            @"^\d+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly LacerteSessionManager _session;

        public DetailsTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Read detail rows for a client from the per-client {CLIENT}.{Mod}{YY} file in original row order. Supports limit/offset pagination: detail files are unbounded by design (every W-2/1099/dependent/partner row), so paging is the canonical defense against MCP message-size limits. Optional `series` push-down narrows to one Lacerte Series ID (11=W-2, 12=Interest, 13=Dividends, 14=Dependents, 20=Income, 30=State, 40=Partners) — live mode dispatches SqlTemplates.DetailBySeries, sim mode filters the in-memory list. Returns the paginated envelope {rows, total, limit, offset, hasMore, nextOffset} plus legacy {count} for v0.1 back-compat.")]
        public Task<string> LacerteDetailRead(
            [Description("Lacerte ClientNumber (the C{t}_0 identifier).")] string clientNumber,
            [Description("Maximum rows to return. Default 100, clamped to Pagination.MaxLimit (1000).")] int limit = 100,
            [Description("Zero-based row offset into the result set. Default 0.")] int offset = 0,
            [Description("Optional Series push-down. Pass a numeric Lacerte Series identifier — e.g. 11 (W-2), 12 (Interest), 13 (Dividends), 14 (Dependents), 20 (Income), 30 (State), 40 (Partners) — to narrow to just that screen's rows. Null = no filter (full detail file).")] int? series = null,
            CancellationToken cancellationToken = default)
        {
            return WrapToolHandler.Run(async () =>
            {
                var allRows = await _session.ReadDetail(clientNumber, series, cancellationToken);
                var page = Pagination.Pagination.Page(allRows, limit, offset);

                // NOTE: `count` and the legacy `rows`-as-full-set semantic are
                // preserved additively for v0.1 — `rows` now holds the paged
                // slice (same key, narrower view) and `count` reflects the
                // slice size, matching the TablesTools.cs:30-52 pattern. New
                // consumers should read `total` for the unpaged size and
                // `hasMore`/`nextOffset` to drive paging. v0.2 will drop the
                // legacy `count` field per DECISIONS.md pagination contract.
                return (object)new
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
                };
            });
        }

        [McpServerTool]
        [Description("Append a detail row to a client's detail file, or replace the row at replaceIndex if supplied. Runs MANDATORY client-side validation before the session call (the SDK does not validate detail-row shape). Returns the TAPIStatus integer (1=Success). Only clientNumber, series, code, amount, description are required — the row-key segments (prefix, suffix, stateAbbrev, cityAbbr, source, tsj, importSrc) default to empty string and can be omitted for the common W-2 / 1099 happy path.")]
        public Task<string> LacerteDetailWrite(
            [Description("Lacerte ClientNumber.")] string clientNumber,
            [Description("Numeric Series identifier per Lacerte spec §2 — must match ^\\d+$ (e.g. '11'=W-2, '12'=Interest, '13'=Dividends, '14'=Dependents, '20'=Income, '30'=State, '40'=Partners). Required.")] string series,
            [Description("Numeric Code (must parse as integer). Required.")] string code,
            [Description("Dollar amount as a number (or null for non-numeric rows like dependent listings). Required.")] double? amount,
            [Description("Free-form description for the row. Required.")] string description,
            // P16-008 — the seven row-key segments below default to empty
            // string so the common W-2 happy path can omit them. C# requires
            // defaulted params come last; required params (clientNumber,
            // series, code, amount, description) reorder above.
            [Description("Prefix string. Defaults to empty — omit unless the row's natural key needs a prefix segment.")] string prefix = "",
            [Description("Suffix string. Defaults to empty — omit unless the row's natural key needs a suffix segment.")] string suffix = "",
            // P16-025/P16-045 — operator-facing wire name is `stateAbbrev`
            // (matches every other write tool). DetailRow.StateAbbr (the
            // model property) keeps its name because it maps to the DBF
            // column.
            [Description("State abbreviation for the row's tax bucket — 2-letter postal code if non-empty (validated against ^[A-Z]{2}$). Defaults to empty. Maps to the DBF column StateAbbr internally.")] string stateAbbrev = "",
            [Description("City abbreviation. Defaults to empty — omit unless the row carries a city-level breakdown.")] string cityAbbr = "",
            [Description("Source tag (e.g. 'W2', '1099INT'). Defaults to empty.")] string source = "",
            [Description("Taxpayer/Spouse/Joint indicator — one of T, S, J, or empty. Defaults to empty.")] string tsj = "",
            [Description("Import source tag. Defaults to empty — omit unless the row originated from an import workflow.")] string importSrc = "",
            [Description("Optional zero-based index to overwrite an existing row instead of appending.")] int? replaceIndex = null,
            [Description("If true, validate + simulate the write but roll back without persisting.")] bool dryRun = false,
            [Description("STRONGLY RECOMMENDED for any write that could be retried. Pass a stable string keyed on op+target+intent (e.g. 'detail_write-{clientNumber}-{series}-{code}') so the server replays the prior result for matching key+payload and rejects with 9102 IdempotencyConflict on conflict. Without this key, an MCP transport timeout + LLM auto-retry will APPEND a duplicate detail row (detail writes default to append when replaceIndex is null — silently doubling a W-2/1099/dependent row). Same key + same payload: cached replay (idempotent for 24h). Same key + different payload: 9102 IdempotencyConflict.")] string? idempotencyKey = null)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Load-bearing client-side validation. Runs BEFORE the session
                // call so both sim and live get the same gating. Failure path:
                // throw LacerteSDKError(9008 DetailWriteInvalid) with a
                // per-field reason in context.
                // -----------------------------------------------------------
                ValidateDetailWrite(series, code, stateAbbrev, tsj);

                var row = new DetailRow(
                    Series: series,
                    Prefix: prefix ?? "",
                    Code: code,
                    Suffix: suffix ?? "",
                    // DetailRow.StateAbbr (model) maps to the DBF column;
                    // operator-facing wire name is stateAbbrev (P16-025).
                    StateAbbr: stateAbbrev ?? "",
                    CityAbbr: cityAbbr ?? "",
                    Source: source ?? "",
                    TSJ: tsj ?? "",
                    ImportSrc: importSrc ?? "",
                    Amount: amount,
                    Description: description ?? "");

                var status = await _session.WriteDetail(
                    clientNumber, row, replaceIndex, dryRun, idempotencyKey);

                var info = StatusCodes.GetStatusInfo(status);
                return (object)new
                {
                    statusCode = status,
                    statusName = info.Name,
                    clientNumber,
                    replaceIndex,
                    dryRun,
                };
            });
        }

        private static void ValidateDetailWrite(string series, string code, string stateAbbrev, string tsj)
        {
            if (string.IsNullOrWhiteSpace(series))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "series",
                        ["reason"] = "Series is required.",
                    });
            }

            // P14-055 lighter-fix: enforce numeric-string invariant so the
            // string is guaranteed to be a parseable non-negative integer.
            // Together with the seed fixture using numeric Series strings,
            // this keeps DetailRow.Series typed `string` on the wire while
            // making SqlTemplates.DetailBySeries (which emits an unquoted
            // integer literal) observationally consistent between sim and
            // live.
            if (!_rxNumericSeries.IsMatch(series))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "series",
                        ["value"] = series,
                        ["reason"] = "Series must be a numeric Lacerte screen identifier (^\\d+$) per spec §2 — e.g. '11'=W-2, '12'=Interest, '13'=Dividends, '14'=Dependents, '20'=Income, '30'=State, '40'=Partners.",
                    });
            }

            if (string.IsNullOrEmpty(code) || !_rxNumeric.IsMatch(code))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "code",
                        ["value"] = code,
                        ["reason"] = "Code must be a numeric integer (e.g. '1', '12', '-3').",
                    });
            }

            if (!string.IsNullOrEmpty(stateAbbrev) && !_rxStateAbbrev.IsMatch(stateAbbrev))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        // P16-025/P16-045 — error context echoes the
                        // operator-facing wire name `stateAbbrev`.
                        ["field"] = "stateAbbrev",
                        ["value"] = stateAbbrev,
                        ["reason"] = "stateAbbrev must be empty OR a 2-letter uppercase postal code (e.g. CA, NY).",
                    });
            }

            if (!string.IsNullOrEmpty(tsj))
            {
                if (tsj.Length != 1 || (tsj != "T" && tsj != "S" && tsj != "J"))
                {
                    throw new LacerteSDKError(
                        SyntheticStatusCodes.DetailWriteInvalid,
                        new Dictionary<string, object?>
                        {
                            ["field"] = "tsj",
                            ["value"] = tsj,
                            ["reason"] = "TSJ must be empty OR one of: T, S, J.",
                        });
                }
            }
        }
    }
}
