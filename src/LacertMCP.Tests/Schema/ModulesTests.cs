using System.Linq;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    public class ModulesTests
    {
        [Fact]
        public void All_Contains_All_Nine_Modules()
        {
            Assert.Equal(9, Modules.All.Count);
        }

        [Theory]
        [InlineData('I', "Individual",    "Individual")]
        [InlineData('C', "Corporate",     "Corporate")]
        [InlineData('P', "Partnership",   "Partnership")]
        [InlineData('S', "S-Corporation", "SCorporate")]
        [InlineData('F', "Fiduciary",     "Fiduciary")]
        [InlineData('R', "Exempt",        "Exempt")]
        [InlineData('T', "Estate",        "Estate")]
        [InlineData('N', "Gift",          "Gift")]
        [InlineData('B', "Benefit",       "Benefit")]
        public void GetByChar_Returns_Expected(char c, string name, string returnType)
        {
            var info = Modules.GetByChar(c);
            Assert.NotNull(info);
            Assert.Equal(c, info!.Char);
            Assert.Equal(name, info.Name);
            Assert.Equal(returnType, info.ReturnType);
        }

        [Theory]
        [InlineData("Individual",  'I')]
        [InlineData("SCorporate",  'S')]
        [InlineData("Benefit",     'B')]
        public void GetByReturnType_Returns_Expected(string returnType, char expectedChar)
        {
            var info = Modules.GetByReturnType(returnType);
            Assert.NotNull(info);
            Assert.Equal(expectedChar, info!.Char);
        }

        [Fact]
        public void GetByChar_Roundtrips_GetByReturnType_For_Every_Module()
        {
            foreach (var module in Modules.All)
            {
                var byChar = Modules.GetByChar(module.Char);
                var byRt = Modules.GetByReturnType(module.ReturnType);
                Assert.Equal(module, byChar);
                Assert.Equal(module, byRt);
            }
        }

        [Fact]
        public void GetByChar_Unknown_Returns_Null()
        {
            Assert.Null(Modules.GetByChar('Z'));
        }

        [Fact]
        public void GetByReturnType_Unknown_Returns_Null()
        {
            Assert.Null(Modules.GetByReturnType("Bogus"));
        }

        [Fact]
        public void All_Module_Chars_Are_Unique()
        {
            var distinctCount = Modules.All.Select(m => m.Char).Distinct().Count();
            Assert.Equal(Modules.All.Count, distinctCount);
        }

        [Fact]
        public void All_Module_ReturnTypes_Are_Unique()
        {
            var distinctCount = Modules.All.Select(m => m.ReturnType).Distinct().Count();
            Assert.Equal(Modules.All.Count, distinctCount);
        }

        [Theory]
        [InlineData('I', "individual")]
        [InlineData('C', "corporate")]
        [InlineData('P', "partnership")]
        [InlineData('S', "scorp")]
        [InlineData('F', "fiduciary")]
        [InlineData('R', "exempt")]
        [InlineData('T', "estate")]
        [InlineData('N', "gift")]
        [InlineData('B', "benefit")]
        public void FileBasenameFor_Returns_Expected(char c, string expected)
        {
            Assert.Equal(expected, Modules.FileBasenameFor(c));
        }

        [Fact]
        public void FileBasenameFor_Unknown_Returns_Null()
        {
            Assert.Null(Modules.FileBasenameFor('Z'));
        }

        [Theory]
        [InlineData('I', "Individual", true)]
        [InlineData('I', "1040", true)]
        [InlineData('I', "individual", true)]     // case-insensitive
        [InlineData('I', "INDIVIDUAL", true)]
        [InlineData('I', "Corporate", false)]    // wrong module
        [InlineData('S', "SCorporate", true)]
        [InlineData('S', "1120S", true)]
        [InlineData('S', "scorp", true)]          // case-insensitive
        [InlineData('S', "S-Corporation", true)]
        [InlineData('B', "5500", true)]
        [InlineData('Z', "Anything", false)]      // unknown module
        [InlineData('I', "", false)]              // empty taxType
        [InlineData('I', null, false)]            // null taxType
        public void AcceptsTaxType_Returns_Expected(char c, string? taxType, bool expected)
        {
            Assert.Equal(expected, Modules.AcceptsTaxType(c, taxType));
        }

        [Fact]
        public void TaxTypeAliasesFor_Unknown_Returns_Null()
        {
            Assert.Null(Modules.TaxTypeAliasesFor('Z'));
        }

        [Fact]
        public void TaxTypeAliasesFor_Individual_Contains_Both_Aliases()
        {
            var aliases = Modules.TaxTypeAliasesFor('I');
            Assert.NotNull(aliases);
            Assert.Contains("Individual", aliases!);
            Assert.Contains("1040", aliases!);
        }

        // ============================================================
        // P15-059 — CanonicalizeTaxType: form-number alias → canonical
        // ============================================================

        [Theory]
        [InlineData('I', "Individual",    "Individual")]
        [InlineData('I', "1040",          "Individual")]
        [InlineData('I', "individual",    "Individual")]   // case-insensitive
        [InlineData('I', "INDIVIDUAL",    "Individual")]
        [InlineData('C', "Corporate",     "Corporate")]
        [InlineData('C', "1120",          "Corporate")]
        [InlineData('P', "Partnership",   "Partnership")]
        [InlineData('P', "1065",          "Partnership")]
        [InlineData('S', "SCorporate",    "SCorporate")]
        [InlineData('S', "1120S",         "SCorporate")]
        [InlineData('S', "S-Corporation", "SCorporate")]
        [InlineData('S', "SCorp",         "SCorporate")]
        [InlineData('S', "scorp",         "SCorporate")]   // case-insensitive
        [InlineData('F', "1041",          "Fiduciary")]
        [InlineData('R', "990",           "Exempt")]
        [InlineData('T', "706",           "Estate")]
        [InlineData('N', "709",           "Gift")]
        [InlineData('B', "5500",          "Benefit")]
        public void CanonicalizeTaxType_Returns_Canonical_ReturnType(char c, string input, string expected)
        {
            Assert.Equal(expected, Modules.CanonicalizeTaxType(c, input));
        }

        [Theory]
        [InlineData('Z', "Individual")]   // unknown module
        [InlineData('I', "Corporate")]    // wrong module's alias
        [InlineData('I', "Bogus")]        // not an alias at all
        [InlineData('I', "")]             // empty
        [InlineData('I', null)]           // null
        public void CanonicalizeTaxType_Returns_Null_For_NoMatch(char c, string? input)
        {
            Assert.Null(Modules.CanonicalizeTaxType(c, input));
        }

        [Fact]
        public void CanonicalizeTaxType_Is_Idempotent_On_Canonical_Input()
        {
            // Canonical input should pass through unchanged for every module.
            foreach (var m in Modules.All)
            {
                Assert.Equal(m.ReturnType, Modules.CanonicalizeTaxType(m.Char, m.ReturnType));
            }
        }
    }
}
