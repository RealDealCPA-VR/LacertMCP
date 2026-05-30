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
    }
}
