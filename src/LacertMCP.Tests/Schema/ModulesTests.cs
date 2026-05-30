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
    }
}
