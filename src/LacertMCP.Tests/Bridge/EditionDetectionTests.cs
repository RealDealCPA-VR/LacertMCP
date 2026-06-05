using System;
using System.Collections.Generic;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// P14-056 — pins both branches of <see cref="EditionDetection.Probe"/> and
    /// the constructor contract on <see cref="EditionInfo"/>.
    ///
    /// <para>
    /// The null-bridge branch is the sim path the session manager hits today;
    /// the non-null-bridge branch is the live path that Phase 9 wires the real
    /// late-bound COM probes into. Until then it must throw
    /// <see cref="SyntheticStatusCodes.BridgeUnavailable"/> with the documented
    /// <c>op</c> / <c>reason</c> context so operators see why edition info is
    /// unavailable.
    /// </para>
    /// </summary>
    public class EditionDetectionTests
    {
        [Fact]
        public void Probe_NullBridge_ReturnsSimSentinels()
        {
            var info = EditionDetection.Probe(null);

            Assert.Equal(EditionDetection.SimulatedEdition, info.LacerteEdition);
            Assert.Equal(EditionDetection.SimulatedBuild, info.LacerteBuild);
            Assert.Equal(EditionDetection.SimulatedSdkVersion, info.SdkVersion);
        }

        [Fact]
        public void Probe_LiveBridge_ThrowsBridgeUnavailable()
        {
            var bridge = new ThrowingBridge();

            var ex = Assert.Throws<LacerteSDKError>(() => EditionDetection.Probe(bridge));

            Assert.Equal(SyntheticStatusCodes.BridgeUnavailable, ex.StatusCode);
            Assert.True(ex.Context.ContainsKey("op"),
                "Context must carry an 'op' tag so wire-log readers can attribute the failure.");
            Assert.Equal("EditionDetection.Probe", ex.Context["op"]?.ToString());
            Assert.True(ex.Context.ContainsKey("reason"),
                "Context must carry a 'reason' tag so the operator sees why edition is unavailable.");
        }

        [Fact]
        public void EditionInfo_Ctor_RejectsNullStrings()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new EditionInfo(LacerteEdition: null!, LacerteBuild: "b", SdkVersion: "s"));
            Assert.Throws<ArgumentNullException>(() =>
                new EditionInfo(LacerteEdition: "e", LacerteBuild: null!, SdkVersion: "s"));
            Assert.Throws<ArgumentNullException>(() =>
                new EditionInfo(LacerteEdition: "e", LacerteBuild: "b", SdkVersion: null!));
        }

        // -----------------------------------------------------------------
        // Minimal no-op bridge — never actually invoked by Probe in this
        // phase; the live branch throws before touching the bridge.
        // -----------------------------------------------------------------

        private sealed class ThrowingBridge : ILacerteBridge
        {
            public int Register(string appName, string appId, int taxYear)
                => throw new NotImplementedException();

            public bool CanUseNewApis(string appId, int year, char module)
                => throw new NotImplementedException();

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(
                string dataPath, int year, char module)
                => throw new NotImplementedException();

            public IReadOnlyDictionary<string, object?>? ReadClient(
                string dataPath, int year, char module, string clientNumber)
                => throw new NotImplementedException();

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(
                string dataPath, int year, char module, int table,
                string? clientNumber = null, string? stateAbbrev = null)
                => throw new NotImplementedException();

            public int WriteField(string appId, string dataPath, int year, char module,
                int table, int rowNum, string field, object? value)
                => throw new NotImplementedException();

            public int UpdateTaxReturn(string appId, string dataPath, int year, char module,
                int table, string clientNumber, string field, object? value,
                int? subClientID, string? stateAbbrev)
                => throw new NotImplementedException();

            public int WriteDetail(string appId, string dataPath, int year, char module,
                string clientNumber, DetailRow row, int? replaceIndex)
                => throw new NotImplementedException();

            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module,
                string clientID, string taxType, string? firstName, string? lastName, string? emailID)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
