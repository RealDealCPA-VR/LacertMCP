using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// Phase 14 — redaction-surface expansion tests (P14-018, P14-019, P14-057).
    /// Verifies the JsonNode-walking redactor covers PII keys beyond the original
    /// 4-key list, handles arrays + nested objects + whitespace-padded JSON, and
    /// scrubs the WriteField {"field":"&lt;PII-code&gt;","value":"&lt;secret&gt;"}
    /// pattern where the secret is keyed by the literal "value".
    /// </summary>
    public class WireLogRedactionTests
    {
        [Fact]
        public void Redact_CoversExpandedPiiKeyset()
        {
            // Sample includes the new key alternatives that didn't exist in the
            // original 4-key regex: TaxpayerSSN, EmailID, FirstName, etc.
            var raw = JsonSerializer.Serialize(new
            {
                TaxpayerSSN = "111-22-3333",
                SpouseSSN = "444-55-6666",
                DependentSSN = "777-88-9999",
                EmployerEIN = "12-3456789",
                DateOfBirth = "1980-01-01",
                EmailID = "victim@example.com",
                HomePhone = "555-1234",
                FirstName = "Jane",
                LastName = "Doe",
                StreetAddress = "1 Main St",
                ZipCode = "90210",
                Description = "free-form notes",
                clientNumber = "1001", // not redacted
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("444-55-6666", redacted);
            Assert.DoesNotContain("777-88-9999", redacted);
            Assert.DoesNotContain("12-3456789", redacted);
            Assert.DoesNotContain("1980-01-01", redacted);
            Assert.DoesNotContain("victim@example.com", redacted);
            Assert.DoesNotContain("555-1234", redacted);
            Assert.DoesNotContain("Jane", redacted);
            Assert.DoesNotContain("Doe", redacted);
            Assert.DoesNotContain("1 Main St", redacted);
            Assert.DoesNotContain("90210", redacted);
            Assert.DoesNotContain("free-form notes", redacted);

            // Non-sensitive keys are passed through.
            Assert.Contains("\"clientNumber\":\"1001\"", redacted);
        }

        [Fact]
        public void Redact_RedactsValueInsideArray()
        {
            // P14-057: prior regex only matched scalars; arrays slipped through.
            var raw = "{\"SSN\":[\"123-45-6789\",\"999-99-9999\"]}";
            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("123-45-6789", redacted);
            Assert.DoesNotContain("999-99-9999", redacted);
        }

        [Fact]
        public void Redact_RedactsValueInsideNestedObject()
        {
            // P14-057: nested-object values weren't reached by the regex.
            var raw = "{\"SSN\":{\"value\":\"123-45-6789\",\"source\":\"intake\"}}";
            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("123-45-6789", redacted);
        }

        [Fact]
        public void Redact_HandlesWhitespacePaddedJson()
        {
            // Pretty-printed shape (WriteIndented=true emits this).
            var raw = "{\n  \"SSN\": \"123-45-6789\",\n  \"other\": \"ok\"\n}";
            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("123-45-6789", redacted);
            // Non-sensitive key preserved.
            Assert.Contains("\"other\":\"ok\"", redacted);
        }

        [Fact]
        public void Redact_RedactsRedundantlyForDeeplyNestedShapes()
        {
            // A record where args has a nested object with the sensitive key
            // several levels down (e.g. {"args":{"row":{"SSN":"..."}}}).
            var raw = JsonSerializer.Serialize(new
            {
                op = "WriteDetail",
                args = new { row = new { SSN = "111-22-3333", desc = "x" } },
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
        }

        [Fact]
        public void Redact_WriteFieldShape_RedactsSiblingValueWhenFieldIsPiiCode()
        {
            // P14-018 second-pass: {"field":"C1_15","value":"<SSN>"} — the
            // secret is keyed by literal "value" so the key-name walker can't
            // catch it; the field-code-aware scrubber must.
            var raw = JsonSerializer.Serialize(new
            {
                table = 1,
                clientNumber = "1001",
                field = "C1_15", // TaxpayerSSN
                value = "111-22-3333",
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
        }

        [Fact]
        public void Redact_WriteFieldShape_DoesNotScrubValueWhenFieldIsNotPii()
        {
            // Sanity check: non-PII field codes preserve the value untouched.
            var raw = JsonSerializer.Serialize(new
            {
                field = "C1_99", // not in PII set
                value = "some-routine-value",
            });

            var redacted = WireLog.Redact(raw);

            Assert.Contains("some-routine-value", redacted);
        }

        [Fact]
        public void Redact_MalformedJson_FallsBackToRegex()
        {
            // The regex fallback only catches scalar shapes but still
            // protects the disk against the legacy keyset.
            var raw = "not-valid-json \"SSN\":\"123-45-6789\" trailing-garbage";
            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("123-45-6789", redacted);
            Assert.Contains("\"SSN\":\"***\"", redacted);
        }

        [Fact]
        public void Redact_CreateClientArgsShape_RedactsNameAndEmail()
        {
            // Exact shape that CreateClient logs: args record with FirstName,
            // LastName, EmailID — all PII that prior regex didn't cover.
            var raw = JsonSerializer.Serialize(new
            {
                args = new
                {
                    ClientID = "WLOG",
                    TaxType = "Individual",
                    FirstName = "Jane",
                    LastName = "Doe",
                    EmailID = "jane.doe@example.com",
                },
                dryRun = false,
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("Jane", redacted);
            Assert.DoesNotContain("Doe", redacted);
            Assert.DoesNotContain("jane.doe@example.com", redacted);
            // Non-PII args preserved.
            Assert.Contains("\"ClientID\":\"WLOG\"", redacted);
            Assert.Contains("\"TaxType\":\"Individual\"", redacted);
        }

        [Fact]
        public void Redact_ToolErrorContext_RedactsRawErrorAndReason()
        {
            // P14-019: error envelope context dict commonly carries rawError
            // (generic Exception wrap) and reason (Bridge/Registration). Both
            // can echo SDK-returned taxpayer data and must be redacted.
            var raw = JsonSerializer.Serialize(new
            {
                success = false,
                context = new
                {
                    rawError = "SDK rejected payload: taxpayer SSN 111-22-3333 invalid",
                    reason = "Late-bound COM call failed: 333-22-4444",
                },
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("333-22-4444", redacted);
        }

        [Fact]
        public void WriteForTest_RedactsExpandedKeyset_BeforeDisk()
        {
            var dir = Path.Combine(Path.GetTempPath(), "wirelog-pii-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var args = new
                {
                    TaxpayerSSN = "111-22-3333",
                    EmailID = "victim@example.com",
                    FirstName = "Jane",
                };
                WireLog.WriteForTest(dir, "CreateClient", args, new { ok = true }, durationMs: 1);

                var files = Directory.GetFiles(dir, "lacerte-*.log");
                Assert.Single(files);
                var line = File.ReadAllLines(files[0]).First();

                Assert.DoesNotContain("111-22-3333", line);
                Assert.DoesNotContain("victim@example.com", line);
                Assert.DoesNotContain("Jane", line);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
