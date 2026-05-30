using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

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
    ///     <c>{ "success": true, "result": &lt;handler return value&gt; }</c>.</description></item>
    ///   <item><description><b><see cref="LacerteSDKError"/></b> →
    ///     <c>{ "success": false, "statusCode", "statusName", "severity", "userMessage",
    ///     "remediation", "context" }</c>.</description></item>
    ///   <item><description><b>Generic <see cref="Exception"/></b> → wrapped as
    ///     <see cref="LacerteSDKError"/> with code 0 (<c>Error</c>), preserving the raw
    ///     message in <c>context.rawError</c>, then formatted with the same shape.</description></item>
    /// </list>
    /// </summary>
    public static class WrapToolHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
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

            try
            {
                var result = await handler().ConfigureAwait(false);
                var envelope = new Dictionary<string, object?>
                {
                    ["success"] = true,
                    ["result"] = result,
                };
                return JsonSerializer.Serialize(envelope, JsonOptions);
            }
            catch (LacerteSDKError sdkErr)
            {
                return SerializeError(sdkErr);
            }
            catch (Exception ex)
            {
                // Wrap as LacerteSDKError(0) so the on-wire shape is consistent and tools
                // never leak a stack trace to the MCP client. Preserves the original message
                // in context.rawError for debugging.
                var context = new Dictionary<string, object?>
                {
                    ["rawError"] = ex.Message,
                    ["exceptionType"] = ex.GetType().FullName,
                };
                var wrapped = new LacerteSDKError(StatusCodes.Error, context, ex);
                return SerializeError(wrapped);
            }
        }

        private static string SerializeError(LacerteSDKError err)
        {
            var envelope = new Dictionary<string, object?>
            {
                ["success"] = false,
                ["statusCode"] = err.StatusCode,
                ["statusName"] = err.StatusName,
                ["severity"] = err.Severity.ToString().ToLowerInvariant(),
                ["userMessage"] = err.UserMessage,
                ["remediation"] = err.Remediation,
                ["context"] = err.Context,
            };
            return JsonSerializer.Serialize(envelope, JsonOptions);
        }
    }
}
