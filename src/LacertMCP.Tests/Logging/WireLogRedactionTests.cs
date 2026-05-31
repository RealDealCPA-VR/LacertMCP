using System.Collections.Generic;
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
        public void Redact_WriteFieldShape_DenylistScrubsValueRegardlessOfFieldCode_P15_058()
        {
            // P15-058 — denylist semantics: every {field, value} pair has
            // its value scrubbed regardless of field code or module. Pre-P15-058
            // this was an allowlist gated on _piiFieldCodes (DATA1-Individual
            // only), which silently leaked entity-module PII through WriteField
            // log records like {field:"C2_99", value:"officer-SSN"}.
            //
            // Rationale: the curated set covers only Individual; the entity
            // modules (Corp officer SSN, Partnership K-1 SSN, Fiduciary
            // beneficiary SSN, all bank columns) have no spec source yet.
            // Over-redacting on a debug log is strictly safer.
            var raw = JsonSerializer.Serialize(new
            {
                field = "C1_99", // not in old _piiFieldCodes set
                value = "some-routine-value",
            });

            var redacted = WireLog.Redact(raw);

            // Value is now scrubbed (was preserved pre-P15-058).
            Assert.DoesNotContain("some-routine-value", redacted);
            Assert.Contains("\"value\":\"***\"", redacted);
            // Field code stays visible so operator can still diagnose the call.
            Assert.Contains("\"field\":\"C1_99\"", redacted);
        }

        [Fact]
        public void Redact_WriteFieldShape_DenylistScrubsEntityModuleValues_P15_058()
        {
            // P15-058 — entity-module coverage that allowlist semantics missed.
            // module="C" (Corp) + an unknown field code under the old allowlist
            // would log the SSN verbatim. Under denylist semantics the value
            // is scrubbed regardless.
            var raw = JsonSerializer.Serialize(new
            {
                module = "C",
                field = "C2_99",  // hypothetical Corp officer SSN slot
                value = "111-22-3333",
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.Contains("\"value\":\"***\"", redacted);
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

        // -----------------------------------------------------------------
        // P15-018 — ODBC dict-row shape keyed by raw Lacerte field code.
        // LacerteDsnReader.Query materializes rows as
        // Dictionary<string, object?> keyed by OdbcDataReader.GetName(i),
        // so a `select * from [DATA1-Client Info]` log line shows
        // `{"C1_15":"111-22-3333"}`. Before P15-018 the key-name walker
        // only checked PascalCase keys (SSN/TaxpayerSSN/etc.) and the
        // field-code pair scrubber required a sibling `field`/`value`
        // shape, so this shape leaked verbatim.
        // -----------------------------------------------------------------

        [Fact]
        public void Redact_ColumnNameKeyedPiiCode_RedactsValue()
        {
            var raw = "{\"C1_15\":\"111-22-3333\"}";
            var redacted = WireLog.Redact(raw);
            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.Contains("\"C1_15\":\"***\"", redacted);
        }

        [Fact]
        public void Redact_OdbcRowShape_RedactsAllPiiColumns()
        {
            // Shape mirrors what LacerteDsnReader.Query logs as result:
            // a list of dicts keyed by the raw ODBC column names which
            // ARE Lacerte field codes.
            var raw = JsonSerializer.Serialize(new
            {
                op = "Odbc.Query",
                args = new { sql = "select * from [DATA1-Client Info]" },
                result = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["C1_0"] = "1001",      // ClientNumber — not PII
                        ["C1_15"] = "111-22-3333", // TaxpayerSSN
                        ["C1_14"] = "222-33-4444", // SpouseSSN
                        ["C1_19"] = "01/15/1985",  // DateOfBirth
                        ["C1_21"] = "06/30/1986",  // SpouseDateOfBirth
                        ["C1_27"] = "555-1111",    // HomePhone
                        ["C1_29"] = "victim@example.com", // EmailID
                        ["C1_71"] = "987654321",   // BankAccountNumber
                        ["C1_72"] = "110000000",   // RoutingNumber
                    },
                },
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("222-33-4444", redacted);
            Assert.DoesNotContain("01/15/1985", redacted);
            Assert.DoesNotContain("06/30/1986", redacted);
            Assert.DoesNotContain("555-1111", redacted);
            Assert.DoesNotContain("victim@example.com", redacted);
            Assert.DoesNotContain("987654321", redacted);
            Assert.DoesNotContain("110000000", redacted);
            // Non-PII column preserved.
            Assert.Contains("\"C1_0\":\"1001\"", redacted);
        }

        [Fact]
        public void Redact_OdbcRowShape_AppliesInWireScopeToo()
        {
            // The PII field codes are true-secret slots regardless of scope —
            // the LLM consuming the wire envelope should never see them either.
            var raw = "{\"C1_15\":\"111-22-3333\",\"C1_71\":\"987654321\"}";
            var redacted = WireLog.Redact(raw, RedactionScope.Wire);
            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("987654321", redacted);
        }

        [Fact]
        public void Redact_MalformedJsonWithColumnKey_FallsBackAndScrubs()
        {
            // Forces the regex fallback (input is not parseable JSON).
            var raw = "garbage \"C1_71\":\"987654321\" trailing";
            var redacted = WireLog.Redact(raw);
            Assert.DoesNotContain("987654321", redacted);
            Assert.Contains("\"C1_71\":\"***\"", redacted);
        }

        // -----------------------------------------------------------------
        // P15-019 — ODBC driver error text echoes the rejected value as a
        // substring. Key-name redactor scrubs the value wholesale (no
        // brittle substring regex needed).
        // -----------------------------------------------------------------

        [Fact]
        public void Redact_OdbcMessage_WipesEntireValue()
        {
            // OdbcErrorMapper.Map stuffs the raw driver text into context.odbcMessage.
            var raw = "{\"odbcMessage\":\"Syntax error near 'TaxpayerSSN = 111-22-3333'\"}";
            var redacted = WireLog.Redact(raw);
            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.Contains("\"odbcMessage\":\"***\"", redacted);
        }

        [Fact]
        public void Redact_OdbcMessage_WipedInWireScopeToo()
        {
            // Substring-leak vector is identical on the MCP wire — the SQLSTATE
            // diagnostic (under "sqlState") survives, but the driver echo is
            // scrubbed so the LLM never sees the rejected literal.
            var raw = "{\"sqlState\":\"42000\",\"odbcMessage\":\"Field C1_15 value '123-45-6789' rejected\"}";
            var redacted = WireLog.Redact(raw, RedactionScope.Wire);
            Assert.DoesNotContain("123-45-6789", redacted);
            Assert.Contains("\"sqlState\":\"42000\"", redacted);
        }

        [Fact]
        public void Redact_InternalErrorMessage_WipedAtDiskScope()
        {
            // WireLog's own catch blocks wrap caught exceptions as
            // {"error":{"type":..., "message": ex.Message}} — when ex.Message
            // is an OdbcException text the PII substring rides in under
            // "message". Wholesale-scrub at disk scope.
            var raw = "{\"error\":{\"type\":\"System.Data.Odbc.OdbcException\",\"message\":\"SSN 111-22-3333 invalid\"}}";
            var redacted = WireLog.Redact(raw);
            Assert.DoesNotContain("111-22-3333", redacted);
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

                // P15-078: date-roll-agnostic enumeration.
                var files = Directory.GetFiles(dir, "lacerte-*.log");
                Assert.NotEmpty(files);
                var line = files.SelectMany(File.ReadAllLines).First();

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
