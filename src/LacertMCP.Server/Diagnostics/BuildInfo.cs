using System;
using System.Reflection;

namespace LacertMCP.Server.Diagnostics
{
    /// <summary>
    /// P14-040 — Reflective accessor for the assembly's version stamps.
    ///
    /// <para>
    /// Reads <see cref="AssemblyInformationalVersionAttribute"/> off the assembly
    /// hosting this type, falling back to <see cref="AssemblyName.Version"/> if
    /// the informational attribute is missing. The version comes from
    /// <c>Directory.Build.props</c> at the repo root (single source of truth) and
    /// is bumped by hand on every release tag.
    /// </para>
    ///
    /// <para>
    /// Surfaces:
    /// <list type="bullet">
    ///   <item><description><c>lacerte-mcp status</c> JSON payload (<c>buildVersion</c>).</description></item>
    ///   <item><description><see cref="Session.SessionStatusSnapshot.BuildVersion"/> for <c>lacerte_session_status</c>.</description></item>
    ///   <item><description><c>lacerte-mcp doctor</c> Build-version probe (always Pass).</description></item>
    /// </list>
    /// Lets the operator copy a build stamp into bug reports so the dev box (sim)
    /// and Windows operator box (live) can be told apart.
    /// </para>
    /// </summary>
    public static class BuildInfo
    {
        private static readonly Lazy<string> _version = new Lazy<string>(ResolveVersion);

        /// <summary>
        /// The informational version of the LacertMCP.Server assembly, e.g.
        /// <c>"0.1.0"</c> or <c>"0.1.0+abc1234"</c> when a git SHA is stamped.
        /// </summary>
        public static string Version => _version.Value;

        private static string ResolveVersion()
        {
            try
            {
                var asm = typeof(BuildInfo).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                {
                    return info.InformationalVersion;
                }
                var name = asm.GetName();
                return name.Version?.ToString() ?? "0.0.0";
            }
            catch
            {
                // Reflection failure is non-fatal — return a sentinel so callers
                // can still surface *something* in the JSON payload.
                return "unknown";
            }
        }
    }
}
