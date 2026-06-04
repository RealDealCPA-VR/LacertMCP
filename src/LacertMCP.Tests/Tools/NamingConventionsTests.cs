using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// X_naming_xtool cluster (P16-024 / P16-025 / P16-026 / P16-027 /
    /// P16-053 / P16-054 / P16-069 / P16-070 / P16-071 / P16-079 / P16-080) —
    /// regression sentinels for the cross-tool naming + response-shape
    /// conventions established in the round-3 preflight. These pin the
    /// canonical wire names so a future "simplify" pass can't quietly
    /// reintroduce the casing drift or the inner-`success`/`year` /
    /// `clientID` variants the cluster removed.
    /// </summary>
    public class NamingConventionsTests
    {
        // -----------------------------------------------------------------
        // P16-054 / P16-080 — every [McpServerTool] parameter is camelCase
        // with `Id` suffix mixed-case (no `clientID`/`emailID`/`subClientID`
        // ALL-CAPS suffix on the wire). Reflection over the tool assembly
        // catches any drift on future-added tools without per-method tests.
        // -----------------------------------------------------------------

        [Fact]
        public void ToolParameters_NoAllCapsIdSuffix_P16_054_P16_080()
        {
            var asm = typeof(SessionTools).Assembly;
            var toolMethods = asm.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

            var offenders = toolMethods
                .SelectMany(m => m.GetParameters().Select(p => new { Method = m, Param = p }))
                .Where(x =>
                {
                    var n = x.Param.Name ?? string.Empty;
                    // Reject the ALL-CAPS Id-suffix pattern: clientID / emailID /
                    // subClientID etc. The valid form is clientId / emailId /
                    // subClientId (Microsoft Framework Design Guidelines —
                    // 2-letter "Id" stays mixed-case in camelCase).
                    return n.EndsWith("ID") && n.Length >= 3 && char.IsLower(n[0]);
                })
                .Select(x => $"{x.Method.DeclaringType?.Name}.{x.Method.Name}({x.Param.Name})")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "Tool parameters must use camelCase `Id` (clientId/emailId/subClientId), not ALL-CAPS `ID`. Offenders: "
                + string.Join(", ", offenders));
        }

        // -----------------------------------------------------------------
        // P16-027 — module parameter is `string` (length-1) on every tool
        // that accepts a module character. `char` traps JSON binding (some
        // clients send "I" not 'I'); the trio (LacerteSetContext /
        // LacerteCanUseNewApis / LacerteFieldAccessMap) must agree.
        // -----------------------------------------------------------------

        [Fact]
        public void ToolParameters_ModuleParam_IsString_NotChar_P16_027()
        {
            var asm = typeof(SessionTools).Assembly;
            var toolMethods = asm.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

            var offenders = toolMethods
                .SelectMany(m => m.GetParameters().Select(p => new { Method = m, Param = p }))
                .Where(x => string.Equals(x.Param.Name, "module", System.StringComparison.Ordinal)
                            && x.Param.ParameterType == typeof(char))
                .Select(x => $"{x.Method.DeclaringType?.Name}.{x.Method.Name}")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "Tool `module` parameters must be `string` (length-1), not `char`. Offenders: "
                + string.Join(", ", offenders));
        }

        // -----------------------------------------------------------------
        // P16-069 / P16-079 — tax-year input parameter is `taxYear`, never
        // `year`. The MetadataTools.LacerteFieldAccessMap + the
        // FieldWriteBulkTools.LacerteFieldWriteBulk preflight both used
        // `year` pre-P16; both must now use `taxYear`.
        // -----------------------------------------------------------------

        [Fact]
        public void ToolParameters_TaxYearNamed_NotYear_P16_069_P16_079()
        {
            var asm = typeof(SessionTools).Assembly;
            var toolMethods = asm.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

            var offenders = toolMethods
                .SelectMany(m => m.GetParameters().Select(p => new { Method = m, Param = p }))
                .Where(x => string.Equals(x.Param.Name, "year", System.StringComparison.Ordinal)
                            && (x.Param.ParameterType == typeof(int) || x.Param.ParameterType == typeof(int?)))
                .Select(x => $"{x.Method.DeclaringType?.Name}.{x.Method.Name}")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "Tool tax-year parameters must be `taxYear`, not `year`. Offenders: "
                + string.Join(", ", offenders));
        }

        // -----------------------------------------------------------------
        // P16-026 — single-write tools use `field`; bulk-write entries also
        // use `Field`. Reflection over BulkWriteEntry confirms the rename.
        // -----------------------------------------------------------------

        [Fact]
        public void BulkWriteEntry_HasFieldProperty_NotFieldName_P16_026()
        {
            var t = typeof(FieldWriteBulkTools.BulkWriteEntry);
            Assert.NotNull(t.GetProperty("Field"));
            Assert.Null(t.GetProperty("FieldName"));
        }

        [Fact]
        public void BulkWriteEntry_HasSubClientIdProperty_NotSubClientID_P16_080()
        {
            var t = typeof(FieldWriteBulkTools.BulkWriteEntry);
            Assert.NotNull(t.GetProperty("SubClientId"));
            Assert.Null(t.GetProperty("SubClientID"));
        }

        // -----------------------------------------------------------------
        // P16-024 — read tools expose `rows` as the canonical collection
        // key. Domain-named aliases (clients/forms/statuses/...) are
        // allowed for v0.1 back-compat but `rows` is the canonical name
        // any new consumer should target.
        // -----------------------------------------------------------------

        [Fact]
        public async Task SubclientsRead_ResultEnvelope_HasRowsKey_P16_024()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathPData2024, 2024, 'P');
            var tool = new SubclientsTools(mgr);
            var json = await tool.LacerteSubclientRead("1001");
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("rows", out _),
                "Subclient read envelope must expose canonical `rows` key.");
        }

        [Fact]
        public async Task FormsRead_ResultEnvelope_HasRowsKey_P16_024()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tool = new FormsTools(mgr);
            var json = await tool.LacerteFormsRead("1001");
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("rows", out _),
                "Forms read envelope must expose canonical `rows` key.");
        }

        [Fact]
        public async Task StateRead_ResultEnvelope_HasRowsKey_P16_024()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathCData2024, 2024, 'C');
            var tool = new StatesTools(mgr);
            var json = await tool.LacerteStateRead("1001");
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("rows", out _),
                "State read envelope must expose canonical `rows` key.");
        }

        [Fact]
        public async Task StatusRead_ResultEnvelope_HasRowsKey_P16_024()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tool = new StatusTools(mgr);
            var json = await tool.LacerteStatusRead("1001");
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("rows", out _),
                "Status read envelope must expose canonical `rows` key.");
        }

        [Fact]
        public async Task ListModules_ResultEnvelope_HasRowsKey_P16_024()
        {
            var tool = new SessionTools(ToolTestHelpers.NewSimManager());
            var json = await tool.LacerteListModules();
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("rows", out _),
                "ListModules envelope must expose canonical `rows` key.");
        }

        [Fact]
        public async Task ReturnCodes_ResultEnvelope_HasRowsKey_P16_024()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteReturnCodes();
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.True(root.GetProperty("result").TryGetProperty("rows", out _),
                "ReturnCodes envelope must expose canonical `rows` key.");
        }

        // -----------------------------------------------------------------
        // P16-053 — wire-envelope error carries `origin` (sdk / synthetic),
        // matching the catalog's `originGroup` column for direct filtering.
        // -----------------------------------------------------------------

        [Fact]
        public async Task ErrorEnvelope_CarriesOriginField_Sdk_P16_053()
        {
            // ExposedForRead = 8 is an SDK-origin code.
            var json = await LacertMCP.Server.Errors.WrapToolHandler.Run(() =>
                throw new LacerteSDKError(StatusCodes.ExposedForRead));
            var root = ToolTestHelpers.Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal("sdk", root.GetProperty("origin").GetString());
        }

        [Fact]
        public async Task ErrorEnvelope_CarriesOriginField_Synthetic_P16_053()
        {
            var json = await LacertMCP.Server.Errors.WrapToolHandler.Run(() =>
                throw new LacerteSDKError(SyntheticStatusCodes.ContextNotSet));
            var root = ToolTestHelpers.Parse(json);
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal("synthetic", root.GetProperty("origin").GetString());
        }

        [Fact]
        public async Task ReturnCodesCatalog_EveryRowHasOriginGroup_P16_053()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteReturnCodes();
            var root = ToolTestHelpers.Parse(json);
            var rows = root.GetProperty("result").GetProperty("rows");
            foreach (var r in rows.EnumerateArray())
            {
                Assert.True(r.TryGetProperty("originGroup", out var og),
                    "Every return-codes row must expose `originGroup`.");
                var v = og.GetString();
                Assert.True(v == "sdk" || v == "synthetic",
                    $"originGroup must be `sdk` or `synthetic`, got `{v}`.");
            }
        }

        // -----------------------------------------------------------------
        // P16-070 — SchemaReload envelope must not carry an inner `success`
        // field (WrapToolHandler's outer envelope already supplies it).
        // -----------------------------------------------------------------

        [Fact]
        public async Task ReloadSchema_ResultEnvelope_NoInnerSuccessField_P16_070()
        {
            var tool = new SchemaReloadTools();
            var json = await tool.LacerteReloadSchema();
            var root = ToolTestHelpers.Parse(json);
            Assert.True(root.GetProperty("success").GetBoolean(), json);
            Assert.False(root.GetProperty("result").TryGetProperty("success", out _),
                "Inner `success` field must be absent — outer envelope owns that key (P16-070).");
        }

        // -----------------------------------------------------------------
        // P16-071 — LacerteClientRead never returns null; it returns
        // { clientNumber, found, client } where `client` is null when the
        // ClientNumber doesn't exist in the active context.
        // -----------------------------------------------------------------

        [Fact]
        public async Task ClientRead_NotFound_ReturnsEnvelopeNotNull_P16_071()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tool = new ClientsTools(mgr);
            var json = await tool.LacerteClientRead("9999999"); // not seeded
            var root = ToolTestHelpers.Parse(json);

            Assert.True(root.GetProperty("success").GetBoolean(), json);
            var result = root.GetProperty("result");
            Assert.Equal(JsonValueKind.Object, result.ValueKind);
            Assert.False(result.GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("client").ValueKind);
        }

        // -----------------------------------------------------------------
        // P16-069 / P16-079 — response envelopes emit `taxYear`, not `year`.
        // Pin the response field name on the three call sites that previously
        // used `year`.
        // -----------------------------------------------------------------

        [Fact]
        public async Task SessionStatus_ContextEnvelope_HasTaxYearKey_P16_069()
        {
            var mgr = ToolTestHelpers.NewSimManager();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            var tool = new SessionTools(mgr);
            var json = await tool.LacerteSessionStatus();
            var root = ToolTestHelpers.Parse(json);
            var ctx = root.GetProperty("result").GetProperty("context");
            Assert.True(ctx.TryGetProperty("taxYear", out var ty),
                "SessionStatus context envelope must emit `taxYear`.");
            Assert.Equal(2024, ty.GetInt32());
            Assert.False(ctx.TryGetProperty("year", out _),
                "Legacy `year` key must be gone from SessionStatus context.");
        }

        [Fact]
        public async Task FieldAccessMap_ResponseEnvelope_HasTaxYearKey_P16_069()
        {
            var tool = new MetadataTools();
            var json = await tool.LacerteFieldAccessMap("I", 1, 2024);
            var root = ToolTestHelpers.Parse(json);
            var result = root.GetProperty("result");
            Assert.Equal(2024, result.GetProperty("taxYear").GetInt32());
            Assert.False(result.TryGetProperty("year", out _),
                "Legacy `year` key must be gone from FieldAccessMap response.");
        }
    }
}
