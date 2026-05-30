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

        [Fact]
        public void IsTransient_RpcUnavailable_True()
        {
            var ex = new COMException("x", unchecked((int)0x800706BA));
            Assert.True(AutoReconnect.IsTransient(ex));
        }

        [Fact]
        public void IsTransient_RpcDisconnected_True()
        {
            var ex = new COMException("x", unchecked((int)0x80010108));
            Assert.True(AutoReconnect.IsTransient(ex));
        }

        [Fact]
        public void IsTransient_RandomHResult_False()
        {
            var ex = new COMException("x", unchecked((int)0x80004005)); // E_FAIL
            Assert.False(AutoReconnect.IsTransient(ex));
        }
    }
}
