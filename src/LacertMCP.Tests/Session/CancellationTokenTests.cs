using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// P15-013 — CancellationToken plumbing on every public LacerteSessionManager
    /// chokepoint. A pre-cancelled token must surface
    /// <see cref="System.OperationCanceledException"/> BEFORE any work runs.
    /// MCP cancel notifications target long-running tool calls (paged
    /// ListClients over 50k rows, hung COM Register); the cooperative cancel
    /// invariant is that the chokepoint either checks ct on entry or threads
    /// it down to the bridge. This suite pins the entry-side check by
    /// constructing a fresh manager with a context already set, then driving
    /// each chokepoint with a CTS that is cancelled before the call.
    /// </summary>
    public class CancellationTokenTests
    {
        private static LacerteSessionManager NewRegisteredSimWithContext()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            mgr.Register().GetAwaiter().GetResult();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            return mgr;
        }

        private static CancellationToken Cancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [Fact]
        public async Task ListClients_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.ListClients(cancellationToken: Cancelled()));
        }

        [Fact]
        public async Task ReadClient_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.ReadClient("1001", Cancelled()));
        }

        [Fact]
        public async Task ReadTable_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.ReadTable(1, cancellationToken: Cancelled()));
        }

        [Fact]
        public async Task ReadDetail_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.ReadDetail("1001", Cancelled()));
        }

        [Fact]
        public async Task ReadEFStatus_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.ReadEFStatus(Cancelled()));
        }

        [Fact]
        public async Task WriteField_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.WriteField(
                    table: 1, clientNumber: "1001", field: "C1_15", value: "x",
                    cancellationToken: Cancelled()));
        }

        [Fact]
        public async Task WriteDetail_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            var row = new DetailRow("11", "", "1", "", "", "", "W2", "T", "", 1.0, "ct-test");
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.WriteDetail("1001", row, cancellationToken: Cancelled()));
        }

        [Fact]
        public async Task CreateClient_PreCancelledToken_Throws()
        {
            using var mgr = NewRegisteredSimWithContext();
            var args = new CreateClientArgs(
                ClientID: "CTTEST",
                TaxType: "Individual",
                FirstName: "Cancel",
                LastName: "Test",
                EmailID: "ct@example.com");
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.CreateClient(args, cancellationToken: Cancelled()));
        }

        [Fact]
        public async Task Register_PreCancelledToken_Throws()
        {
            // Register is invoked on a fresh (not-yet-registered) manager.
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                async () => await mgr.Register(Cancelled()));
        }

        [Fact]
        public async Task NonCancelledToken_PreservesExistingShape()
        {
            // Regression: default-token paths still return success envelopes.
            using var mgr = NewRegisteredSimWithContext();
            var clients = await mgr.ListClients(cancellationToken: CancellationToken.None);
            Assert.NotNull(clients);
        }
    }
}
