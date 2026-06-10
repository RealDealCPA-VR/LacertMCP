using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// P18-025 (cluster X_feat_resource) — <c>lacerte_export_client</c>: the
    /// portable, self-documenting export envelope keyed by
    /// <c>lacerte://client/{clientNumber}</c>.
    /// <para>
    /// These tests pin the wire contract that makes the export tool useful as
    /// a Resource-shape stand-in:
    /// </para>
    /// <list type="bullet">
    ///   <item>Pre-context guard returns 9009 (matches every other read tool's
    ///     no-context contract).</item>
    ///   <item>Bad <c>clientNumber</c> shapes (empty, non-digit, oversized) are
    ///     rejected at the tool boundary BEFORE touching the session.</item>
    ///   <item>Happy path returns <c>uri</c>, <c>found=true</c>, the
    ///     <c>(dataPath, taxYear, module)</c> echo, and the same DBF row shape
    ///     <c>lacerte_client_read</c> emits.</item>
    ///   <item>Not-found returns <c>found=false</c>, <c>client=null</c>, the
    ///     same <c>uri</c> + context echo, and a hint matching the
    ///     <c>LacerteClientRead</c> hint contract.</item>
    ///   <item>R6-18 — the <c>module</c> echo is the canonical single-char
    ///     DBF code (<c>"I"</c>, <c>"C"</c>) matching every other tool; the
    ///     lowercase basename (<c>"individual"</c>, <c>"corporate"</c>)
    ///     moved to <c>moduleFileBasename</c>.</item>
    ///   <item>Parity with <c>LacerteClientRead</c> — the inner <c>client</c>
    ///     row is byte-identical, locking in observational identity.</item>
    /// </list>
    /// </summary>
    public class ClientResourceToolsTests
    {
        [Fact]
        public async Task ExportClient_NoContext_Returns9009()
        {
            var tools = new ClientResourceTools(ToolTestHelpers.NewSimManager());
            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("1001"));
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(9009, root.GetProperty("statusCode").GetInt32());
        }

        [Fact]
        public async Task ExportClient_EmptyClientNumber_Rejected()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientResourceTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("  "));
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task ExportClient_NonDigitClientNumber_Rejected()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientResourceTools(mgr);

            // "BROW" is the operator-supplied ClientID, NOT a ClientNumber —
            // the tool must reject it at the boundary so the LLM sees the
            // disambiguation hint instead of an opaque "not found" miss.
            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("BROW"));
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task ExportClient_TooLongClientNumber_Rejected()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientResourceTools(mgr);

            // 17 digits — one over the MaxClientNumberLength ceiling. The
            // pre-flight rejects anything that can't possibly be a Lacerte
            // ClientNumber before delegating to the session.
            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("12345678901234567"));
            Assert.False(root.GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task ExportClient_KnownClient_Returns_UriFoundContextRow()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientResourceTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");

            // URI is the canonical export key.
            Assert.Equal("lacerte://client/1001", result.GetProperty("uri").GetString());
            Assert.Equal("1001", result.GetProperty("clientNumber").GetString());
            Assert.True(result.GetProperty("found").GetBoolean());

            // Context echo pins resolution context for downstream consumers.
            var ctx = result.GetProperty("context");
            Assert.Equal(SeedData.DataPathIData2024, ctx.GetProperty("dataPath").GetString());
            Assert.Equal(2024, ctx.GetProperty("taxYear").GetInt32());
            // R6-18 — `module` carries the canonical single-char DBF code;
            // the lowercase basename moved to `moduleFileBasename`.
            Assert.Equal("I", ctx.GetProperty("module").GetString());
            Assert.Equal("individual", ctx.GetProperty("moduleFileBasename").GetString());

            // Inner client row carries the same shape as LacerteClientRead
            // (camelCase clientId per P17-011 / P16-054).
            Assert.Equal("BROW", result.GetProperty("client").GetProperty("clientId").GetString());
        }

        [Fact]
        public async Task ExportClient_UnknownClient_FoundFalse_WithHint()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientResourceTools(mgr);

            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("9999"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");

            // URI + context echo persist on the miss path so an LLM cache
            // keyed on the URI can still bind the negative result.
            Assert.Equal("lacerte://client/9999", result.GetProperty("uri").GetString());
            Assert.False(result.GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("client").ValueKind);

            // Hint matches the LacerteClientRead miss-path contract — names
            // the missing ClientNumber and the recovery tool.
            Assert.True(result.TryGetProperty("hint", out var hint));
            var hintText = hint.GetString();
            Assert.NotNull(hintText);
            Assert.Contains("9999", hintText);
            Assert.Contains("lacerte_client_list", hintText);
            Assert.Contains("ClientID", hintText);
        }

        [Fact]
        public async Task ExportClient_CorporateModule_EmitsCanonicalModuleCode()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tools = new ClientResourceTools(mgr);

            // ACME has ClientNumber "1001" in the corporate seed.
            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("1001"));
            Assert.True(root.GetProperty("success").GetBoolean());
            var result = root.GetProperty("result");
            Assert.True(result.GetProperty("found").GetBoolean());

            var ctx = result.GetProperty("context");
            // R6-18 — canonical single-char code on `module`; the lowercase
            // basename info is preserved under `moduleFileBasename`.
            Assert.Equal("C", ctx.GetProperty("module").GetString());
            Assert.Equal("corporate", ctx.GetProperty("moduleFileBasename").GetString());
            Assert.Equal(SeedData.DataPathCData2024, ctx.GetProperty("dataPath").GetString());

            // Same URI shape regardless of module — the URI binds to active
            // context, not to (dataPath, year, module).
            Assert.Equal("lacerte://client/1001", result.GetProperty("uri").GetString());
        }

        [Fact]
        public async Task ExportClient_ParityWithClientRead_SameClientPayload()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var exportTools = new ClientResourceTools(mgr);
            var readTools = new ClientsTools(mgr);

            var exportRoot = ToolTestHelpers.Parse(await exportTools.LacerteExportClient("1001"));
            var readRoot = ToolTestHelpers.Parse(await readTools.LacerteClientRead("1001"));

            // Both envelopes must report the same client identity — the
            // export tool is a superset, not a divergence. Lock the parity
            // on the canonical clientId so a future drift in either path
            // surfaces here first.
            Assert.True(exportRoot.GetProperty("success").GetBoolean());
            Assert.True(readRoot.GetProperty("success").GetBoolean());

            var exportClient = exportRoot.GetProperty("result").GetProperty("client");
            var readClient = readRoot.GetProperty("result").GetProperty("client");

            Assert.Equal(
                readClient.GetProperty("clientId").GetString(),
                exportClient.GetProperty("clientId").GetString());
            Assert.Equal(
                readClient.GetProperty("clientNumber").GetString(),
                exportClient.GetProperty("clientNumber").GetString());
        }

        [Fact]
        public async Task ExportClient_NotFoundEnvelope_StillCarriesContextEcho()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tools = new ClientResourceTools(mgr);

            // Even on a miss, the context echo must be present so an LLM
            // pinning the negative result for caching can scope the absence
            // to the resolution context — "not present in 2024 Individual"
            // is far more useful than an unscoped "not found".
            var root = ToolTestHelpers.Parse(await tools.LacerteExportClient("8888"));
            var result = root.GetProperty("result");
            Assert.False(result.GetProperty("found").GetBoolean());

            var ctx = result.GetProperty("context");
            Assert.Equal(2024, ctx.GetProperty("taxYear").GetInt32());
            // R6-18 — canonical single-char module code.
            Assert.Equal("I", ctx.GetProperty("module").GetString());
        }
    }
}
