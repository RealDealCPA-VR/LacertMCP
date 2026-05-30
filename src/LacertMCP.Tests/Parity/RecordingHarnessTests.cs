using System;
using System.IO;
using LacertMCP.Server.Session;
using Xunit;

namespace LacertMCP.Tests.Parity
{
    /// <summary>
    /// F10 — proves the parity recording adapter works end-to-end.
    ///
    /// <para>
    /// Loads <c>Parity/fixtures/example-recording.json</c>, constructs a
    /// live-mode <see cref="LacerteSessionManager"/> wired to the
    /// <see cref="RecordingBridge"/>, and asserts the Live leg returns the
    /// value recorded in the fixture.
    /// </para>
    ///
    /// <para>
    /// Existing parity tests are unaffected — they don't set
    /// <c>LACERTE_PARITY_RECORDING</c>, so the legacy sim-pinned path still
    /// runs. The recording harness only activates when this test (or another
    /// caller) explicitly calls <see cref="ParitySessionFactory.ForRecording"/>.
    /// </para>
    ///
    /// <para>
    /// AppID isolation: the session manager's <c>CanUseNewApis</c> path
    /// reads the persisted AppID via <c>Bridge.Registration.GetStoredAppId()</c>
    /// — which by default consults Windows Credential Manager (or the file
    /// fallback off-Windows). If a sibling test class (e.g. <c>Cli</c>
    /// AppIdStore tests) has minted a GUID into either backend, the actual
    /// arg passed to <c>RecordingBridge.CanUseNewApis</c> wouldn't match the
    /// fixture's empty-string <c>appId</c>. We sidestep that with a per-test
    /// <see cref="IsolatedAppIdScope"/>: force the file backend, point
    /// <c>APPDATA</c> at a tempdir, restore on dispose.
    /// </para>
    /// </summary>
    [Collection(LacertMCP.Tests.Cli.CliCollection.Name)]
    public class RecordingHarnessTests
    {
        [Fact]
        public void CanUseNewApis_ReturnsValueFromRecording()
        {
            using var _ = new IsolatedAppIdScope();
            using var mgr = ParitySessionFactory.ForRecording("example-recording.json");

            // Mode should be Live since the recording bridge backs the manager.
            Assert.Equal(SessionMode.Live, mgr.Mode);

            // The fixture pins CanUseNewApis(appId="", 2024, 'I') → true
            // and (appId="", 2019, 'I') → false. IsolatedAppIdScope ensures
            // GetStoredAppId() returns null → empty string, matching the fixture.
            Assert.True(mgr.CanUseNewApis(2024, 'I'));
            Assert.False(mgr.CanUseNewApis(2019, 'I'));
        }

        /// <summary>
        /// Scoped env-var override that forces <c>Bridge.Registration.GetStoredAppId()</c>
        /// to read from a fresh empty tempdir for the duration of the test.
        /// Restores the original env on dispose.
        /// </summary>
        private sealed class IsolatedAppIdScope : IDisposable
        {
            private readonly string _tempDir;
            private readonly string? _origAppData;
            private readonly string? _origForceFile;

            public IsolatedAppIdScope()
            {
                _origAppData = Environment.GetEnvironmentVariable("APPDATA");
                _origForceFile = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");

                _tempDir = Path.Combine(Path.GetTempPath(),
                    "lacerte-mcp-parity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);

                Environment.SetEnvironmentVariable("APPDATA", _tempDir);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("APPDATA", _origAppData);
                Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", _origForceFile);
                try
                {
                    if (Directory.Exists(_tempDir))
                    {
                        Directory.Delete(_tempDir, recursive: true);
                    }
                }
                catch
                {
                    // best-effort cleanup; tempdir will be reaped by the OS eventually.
                }
            }
        }
    }
}
