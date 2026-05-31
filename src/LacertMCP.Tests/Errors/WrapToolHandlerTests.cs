using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    public class WrapToolHandlerTests
    {
        [Fact]
        public async Task Run_Success_ReturnsSuccessEnvelope()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                return (object)new { count = 3, message = "ok" };
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("count").GetInt32());
            Assert.Equal("ok", result.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Run_LacerteSDKError_ReturnsStructuredErrorEnvelope()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(StatusCodes.NotExposed);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(7, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("NotExposed", root.GetProperty("statusName").GetString());
            Assert.Equal("error", root.GetProperty("severity").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("userMessage").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("remediation").GetString()));
        }

        [Fact]
        public async Task Run_GenericException_WrapsAsInternalServerError_P15_035()
        {
            // P15-035 — generic Exception fallthrough now maps to
            // SyntheticStatusCodes.InternalServerError (9203) rather than
            // StatusCodes.Error(0). The Error(0) userMessage said "Lacerte
            // returned a generic error..." which misattributed in-server
            // faults (NullRef/InvalidOperationException/JsonException) as
            // SDK traces and sent the operator to Windows Event Viewer for
            // stacks that don't live there.
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.InternalServerError, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("InternalServerError", root.GetProperty("statusName").GetString());
            Assert.Equal("error", root.GetProperty("severity").GetString());

            var context = root.GetProperty("context");
            // P15-001: rawError is NOT in the wire-scope keyset, so the
            // actionable prefix passes through. (Disk-scope WireLog still
            // scrubs rawError on the archived debug log — see WireLogTests.)
            Assert.Equal("boom", context.GetProperty("rawError").GetString());
            Assert.Contains("InvalidOperationException", context.GetProperty("exceptionType").GetString());
        }

        [Fact]
        public async Task Run_ErrorContext_PreservesReasonButRedactsSecrets()
        {
            // P15-001 — error envelope wire scope: SSN/EIN/Bank/DOB/Email
            // values still get *** but the actionable diagnostic in `reason`
            // passes through so the LLM consumer can react.
            var context = new Dictionary<string, object?>
            {
                ["field"] = "C1_15",
                ["reason"] = "Series is required.",
                ["TaxpayerSSN"] = "123-45-6789",
                ["EmployerEIN"] = "12-3456789",
                ["BankAccountNumber"] = "9876543210",
                ["DateOfBirth"] = "1980-01-15",
                ["Email"] = "tax@example.com",
            };

            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet, context);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("success").GetBoolean());

            // reason preserved verbatim — restores LLM-actionability.
            var ctx = root.GetProperty("context");
            Assert.Equal("Series is required.", ctx.GetProperty("reason").GetString());
            Assert.Equal("C1_15", ctx.GetProperty("field").GetString());

            // True secrets still scrubbed on the wire.
            Assert.Equal("***", ctx.GetProperty("TaxpayerSSN").GetString());
            Assert.Equal("***", ctx.GetProperty("EmployerEIN").GetString());
            Assert.Equal("***", ctx.GetProperty("BankAccountNumber").GetString());
            Assert.Equal("***", ctx.GetProperty("DateOfBirth").GetString());
            Assert.Equal("***", ctx.GetProperty("Email").GetString());

            Assert.DoesNotContain("123-45-6789", json);
            Assert.DoesNotContain("12-3456789", json);
            Assert.DoesNotContain("9876543210", json);
            Assert.DoesNotContain("1980-01-15", json);
            Assert.DoesNotContain("tax@example.com", json);
        }

        [Fact]
        public async Task Run_SyntheticCode_ResolvesNameFromSyntheticTable()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("ContextNotSet", root.GetProperty("statusName").GetString());
        }
    }
}
