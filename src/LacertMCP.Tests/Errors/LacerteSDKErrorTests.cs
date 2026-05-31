using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    /// <summary>
    /// P15-037: Origin discriminator on LacerteSDKError. The threshold lives at
    /// SyntheticCodeThreshold = 9000 and the property is derived (no setter, no
    /// per-throw plumbing). These tests pin the boundary so a future renumbering
    /// of synthetic codes can't silently break the wire-envelope discriminator.
    /// </summary>
    public class LacerteSDKErrorTests
    {
        [Fact]
        public void Origin_TAPIStatusCode_IsSdk()
        {
            // Codes 0-28 are TAPIStatus (general SDK calls).
            var err = new LacerteSDKError(StatusCodes.NotExposed);
            Assert.Equal("sdk", err.Origin);
        }

        [Fact]
        public void Origin_TAPIStatusErrorZero_IsSdk()
        {
            // Code 0 (StatusCodes.Error) is below the threshold even though it's
            // the generic-error fallback. Origin still reflects the numeric range.
            var err = new LacerteSDKError(StatusCodes.Error);
            Assert.Equal("sdk", err.Origin);
        }

        [Fact]
        public void Origin_TPSDKStatusCode_IsSdk()
        {
            // Codes 200-204 are TPSDKStatus (registration). Still SDK-origin.
            var err = new LacerteSDKError(StatusCodes.NotSignedIn);
            Assert.Equal("sdk", err.Origin);
        }

        [Fact]
        public void Origin_SyntheticValidationCode_IsSynthetic()
        {
            // 9001-9099 — validation / preflight gates.
            var err = new LacerteSDKError(SyntheticStatusCodes.FieldIsReadOnly);
            Assert.Equal("synthetic", err.Origin);
        }

        [Fact]
        public void Origin_SyntheticAuthCode_IsSynthetic()
        {
            // 9100-9199 — auth lifecycle.
            var err = new LacerteSDKError(SyntheticStatusCodes.ReAuthRequired);
            Assert.Equal("synthetic", err.Origin);
        }

        [Fact]
        public void Origin_SyntheticBridgeCode_IsSynthetic()
        {
            // 9200-9299 — COM bridge / runtime.
            var err = new LacerteSDKError(SyntheticStatusCodes.SimulationOnly);
            Assert.Equal("synthetic", err.Origin);
        }

        [Fact]
        public void Origin_SyntheticContextNotSet_IsSynthetic()
        {
            var err = new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
            Assert.Equal("synthetic", err.Origin);
        }

        [Fact]
        public void SyntheticCodeThreshold_IsExactly9000()
        {
            // Load-bearing constant — wire envelope serialization depends on it.
            // Bump only with a corresponding DECISIONS.md update + sweep of any
            // SDK code that crept into the 9000+ space.
            Assert.Equal(9000, LacerteSDKError.SyntheticCodeThreshold);
        }

        [Fact]
        public void Origin_BoundaryJustBelow9000_IsSdk()
        {
            // No real SDK code lives at 8999 today, but the boundary contract is
            // worth pinning so a future renumbering doesn't drift unnoticed.
            var err = new LacerteSDKError(8999);
            Assert.Equal("sdk", err.Origin);
        }

        [Fact]
        public void Origin_BoundaryAt9000_IsSynthetic()
        {
            var err = new LacerteSDKError(9000);
            Assert.Equal("synthetic", err.Origin);
        }
    }
}
