using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// Phase 11 task #75 — debug wire log tests. Verifies redaction + the
    /// JSON-per-line record shape. Activation-by-env is hard to test from
    /// inside the process (the Lazy resolves once at first call); the harness
    /// uses <c>WriteForTest</c> to drive the write path directly with a temp
    /// dir, which exercises EVERY write-path branch except the env probe.
    /// </summary>
    public class WireLogTests
    {
        [Fact]
        public void Redact_ReplacesSsnEinBankAcctRouting()
        {
            var raw = "{\"SSN\":\"123-45-6789\",\"EIN\":12345,\"BankAccountNumber\":\"9999\",\"RoutingNumber\":\"111\",\"other\":\"ok\"}";
            var redacted = WireLog.Redact(raw);
            Assert.DoesNotContain("123-45-6789", redacted);
            Assert.DoesNotContain("12345", redacted);
            Assert.DoesNotContain("9999", redacted);
            Assert.DoesNotContain("111", redacted);
            Assert.Contains("\"SSN\":\"***\"", redacted);
            Assert.Contains("\"EIN\":\"***\"", redacted);
            Assert.Contains("\"BankAccountNumber\":\"***\"", redacted);
            Assert.Contains("\"RoutingNumber\":\"***\"", redacted);
            // Non-sensitive keys are untouched.
            Assert.Contains("\"other\":\"ok\"", redacted);
        }

        [Fact]
        public void Redact_IsCaseInsensitive()
        {
            var raw = "{\"ssn\":\"x\",\"Ein\":\"y\"}";
            var redacted = WireLog.Redact(raw);
            Assert.DoesNotContain("\"ssn\":\"x\"", redacted);
            Assert.DoesNotContain("\"Ein\":\"y\"", redacted);
        }

        [Fact]
        public void Redact_NullValuesAlsoCovered()
        {
            var raw = "{\"SSN\":null,\"other\":1}";
            var redacted = WireLog.Redact(raw);
            // null must be replaced with "***" sentinel.
            Assert.Contains("\"SSN\":\"***\"", redacted);
            Assert.Contains("\"other\":1", redacted);
        }

        [Fact]
        public void WriteForTest_WritesOneJsonLine_RedactedFirst()
        {
            var dir = Path.Combine(Path.GetTempPath(), "wirelog-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var args = new { SSN = "111-22-3333", clientNumber = "1001" };
                var result = new { ok = true };
                WireLog.WriteForTest(dir, "ListClients", args, result, durationMs: 42);

                Assert.True(Directory.Exists(dir));
                var files = Directory.GetFiles(dir, "lacerte-*.log");
                Assert.Single(files);

                var line = File.ReadAllLines(files[0]).First();
                Assert.DoesNotContain("111-22-3333", line);
                Assert.Contains("\"SSN\":\"***\"", line);

                // Must parse as JSON.
                using var doc = JsonDocument.Parse(line);
                Assert.Equal("ListClients", doc.RootElement.GetProperty("op").GetString());
                Assert.Equal(42, doc.RootElement.GetProperty("durationMs").GetInt64());
                Assert.True(doc.RootElement.TryGetProperty("ts", out _));
                Assert.True(doc.RootElement.TryGetProperty("result", out _));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task Record_DisabledByDefault_IsPassthrough()
        {
            // No LACERTE_DEBUG_WIRE env in test runner — Record should just
            // return the work result.
            var got = await WireLog.Record("op-x", new { a = 1 }, () => Task.FromResult(42));
            Assert.Equal(42, got);
        }
    }
}
