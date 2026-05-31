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
        /// <summary>
        /// Fresh sim-mode session manager — explicit empty env defeats any host shell flags.
        /// <para>
        /// P15-050 — auto-Register so the manager-side RequireRegistered gate
        /// (which now fires on every read/write chokepoint to match the live
        /// SDK's NotSignedIn rejection) doesn't surprise legacy tool tests
        /// that exercise read/write semantics rather than the registration
        /// gate itself. The pre-context guard tests (no SetContext) still
        /// see the empty manager because they assert before any chokepoint.
        /// </para>
        /// </summary>
        public static LacerteSessionManager NewSimManager()
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            mgr.Register().GetAwaiter().GetResult();
            return mgr;
        }

        /// <summary>
        /// P15-050 — variant for the small number of legacy tests that
        /// explicitly want a never-Registered manager to assert pre-context /
        /// NotSignedIn behavior. New tests that need to assert
        /// RequireRegistered should call this directly.
        /// </summary>
        public static LacerteSessionManager NewUnregisteredSimManager()
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
