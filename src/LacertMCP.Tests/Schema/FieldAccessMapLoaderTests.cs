using LacertMCP.Server.Errors;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Schema
{
    public class FieldAccessMapLoaderTests
    {
        [Fact]
        public void GetFieldAccess_ClientNumber_Field_Returns_ReadOnly_Universal_Rule()
        {
            // C{table}_0 is always ReadOnly across every module — no JSON needed.
            Assert.Equal(FieldAccess.ReadOnly,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_0", 2024));
        }

        [Fact]
        public void GetFieldAccess_ClientNumber_Field_Universal_Rule_Holds_For_Every_Table()
        {
            for (var table = 1; table <= 7; table++)
            {
                var field = "C" + table + "_0";
                Assert.Equal(FieldAccess.ReadOnly,
                    FieldAccessMapLoader.GetFieldAccess('I', table, field, 2024));
            }
        }

        [Fact]
        public void GetFieldAccess_Uncatalogued_Field_Returns_Unknown()
        {
            Assert.Equal(FieldAccess.Unknown,
                FieldAccessMapLoader.GetFieldAccess('I', 1, "C1_99999", 2024));
        }

        [Fact]
        public void GetFieldAccess_Field_With_Null_Returns_Unknown()
        {
            Assert.Equal(FieldAccess.Unknown,
                FieldAccessMapLoader.GetFieldAccess('I', 1, null!, 2024));
        }

        [Fact]
        public void AssertWritable_ClientNumber_Throws_ExposedForRead()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, "C1_0", 2024));
            Assert.Equal(StatusCodes.ExposedForRead, ex.StatusCode);
            Assert.Equal("ExposedForRead", ex.StatusName);
        }

        [Fact]
        public void AssertWritable_Unknown_Field_Throws_UnknownFieldAccess_Synthetic_9012()
        {
            var ex = Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, "C1_99999", 2024));
            Assert.Equal(SyntheticStatusCodes.UnknownFieldAccess, ex.StatusCode);
            Assert.Equal("UnknownFieldAccess", ex.StatusName);
            // Context carries the precise field/module/table/year coords so the
            // operator can either fix the payload or populate the access map.
            Assert.Equal("C1_99999", ex.Context["field"]?.ToString());
            Assert.Equal("I", ex.Context["module"]?.ToString());
            Assert.Equal(1, (int)ex.Context["table"]!);
            Assert.Equal(2024, (int)ex.Context["taxYear"]!);
        }

        [Fact]
        public void AssertWritable_Unknown_Field_With_Force_Does_Not_Throw()
        {
            // No exception expected when force:true overrides the Unknown gate.
            FieldAccessMapLoader.AssertWritable('I', 1, "C1_99999", 2024, force: true);
        }

        [Fact]
        public void AssertWritable_ClientNumber_With_Force_Still_Throws()
        {
            // force:true ONLY overrides Unknown — ReadOnly is still hard-blocked.
            Assert.Throws<LacerteSDKError>(() =>
                FieldAccessMapLoader.AssertWritable('I', 1, "C1_0", 2024, force: true));
        }
    }
}
