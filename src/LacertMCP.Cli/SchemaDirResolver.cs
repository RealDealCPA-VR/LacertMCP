using System;
using System.IO;

namespace LacertMCP.Cli
{
    /// <summary>
    /// Resolves the <c>Schema/FieldAccessMap</c> root the CLI should read and
    /// write (field-access list/set, doctor's shard probe).
    ///
    /// <para>
    /// Why this exists: the server's <c>FieldAccessMapLoader</c> reads the tree
    /// next to <c>LacertMCP.Server.dll</c>. In the dev bin layout the CLI and
    /// server share one output folder, so "next to lacerte-mcp.exe" is the same
    /// place. In the release zip (scripts/release.ps1) the server and CLI are
    /// staged into separate <c>server/</c> and <c>cli/</c> folders — and because
    /// the CLI project references the server, <c>cli/</c> carries its own copy
    /// of the schema tree. Writing beside lacerte-mcp.exe there edits a copy the
    /// server never reads.
    /// </para>
    ///
    /// <para>Resolution order:</para>
    /// <list type="number">
    ///   <item><description>Explicit <c>--schema-dir</c> (a FieldAccessMap root, or an
    ///   install folder that contains <c>Schema/FieldAccessMap</c>).</description></item>
    ///   <item><description>Release layout: <c>..\server\Schema\FieldAccessMap</c>
    ///   relative to the CLI exe, when that directory exists.</description></item>
    ///   <item><description>Dev layout: <c>Schema\FieldAccessMap</c> next to the CLI exe.</description></item>
    /// </list>
    ///
    /// <para>
    /// There is deliberately no env-var tier: <c>FieldAccessMapLoader</c> does not
    /// honor one, so an env override here would only re-create the
    /// "written where the server never reads" drift.
    /// </para>
    /// </summary>
    public static class SchemaDirResolver
    {
        public enum SchemaDirSource { Explicit, ReleaseServerSibling, CliLocal }

        public readonly struct Resolution
        {
            public Resolution(string root, SchemaDirSource source)
            {
                Root = root;
                Source = source;
            }

            /// <summary>Absolute path of the FieldAccessMap root (contains base/ and overlays/).</summary>
            public string Root { get; }

            public SchemaDirSource Source { get; }

            public string BaseDir => Path.Combine(Root, "base");

            public string OverlaysDir => Path.Combine(Root, "overlays");

            /// <summary>Human-readable provenance for command output.</summary>
            public string Describe() => Source switch
            {
                SchemaDirSource.Explicit => $"{Root} (from --schema-dir)",
                SchemaDirSource.ReleaseServerSibling => $"{Root} (release layout: the server's schema tree)",
                _ => $"{Root} (next to lacerte-mcp.exe)",
            };
        }

        /// <summary>
        /// Original on-disk path of lacerte-mcp.exe. Prefers <c>Assembly.CodeBase</c>
        /// (the load-from location) over <c>Assembly.Location</c>, which points into the
        /// shadow-copy cache when the assembly is shadow-copied (e.g. under the xUnit
        /// net48 runner) — a folder with no Schema tree and no .exe.config. Identical
        /// to Location for a normal lacerte-mcp.exe launch.
        /// </summary>
        public static string CliAssemblyPath()
        {
            var asm = typeof(SchemaDirResolver).Assembly;
            try
            {
                var codeBase = asm.CodeBase;
                if (!string.IsNullOrEmpty(codeBase))
                {
                    var uri = new Uri(codeBase);
                    if (uri.IsFile && File.Exists(uri.LocalPath))
                    {
                        return uri.LocalPath;
                    }
                }
            }
            catch
            {
                // Fall back to Location.
            }
            return asm.Location;
        }

        /// <summary>Directory holding the running CLI assembly (lacerte-mcp.exe).</summary>
        public static string CliExeDir()
        {
            var path = CliAssemblyPath();
            return (string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path))
                   ?? Directory.GetCurrentDirectory();
        }

        /// <summary>Production entry point: resolve relative to the running CLI exe.</summary>
        public static Resolution Resolve(string? explicitDir = null)
            => Resolve(explicitDir, CliExeDir());

        /// <summary>
        /// Pure resolution seam — <paramref name="exeDir"/> stands in for the CLI
        /// exe folder so tests can lay out fake dev / release trees in temp.
        /// </summary>
        public static Resolution Resolve(string? explicitDir, string exeDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                var full = Path.GetFullPath(explicitDir!.Trim());
                // Leniency: accept an install folder (e.g. ...\server) that
                // contains Schema\FieldAccessMap, not just the map root itself.
                var nested = Path.Combine(full, "Schema", "FieldAccessMap");
                if (!Directory.Exists(Path.Combine(full, "base")) && Directory.Exists(nested))
                {
                    full = nested;
                }
                return new Resolution(full, SchemaDirSource.Explicit);
            }

            var sibling = Path.GetFullPath(Path.Combine(exeDir, "..", "server", "Schema", "FieldAccessMap"));
            if (Directory.Exists(sibling))
            {
                return new Resolution(sibling, SchemaDirSource.ReleaseServerSibling);
            }

            return new Resolution(Path.GetFullPath(Path.Combine(exeDir, "Schema", "FieldAccessMap")),
                SchemaDirSource.CliLocal);
        }

        /// <summary>True when two directory paths name the same folder (case-insensitive, trailing-separator tolerant).</summary>
        public static bool SamePath(string a, string b)
        {
            static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
