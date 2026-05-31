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
        public async Task Run_GenericException_PreservesRawErrorPrefix()
        {
            // P15-001 — wire-scope redaction no longer blanks the rawError
            // key wholesale; the actionable diagnostic prefix survives and
            // the LLM can react to it. (Disk-scope still scrubs the key on
            // the archived log because rawError CAN echo taxpayer data —
            // that path is exercised in WireLogTests.)
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("SDK rejected the field");
            });

            // The actionable prefix is preserved on the wire so the LLM can react.
            Assert.Contains("SDK rejected the field", json);
            Assert.DoesNotContain("\"rawError\":\"***\"", json);
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
        public async Task Run_ErrorContext_PreservesReasonDiagnostic()
        {
            // P15-001 — Bridge/validation throws put the actionable
            // diagnostic in context.reason ("Series is required", "dbf
            // must match the DATA{n}{module}{YY}.DBF shape...", etc.).
            // Wire-scope redaction no longer blanks the key, so the LLM
            // remediation flow sees the actual validation phrase.
            var context = new Dictionary<string, object?>
            {
                ["op"] = "Bridge.Register",
                ["reason"] = "dbf must match the DATA{n}{module}{YY}.DBF shape (uppercase ASCII alphanumerics + single dot).",
            };

            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable, context);
            });

            // The diagnostic substring the LLM needs to fix the call survives.
            Assert.Contains("DATA{n}{module}{YY}.DBF", json);
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
