using System.Reflection;
using System.Text.RegularExpressions;
using LacertMCP.Server.Diagnostics;
using Xunit;

namespace LacertMCP.Tests.Diagnostics
{
    /// <summary>
    /// P14-040 — Guardrail that the assembly version stamp from
    /// Directory.Build.props actually lands on the compiled Server assembly.
    ///
    /// <para>
    /// Without this test, a future agent who removes Directory.Build.props (or
    /// flips GenerateAssemblyInfo=false in the Server csproj) would silently
    /// regress the build stamp back to MSBuild's default 1.0.0.0, breaking the
    /// promise made by `lacerte-mcp status` / `lacerte_session_status` /
    /// `lacerte-mcp doctor`.
    /// </para>
    /// </summary>
    public class BuildInfoTests
    {
        [Fact]
        public void Version_IsNotEmpty()
        {
            Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version));
        }

        [Fact]
        public void Version_IsNotDefaultMsbuildStamp()
        {
            // 1.0.0.0 is what MSBuild auto-stamps when no Version property is set.
            // If Directory.Build.props is removed or its Version block is deleted,
            // this assert fires and the regression is caught at test time instead of
            // when an operator looks at a status payload and sees the wrong version.
            Assert.NotEqual("1.0.0.0", BuildInfo.Version);
            Assert.NotEqual("1.0.0", BuildInfo.Version);
        }

        [Fact]
        public void Version_StartsWithExpectedMajorMinor()
        {
            // Loose match — pins the current release line (0.1.x). Bump this when
            // Directory.Build.props is bumped, so the assertion stays accurate.
            Assert.StartsWith("0.1.", BuildInfo.Version);
        }

        // ----- P17-009: SourceRevisionId git-SHA stamping ------------------

        /// <summary>
        /// P17-009 — When the build was invoked with
        /// <c>-p:SourceRevisionId=&lt;sha&gt;</c> (the normal path via
        /// <c>.github/workflows/ci.yml</c> and <c>scripts/release.ps1</c>),
        /// Directory.Build.props line 35 stamps <c>$(Version)+&lt;short-sha&gt;</c>
        /// into <see cref="AssemblyInformationalVersionAttribute"/>. This test
        /// reflects on the live attribute and, when a <c>+</c> suffix is
        /// present, asserts it matches the documented <c>0.1.0+abc1234</c> shape
        /// (semver build metadata: 7 hex chars).
        /// <para>
        /// When run from a local <c>dotnet build</c> without the SourceRevisionId
        /// arg (the intentional sim/tarball/hand-built fallback), the attribute
        /// carries only the bare version and the test passes vacuously. This
        /// mirrors the comment in <c>StatusCommandTests.Status_Json_BuildVersion_Plus_Sign_NotEscaped</c>:
        /// the SHA suffix is opt-in per build invocation, but when emitted, its
        /// shape is locked.
        /// </para>
        /// </summary>
        [Fact]
        public void InformationalVersion_WhenSourceRevisionIdStamped_HasShortShaSuffix()
        {
            var asm = typeof(BuildInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            Assert.NotNull(info);
            var version = info!.InformationalVersion;
            Assert.False(string.IsNullOrWhiteSpace(version));

            var plusIndex = version.IndexOf('+');
            if (plusIndex < 0)
            {
                // No SourceRevisionId was supplied for this build (typical for
                // local `dotnet build`). The bare-version fallback is the
                // documented Directory.Build.props line-36 path — vacuous pass.
                return;
            }

            // Suffix shape: hex only. Either:
            //   - 7-char short SHA from our Directory.Build.props line 40
            //     when `-p:SourceRevisionId=<sha>` was passed explicitly
            //     (CI / scripts/release.ps1 path), or
            //   - 40-char full SHA auto-stamped by the .NET SDK's default
            //     `IncludeSourceRevisionInInformationalVersion=true` behavior
            //     when SourceRevisionId is auto-detected from the local git
            //     repo (the typical local `dotnet build` from a checkout).
            // Both are valid hex SHAs — pin the hex+length-set shape so a
            // non-hex suffix (e.g. a tag name leak) still trips the assert.
            var suffix = version.Substring(plusIndex + 1);
            Assert.Matches(new Regex("^[0-9a-fA-F]{7}([0-9a-fA-F]{33})?$"), suffix);

            // And the bit before the `+` must still be the bare version
            // (e.g. "0.1.0") — the SHA never replaces the version, only appends.
            var bareVersion = version.Substring(0, plusIndex);
            Assert.StartsWith("0.1.", bareVersion);
        }

        /// <summary>
        /// P17-009 — When the assembly carries a SHA suffix, BuildInfo.Version
        /// must propagate it verbatim. Closes the loop: status payloads /
        /// heartbeat / doctor see the same string the assembly attribute holds,
        /// so an operator's pasted bug report carries the commit anchor.
        /// </summary>
        [Fact]
        public void Version_PropagatesInformationalVersionIncludingShaSuffix()
        {
            var asm = typeof(BuildInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info == null || string.IsNullOrEmpty(info.InformationalVersion))
            {
                // No informational attribute means BuildInfo falls back to
                // AssemblyName.Version — that path is exercised by the existing
                // Version_IsNotEmpty test. Skip here.
                return;
            }

            // BuildInfo.Version is the surface that reaches the wire — it must
            // be exactly what the assembly attribute carries (no trimming, no
            // SHA stripping, no version replacement).
            Assert.Equal(info.InformationalVersion, BuildInfo.Version);
        }
    }
}
