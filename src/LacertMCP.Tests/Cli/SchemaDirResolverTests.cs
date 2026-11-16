using System;
using System.IO;
using LacertMCP.Cli;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Pins the field-access schema-root resolution order (explicit --schema-dir →
    /// release-layout ..\server\Schema\FieldAccessMap → next to lacerte-mcp.exe) so
    /// `field-access set` can never again write a copy the server doesn't read. Uses
    /// temp trees standing in for the dev and release-zip layouts; never touches the
    /// real bin.
    /// </summary>
    public sealed class SchemaDirResolverTests : IDisposable
    {
        private readonly string _root;

        public SchemaDirResolverTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "lacerte-mcp-schemadir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void ReleaseLayout_PrefersServerSiblingSchemaTree()
        {
            // <stage>/cli/lacerte-mcp.exe + <stage>/server/Schema/FieldAccessMap (release.ps1 layout).
            var cli = Directory.CreateDirectory(Path.Combine(_root, "cli")).FullName;
            Directory.CreateDirectory(Path.Combine(cli, "Schema", "FieldAccessMap", "base")); // CLI's own copy
            var serverMap = Directory.CreateDirectory(Path.Combine(_root, "server", "Schema", "FieldAccessMap")).FullName;

            var res = SchemaDirResolver.Resolve(null, cli);

            Assert.Equal(SchemaDirResolver.SchemaDirSource.ReleaseServerSibling, res.Source);
            Assert.True(SchemaDirResolver.SamePath(serverMap, res.Root), $"expected {serverMap}, got {res.Root}");
            Assert.Contains(res.Root, res.Describe());
        }

        [Fact]
        public void DevLayout_NoServerSibling_UsesCliLocalTree()
        {
            var bin = Directory.CreateDirectory(Path.Combine(_root, "bin", "x64", "Debug")).FullName;

            var res = SchemaDirResolver.Resolve(null, bin);

            Assert.Equal(SchemaDirResolver.SchemaDirSource.CliLocal, res.Source);
            Assert.True(SchemaDirResolver.SamePath(Path.Combine(bin, "Schema", "FieldAccessMap"), res.Root));
        }

        [Fact]
        public void ExplicitSchemaDir_WinsOverReleaseSibling()
        {
            var cli = Directory.CreateDirectory(Path.Combine(_root, "cli")).FullName;
            Directory.CreateDirectory(Path.Combine(_root, "server", "Schema", "FieldAccessMap"));
            var custom = Directory.CreateDirectory(Path.Combine(_root, "custom-map", "base")).Parent!.FullName;

            var res = SchemaDirResolver.Resolve(custom, cli);

            Assert.Equal(SchemaDirSource(), res.Source);
            Assert.True(SchemaDirResolver.SamePath(custom, res.Root));
            Assert.Contains("--schema-dir", res.Describe());
        }

        [Fact]
        public void ExplicitSchemaDir_InstallFolder_DescendsIntoSchemaFieldAccessMap()
        {
            // Operator passes the server install folder rather than the map root.
            var serverDir = Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;
            var map = Directory.CreateDirectory(Path.Combine(serverDir, "Schema", "FieldAccessMap", "base")).Parent!.FullName;

            var res = SchemaDirResolver.Resolve(serverDir, Path.Combine(_root, "elsewhere"));

            Assert.True(SchemaDirResolver.SamePath(map, res.Root), $"expected {map}, got {res.Root}");
        }

        [Fact]
        public void CliExeDir_IsOriginalBinFolder_NotShadowCopyCache()
        {
            // The xUnit net48 runner shadow-copies assemblies (Assembly.Location points
            // into ...\assembly\dl3\...). The resolver must use the original bin folder,
            // where lacerte-mcp.exe.config and the Schema tree actually live.
            var exeDir = SchemaDirResolver.CliExeDir();
            Assert.True(SchemaDirResolver.SamePath(AppContext.BaseDirectory, exeDir),
                $"CliExeDir={exeDir} BaseDirectory={AppContext.BaseDirectory}");

            var res = SchemaDirResolver.Resolve();
            Assert.Equal(SchemaDirResolver.SchemaDirSource.CliLocal, res.Source);
            Assert.True(Directory.Exists(Path.Combine(res.Root, "base")), $"no base/ under {res.Root}");
            // Sanity: the loader's file layout convention matches (Schema/FieldAccessMap).
            Assert.EndsWith(Path.Combine("Schema", "FieldAccessMap"), FieldAccessMapLoader.GetSchemaRoot());
        }

        private static SchemaDirResolver.SchemaDirSource SchemaDirSource()
            => SchemaDirResolver.SchemaDirSource.Explicit;
    }
}
