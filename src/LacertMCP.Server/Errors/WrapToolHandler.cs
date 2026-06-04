using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;
using LacertMCP.Server.Logging;
using LacertMCP.Server.Odbc;

namespace LacertMCP.Server.Errors
{
    /// <summary>
    /// Static helper that wraps an MCP tool handler body in the canonical try/catch +
    /// success-envelope shape required by <see cref="ARCHITECTURE"/> §"Tool Conventions"
    /// and agent 05 §7. Tools never catch <see cref="Exception"/> directly — they always
    /// delegate to <see cref="Run"/>.
    ///
    /// <para>Result shapes:</para>
    /// <list type="bullet">
    ///   <item><description><b>Success</b> →
    ///     <c>{ "success": true, "result": &lt;handler return value&gt;, "cid": "..." }</c>.</description></item>
    ///   <item><description><b><see cref="LacerteSDKError"/></b> →
    ///     <c>{ "success": false, "statusCode", "statusName", "origin", "severity",
    ///     "userMessage", "remediation", "context", "cid" }</c>.
    ///     P15-037/P16-002/P16-060: <c>origin</c> is <c>"sdk"</c> for SDK-origin codes
    ///     (TAPIStatus 0-28, TPSDKStatus 200-204) and <c>"synthetic"</c> for MCP-minted
    ///     codes (>=9000). The same discriminator appears on every <c>lacerte_return_codes</c>
    ///     row as <c>originGroup</c> so an LLM can filter the catalog by it.</description></item>
    ///   <item><description><b><see cref="OdbcException"/></b> → routed via
    ///     <see cref="OdbcErrorMapper.Map(OdbcException, IReadOnlyDictionary{string,object?})"/>
    ///     so SQLSTATE telemetry maps to proper TAPIStatus codes (DBFNotAccessable,
    ///     CodeBaseError, NotExposed, ...).</description></item>
    ///   <item><description><b>Generic <see cref="Exception"/></b> → wrapped as
    ///     <see cref="LacerteSDKError"/> with synthetic
    ///     <see cref="SyntheticStatusCodes.InternalServerError"/> (9203), preserving
    ///     the raw message in <c>context.rawError</c>. P15-035: previously this used
    ///     <see cref="StatusCodes.Error"/> (0) which falsely advertised an SDK origin
    ///     and pointed the operator at Windows Event Viewer for stacks that don't
    ///     live there.</description></item>
    /// </list>
    ///
    /// <para>
    /// Redaction: BOTH the success envelope (which may carry ODBC-read row data
    /// containing SSN/EIN columns) AND the error envelope (whose context dict can
    /// carry write-payload values and COM-echoed exception messages) are run
    /// through <see cref="WireLog.Redact(string, RedactionScope)"/> with
    /// <see cref="RedactionScope.Wire"/> before they leave this method.
    /// </para>
    ///
    /// <para>
    /// P15-001 — wire scope uses a NARROW keyset (true secrets only —
    /// BankAccountNumber / RoutingNumber, every SSN/TIN/EIN variant, DOB,
    /// email, phone). Operator-facing diagnostics (<c>reason</c>,
    /// <c>rawError</c>, detail-row <c>Description</c>, address columns
    /// <c>City</c>/<c>Zip</c>) survive so error envelopes carry actionable
    /// LLM-readable messages and detail-row reads return the seeded text.
    /// The on-disk debug log path (<see cref="WireLog.WriteRecord"/>)
    /// continues to use the broad disk-scope keyset — that asymmetry is
    /// the point of P15-001.
    /// </para>
    ///
    /// <para>
    /// P15-052 — every handler entry installs a per-request correlation ID
    /// (8-hex Guid suffix) via <see cref="WireLog.CurrentCorrelationId"/> so
    /// nested <see cref="WireLog.Record{T}(string, object?, Func{Task{T}})"/>
    /// calls (CanUseNewApis, AutoReconnect retries, sub-ops in the bridge)
    /// inherit it on the disk log. The same cid is stamped on the success
    /// and error envelopes so an LLM client can echo it back in a bug
    /// report and the operator can grep the whole causal chain.
    /// </para>
    /// </summary>
    public static class WrapToolHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            // P16-010 — pass Unicode through verbatim so accented client
            // names (José García), em-dash detail-row descriptions
            // (Form 2106 — Employee Business Expenses), and SDK-emitted
            // localized error text reach the LLM as readable glyphs rather
            // than `\u00XX` escape sequences. JavaScriptEncoder.Default
            // escapes every non-ASCII codepoint, costing extra tokens and
            // hurting model parsing on a transport (JSON-RPC over stdio)
            // that has no need for that level of escaping. Keep HTML-sensitive
            // chars (`<`, `>`, `&`, `'`, `+`) escaped as defense-in-depth in
            // case a future MCP host embeds responses in HTML chrome.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        /// <summary>
        /// Executes <paramref name="handler"/> and returns a JSON-serialized envelope.
        /// </summary>
        public static async Task<string> Run(Func<Task<object>> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            // P15-052 — per-request correlation ID, stamped on the envelope
            // AND propagated to nested WireLog.Record calls via AsyncLocal so
            // the disk log's nested records (CanUseNewApis, AutoReconnect,
            // bridge sub-ops) carry the same cid for grep-by-id forensics.
            var cid = GenerateCorrelationId();
            var prior = WireLog.CurrentCorrelationId.Value;
            WireLog.CurrentCorrelationId.Value = cid;
            try
            {
                try
                {
                    var result = await handler().ConfigureAwait(false);
                    var envelope = new Dictionary<string, object?>
                    {
                        ["success"] = true,
                        ["result"] = result,
                        ["cid"] = cid,
                    };
                    return WireLog.Redact(JsonSerializer.Serialize(envelope, JsonOptions), RedactionScope.Wire);
                }
                catch (LacerteSDKError sdkErr)
                {
                    return SerializeError(sdkErr, cid);
                }
                catch (OdbcException odbcEx)
                {
                    // P15-035 — preserve SQLSTATE → TAPIStatus mapping so an
                    // OdbcException doesn't fall through to the generic
                    // catch-all (which would label it as InternalServerError).
                    return SerializeError(OdbcErrorMapper.Map(odbcEx), cid);
                }
                catch (Exception ex)
                {
                    // P15-035 — wrap as SyntheticStatusCodes.InternalServerError
                    // (9203) rather than StatusCodes.Error (0). The legacy 0
                    // userMessage said "Lacerte returned a generic error..."
                    // which lies for in-server faults like NullReferenceException
                    // or JsonException and sends the operator to Windows Event
                    // Viewer for a stack that doesn't live there. The 9203
                    // remediation explicitly tells the LLM to capture
                    // context.exceptionType and re-run with LACERTE_DEBUG_WIRE=1.
                    var context = new Dictionary<string, object?>
                    {
                        ["rawError"] = ex.Message,
                        ["exceptionType"] = ex.GetType().FullName,
                    };
                    var wrapped = new LacerteSDKError(SyntheticStatusCodes.InternalServerError, context, ex);
                    return SerializeError(wrapped, cid);
                }
            }
            finally
            {
                // Defensive restore — AsyncLocal flows naturally on async/await
                // but threadpool reuse and parallel calls share the same
                // ExecutionContext snapshot, so make sure we leave the slot
                // in whatever state we found it.
                WireLog.CurrentCorrelationId.Value = prior;
            }
        }

        private static string SerializeError(LacerteSDKError err, string cid)
        {
            var envelope = new Dictionary<string, object?>
            {
                ["success"] = false,
                ["statusCode"] = err.StatusCode,
                ["statusName"] = err.StatusName,
                // P16-053 / P15-037 — surface `origin` so wire consumers can
                // tell at a glance whether the code came from the SDK or was
                // MCP-minted (synthetic). Matches the `originGroup` column
                // on lacerte_return_codes for direct filtering.
                ["origin"] = err.Origin,
                ["severity"] = err.Severity.ToString().ToLowerInvariant(),
                ["userMessage"] = err.UserMessage,
                ["remediation"] = err.Remediation,
                ["context"] = err.Context,
                ["cid"] = cid,
            };
            return WireLog.Redact(JsonSerializer.Serialize(envelope, JsonOptions), RedactionScope.Wire);
        }

        private static string GenerateCorrelationId()
            => Guid.NewGuid().ToString("N").Substring(0, 8);
    }
}
