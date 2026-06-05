using System;
using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-033 — `lacerte_firm_dashboard` (X_feat_accel cluster).
    ///
    /// <para>Tests use <see cref="FirmDashboardTools.RootOverrideForTests"/>
    /// to redirect discovery to a temp directory WITHOUT mutating the
    /// process-wide <c>LACERTE_DATA_ROOT</c> env var. The env mutation
    /// would race against any parallel test class that calls
    /// <c>SetContext</c> on the canonical <c>C:\Lacerte\...</c> seed
    /// path (which would suddenly fall outside the allowlist and fail
    /// with 9013). The collection attribute below additionally
    /// serializes us with sibling tests that DO touch the env var.</para>
    /// </summary>
    [Collection(LacertMCP.Tests.DataRootEnvCollection.Name)]
    public class FirmDashboardToolsTests
    {
        // ----------------------------------------------------------------
        // Per-test scope helper — temp dir + RootOverrideForTests seam.
        // Avoids touching LACERTE_DATA_ROOT entirely so we don't race
        // against parallel test classes calling SetContext on canonical
        // seed paths.
        // ----------------------------------------------------------------
        private sealed class DataRootScope : IDisposable
        {
            public string TempDir { get; }

            public DataRootScope(bool create = true)
            {
                TempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "lacerte-firm-dashboard-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                if (create) System.IO.Directory.CreateDirectory(TempDir);
                FirmDashboardTools.RootOverrideForTests = () => TempDir;
            }

            public void Dispose()
            {
                FirmDashboardTools.RootOverrideForTests = null;
                try { System.IO.Directory.Delete(TempDir, recursive: true); } catch { /* swallow */ }
            }
        }

        [Fact]
        public async Task FirmDashboard_EmptyRoot_ReturnsEmptyRollup()
        {
            using var scope = new DataRootScope();
            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(0, result.GetProperty("summary").GetProperty("totalClients").GetInt32());
            Assert.Equal(0, result.GetProperty("summary").GetProperty("totalEfRows").GetInt32());
            Assert.Equal(0, result.GetProperty("byContext").GetArrayLength());
            var hist = result.GetProperty("summary").GetProperty("statusHistogram");
            Assert.True(hist.TryGetProperty("Accepted", out var acc));
            Assert.Equal(0, acc.GetInt32());
            Assert.True(hist.TryGetProperty("Rejected", out _));
            Assert.True(hist.TryGetProperty("Pending", out _));
            Assert.True(hist.TryGetProperty("Transmitted", out _));
            Assert.True(hist.TryGetProperty("other", out _));
        }

        [Fact]
        public async Task FirmDashboard_RootMissing_ReturnsInvalidArgument()
        {
            var bogus = @"Z:\nonexistent-path-" + Guid.NewGuid().ToString("N");
            FirmDashboardTools.RootOverrideForTests = () => bogus;
            try
            {
                var mgr = ToolTestHelpers.NewSimManager();
                var tools = new FirmDashboardTools(mgr);
                var root = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard());
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
                var ctx = root.GetProperty("context");
                Assert.Equal("LACERTE_DATA_ROOT", ctx.GetProperty("argument").GetString());
            }
            finally
            {
                FirmDashboardTools.RootOverrideForTests = null;
            }
        }

        [Fact]
        public async Task FirmDashboard_PopulatedRoot_AggregatesByContext()
        {
            using var scope = new DataRootScope();
            var iDataDir = System.IO.Path.Combine(scope.TempDir, "24Tax", "IData");
            System.IO.Directory.CreateDirectory(iDataDir);

            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard());
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            var byContext = result.GetProperty("byContext");
            Assert.Equal(1, byContext.GetArrayLength());
            var tuple = byContext[0];
            Assert.Equal(2024, tuple.GetProperty("taxYear").GetInt32());
            Assert.Equal("I", tuple.GetProperty("module").GetString());
            Assert.Equal("Individual", tuple.GetProperty("moduleName").GetString());
            Assert.True(tuple.TryGetProperty("clientCount", out _));
            Assert.True(tuple.TryGetProperty("efRowCount", out _));
            Assert.True(tuple.TryGetProperty("statusHistogram", out _));
            Assert.True(tuple.TryGetProperty("latestStatusDate", out _));
        }

        [Fact]
        public async Task FirmDashboard_YearFilter_NarrowsTuples()
        {
            using var scope = new DataRootScope();
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "23Tax", "IData"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "IData"));

            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var unfiltered = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard());
            Assert.Equal(2, unfiltered.GetProperty("result").GetProperty("byContext").GetArrayLength());

            var filtered = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard(yearFilter: 2024));
            var byContext = filtered.GetProperty("result").GetProperty("byContext");
            Assert.Equal(1, byContext.GetArrayLength());
            Assert.Equal(2024, byContext[0].GetProperty("taxYear").GetInt32());
        }

        [Fact]
        public async Task FirmDashboard_ModulesFilter_NarrowsTuples()
        {
            using var scope = new DataRootScope();
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "IData"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "CData"));

            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var filtered = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard(modulesFilter: "C"));
            var byContext = filtered.GetProperty("result").GetProperty("byContext");
            Assert.Equal(1, byContext.GetArrayLength());
            Assert.Equal("C", byContext[0].GetProperty("module").GetString());
        }

        [Fact]
        public async Task FirmDashboard_SinceUtc_ThreadsThroughToEfRead()
        {
            using var scope = new DataRootScope();
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "IData"));

            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard(sinceUtc: "2099-01-01"));
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("2099-01-01",
                root.GetProperty("result").GetProperty("sinceUtc").GetString());
        }

        [Fact]
        public async Task FirmDashboard_SinceUtcInvalid_ReturnsInvalidArgument()
        {
            using var scope = new DataRootScope();
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "IData"));
            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard(sinceUtc: "not-a-date"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9017, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("sinceUtc",
                root.GetProperty("context").GetProperty("argument").GetString());
        }

        [Fact]
        public async Task FirmDashboard_RestoresPriorContext()
        {
            using var scope = new DataRootScope();
            // No LACERTE_DATA_ROOT env mutation means PathValidation
            // accepts canonical sim seed paths. We use SeedData here.
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "IData"));
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P', readOnly: false);
            var beforeCtx = mgr.SessionStatus().Context!;

            var tools = new FirmDashboardTools(mgr);
            _ = await tools.LacerteFirmDashboard();

            var afterCtx = mgr.SessionStatus().Context!;
            Assert.Equal(beforeCtx.DataPath, afterCtx.DataPath);
            Assert.Equal(beforeCtx.Year, afterCtx.Year);
            Assert.Equal(beforeCtx.Module, afterCtx.Module);
            Assert.Equal(beforeCtx.ReadOnly, afterCtx.ReadOnly);
        }

        [Fact]
        public async Task FirmDashboard_HistogramSumsToEfRowCount()
        {
            using var scope = new DataRootScope();
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scope.TempDir, "24Tax", "IData"));
            var mgr = ToolTestHelpers.NewSimManager();
            var tools = new FirmDashboardTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteFirmDashboard());
            var byContext = root.GetProperty("result").GetProperty("byContext");
            foreach (var tuple in byContext.EnumerateArray())
            {
                var efRowCount = tuple.GetProperty("efRowCount").GetInt32();
                var hist = tuple.GetProperty("statusHistogram");
                int histSum = 0;
                foreach (var bucket in hist.EnumerateObject())
                {
                    histSum += bucket.Value.GetInt32();
                }
                Assert.Equal(efRowCount, histSum);
            }
        }
    }
}
