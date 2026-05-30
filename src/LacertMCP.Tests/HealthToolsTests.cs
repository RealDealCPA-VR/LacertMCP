using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests
{
    public class HealthToolsTests
    {
        [Fact]
        public void LacerteHealth_ReturnsNonEmptyString_InSimMode()
        {
            // Arrange — session manager defaults to sim mode (Phase 0 stub).
            var session = new LacerteSessionManager();
            var tools = new HealthTools(session);

            // Act
            var result = tools.LacerteHealth();

            // Assert — Phase-0 spike: any non-empty response proves the wiring works.
            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.Contains("sim", result);
        }
    }
}
