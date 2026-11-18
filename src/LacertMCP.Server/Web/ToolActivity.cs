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
        private const string ProposalToolPrefix = "lacerte_proposal_";
        internal const string ProposalCommitTool = "lacerte_proposal_commit";

        /// <summary>
        /// Tools that change a return. Proposal tools only prepare a proposal
        /// (create / add_* / update_line / remove_line / submit write nothing to
        /// Lacerte), so only lacerte_proposal_commit counts.
        /// </summary>
        public static bool IsWrite(string tool)
        {
            if (tool.StartsWith(ProposalToolPrefix, StringComparison.OrdinalIgnoreCase))
                return string.Equals(tool, ProposalCommitTool, StringComparison.OrdinalIgnoreCase);
            return tool.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                   || tool.IndexOf("create", StringComparison.OrdinalIgnoreCase) >= 0
                   || tool.IndexOf("update_return", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Record(ActivityLog activity, CallToolRequestParams? call, CallToolResult result)
        {
            var tool = call?.Name ?? "(unknown tool)";
            var device = SessionScope.Current?.DeviceName;
            bool ok = result.IsError != true;
            string? statusName = null;
            bool dryRun = false;
            bool? verified = null;
            string? commitSummary = null;
            var isCommit = string.Equals(tool, ProposalCommitTool, StringComparison.OrdinalIgnoreCase);
            var text =result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
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
                        else if (inner.TryGetProperty("perItem", out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            // Bulk field write: verified only when every row was
                            // written and read back (dry runs carry no verification).
                            var rows = items.EnumerateArray().ToList();
                            if (rows.Count > 0 && rows.All(i => i.ValueKind == JsonValueKind.Object
                                    && i.TryGetProperty("verified", out var iv) && iv.ValueKind == JsonValueKind.True))
                                verified = true;
                        }
                        if (inner.TryGetProperty("statusName", out var n2) && n2.ValueKind == JsonValueKind.String && statusName == null) statusName = n2.GetString();
                        if (isCommit && ok && inner.TryGetProperty("ranCount", out _))
                        {
                            // Proposal commit report: verified only when every line it ran
                            // was read back; CommitFailed (nothing written) is a failure.
                            int Count(string name) => inner.TryGetProperty(name, out var c) && c.TryGetInt32(out var i) ? i : 0;
                            var status = inner.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                            int ran = Count("ranCount"), succeeded = Count("succeededCount");
                            int written = succeeded + Count("succeededUnverifiedCount");
                            verified = status == "Committed" && ran > 0 && succeeded == ran;
                            if (status == "CommitFailed") { ok = false; statusName ??= "CommitFailed"; }
                            commitSummary = written + " of " + ran + " line(s) written (" + succeeded + " verified)"
                                + (Count("failedCount") > 0 ? ", " + Count("failedCount") + " failed" : "")
                                + (Count("uncertainCount") > 0 ? ", " + Count("uncertainCount") + " uncertain" : "")
                                + (status != null ? "; " + Plain(status) : "");
                        }
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
                var parts = new[] { "proposalId", "clientNumber", "field", "table" }
                    .Where(k => call.Arguments.ContainsKey(k))
                    .Select(k => k + " " + Plain(call.Arguments[k]));
                target = string.Join(", ", parts);
                if (call.Arguments.TryGetValue("entries", out var e) && e.ValueKind == JsonValueKind.Array)
                    target = (target.Length > 0 ? target + ", " : string.Empty) + e.GetArrayLength() + " entries";
            }
            if (write && commitSummary != null)
                target = (string.IsNullOrEmpty(target) ? string.Empty : target + ": ") + commitSummary;

            string message;
            if (!ok) message = tool + " failed" + (statusName != null ? ": " + statusName : string.Empty);
            else if (write) message = tool + (verified == true ? " (verified)" : verified == null && tool.Contains("field") ? " (not verifiable)" : string.Empty);
            else message = tool + (dryRun ? " (dry run)" : string.Empty);

            activity.Append(new ActivityEvent
            {
                At = DateTime.UtcNow,
                Level = ok ? (write ? "success" : "info") : "error",
                Category = write ? "write" : "tool",
                Message = message,
                Device = device,
                Detail = target,
                Verified = ok && write && verified == true,
            });
        }

        private const int MaxTargetChars = 64;

        /// <summary>
        /// An argument as it may appear in the activity file: identifier-safe
        /// characters only (letters, digits, _ - .), at most 64 of them, and an
        /// SSN-shaped value (some firms number clients by SSN) masked to its last
        /// four digits. Applies to refused writes too: whatever the agent sent
        /// lands on disk in plain text otherwise.
        /// </summary>
        internal static string Plain(JsonElement v)
            => Plain(v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString());

        /// <inheritdoc cref="Plain(JsonElement)"/>
        internal static string Plain(string? raw)
        {
            raw ??= string.Empty;
            var safe = new string(raw.Where(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '_' || c == '-' || c == '.').ToArray());
            var digits = new string(safe.Where(char.IsDigit).ToArray());
            if (digits.Length == 9 && safe.All(c => char.IsDigit(c) || c == '-'))
                return "*****" + digits.Substring(5);
            return safe.Length > MaxTargetChars ? safe.Substring(0, MaxTargetChars) + "..." : safe;
        }
    }
}
