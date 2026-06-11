using System.Collections.Generic;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// R6-D2 — explicit-context read overloads (<c>ListClientsAt</c> /
    /// <c>ReadClientAt</c> / <c>ReadDetailAt</c> / <c>ReadEFStatusAt</c>).
    /// The contract under test:
    /// <list type="bullet">
    ///   <item><description>Results are identical to SetContext + the
    ///   active-context read for the same tuple (parity).</description></item>
    ///   <item><description>The active <c>Context</c> is NEVER read, set, or
    ///   replaced — a null context stays null and a set context stays the
    ///   same reference.</description></item>
    ///   <item><description>Tuple validation is identical to
    ///   <c>SetContext</c> (year floor/ceiling, module charset, path
    ///   validation) and the NotSignedIn gate still fires.</description></item>
    /// </list>
    /// </summary>
    [Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class SessionManagerAtOverloadsTests
    {
        private static async Task<LacerteSessionManager> NewRegisteredSimAsync()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            return mgr;
        }

        // -----------------------------------------------------------------
        // Parity with the active-context reads
        // -----------------------------------------------------------------

        [Fact]
        public async Task ListClientsAt_MatchesActiveContextListClients()
        {
            var mgr = await NewRegisteredSimAsync();
            var atRows = await mgr.ListClientsAt(SeedData.DataPathIData2024, 2024, 'I');

            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var ctxRows = await mgr.ListClients();

            Assert.Equal(ctxRows.Count, atRows.Count);
        }

        [Fact]
        public async Task ListClientsAt_LastNameFilterAndLimit_Honored()
        {
            var mgr = await NewRegisteredSimAsync();
            var filtered = await mgr.ListClientsAt(
                SeedData.DataPathIData2024, 2024, 'I',
                lastNameFilter: "Bro", limit: 5);
            Assert.Single(filtered);
        }

        [Fact]
        public async Task ReadClientAt_ReturnsRow_AndNullForMissing()
        {
            var mgr = await NewRegisteredSimAsync();
            var row = await mgr.ReadClientAt(
                SeedData.DataPathIData2024, 2024, 'I', SeedData.BROW.ClientNumber);
            Assert.NotNull(row);

            var missing = await mgr.ReadClientAt(
                SeedData.DataPathIData2024, 2024, 'I', "no-such-client");
            Assert.Null(missing);
        }

        [Fact]
        public async Task ReadDetailAt_MatchesActiveContextReadDetail()
        {
            var mgr = await NewRegisteredSimAsync();
            var atRows = await mgr.ReadDetailAt(
                SeedData.DataPathIData2024, 2024, 'I', SeedData.BROW.ClientNumber);

            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var ctxRows = await mgr.ReadDetail(SeedData.BROW.ClientNumber);

            Assert.Equal(ctxRows.Count, atRows.Count);
        }

        [Fact]
        public async Task ReadDetailAt_SeriesFilter_NarrowsRows()
        {
            var mgr = await NewRegisteredSimAsync();
            var all = await mgr.ReadDetailAt(
                SeedData.DataPathIData2024, 2024, 'I', SeedData.BROW.ClientNumber);
            var wagesOnly = await mgr.ReadDetailAt(
                SeedData.DataPathIData2024, 2024, 'I', SeedData.BROW.ClientNumber,
                seriesFilter: 11);

            Assert.True(wagesOnly.Count <= all.Count);
            Assert.All(wagesOnly, r => Assert.Equal("11", r.Series));
        }

        [Fact]
        public async Task ReadEFStatusAt_MatchesActiveContextReadEFStatus()
        {
            var mgr = await NewRegisteredSimAsync();
            var atRows = await mgr.ReadEFStatusAt(SeedData.DataPathIData2024, 2024, 'I');

            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var ctxRows = await mgr.ReadEFStatus();

            Assert.Equal(ctxRows.Count, atRows.Count);
        }

        // -----------------------------------------------------------------
        // Context isolation — the *At surface never touches Context
        // -----------------------------------------------------------------

        [Fact]
        public async Task AtOverloads_WorkWithNoActiveContext_AndLeaveItNull()
        {
            var mgr = await NewRegisteredSimAsync();
            Assert.Null(mgr.SessionStatus().Context);

            _ = await mgr.ListClientsAt(SeedData.DataPathIData2024, 2024, 'I');
            _ = await mgr.ReadClientAt(SeedData.DataPathIData2024, 2024, 'I', SeedData.BROW.ClientNumber);
            _ = await mgr.ReadDetailAt(SeedData.DataPathIData2024, 2024, 'I', SeedData.BROW.ClientNumber);
            _ = await mgr.ReadEFStatusAt(SeedData.DataPathIData2024, 2024, 'I');

            Assert.Null(mgr.SessionStatus().Context);
        }

        [Fact]
        public async Task AtOverloads_LeaveSetContextReferenceUntouched()
        {
            var mgr = await NewRegisteredSimAsync();
            // Writable context on a DIFFERENT tuple than the *At reads —
            // reference equality after the reads proves no SetContext call
            // happened (SessionContext is an immutable record the manager
            // only replaces wholesale).
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P', readOnly: false);
            var before = mgr.SessionStatus().Context!;

            _ = await mgr.ListClientsAt(SeedData.DataPathIData2024, 2024, 'I');
            _ = await mgr.ReadEFStatusAt(SeedData.DataPathIData2024, 2024, 'I');

            var after = mgr.SessionStatus().Context!;
            Assert.Same(before, after);
            Assert.False(after.ReadOnly);
        }

        // -----------------------------------------------------------------
        // Validation parity with SetContext + registration gate
        // -----------------------------------------------------------------

        [Fact]
        public async Task ListClientsAt_YearBelowFloor_Throws9003()
        {
            var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.ListClientsAt(SeedData.DataPathIData2024, 2010, 'I'));
            Assert.Equal(SyntheticStatusCodes.TaxYearTooOld, ex.StatusCode);
        }

        [Fact]
        public async Task ListClientsAt_YearAboveCeiling_ThrowsTaxYearTooNew()
        {
            var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.ListClientsAt(SeedData.DataPathIData2024, 2098, 'I'));
            Assert.Equal(SyntheticStatusCodes.TaxYearTooNew, ex.StatusCode);
        }

        [Fact]
        public async Task ReadClientAt_UnknownModule_ThrowsInValidClientName()
        {
            var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.ReadClientAt(SeedData.DataPathIData2024, 2024, 'Z', "1001"));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        [Fact]
        public async Task ReadEFStatusAt_EmptyDataPath_ThrowsContextNotSet()
        {
            var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.ReadEFStatusAt("  ", 2024, 'I'));
            Assert.Equal(SyntheticStatusCodes.ContextNotSet, ex.StatusCode);
        }

        [Fact]
        public async Task ReadDetailAt_RelativeDataPath_ThrowsInvalidDataPath()
        {
            var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.ReadDetailAt(@"..\24Tax\IData", 2024, 'I', "1001"));
            Assert.Equal(SyntheticStatusCodes.InvalidDataPath, ex.StatusCode);
        }

        [Fact]
        public async Task AtOverloads_Unregistered_ThrowNotSignedIn()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.ListClientsAt(SeedData.DataPathIData2024, 2024, 'I'));
            Assert.Equal(StatusCodes.NotSignedIn, ex.StatusCode);
        }
    }
}
