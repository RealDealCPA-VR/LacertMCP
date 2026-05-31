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
                // P15-078: date-roll-agnostic enumeration. If the test happens
                // to span UTC midnight the writer creates two files and the
                // old `Assert.Single(files)` flakes. Concatenating all matching
                // files preserves the original assertion semantics.
                var files = Directory.GetFiles(dir, "lacerte-*.log");
                Assert.NotEmpty(files);

                var line = files.SelectMany(File.ReadAllLines).First();
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

        // -----------------------------------------------------------------
        // P15-001 — split-keyset behavior. Disk scope keeps the broad set
        // (Description / City / Zip / reason); Wire scope narrows to true
        // secrets so operator-facing diagnostics survive the MCP envelope.
        // -----------------------------------------------------------------

        [Fact]
        public void Redact_WireScope_PreservesDescriptionCityZipReason()
        {
            var raw = "{"
                + "\"Description\":\"Bill primary wages\","
                + "\"City\":\"Springfield\","
                + "\"Zip\":\"12345\","
                + "\"ZipCode\":\"54321\","
                + "\"reason\":\"Series is required.\","
                + "\"rawError\":\"validation failed\","
                + "\"FirstName\":\"Bill\","
                + "\"LastName\":\"Brown\","
                + "\"SSN\":\"123-45-6789\""
                + "}";

            var redacted = WireLog.Redact(raw, RedactionScope.Wire);

            // Operator-facing diagnostics survive the wire envelope.
            Assert.Contains("\"Description\":\"Bill primary wages\"", redacted);
            Assert.Contains("\"City\":\"Springfield\"", redacted);
            Assert.Contains("\"Zip\":\"12345\"", redacted);
            Assert.Contains("\"ZipCode\":\"54321\"", redacted);
            Assert.Contains("\"reason\":\"Series is required.\"", redacted);
            Assert.Contains("\"rawError\":\"validation failed\"", redacted);
            Assert.Contains("\"FirstName\":\"Bill\"", redacted);
            Assert.Contains("\"LastName\":\"Brown\"", redacted);

            // True secrets still scrubbed.
            Assert.Contains("\"SSN\":\"***\"", redacted);
            Assert.DoesNotContain("123-45-6789", redacted);
        }

        [Fact]
        public void Redact_DiskScope_StillRedactsDescriptionCityZipReason()
        {
            var raw = "{"
                + "\"Description\":\"Bill primary wages\","
                + "\"City\":\"Springfield\","
                + "\"Zip\":\"12345\","
                + "\"ZipCode\":\"54321\","
                + "\"reason\":\"Series is required.\","
                + "\"rawError\":\"validation failed\","
                + "\"FirstName\":\"Bill\","
                + "\"LastName\":\"Brown\","
                + "\"SSN\":\"123-45-6789\""
                + "}";

            // Default scope is Disk — broad keyset, matches pre-P15-001 behavior.
            var redacted = WireLog.Redact(raw);

            Assert.Contains("\"Description\":\"***\"", redacted);
            Assert.Contains("\"City\":\"***\"", redacted);
            Assert.Contains("\"Zip\":\"***\"", redacted);
            Assert.Contains("\"ZipCode\":\"***\"", redacted);
            Assert.Contains("\"reason\":\"***\"", redacted);
            Assert.Contains("\"rawError\":\"***\"", redacted);
            Assert.Contains("\"FirstName\":\"***\"", redacted);
            Assert.Contains("\"LastName\":\"***\"", redacted);
            Assert.Contains("\"SSN\":\"***\"", redacted);

            Assert.DoesNotContain("Bill primary wages", redacted);
            Assert.DoesNotContain("Springfield", redacted);
            Assert.DoesNotContain("Series is required", redacted);
        }

        [Fact]
        public void Redact_WireScope_StillRedactsCoreSecrets()
        {
            // Sanity: the narrow wire keyset still covers every true-secret
            // column (bank / SSN family / TIN / EIN / DOB / email / phone).
            var raw = "{"
                + "\"BankAccountNumber\":\"9876543210\","
                + "\"RoutingNumber\":\"110000000\","
                + "\"SSN\":\"111-22-3333\","
                + "\"TaxpayerSSN\":\"222-33-4444\","
                + "\"SpouseSSN\":\"333-44-5555\","
                + "\"DependentSSN\":\"444-55-6666\","
                + "\"TIN\":\"555667777\","
                + "\"ITIN\":\"9NN-7N-NNNN\","
                + "\"PTIN\":\"P12345678\","
                + "\"EIN\":\"12-3456789\","
                + "\"EmployerEIN\":\"98-7654321\","
                + "\"FederalID\":\"99-9999999\","
                + "\"FedEIN\":\"88-8888888\","
                + "\"DateOfBirth\":\"1980-01-15\","
                + "\"DOB\":\"1985-06-30\","
                + "\"BirthDate\":\"1990-12-01\","
                + "\"EmailID\":\"a@b.com\","
                + "\"Email\":\"x@y.com\","
                + "\"HomePhone\":\"555-1111\","
                + "\"WorkPhone\":\"555-2222\","
                + "\"CellPhone\":\"555-3333\","
                + "\"PhoneNumber\":\"555-4444\""
                + "}";

            var redacted = WireLog.Redact(raw, RedactionScope.Wire);

            Assert.DoesNotContain("9876543210", redacted);
            Assert.DoesNotContain("110000000", redacted);
            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("222-33-4444", redacted);
            Assert.DoesNotContain("333-44-5555", redacted);
            Assert.DoesNotContain("444-55-6666", redacted);
            Assert.DoesNotContain("555667777", redacted);
            Assert.DoesNotContain("P12345678", redacted);
            Assert.DoesNotContain("12-3456789", redacted);
            Assert.DoesNotContain("98-7654321", redacted);
            Assert.DoesNotContain("99-9999999", redacted);
            Assert.DoesNotContain("88-8888888", redacted);
            Assert.DoesNotContain("1980-01-15", redacted);
            Assert.DoesNotContain("1985-06-30", redacted);
            Assert.DoesNotContain("1990-12-01", redacted);
            Assert.DoesNotContain("a@b.com", redacted);
            Assert.DoesNotContain("x@y.com", redacted);
            Assert.DoesNotContain("555-1111", redacted);
            Assert.DoesNotContain("555-2222", redacted);
            Assert.DoesNotContain("555-3333", redacted);
            Assert.DoesNotContain("555-4444", redacted);
        }
    }
}
