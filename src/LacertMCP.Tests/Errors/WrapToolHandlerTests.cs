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

        // ---------------------------------------------------------------
        // P16-002 / P16-060 — `origin` field on the error envelope
        // ---------------------------------------------------------------
        //
        // LacerteSDKError.Origin (sdk | synthetic) was added in P15-037 so
        // the wire can carry a documented discriminator instead of forcing
        // every LLM client to memorize the 9000+ synthetic threshold. The
        // envelope-emission half is in WrapToolHandler.SerializeError. These
        // tests pin the contract for every error path the wrapper exposes
        // (LacerteSDKError sdk, LacerteSDKError synthetic, OdbcException
        // mapped to TAPIStatus, and the generic catch-all that wraps as
        // synthetic InternalServerError 9203).

        [Fact]
        public async Task Run_SdkOriginCode_EmitsOriginSdkOnEnvelope()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(StatusCodes.ExposedForRead);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(StatusCodes.ExposedForRead, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("sdk", root.GetProperty("origin").GetString());
        }

        [Fact]
        public async Task Run_SyntheticOriginCode_EmitsOriginSyntheticOnEnvelope()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.SimulationOnly);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.SimulationOnly, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("synthetic", root.GetProperty("origin").GetString());
        }

        [Fact]
        public async Task Run_GenericException_EmitsOriginSyntheticOnEnvelope()
        {
            // P15-035 routes generic Exception through
            // SyntheticStatusCodes.InternalServerError (9203); P16-002 pins
            // that the origin discriminator is honest about that wrap so an
            // LLM doesn't think a NullReferenceException came from the SDK.
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.InternalServerError, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("synthetic", root.GetProperty("origin").GetString());
        }

        // ---------------------------------------------------------------
        // P16-010 — JavaScriptEncoder.Create(UnicodeRanges.All)
        // ---------------------------------------------------------------
        //
        // Default JavaScriptEncoder escapes every non-ASCII codepoint as
        // `\u00XX`, producing unreadable wire envelopes for accented client
        // names, em-dash detail-row descriptions, and SDK-emitted localized
        // error text. These tests pin the encoder so the bytes that reach
        // the LLM are the literal Unicode glyphs.

        [Fact]
        public async Task Run_Success_PassesUnicodeGlyphsThroughLiteral_P16_010()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                return (object)new
                {
                    clientName = "José García",          // é, í
                    description = "Form 2106 — Employee Expenses", // em-dash
                    sectionRef = "REQUIREMENTS §2.1",         // section sign
                };
            });

            // Literal glyphs reach the wire — token-efficient and
            // operator-readable in Claude Desktop / Cursor / Windsurf.
            Assert.Contains("José García", json);
            Assert.Contains("Form 2106 — Employee Expenses", json);
            Assert.Contains("§2.1", json);

            // And the escape sequences are NOT in the wire bytes.
            Assert.DoesNotContain("\\u00e9", json);
            Assert.DoesNotContain("\\u00ed", json);
            Assert.DoesNotContain("\\u2014", json);
            Assert.DoesNotContain("\\u00a7", json);
        }

        [Fact]
        public async Task Run_Error_PassesUnicodeGlyphsThroughLiteralInUserMessage_P16_010()
        {
            // Synthesize an envelope whose userMessage carries Unicode (via
            // a synthetic status code with a localized-feel message string).
            // We use the context dict as the carrier because real status
            // messages are ASCII today — the test is about the encoder,
            // not the message text.
            var context = new Dictionary<string, object?>
            {
                ["reason"] = "Field §24 requires José — see Form 2106.",
            };

            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet, context);
            });

            Assert.Contains("§24", json);
            Assert.Contains("José", json);
            Assert.Contains("—", json);
            Assert.DoesNotContain("\\u00a7", json);
            Assert.DoesNotContain("\\u00e9", json);
            Assert.DoesNotContain("\\u2014", json);
        }
    }
}
