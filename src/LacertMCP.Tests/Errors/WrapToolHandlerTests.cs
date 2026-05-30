using System;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    public class WrapToolHandlerTests
    {
        [Fact]
        public async Task Run_Success_ReturnsSuccessEnvelope()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                return (object)new { count = 3, message = "ok" };
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.Equal(3, result.GetProperty("count").GetInt32());
            Assert.Equal("ok", result.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Run_LacerteSDKError_ReturnsStructuredErrorEnvelope()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(StatusCodes.NotExposed);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(7, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("NotExposed", root.GetProperty("statusName").GetString());
            Assert.Equal("error", root.GetProperty("severity").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("userMessage").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("remediation").GetString()));
        }

        [Fact]
        public async Task Run_GenericException_FallsThroughToStatusCodeZero()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(0, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("Error", root.GetProperty("statusName").GetString());
            Assert.Equal("error", root.GetProperty("severity").GetString());

            var context = root.GetProperty("context");
            Assert.Equal("boom", context.GetProperty("rawError").GetString());
            Assert.Contains("InvalidOperationException", context.GetProperty("exceptionType").GetString());
        }

        [Fact]
        public async Task Run_SyntheticCode_ResolvesNameFromSyntheticTable()
        {
            var json = await WrapToolHandler.Run(async () =>
            {
                await Task.Yield();
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet);
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("ContextNotSet", root.GetProperty("statusName").GetString());
        }
    }
}
