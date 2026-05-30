using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests
{
    public class HealthToolsTests
    {
        [Fact]
        public async Task LacerteHealth_ReturnsStructuredEnvelope_InSimMode()
        {
            // Arrange — session manager defaults to sim mode (Phase 0 stub).
            var session = new LacerteSessionManager();
            var tools = new HealthTools(session);

            // Act
            var result = await tools.LacerteHealth();

            // Assert — must ride the canonical WrapToolHandler envelope and
            // report the real session mode (P14-001).
            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.Contains("\"success\":true", result);
            Assert.Contains("\"mode\":\"simulation\"", result);
            Assert.Contains("\"status\":\"reachable\"", result);
        }
    }
}
