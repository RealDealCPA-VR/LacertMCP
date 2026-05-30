using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    /// <summary>
    /// Phase 14 — P14-019. Verifies that BOTH success and error envelopes
    /// returned by <see cref="WrapToolHandler"/> are run through the WireLog
    /// redactor before they leave the server. Closes the wire-vs-disk
    /// asymmetry where the MCP wire envelope previously shipped unredacted
    /// PII to the LLM client.
    /// </summary>
    public class WrapToolHandlerRedactionTests
    {
        [Fact]
        public async Task Run_FieldWriteRejection_RedactsSsnShapedContextValue()
        {
            // Simulates a write rejection that put the rejected field+value in
            // context. Without redaction, the value (an SSN) would echo back to
            // the LLM client over MCP.
            var context = new Dictionary<string, object?>
            {
                ["field"] = "C1_15",
                ["value"] = "123-45-6789",
                ["TaxpayerSSN"] = "123-45-6789",
            };

            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(StatusCodes.InValidClientName, context);
            });

            Assert.DoesNotContain("123-45-6789", json);
        }

        [Fact]
        public async Task Run_GenericException_RedactsRawErrorMessage()
        {
            // The generic-exception fallback puts ex.Message in
            // context.rawError. If the COM layer echoed taxpayer data, the
            // unredacted envelope would expose it. WrapToolHandler must scrub.
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("SDK rejected: SSN 123-45-6789 invalid");
            });

            Assert.DoesNotContain("123-45-6789", json);
            // rawError value replaced with the *** sentinel.
            Assert.Contains("\"rawError\":\"***\"", json);
        }

        [Fact]
        public async Task Run_SuccessEnvelope_RedactsSsnInResultRow()
        {
            // The success envelope can carry ODBC-read rows that include
            // SSN/EIN columns. Without redaction those landed unredacted on
            // the wire (only the disk log was scrubbed).
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                return (object)new
                {
                    rows = new[]
                    {
                        new { ClientID = "1001", TaxpayerSSN = "111-22-3333" },
                    },
                };
            });

            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.DoesNotContain("111-22-3333", json);
        }

        [Fact]
        public async Task Run_ErrorContext_RedactsReasonKey()
        {
            // Bridge/Registration puts COM ex.Message in context.reason. Verify
            // the new 'reason' alternation hits.
            var context = new Dictionary<string, object?>
            {
                ["op"] = "Bridge.Register",
                ["reason"] = "Late-bound COM call failed: SSN 555-66-7777",
            };

            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable, context);
            });

            Assert.DoesNotContain("555-66-7777", json);
        }

        [Fact]
        public async Task Run_SuccessEnvelope_NonPiiResultPassesThrough()
        {
            // Regression: redaction must not nuke routine output. Health
            // probes, schema lookups, etc. emit fully non-PII responses.
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                return (object)new { ok = true, count = 42, message = "ready" };
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());

            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(42, result.GetProperty("count").GetInt32());
            Assert.Equal("ready", result.GetProperty("message").GetString());
        }
    }
}
