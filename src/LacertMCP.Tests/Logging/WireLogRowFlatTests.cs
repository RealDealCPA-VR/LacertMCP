using System.Text.Json;
using LacertMCP.Server.Logging;
using Xunit;

namespace LacertMCP.Tests.Logging
{
    /// <summary>
    /// P15-057 — row-flat dict redaction. ListClients / ReadClient / ReadTable
    /// success envelopes return rows shaped
    /// <c>{"C1_15":"111-22-3333","C1_27":"555-1234",...}</c> where the row key
    /// IS the Lacerte field code. The pre-P15-057 key-name walker only
    /// matched friendly names (TaxpayerSSN, HomePhone) so the row-flat shape
    /// shipped raw PII through both the disk log AND the wire envelope.
    ///
    /// <para>
    /// The fix consults <c>_piiFieldCodes</c> directly as a key-name match
    /// inside RedactNode, independent of the sibling-pair pass for
    /// WriteField. Applies to BOTH scopes — these codes are true-secret
    /// slots regardless of whether a third party reads the disk log or the
    /// LLM reads the wire envelope.
    /// </para>
    /// </summary>
    public class WireLogRowFlatTests
    {
        [Fact]
        public void Redact_RowFlatDict_RedactsRawFieldCodeKeys_P15_057()
        {
            // Mirror what SimulationStore.CloneRow / LacerteDsnReader.Query
            // emits: a flat row keyed by raw column names. The PII slots
            // (C1_15 TaxpayerSSN, C1_27 HomePhone, C1_29 EmailID, C1_71/72
            // bank) must be scrubbed; non-PII columns (ClientNumber, etc.)
            // pass through.
            var raw = JsonSerializer.Serialize(new
            {
                ClientNumber = "1001",
                C1_15 = "111-22-3333",  // TaxpayerSSN
                C1_27 = "555-1234",     // HomePhone
                C1_29 = "tp@example.com", // EmailID
                C1_71 = "9876543210",   // BankAccountNumber
                C1_72 = "110000000",    // RoutingNumber
                Status = "ACTIVE",
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("555-1234", redacted);
            Assert.DoesNotContain("tp@example.com", redacted);
            Assert.DoesNotContain("9876543210", redacted);
            Assert.DoesNotContain("110000000", redacted);

            // Non-PII columns preserved.
            Assert.Contains("\"ClientNumber\":\"1001\"", redacted);
            Assert.Contains("\"Status\":\"ACTIVE\"", redacted);
        }

        [Fact]
        public void Redact_RowFlatDict_BothScopes_RedactPiiFieldCodes_P15_057()
        {
            // Wire scope must also catch the row-flat shape — these are true
            // secrets, not operator-facing diagnostics.
            var raw = JsonSerializer.Serialize(new
            {
                ClientNumber = "1001",
                C1_15 = "111-22-3333",
            });

            var wireRedacted = WireLog.Redact(raw, RedactionScope.Wire);
            var diskRedacted = WireLog.Redact(raw, RedactionScope.Disk);

            Assert.DoesNotContain("111-22-3333", wireRedacted);
            Assert.DoesNotContain("111-22-3333", diskRedacted);
            Assert.Contains("\"ClientNumber\":\"1001\"", wireRedacted);
        }

        [Fact]
        public void Redact_RowFlatDict_NestedInsideRowsArray_StillRedacts_P15_057()
        {
            // ListClients returns {"rows":[{...},{...}], "totalCount": N}.
            // The walker must recurse into the array to reach the row dicts.
            var raw = JsonSerializer.Serialize(new
            {
                totalCount = 2,
                rows = new object[]
                {
                    new { ClientNumber = "1001", C1_15 = "111-22-3333" },
                    new { ClientNumber = "1002", C1_15 = "222-33-4444" },
                },
            });

            var redacted = WireLog.Redact(raw);

            Assert.DoesNotContain("111-22-3333", redacted);
            Assert.DoesNotContain("222-33-4444", redacted);
            Assert.Contains("\"ClientNumber\":\"1001\"", redacted);
            Assert.Contains("\"ClientNumber\":\"1002\"", redacted);
        }
    }
}
