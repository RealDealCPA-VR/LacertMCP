using System.Collections.Generic;
using System.IO;
using LacertMCP.Server.Startup;
using Xunit;

namespace LacertMCP.Tests.Startup
{
    /// <summary>R7-21 — the banner shows the effective default tax year.</summary>
    public class EnvValidatorR7Tests
    {
        private static string Banner(string? taxYear)
        {
            var sw = new StringWriter();
            EnvValidator.Validate(sw, new Dictionary<string, string?>
            {
                ["LACERTE_SIMULATION"] = "1",
                ["LACERTE_DEFAULT_TAX_YEAR"] = taxYear,
            });
            return sw.ToString();
        }

        [Fact]
        public void Banner_InvalidDefaultTaxYear_ShowsIgnored()
        {
            var text = Banner("abc");
            Assert.Contains("defaultTaxYear=<ignored>", text);
            Assert.DoesNotContain("defaultTaxYear=abc", text);
        }

        [Fact]
        public void Banner_ValidDefaultTaxYear_ShowsValue()
            => Assert.Contains("defaultTaxYear=2024", Banner("2024"));
    }
}
