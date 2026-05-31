using System;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    /// <summary>
    /// P15-035 — generic Exception fallthrough now maps to
    /// <see cref="SyntheticStatusCodes.InternalServerError"/> (9203) rather
    /// than the legacy StatusCodes.Error(0). The Error(0) userMessage said
    /// "Lacerte returned a generic error..." which lied for in-server faults
    /// like NullReferenceException and sent the operator to Windows Event
    /// Viewer for a stack trace that doesn't live there. The 9203
    /// remediation explicitly tells the LLM to capture the exceptionType
    /// from context and re-run with LACERTE_DEBUG_WIRE=1.
    ///
    /// <para>
    /// P15-052 — every envelope (success + error) now carries a per-request
    /// `cid` (8-hex Guid suffix) so an LLM client can echo it back in a
    /// failure report and the operator can grep the disk log for the whole
    /// causal chain.
    /// </para>
    /// </summary>
    public class WrapToolHandlerInternalErrorTests
    {
        [Fact]
        public async Task Run_GenericException_WrapsAsInternalServerError_P15_035()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.InternalServerError,
                root.GetProperty("statusCode").GetInt32());
            Assert.Equal("InternalServerError", root.GetProperty("statusName").GetString());
            Assert.Equal("error", root.GetProperty("severity").GetString());
            Assert.StartsWith("The MCP server itself",
                root.GetProperty("userMessage").GetString());

            var ctx = root.GetProperty("context");
            Assert.Contains("InvalidOperationException",
                ctx.GetProperty("exceptionType").GetString());
        }

        [Fact]
        public async Task Run_NullReferenceException_WrapsAsInternalServerError_P15_035()
        {
            // The most damaging misattribution — NullReferenceException is an
            // in-server bug, not an SDK error. Pre-P15-035 the operator saw
            // remediation "check Windows Event Viewer for SDK traces" for a
            // crash that has no Event Viewer entry.
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new NullReferenceException("oops");
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(9203, root.GetProperty("statusCode").GetInt32());
            Assert.Contains("NullReferenceException",
                root.GetProperty("context").GetProperty("exceptionType").GetString());
        }

        [Fact]
        public async Task Run_LacerteSDKError_PreservedUnchanged_P15_035()
        {
            // Regression guard: P15-035 must not perturb the LacerteSDKError
            // passthrough. The catch order in WrapToolHandler.Run is
            // (LacerteSDKError, OdbcException, Exception) — only the last
            // arm changed.
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(StatusCodes.NotExposed);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(7, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("NotExposed", root.GetProperty("statusName").GetString());
        }

        // --------------------------------------------------------------
        // P15-052 — correlation IDs.
        // --------------------------------------------------------------

        [Fact]
        public async Task Run_SuccessEnvelope_CarriesCidField_P15_052()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                return (object)new { ok = true };
            });

            using var doc = JsonDocument.Parse(json);
            var cid = doc.RootElement.GetProperty("cid").GetString();
            Assert.NotNull(cid);
            // 8-hex shape (Guid "N" first 8 chars).
            Assert.Matches("^[0-9a-fA-F]{8}$", cid!);
        }

        [Fact]
        public async Task Run_ErrorEnvelope_CarriesCidField_P15_052()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(StatusCodes.NotExposed);
            });

            using var doc = JsonDocument.Parse(json);
            var cid = doc.RootElement.GetProperty("cid").GetString();
            Assert.NotNull(cid);
            Assert.Matches("^[0-9a-fA-F]{8}$", cid!);
        }

        [Fact]
        public async Task Run_ConcurrentCalls_HaveDistinctCorrelationIds_P15_052()
        {
            // Each tool invocation gets its own cid.
            var tasks = new Task<string>[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = WrapToolHandler.Run(async () =>
                {
                    await Task.Yield();
                    return (object)new { ok = true };
                });
            }
            var results = await Task.WhenAll(tasks);
            var cids = new System.Collections.Generic.HashSet<string>();
            foreach (var json in results)
            {
                using var doc = JsonDocument.Parse(json);
                cids.Add(doc.RootElement.GetProperty("cid").GetString()!);
            }
            // All cids must be unique (extremely high probability with 8-hex
            // randomness; collision chance is ~1.4e-9 per pair).
            Assert.Equal(tasks.Length, cids.Count);
        }

        [Fact]
        public async Task Run_PropagatesCidToWireLogAsyncLocal_P15_052()
        {
            // Inside the handler, WireLog.CurrentCorrelationId.Value should
            // match what ends up in the envelope. Confirms the AsyncLocal
            // flows through await continuations.
            string? observedCid = null;
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                observedCid = WireLog.CurrentCorrelationId.Value;
                return (object)new { ok = true };
            });

            using var doc = JsonDocument.Parse(json);
            var envelopeCid = doc.RootElement.GetProperty("cid").GetString();
            Assert.NotNull(observedCid);
            Assert.Equal(envelopeCid, observedCid);
        }

        [Fact]
        public async Task Run_RestoresPriorCorrelationId_OnExit_P15_052()
        {
            // Set a prior cid; after the handler returns it should be back
            // to what we set. Defensive restore in the WrapToolHandler
            // finally block guards against ExecutionContext sharing.
            WireLog.CurrentCorrelationId.Value = "deadbeef";
            try
            {
                await WrapToolHandler.Run(async () =>
                {
                    await Task.Yield();
                    return (object)new { ok = true };
                });
                Assert.Equal("deadbeef", WireLog.CurrentCorrelationId.Value);
            }
            finally
            {
                WireLog.CurrentCorrelationId.Value = null;
            }
        }
    }
}
