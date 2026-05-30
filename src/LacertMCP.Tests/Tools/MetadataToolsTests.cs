using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Phase 8 #60-61: metadata discovery tools.
    /// </summary>
    public class MetadataToolsTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public async Task FieldAccessMap_Individual_Table1_TY24_IncludesC1_0_AsReadOnly()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteFieldAccessMap('I', 1, 2024);
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var fields = root.GetProperty("result").GetProperty("fields");
            Assert.True(fields.TryGetProperty("C1_0", out var c10));
            Assert.Equal("ReadOnly", c10.GetString());
        }

        [Fact]
        public async Task FieldAccessMap_InvalidModule_Throws()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteFieldAccessMap('Z', 1, 2024);
            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task FieldAccessMap_InvalidTable_Throws()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteFieldAccessMap('I', 8, 2024); // DATA8 reserved
            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task FieldAccessMap_TaxYearTooOld_Returns9003()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteFieldAccessMap('I', 1, 2017);
            var root = Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(SyntheticStatusCodes.TaxYearTooOld,
                root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ReturnCodes_ReturnsAtLeast30Entries()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteReturnCodes();
            var root = Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);

            var count = root.GetProperty("result").GetProperty("count").GetInt32();
            Assert.True(count >= 30, $"expected at least 30 codes, got {count}");
        }

        [Fact]
        public async Task ReturnCodes_IncludesSuccessAndSyntheticCodes()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteReturnCodes();
            var root = Parse(json);

            var codes = root.GetProperty("result").GetProperty("codes");
            bool hasSuccess = false;
            bool hasFieldIsReadOnly = false;
            bool hasNotSignedIn = false;
            foreach (var entry in codes.EnumerateArray())
            {
                var code = entry.GetProperty("code").GetInt32();
                if (code == StatusCodes.Success) hasSuccess = true;
                if (code == SyntheticStatusCodes.FieldIsReadOnly) hasFieldIsReadOnly = true;
                if (code == StatusCodes.NotSignedIn) hasNotSignedIn = true;
            }
            Assert.True(hasSuccess);
            Assert.True(hasFieldIsReadOnly);
            Assert.True(hasNotSignedIn);
        }
    }
}
