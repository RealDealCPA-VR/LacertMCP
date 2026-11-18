using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools.Internal;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Round-8 review regressions: write read-back + idempotency, context
    /// pinning, and HTTP-mode fail-closed scope.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionManagerR8Tests
    {
        private const string Field = "C1_15";

        public SessionManagerR8Tests()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: Field);
        }

        private static async Task<LacerteSessionManager> NewSimAsync(string dataPath = SeedData.DataPathIData2024, int year = 2024)
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(dataPath, year, 'I');
            return mgr;
        }

        // A write the SDK accepted but whose read-back differs (9022) must not
        // leave its Success cached under the idempotency key: the retry has to
        // really re-write instead of replaying the cached Success forever.
        [Fact]
        public async Task FailedVerification_ForgetsIdempotencyKey_SoRetryReExecutes()
        {
            using var mgr = await NewSimAsync();
            Assert.Equal(StatusCodes.Success, await mgr.WriteField(1, "1001", Field, "A", idempotencyKey: "r8-k1"));

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(() => FieldWriteCore.VerifyOrThrow(
                mgr, "WriteField", 1, "1001", Field, "B", null, null, CancellationToken.None, "r8-k1"));
            Assert.Equal(SyntheticStatusCodes.WriteNotVerified, ex.StatusCode);

            // Same key, different args: with the entry still cached this would
            // be 9102 IdempotencyConflict; forgotten, it executes.
            Assert.Equal(StatusCodes.Success, await mgr.WriteField(1, "1001", Field, "C", idempotencyKey: "r8-k1"));
            var v = await mgr.VerifyFieldWrite(1, "1001", Field, "C", null, null);
            Assert.True(v.Verified);
        }

        // A read-back that can't run is not evidence the write failed.
        [Fact]
        public async Task ReadBackFailure_IsNotVerified_NotAFailedWrite()
        {
            using var mgr = await NewSimAsync();
            var v = await FieldWriteCore.VerifyOrThrow(
                mgr, "WriteField", 1, "1001", "C1_NOT_A_COLUMN", "x", null, null, CancellationToken.None);
            Assert.Null(v.Verified);
            Assert.NotNull(v.Note);
        }

        // Read-back uses the context the write ran in, even if the active
        // context changed in between.
        [Fact]
        public async Task VerifyFieldWrite_UsesWriteContext_NotTheCurrentOne()
        {
            using var mgr = await NewSimAsync();
            var writeCtx = mgr.Context!;
            await mgr.WriteField(1, "1001", Field, "R8-PINNED");

            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');

            var v = await mgr.VerifyFieldWrite(1, "1001", Field, "R8-PINNED", null, null,
                CancellationToken.None, writeCtx);
            Assert.True(v.Verified);
        }

    }

    /// <summary>
    /// Flips the process-wide <see cref="SessionScope.Required"/>, so it runs
    /// in a non-parallel collection: while it is on, every scope-less manager
    /// in a concurrently running test would see no context.
    /// </summary>
    [Collection(LacertMCP.Tests.ProcessStateCollection.Name)]
    public class SessionScopeRequiredTests
    {
        private const string Field = "C1_15";

        public SessionScopeRequiredTests()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: Field);
        }

        // HTTP mode: a tool call that arrives without its connection scope
        // gets no shared context and can't write.
        [Fact]
        public async Task ScopeRequired_WithoutScope_FailsClosed()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I'); // global context set before HTTP mode
            using var _ = mgr;
            SessionScope.Required = true;
            try
            {
                Assert.Null(mgr.Context);
                var noCtx = await Assert.ThrowsAsync<LacerteSDKError>(() => mgr.WriteField(1, "1001", Field, "x"));
                Assert.Equal(SyntheticStatusCodes.ContextNotSet, noCtx.StatusCode);
                var setEx = Assert.Throws<LacerteSDKError>(() => mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I'));
                Assert.Equal(SyntheticStatusCodes.InternalServerError, setEx.StatusCode);

                // Inside a scope everything works as normal.
                SessionScope.Current = new SessionScope();
                try
                {
                    mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                    Assert.Equal(StatusCodes.Success, await mgr.WriteField(1, "1001", Field, "scoped"));
                }
                finally
                {
                    SessionScope.Current = null;
                }
            }
            finally
            {
                SessionScope.Required = false;
            }
        }
    }
}
