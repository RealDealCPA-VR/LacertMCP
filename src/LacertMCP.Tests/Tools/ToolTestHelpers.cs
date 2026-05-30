using System.Collections.Generic;
using System.Text.Json;
using LacertMCP.Server.Session;

namespace LacertMCP.Tests.Tools
{
    /// <summary>
    /// Shared helpers for Phase 5 tool tests. Tools return a JSON-serialized
    /// envelope produced by <c>WrapToolHandler</c>; tests parse it and assert
    /// on <c>success</c>, <c>statusCode</c>, and the embedded <c>result</c>.
    /// </summary>
    internal static class ToolTestHelpers
    {
        /// <summary>Fresh sim-mode session manager — explicit empty env defeats any host shell flags.</summary>
        public static LacerteSessionManager NewSimManager()
            => new LacerteSessionManager(new Dictionary<string, string?>());

        /// <summary>Parse a tool envelope and return the root JsonElement.</summary>
        public static JsonElement Parse(string json)
        {
            // JsonDocument.Parse returns a disposable doc; we Clone() so the
            // caller can use the element after this helper's doc is GC'd.
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}
