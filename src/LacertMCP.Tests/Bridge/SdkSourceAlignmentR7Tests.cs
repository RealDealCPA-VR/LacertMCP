using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Schema;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using Xunit;

namespace LacertMCP.Tests.Bridge
{
    /// <summary>
    /// R7-SRC — facts pinned from the Intuit "Lacerte Software Development Kit
    /// Developer Instructions" (static.developer.intuit.com/resources/
    /// Lacerte_SDK_Developer_Instructions.pdf). Each test names the section.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SdkSourceAlignmentR7Tests
    {
        // "Parameters for Register function — Return Status (TPSDKStatus):
        //  ConsentDenied = 0, AccessGranted, VersionBlocked, NotSignedIn,
        //  LacerteTaxSetupRequired" → raw 0..4 map onto the 200..204 namespace.
        [Theory]
        [InlineData(0, StatusCodes.ConsentDenied)]
        [InlineData(1, StatusCodes.AccessGranted)]
        [InlineData(2, StatusCodes.RegVersionBlocked)]
        [InlineData(3, StatusCodes.NotSignedIn)]
        [InlineData(4, StatusCodes.LacerteTaxSetupRequired)]
        [InlineData(201, StatusCodes.AccessGranted)] // already namespaced: unchanged
        public void TpsdkStatus_RawEnum_MapsToNamespacedCodes(int raw, int expected)
            => Assert.Equal(expected, SdkAccess.MapTpsdkStatus(raw));

        // "6. String – Value (Value to be written)" — values are rendered as
        // invariant strings regardless of the host culture.
        [Fact]
        public void SdkValueString_IsInvariant()
        {
            var prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fi-FI");
                Assert.Equal("1234.5", SdkAccess.ToSdkValueString(1234.5));
                Assert.Equal("42", SdkAccess.ToSdkValueString(42));
                Assert.Equal("True", SdkAccess.ToSdkValueString(true));
                Assert.Equal("", SdkAccess.ToSdkValueString(null));
                using var doc = JsonDocument.Parse("{\"n\":12.75,\"s\":\"x\"}");
                Assert.Equal("12.75", SdkAccess.ToSdkValueString(doc.RootElement.GetProperty("n")));
                Assert.Equal("x", SdkAccess.ToSdkValueString(doc.RootElement.GetProperty("s")));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        // "objClient.TaxType = (TaxModule)(taxTypeComboBox.SelectedIndex + 1)"
        // with the documented module order Individual … Benefit.
        [Theory]
        [InlineData('I', "Individual", 1)]
        [InlineData('C', "Corporate", 2)]
        [InlineData('S', "SCorporate", 4)]
        [InlineData('B', "Benefit", 9)]
        [InlineData('P', null, 3)]
        public void TaxModuleOrdinal_FollowsDocumentedOrder(char module, string? taxType, int expected)
            => Assert.Equal(expected, Modules.TaxModuleOrdinal(module, taxType));

        // "newQueryClientState = select * from [TEST-Detail-ClientStateInfo] where C3_G1 = 'TX'"
        // "newQuerySubClientState = select * from [TEST-Detail-SubClientStateInfo] where C4_G1 = 'TX' and C4_1 = 1"
        [Fact]
        public void Ty21Queries_UseDocumentedTableNames()
        {
            Assert.Equal("select * from [TEST-Detail-ClientStateInfo] where C3_G1 = 'TX'",
                SqlTemplates.Data3NewFormByState("TEST", "TX"));
            Assert.Equal("select * from [TEST-Detail-SubClientStateInfo] where C4_G1 = 'TX' and C4_1 = 1",
                SqlTemplates.Data4NewFormByState("TEST", "TX", 1));
            Assert.Equal("select * from [TEST-SubClientInfo]", SqlTemplates.Data2NewForm("TEST"));
        }

        // Connection String: "driver=LacerteDSIIDriver; DataDir=...; TaxYear=2019; ReturnType=Individual"
        [Fact]
        public void ConnectionString_UsesDataDirAndFourDigitYear()
        {
            var cs = ConnectionStringBuilder.Build(@"C:\Lacerte\24Tax\IData", "Individual", 2024);
            Assert.Contains("DataDir={", cs);
            Assert.Contains(";TaxYear=2024;", cs);
            Assert.DoesNotContain("Datapath=", cs);
        }

        // "Working with Detail Files" column constraints, enforced in both modes.
        private static DetailRow Row(
            string prefix = "", string suffix = "", string cityAbbr = "", string source = "",
            string tsj = "", string importSrc = "", double? amount = 1.0)
            => new DetailRow("11", prefix, "1", suffix, "", cityAbbr, source, tsj, importSrc, amount, "r7-src");

        [Theory]
        [InlineData("W2")]       // a form tag is not a Source flag
        [InlineData("1099INT")]
        public void DetailRow_SourceOutsideSdkVocabulary_Rejected(string source)
            => AssertInvalid(Row(source: source));

        [Fact] public void DetailRow_ImportSrcIsReadOnly() => AssertInvalid(Row(importSrc: "CSV"));
        [Fact] public void DetailRow_CityAbbrMax3() => AssertInvalid(Row(cityAbbr: "SFOX"));
        [Fact] public void DetailRow_PrefixMustBeInteger() => AssertInvalid(Row(prefix: "PX"));
        [Fact] public void DetailRow_SuffixMustBeInteger() => AssertInvalid(Row(suffix: "SX"));
        [Fact] public void DetailRow_AmountRange() => AssertInvalid(Row(amount: 1_000_000_000d));
        [Fact] public void DetailRow_AmountPrecision() => AssertInvalid(Row(amount: 1.1234567890123));
        [Fact] public void DetailRow_TsjOutsideVocabulary_Rejected() => AssertInvalid(Row(tsj: "X"));

        [Fact]
        public void DetailRow_SdkValidValues_Accepted()
        {
            BridgeInputValidation.ValidateDetailRowContent(
                Row(prefix: "1", suffix: "1000", cityAbbr: "SFO", source: "Nonsource", tsj: "Joint", amount: -999_999_999d),
                "test");
        }

        private static void AssertInvalid(DetailRow row)
        {
            var ex = Assert.Throws<LacerteSDKError>(() => BridgeInputValidation.ValidateDetailRowContent(row, "test"));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        // Appendix "Database Field Access" + "Not Supported Fields in TY21",
        // shipped as literal shard / overlay entries (source "sdk-pdf").
        [Theory]
        [InlineData('I', 1, "C1_G4", 2024, FieldAccess.ReadWrite)]
        [InlineData('I', 1, "C1_G5", 2024, FieldAccess.ReadOnly)]
        [InlineData('P', 5, "C5_G61", 2024, FieldAccess.Hidden)]
        [InlineData('C', 3, "C3_G1", 2024, FieldAccess.ReadOnly)]   // state abbreviation
        [InlineData('S', 4, "C4_1", 2024, FieldAccess.ReadOnly)]    // subclient number
        [InlineData('P', 1, "C1_36", 2021, FieldAccess.NotSupported)] // TY21 overlay (P)
        [InlineData('I', 5, "C5_67", 2021, FieldAccess.NotSupported)] // TY21 overlay range C5_1–C5_67
        [InlineData('P', 1, "C1_36", 2024, FieldAccess.Unknown)]    // exact-year overlay semantics
        [InlineData('I', 1, "C1_15", 2024, FieldAccess.Unknown)]    // not literally listed → fail-closed
        public void FieldAccess_PdfEntries_Resolve(char module, int table, string field, int year, FieldAccess expected)
        {
            FieldAccessMapLoader.ResetCacheForTests();
            Assert.Equal(expected, FieldAccessMapLoader.GetFieldAccess(module, table, field, year));
        }

        // R7 — a shard entry without "access" must not deserialize as the
        // enum default (ReadWrite): [JsonRequired] turns it into a JsonException,
        // which the loader handles fail-closed.
        [Fact]
        public void FieldEntry_MissingAccess_IsRejected_NotReadWrite()
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            Assert.ThrowsAny<JsonException>(() =>
                JsonSerializer.Deserialize<Dictionary<string, FieldEntry>>("{\"I|1|C1_15\":{\"source\":\"manual\"}}", opts));
            var ok = JsonSerializer.Deserialize<Dictionary<string, FieldEntry>>("{\"I|1|C1_15\":{\"access\":\"ReadOnly\"}}", opts);
            Assert.Equal(FieldAccess.ReadOnly, ok!["I|1|C1_15"].Access);
        }

        // R7-SRC — TaxType is the SDK TaxModule enum; a managed SDK object
        // needs a real enum value (dynamic int assignment would throw).
        private enum FakeTaxModule { TaxModule_Individual = 1, TaxModule_Corporate = 2, TaxModule_Benefit = 9 }
        private sealed class FakeManagedClient { public FakeTaxModule TaxType { get; set; } }

        [Fact]
        public void SetTaxType_ManagedEnumProperty_UsesEnumValue()
        {
            var c = new FakeManagedClient();
            LacerteAccessClient.SetTaxType(c, Modules.TaxModuleOrdinal('C', "Corporate"));
            Assert.Equal(FakeTaxModule.TaxModule_Corporate, c.TaxType);
        }

        // TSJ "Must be equal to Joint (or blank), Taxpayer, Spouse": aliases
        // are accepted at the tool boundary and stored as the SDK value.
        [Theory]
        [InlineData("T", "Taxpayer")]
        [InlineData("s", "Spouse")]
        [InlineData("Joint", "Joint")]
        [InlineData("", "")]
        public async Task DetailWrite_TsjAlias_StoredAsSdkValue(string tsj, string stored)
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            await mgr.WriteDetail("1001", Row(tsj: tsj, source: "source"));
            var last = (await mgr.ReadDetail("1001")).Last();
            Assert.Equal(stored, last.TSJ);
            Assert.Equal("Source", last.Source); // case-normalized
        }
    }
}
