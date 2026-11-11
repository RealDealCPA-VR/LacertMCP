using System;
using System.Linq;
using System.Text.Json;
using LacertMCP.Server.Session;
using ModelContextProtocol.Protocol;

namespace LacertMCP.Server.Web
{
    /// <summary>
    /// Turns one tool call into an activity event. Records the tool name, the
    /// device, the outcome and, for writes, only the client number and field —
    /// never the values written.
    /// </summary>
    internal static class ToolActivity
    {
        public static bool IsWrite(string tool)
            => tool.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
               || tool.IndexOf("create", StringComparison.OrdinalIgnoreCase) >= 0
               || tool.IndexOf("update_return", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void Record(ActivityLog activity, CallToolRequestParams? call, CallToolResult result)
        {
            var tool = call?.Name ?? "(unknown tool)";
            var device = SessionScope.Current?.DeviceName;
            bool ok = result.IsError != true;
            string? statusName = null;
            bool dryRun = false;
            bool? verified = null;
            var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text!);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False) ok = false;
                        if (root.TryGetProperty("statusName", out var n) && n.ValueKind == JsonValueKind.String) statusName = n.GetString();
                        var inner = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;
                        if (inner.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True) dryRun = true;
                        if (inner.TryGetProperty("verified", out var v))
                            verified = v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : (bool?)null;
                        if (inner.TryGetProperty("statusName", out var n2) && n2.ValueKind == JsonValueKind.String && statusName == null) statusName = n2.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Not JSON (e.g. a prompt); outcome stays as IsError says.
                }
            }

            var write = IsWrite(tool) && !dryRun;
            string? target = null;
            if (write && call?.Arguments != null)
            {
                var parts = new[] { "clientNumber", "field", "table" }
                    .Where(k => call.Arguments.ContainsKey(k))
                    .Select(k => k + " " + Plain(call.Arguments[k]));
                target = string.Join(", ", parts);
                if (call.Arguments.TryGetValue("entries", out var e) && e.ValueKind == JsonValueKind.Array)
                    target = (target.Length > 0 ? target + ", " : string.Empty) + e.GetArrayLength() + " entries";
            }

            string message;
            if (!ok) message = tool + " failed" + (statusName != null ? ": " + statusName : string.Empty);
            else if (write) message = tool + (verified == true ? " (verified)" : verified == null && tool.Contains("field") ? " (not verifiable)" : string.Empty);
            else message = tool + (dryRun ? " (dry run)" : string.Empty);

            activity.Record(ok ? (write ? "success" : "info") : "error", write ? "write" : "tool", message, device, target);
        }

        private static string Plain(JsonElement v)
            => v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
    }
}
