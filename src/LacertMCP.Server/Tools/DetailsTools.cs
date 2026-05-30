using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
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
    ///   <item><description><c>Series</c> non-empty (v1 accepts any string — TODO: real per-module Series enum).</description></item>
    ///   <item><description><c>Code</c> numeric.</description></item>
    ///   <item><description><c>StateAbbr</c> matches <c>^[A-Z]{2}$</c> when non-empty.</description></item>
    ///   <item><description><c>Amount</c> numeric (already typed as <c>double?</c> — the JSON-binding stage will throw before reaching us if a non-number lands).</description></item>
    /// </list>
    /// </summary>
    [McpServerToolType]
    public class DetailsTools
    {
        // 2-letter state postal-code shape. Mirrors Odbc/SqlTemplates.cs and
        // agent 06 §5 detail validation table.
        private static readonly Regex _rxStateAbbr = new Regex(
            @"^[A-Z]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Numeric Code shape — Lacerte detail Code fields are integer-valued
        // per the detail-file spec. Allow leading minus for negative codes;
        // do NOT allow decimals (use Amount for those).
        private static readonly Regex _rxNumeric = new Regex(
            @"^-?\d+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly LacerteSessionManager _session;

        public DetailsTools(LacerteSessionManager session)
        {
            _session = session;
        }

        [McpServerTool]
        [Description("Read all detail rows for a client from the per-client {CLIENT}.{Mod}{YY} file. No filtering — returns the full ordered list.")]
        public Task<string> LacerteDetailRead(
            [Description("Lacerte ClientNumber (the C{t}_0 identifier).")] string clientNumber)
        {
            return WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadDetail(clientNumber);
                return (object)new
                {
                    clientNumber,
                    count = rows.Count,
                    rows,
                };
            });
        }

        [McpServerTool]
        [Description("Append a detail row to a client's detail file, or replace the row at replaceIndex if supplied. Runs MANDATORY client-side validation before the session call (the SDK does not validate detail-row shape). Returns the TAPIStatus integer (1=Success).")]
        public Task<string> LacerteDetailWrite(
            [Description("Lacerte ClientNumber.")] string clientNumber,
            [Description("Series identifier (e.g. 'Wages', 'Int', 'Div', 'Dep'). Required.")] string series,
            [Description("Prefix string (often empty).")] string prefix,
            [Description("Numeric Code (must parse as integer).")] string code,
            [Description("Suffix string (often empty).")] string suffix,
            [Description("State abbreviation — 2-letter postal code if non-empty (validated against ^[A-Z]{2}$).")] string stateAbbr,
            [Description("City abbreviation (often empty).")] string cityAbbr,
            [Description("Source tag (e.g. 'W2', '1099INT'). May be empty.")] string source,
            [Description("Taxpayer/Spouse/Joint indicator — one of T, S, J, or empty.")] string tsj,
            [Description("Import source tag (often empty).")] string importSrc,
            [Description("Dollar amount as a number (or null for non-numeric rows like dependent listings).")] double? amount,
            [Description("Free-form description for the row.")] string description,
            [Description("Optional zero-based index to overwrite an existing row instead of appending.")] int? replaceIndex = null,
            [Description("If true, validate + simulate the write but roll back without persisting.")] bool dryRun = false,
            [Description("Optional idempotency key.")] string? idempotencyKey = null)
        {
            return WrapToolHandler.Run(async () =>
            {
                // -----------------------------------------------------------
                // Load-bearing client-side validation. Runs BEFORE the session
                // call so both sim and live get the same gating. Failure path:
                // throw LacerteSDKError(9008 DetailWriteInvalid) with a
                // per-field reason in context.
                // -----------------------------------------------------------
                ValidateDetailWrite(series, code, stateAbbr, tsj);

                var row = new DetailRow(
                    Series: series,
                    Prefix: prefix ?? "",
                    Code: code,
                    Suffix: suffix ?? "",
                    StateAbbr: stateAbbr ?? "",
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

        private static void ValidateDetailWrite(string series, string code, string stateAbbr, string tsj)
        {
            if (string.IsNullOrWhiteSpace(series))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "series",
                        ["reason"] = "Series is required (v1 accepts any non-empty string; real per-module Series enum is a follow-up).",
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

            if (!string.IsNullOrEmpty(stateAbbr) && !_rxStateAbbr.IsMatch(stateAbbr))
            {
                throw new LacerteSDKError(
                    SyntheticStatusCodes.DetailWriteInvalid,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "stateAbbr",
                        ["value"] = stateAbbr,
                        ["reason"] = "StateAbbr must be empty OR a 2-letter uppercase postal code (e.g. CA, NY).",
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
