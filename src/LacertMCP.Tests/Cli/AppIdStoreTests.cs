using System;
using System.IO;
using LacertMCP.Cli.Storage;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Round-trip tests for <see cref="AppIdStore"/>. The Credential Manager backend
    /// is the production path on Windows; off-Windows tests fall through to the
    /// JSON-file fallback. Either way we redirect <c>APPDATA</c> at the test boundary
    /// so we never touch a real config file.
    /// </summary>
    [Collection(CliCollection.Name)]
    public class AppIdStoreTests : IDisposable
    {
        private readonly string _tempAppData;
        private readonly string? _originalAppData;
        private readonly string? _originalForceFlag;

        public AppIdStoreTests()
        {
            _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            _originalForceFlag = Environment.GetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE");
            _tempAppData = Path.Combine(Path.GetTempPath(), "lacerte-mcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempAppData);
            Environment.SetEnvironmentVariable("APPDATA", _tempAppData);
            // Force the file-backed fallback so we never touch the operator's real Credential Manager
            // hive during tests — even when these tests run on the Windows box.
            Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", "1");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
            Environment.SetEnvironmentVariable("LACERTE_MCP_FORCE_FILE_STORE", _originalForceFlag);
            try
            {
                if (Directory.Exists(_tempAppData))
                {
                    Directory.Delete(_tempAppData, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        // ---- File-backed fallback ---------------------------------------------------

        [Fact]
        public void FileFallback_Get_Returns_Null_When_No_Config()
        {
            // No file yet — Get should be null.
            // (Verify the fallback path is indeed redirected by our APPDATA override.)
            Assert.Contains("lacerte-mcp-test-", AppIdStore.FallbackConfigPath);
            Assert.False(AppIdStore.UsesCredentialManager); // sanity: the force-file flag took effect
            Assert.Null(AppIdStore.Get());
        }

        [Fact]
        public void FileFallback_Set_Then_Get_RoundTrips()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var read = AppIdStore.Get();
            Assert.NotNull(read);
            Assert.Equal(id, read!.Value);
        }

        [Fact]
        public void FileFallback_Reset_Removes_Persisted_GUID()
        {
            AppIdStore.Set(Guid.NewGuid());
            Assert.NotNull(AppIdStore.Get());

            AppIdStore.Reset();
            Assert.Null(AppIdStore.Get());
        }

        [Fact]
        public void FileFallback_Reset_Is_Idempotent_When_No_File()
        {
            // No file present; Reset should not throw.
            AppIdStore.Reset();
            Assert.Null(AppIdStore.Get());
        }

        [Fact]
        public void FileFallback_Set_Then_Set_Replaces()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            AppIdStore.Set(a);
            AppIdStore.Set(b);

            Assert.Equal(b, AppIdStore.Get());
        }

        [Fact]
        public void FallbackConfigPath_Is_Under_AppData()
        {
            // Sanity: the resolver picks up our redirected APPDATA.
            var path = AppIdStore.FallbackConfigPath;
            Assert.EndsWith(Path.Combine("lacerte-mcp", "config.json"), path);
        }

        [Fact]
        public void FileFallback_WritesActualFile_With_GUID_String()
        {
            var id = Guid.NewGuid();
            AppIdStore.Set(id);

            var path = AppIdStore.FallbackConfigPath;
            Assert.True(File.Exists(path));
            var contents = File.ReadAllText(path);
            Assert.Contains(id.ToString("D"), contents);
        }

        // ---- Credential Manager path (Windows only) ---------------------------------

        [Fact(Skip = "Windows-only — Credential Manager P/Invoke is exercised manually on the Windows + Lacerte box.")]
        public void CredentialManager_Set_Then_Get_RoundTrips()
        {
            // Intentionally skipped — this hits the live Credential Manager on the dev's
            // machine, which would persist across test runs and pollute the operator's
            // real install. Re-enable manually when validating on the target Windows box.
            var id = Guid.NewGuid();
            try
            {
                AppIdStore.Set(id);
                Assert.Equal(id, AppIdStore.Get());
            }
            finally
            {
                AppIdStore.Reset();
            }
        }
    }
}
