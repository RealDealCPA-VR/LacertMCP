using LacertMCP.Server.Startup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LacertMCP.Tests.Startup
{
    /// <summary>
    /// P15-053 — LACERTE_LOG_LEVEL parse-or-default helper. Pure function;
    /// tests cover null/empty/whitespace, every documented value
    /// (case-insensitive), and bogus input falling back to Information.
    /// </summary>
    public class LogLevelEnvTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_NullOrEmpty_ReturnsDefault(string? raw)
        {
            Assert.Equal(LogLevelEnv.Default, LogLevelEnv.Parse(raw));
            Assert.Equal(LogLevel.Information, LogLevelEnv.Default);
        }

        [Theory]
        [InlineData("Trace", LogLevel.Trace)]
        [InlineData("debug", LogLevel.Debug)]
        [InlineData("INFORMATION", LogLevel.Information)]
        [InlineData("Warning", LogLevel.Warning)]
        [InlineData("Error", LogLevel.Error)]
        [InlineData("Critical", LogLevel.Critical)]
        [InlineData("None", LogLevel.None)]
        [InlineData("  Warning  ", LogLevel.Warning)] // trimmed
        public void Parse_KnownValues_ReturnsLevel(string raw, LogLevel expected)
        {
            Assert.Equal(expected, LogLevelEnv.Parse(raw));
        }

        [Theory]
        [InlineData("verbose")]
        [InlineData("foo")]
        [InlineData("9")]
        public void Parse_Unknown_ReturnsDefault(string raw)
        {
            Assert.Equal(LogLevelEnv.Default, LogLevelEnv.Parse(raw));
        }
    }
}
