using System.Linq;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    public class TablesTests
    {
        [Fact]
        public void All_Contains_All_Eight_Tables()
        {
            Assert.Equal(8, Tables.All.Count);
        }

        [Fact]
        public void Table_Numbers_Are_One_Through_Eight()
        {
            var numbers = Tables.All.Select(t => t.Number).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, numbers);
        }

        [Theory]
        [InlineData(1, "DATA1", false)]
        [InlineData(2, "DATA2", false)]
        [InlineData(3, "DATA3", false)]
        [InlineData(4, "DATA4", false)]
        [InlineData(5, "DATA5", false)]
        [InlineData(6, "DATA6", false)]
        [InlineData(7, "DATA7", false)]
        [InlineData(8, "DATA8", true)]
        public void Each_Table_Has_Expected_Shape(int number, string dbfName, bool reserved)
        {
            var info = Tables.All.Single(t => t.Number == number);
            Assert.Equal(dbfName, info.DbfName);
            Assert.Equal(reserved, info.Reserved);
            Assert.False(string.IsNullOrWhiteSpace(info.Contents));
            Assert.False(string.IsNullOrWhiteSpace(info.AccessNote));
        }

        [Fact]
        public void Only_Table_Eight_Is_Reserved()
        {
            var reservedNumbers = Tables.All.Where(t => t.Reserved).Select(t => t.Number).ToArray();
            Assert.Equal(new[] { 8 }, reservedNumbers);
        }
    }
}
