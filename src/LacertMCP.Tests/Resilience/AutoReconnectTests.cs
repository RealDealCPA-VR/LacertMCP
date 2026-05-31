using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Resilience;
using Xunit;

namespace LacertMCP.Tests.Resilience
{
    /// <summary>
    /// Phase 11 task #76 — auto-reconnect tests. Asserts:
    /// <list type="bullet">
    ///   <item><description>Happy path: no retry, value returned.</description></item>
    ///   <item><description>Transient HRESULT 0x800706BA: retried then succeeded.</description></item>
    ///   <item><description>Non-transient LacerteSDKError: no retry, propagates immediately.</description></item>
    ///   <item><description>Exhausted retries: rethrows the last exception with metadata.</description></item>
    /// </list>
    /// </summary>
    // P15-014 — added to the AccessMapMutationCollection so concurrent
    // AutoReconnect.WithRetry calls in this suite don't bleed retry/exhausted
    // WireLog records into the AutoReconnectLoggingTests log file (which sets
    // the global WireLog._testForceConfig). The collection serializes both
    // suites so the global logging flag is owned by exactly one test at a time.
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class AutoReconnectTests
    {
        [Fact]
        public async Task HappyPath_NoRetry()
        {
            int calls = 0;
            var result = await AutoReconnect.WithRetry<int>(() =>
            {
                calls++;
                return Task.FromResult(7);
            });
            Assert.Equal(7, result);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task TransientHResult_RetriesUntilSuccess()
        {
            int calls = 0;
            var result = await AutoReconnect.WithRetry<int>(() =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new COMException("RPC server unavailable", unchecked((int)0x800706BA));
                }
                return Task.FromResult(99);
            });
            Assert.Equal(99, result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task NonTransient_LacerteSDKError_PropagatesImmediately()
        {
            int calls = 0;
            var err = await Assert.ThrowsAsync<LacerteSDKError>(async () =>
                await AutoReconnect.WithRetry<int>(() =>
                {
                    calls++;
                    throw new LacerteSDKError(StatusCodes.NotSignedIn);
                }));
            Assert.Equal(1, calls);
            Assert.Equal(StatusCodes.NotSignedIn, err.StatusCode);
        }

        [Fact]
        public async Task NonTransient_GenericException_PropagatesImmediately()
        {
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await AutoReconnect.WithRetry<int>(() =>
                {
                    calls++;
                    throw new InvalidOperationException("oops");
                }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task TransientFail_ExhaustsRetries_RethrowsLastWithMetadata()
        {
            int calls = 0;
            var ex = await Assert.ThrowsAsync<COMException>(async () =>
                await AutoReconnect.WithRetry<int>(() =>
                {
                    calls++;
                    throw new COMException("RPC server unavailable", unchecked((int)0x800706BA));
                }));
            // 1 initial + MaxRetries = 4 attempts total.
            Assert.Equal(AutoReconnect.MaxRetries + 1, calls);
            Assert.Equal(true, ex.Data["AutoReconnect.Exhausted"]);
            Assert.Equal(AutoReconnect.MaxRetries + 1, ex.Data["AutoReconnect.Attempts"]);
        }

        [Fact]
        public void IsTransient_LacerteSDKError_AlwaysFalse()
        {
            Assert.False(AutoReconnect.IsTransient(new LacerteSDKError(StatusCodes.NotSignedIn)));
            Assert.False(AutoReconnect.IsTransient(new LacerteSDKError(StatusCodes.ConsentDenied)));
        }

        /// <summary>
        /// P14-023: pin all 4 whitelisted HRESULTs for COMException — the
        /// AutoReconnect.cs:43-46 contract. Previously only the first two
        /// (RpcUnavailable + RpcDisconnected) were tested; RPC_E_SERVERCALL_RETRYLATER
        /// (0x8001010A) and RPC_E_CALL_CANCELED (0x80010001) had zero coverage.
        /// </summary>
        [Theory]
        [InlineData(unchecked((int)0x800706BA))] // RPC_S_SERVER_UNAVAILABLE
        [InlineData(unchecked((int)0x80010108))] // RPC_E_DISCONNECTED
        [InlineData(unchecked((int)0x8001010A))] // RPC_E_SERVERCALL_RETRYLATER
        [InlineData(unchecked((int)0x80010001))] // RPC_E_CALL_CANCELED
        public void IsTransient_COMException_TrueForAllWhitelisted(int hresult)
        {
            var ex = new COMException("x", hresult);
            Assert.True(AutoReconnect.IsTransient(ex));
        }

        /// <summary>
        /// P14-023: pins the AutoReconnect.cs:148-151 ExternalException branch — some
        /// marshaled COM errors arrive as a base ExternalException (e.g. SEH-converted
        /// on x64) rather than a typed COMException.
        /// </summary>
        [Theory]
        [InlineData(unchecked((int)0x800706BA))]
        [InlineData(unchecked((int)0x80010108))]
        [InlineData(unchecked((int)0x8001010A))]
        [InlineData(unchecked((int)0x80010001))]
        public void IsTransient_ExternalException_TrueForAllWhitelisted(int hresult)
        {
            var ex = new ExternalException("x", hresult);
            Assert.True(AutoReconnect.IsTransient(ex));
        }

        /// <summary>
        /// Helper: net48's <c>Exception.HResult</c> setter is protected, so use a
        /// minimal subclass to populate it for the base-Exception fallback test.
        /// This pins the path where some COM interop populates HResult without
        /// re-typing the exception as COMException/ExternalException.
        /// </summary>
        private sealed class HResultException : Exception
        {
            public HResultException(string msg, int hr) : base(msg) { HResult = hr; }
        }

        /// <summary>
        /// P14-023: pins the AutoReconnect.cs:155-158 base-Exception HResult fallback.
        /// Some COM interop paths populate ex.HResult without re-typing the exception.
        /// </summary>
        [Fact]
        public void IsTransient_BaseException_HResultFallback_True()
        {
            var ex = new HResultException("rpc busy", unchecked((int)0x8001010A));
            Assert.True(AutoReconnect.IsTransient(ex));
        }

        /// <summary>
        /// P14-023 negative control: a base Exception with a non-whitelisted HResult
        /// must NOT be classified as transient.
        /// </summary>
        [Fact]
        public void IsTransient_BaseException_NonWhitelistedHResult_False()
        {
            var ex = new HResultException("nope", unchecked((int)0x80004005)); // E_FAIL
            Assert.False(AutoReconnect.IsTransient(ex));
        }

        [Fact]
        public void IsTransient_RandomHResult_False()
        {
            var ex = new COMException("x", unchecked((int)0x80004005)); // E_FAIL
            Assert.False(AutoReconnect.IsTransient(ex));
        }
    }
}
