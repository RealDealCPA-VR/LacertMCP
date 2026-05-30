using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// Phase 9 task #66 — scaffolding tests for <see cref="CanUseNewApisProbe"/>.
    ///
    /// <para>
    /// The session manager caches the result per <c>(year, module)</c>; this
    /// class owns only the wire call. Off-Windows OR without the SDK
    /// registered, every probe throws
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/>.
    /// </para>
    /// </summary>
    public class CanUseNewApisProbeTests
    {
        [Fact]
        public void Probe_NoSdk_ThrowsBridgeUnavailable()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                CanUseNewApisProbe.Probe(appId: "test", year: 2024, module: 'I'));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        [Theory]
        [InlineData(2018, 'I')]
        [InlineData(2020, 'C')]
        [InlineData(2021, 'P')]
        [InlineData(2024, 'I')]
        public void Probe_NoSdk_ConsistentAcrossInputs(int year, char module)
        {
            // Regardless of year/module combo, no SDK = BridgeUnavailable.
            // The session manager's cache key insulates callers from this so
            // probing per-pair is safe.
            var ex = Assert.Throws<LacerteSDKError>(() =>
                CanUseNewApisProbe.Probe(appId: "test", year: year, module: module));
            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // Live COM round-trip (skipped — requires real SDK)
        // -----------------------------------------------------------------

        [Fact(Skip = "Requires real Lacerte SDK + registered AppID.")]
        public void Live_Probe_TY24Individual_ReturnsTrue()
        {
            var appId = Registration.GetStoredAppId()!.Value.ToString("D");
            Assert.True(CanUseNewApisProbe.Probe(appId, 2024, 'I'));
        }

        [Fact(Skip = "Requires real Lacerte SDK + registered AppID + TY20 module installed.")]
        public void Live_Probe_TY20Individual_ReturnsFalse()
        {
            var appId = Registration.GetStoredAppId()!.Value.ToString("D");
            Assert.False(CanUseNewApisProbe.Probe(appId, 2020, 'I'));
        }
    }
}
