using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LacertMCP.Server.Errors;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// Defensive path-string validation used by tool-layer chokepoints whose
    /// inputs flow to the filesystem (ODBC DBF reader, COM bridge write path,
    /// debug log directory). Threat model per P14-049: an LLM-callable tool
    /// (<c>lacerte_set_context</c>, env-controlled <c>LACERTE_DEBUG_LOG_PATH</c>)
    /// must not be coercible into reading or writing arbitrary filesystem
    /// locations — UNC shares, parent-traversal, control-char-laced strings,
    /// or paths outside the operator's expected Lacerte install root.
    ///
    /// <para>
    /// Rules applied by <see cref="ValidateDataPath"/> (in order — first hit
    /// wins and produces a single rejection reason):
    /// <list type="number">
    ///   <item><description>Reject null / whitespace-only input.</description></item>
    ///   <item><description>Reject any control character (<c>c &lt; 0x20</c> or <c>0x7F</c>).</description></item>
    ///   <item><description>Reject literal <c>".."</c> segments (separator-bounded — a path like <c>"C:\\..foo"</c> is fine, <c>"C:\\..\\foo"</c> is not).</description></item>
    ///   <item><description>Reject UNC prefixes (<c>"\\\\"</c> or <c>"//"</c>) unless <c>LACERTE_ALLOW_UNC=1</c>.</description></item>
    ///   <item><description>Require <see cref="Path.IsPathRooted"/> + canonicalize via <see cref="Path.GetFullPath"/>.</description></item>
    ///   <item><description>If <c>LACERTE_DATA_ROOT</c> is set, require the canonical path to live under it (case-insensitive, trailing-separator-normalized).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Rejection throws <see cref="LacerteSDKError"/> with
    /// <see cref="SyntheticStatusCodes.InvalidDataPath"/> (9013) and carries
    /// <c>{ dataPath, reason }</c> so the tool layer renders the precise
    /// failure to the operator.
    /// </para>
    /// </summary>
    public static class PathValidation
    {
        /// <summary>
        /// Validate a dataPath string. Returns the canonicalized full path on
        /// success; throws <see cref="LacerteSDKError"/> (9013 InvalidDataPath)
        /// on any rule violation. The caller (e.g.
        /// <see cref="LacerteSessionManager.SetContext"/>) typically discards
        /// the canonical return and keeps the original string — canonicalization
        /// is used to apply the LACERTE_DATA_ROOT allowlist check only.
        /// </summary>
        /// <param name="dataPath">The operator-supplied path.</param>
        /// <param name="env">
        /// Environment lookup. Pass null to read process env directly; pass an
        /// explicit dictionary in tests to drive LACERTE_ALLOW_UNC /
        /// LACERTE_DATA_ROOT without process-wide mutation.
        /// </param>
        public static string ValidateDataPath(string? dataPath, IDictionary<string, string?>? env = null)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                throw Reject(dataPath, "dataPath is required.");
            }

            // Control characters — null bytes, CR/LF, BEL, etc. have no place
            // in a filesystem path and are the highest-risk parser-confusion
            // characters across both the ODBC connection-string layer and the
            // COM marshaller.
            for (int i = 0; i < dataPath!.Length; i++)
            {
                var c = dataPath[i];
                if (c < 0x20 || c == 0x7F)
                {
                    throw Reject(dataPath, "dataPath contains control characters (null/CR/LF/TAB/etc).");
                }
            }

            // '..' traversal — separator-bounded. This catches "C:\foo\..\bar"
            // but not "C:\foo..bar" or "C:\..foo". Doing this BEFORE GetFullPath
            // is intentional: GetFullPath would collapse the '..' silently and
            // we'd lose the intent.
            var segments = dataPath.Split(new[] { '\\', '/' }, StringSplitOptions.None);
            foreach (var seg in segments)
            {
                if (seg == "..")
                {
                    throw Reject(dataPath, "dataPath contains '..' traversal segment.");
                }
            }

            // UNC gate. Windows UNC is "\\server\share"; POSIX-style "//host"
            // is rarely meaningful here but we treat it the same defensively.
            bool isUnc = dataPath.StartsWith(@"\\", StringComparison.Ordinal)
                      || dataPath.StartsWith("//", StringComparison.Ordinal);
            if (isUnc)
            {
                var allowUnc = ReadEnv(env, "LACERTE_ALLOW_UNC") == "1";
                if (!allowUnc)
                {
                    throw Reject(dataPath, "UNC path rejected. Set LACERTE_ALLOW_UNC=1 to permit \\\\server\\share paths.");
                }
            }

            // Must be rooted. "C:\foo" is rooted; "foo\bar" is not. On Mono /
            // off-Windows the "C:" prefix isn't recognized as rooted, so to
            // keep the validation cross-platform-testable we also accept the
            // sim-canonical "C:\..." prefix explicitly.
            bool looksAbsolute = Path.IsPathRooted(dataPath)
                || (dataPath.Length >= 3
                    && char.IsLetter(dataPath[0])
                    && dataPath[1] == ':'
                    && (dataPath[2] == '\\' || dataPath[2] == '/'));
            if (!looksAbsolute)
            {
                throw Reject(dataPath, "dataPath must be absolute (e.g. C:\\Lacerte\\24Tax\\IData).");
            }

            // Canonicalize. GetFullPath throws on malformed input (illegal
            // chars per Path.GetInvalidPathChars, mixed-quoted, etc) — we
            // surface as a structured rejection rather than letting the raw
            // ArgumentException bubble.
            string canonical;
            try
            {
                canonical = Path.GetFullPath(dataPath);
            }
            catch (Exception ex)
            {
                throw Reject(dataPath, "dataPath is malformed: " + ex.Message);
            }

            // LACERTE_DATA_ROOT allowlist. When set, the canonicalized path
            // must live UNDER that root (StartsWith with trailing-sep
            // normalization to avoid "C:\Lacerte2" matching root "C:\Lacerte").
            var dataRoot = ReadEnv(env, "LACERTE_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                string rootCanonical;
                try
                {
                    rootCanonical = Path.GetFullPath(dataRoot!);
                }
                catch
                {
                    // A malformed LACERTE_DATA_ROOT can't be enforced; treat
                    // the env var as absent rather than failing every call.
                    rootCanonical = null!;
                }

                if (rootCanonical != null)
                {
                    var rootWithSep = EnsureTrailingSeparator(rootCanonical);
                    var pathWithSep = EnsureTrailingSeparator(canonical);
                    if (!pathWithSep.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                    {
                        throw Reject(dataPath, "dataPath is outside LACERTE_DATA_ROOT (" + rootCanonical + ").");
                    }

                    // P17-016 — same reparse-point bypass applies to
                    // LACERTE_DATA_ROOT: an attacker who can create a junction
                    // inside the operator's data root could redirect ODBC and
                    // bridge reads/writes to a sensitive directory. Lower
                    // exposure than the log-dir vector (LACERTE_DATA_ROOT is
                    // operator-set and dataPath comes through lacerte_set_context
                    // which the operator drives) but the structural gap is
                    // identical; close it for symmetry.
                    if (ContainsReparsePointSegment(canonical, rootWithSep))
                    {
                        throw Reject(dataPath, "dataPath traverses an NTFS reparse point (junction/symlink) — rejected to prevent allowlist bypass.");
                    }
                }
            }

            return canonical;
        }

        /// <summary>
        /// Validate a debug-log directory path. Same rules as
        /// <see cref="ValidateDataPath"/> EXCEPT the allowlist root is a
        /// hard-coded set ({USERPROFILE, TEMP, LACERTE_DATA_ROOT}) rather than
        /// configurable per call. Returns the canonical path on success or
        /// <c>null</c> when the input fails any rule — callers are expected to
        /// fall back to a default rather than throw, because WireLog policy
        /// (see <c>Logging/WireLog.cs</c>) is "debug logging must never break
        /// the operator path."
        ///
        /// <para>
        /// P17-010 — fail-CLOSED when zero allowlist roots are discovered
        /// (typical on POSIX dev boxes where USERPROFILE/TEMP/TMP are not
        /// conventional). The prior behavior of permitting any canonical path
        /// in that case silently disabled the gate off-Windows, breaking the
        /// 'sim and live observationally identical' boundary invariant.
        /// </para>
        ///
        /// <para>
        /// P17-016 — also walk the path segments from the allowlist root down
        /// to the leaf and reject if any extant directory in the chain is an
        /// NTFS reparse point (junction / symlink). <see cref="Path.GetFullPath"/>
        /// only collapses '..' and normalizes separators — it does NOT follow
        /// reparse points, so an attacker with write access to %TEMP% (no admin
        /// needed for `mklink /J`) could create a junction inside the
        /// allowlist root that redirects the wire log into a sensitive
        /// directory carrying disk-scope PII. Non-existent segments are
        /// treated as clean (junctions can only exist on extant directories).
        /// </para>
        /// </summary>
        public static string? ValidateLogDir(string? logDir, IDictionary<string, string?>? env = null)
        {
            if (string.IsNullOrWhiteSpace(logDir)) return null;

            // Same control-char gate.
            for (int i = 0; i < logDir!.Length; i++)
            {
                var c = logDir[i];
                if (c < 0x20 || c == 0x7F) return null;
            }

            // Same '..' segment gate.
            foreach (var seg in logDir.Split(new[] { '\\', '/' }, StringSplitOptions.None))
            {
                if (seg == "..") return null;
            }

            // UNC gate — identical escape hatch as data paths.
            bool isUnc = logDir.StartsWith(@"\\", StringComparison.Ordinal)
                      || logDir.StartsWith("//", StringComparison.Ordinal);
            if (isUnc && ReadEnv(env, "LACERTE_ALLOW_UNC") != "1") return null;

            // Must be rooted (same accept-rule as ValidateDataPath).
            bool looksAbsolute = Path.IsPathRooted(logDir)
                || (logDir.Length >= 3
                    && char.IsLetter(logDir[0])
                    && logDir[1] == ':'
                    && (logDir[2] == '\\' || logDir[2] == '/'));
            if (!looksAbsolute) return null;

            string canonical;
            try { canonical = Path.GetFullPath(logDir); }
            catch { return null; }

            // Allowlist: USERPROFILE, TEMP, LACERTE_DATA_ROOT — anything else
            // is silently rejected so a hostile LACERTE_DEBUG_LOG_PATH can't
            // create directories in arbitrary locations.
            var roots = new List<string>();
            void TryAddRoot(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                try { roots.Add(EnsureTrailingSeparator(Path.GetFullPath(raw!))); }
                catch { /* malformed env — skip */ }
            }
            TryAddRoot(ReadEnv(env, "USERPROFILE"));
            TryAddRoot(ReadEnv(env, "TEMP"));
            TryAddRoot(ReadEnv(env, "TMP"));
            TryAddRoot(ReadEnv(env, "LACERTE_DATA_ROOT"));

            if (roots.Count == 0)
            {
                // P17-010 — fail-CLOSED. No allowlist roots discovered means
                // we cannot enforce the gate, so we MUST reject rather than
                // permit. Forcing WireLog.ResolveConfig to fall back to the
                // CWD-relative ./logs default is always safe and matches
                // WireLog's never-throw-just-fall-back policy. Operators who
                // need a custom log path must populate USERPROFILE,
                // LACERTE_DATA_ROOT, or TEMP/TMP explicitly so the allowlist
                // has something to enforce against. (The prior behavior of
                // returning the canonical path silently disabled the gate on
                // POSIX dev boxes where the Windows-conventional env vars are
                // typically unset, breaking the sim/live boundary invariant
                // from CLAUDE.md.)
                return null;
            }

            var pathWithSep = EnsureTrailingSeparator(canonical);
            string? matchedRoot = null;
            foreach (var r in roots)
            {
                if (pathWithSep.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                {
                    matchedRoot = r;
                    break;
                }
            }
            if (matchedRoot == null) return null;

            // P17-016 — walk extant segments from the matched root down to the
            // leaf. Reject if any segment in the chain (including the leaf
            // itself, if it exists) is an NTFS reparse point — junctions and
            // symlinks both carry FileAttributes.ReparsePoint. Non-existent
            // tail segments are treated as clean: directories that don't yet
            // exist can't be reparse points, and lazy-create-on-first-write is
            // a legitimate pattern WireLog relies on.
            if (ContainsReparsePointSegment(canonical, matchedRoot))
            {
                return null;
            }

            return canonical;
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var c = path[path.Length - 1];
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) return path;
            return path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// P17-016 — walk extant directory segments from <paramref name="rootWithSep"/>
        /// down to <paramref name="canonical"/> and return <c>true</c> if any
        /// existing directory in the chain carries
        /// <see cref="FileAttributes.ReparsePoint"/> (NTFS junctions and
        /// symbolic links both set this attribute). Non-existent tail
        /// segments are treated as clean — directories that don't yet exist
        /// can't be reparse points and the lazy-create-on-first-write pattern
        /// must keep working. On any unexpected IO failure during the walk
        /// (permission denied, etc.) we conservatively treat as reparse
        /// (return <c>true</c>) since we can't prove the segment is safe.
        /// </summary>
        private static bool ContainsReparsePointSegment(string canonical, string rootWithSep)
        {
            try
            {
                // Strip the root prefix so we only walk segments below it.
                // canonical may or may not have a trailing separator — we
                // build pathWithSep against rootWithSep using OrdinalIgnoreCase
                // semantics consistent with the StartsWith allowlist check.
                var canonicalWithSep = EnsureTrailingSeparator(canonical);
                if (canonicalWithSep.Length < rootWithSep.Length) return false;

                // Walk from the root downward. We check the root itself plus
                // every descendant directory up to the leaf. A reparse-point
                // anywhere in the chain redirects further traversal, so any
                // hit poisons the whole path.
                var current = rootWithSep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (DirectoryIsReparsePoint(current)) return true;

                // Iterate the path components below the root.
                var remainder = canonicalWithSep.Substring(rootWithSep.Length);
                if (string.IsNullOrEmpty(remainder)) return false;

                var segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in segments)
                {
                    current = current + Path.DirectorySeparatorChar + seg;
                    if (!Directory.Exists(current))
                    {
                        // Tail not yet provisioned — safe to lazy-create later.
                        return false;
                    }
                    if (DirectoryIsReparsePoint(current)) return true;
                }
                return false;
            }
            catch
            {
                // Unexpected IO failure — fail-CLOSED.
                return true;
            }
        }

        private static bool DirectoryIsReparsePoint(string path)
        {
            try
            {
                var info = new DirectoryInfo(path);
                if (!info.Exists) return false;
                return (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                // Can't determine attributes (permission denied / race) →
                // fail-CLOSED so the caller treats it as a reparse hit.
                return true;
            }
        }

        private static string? ReadEnv(IDictionary<string, string?>? env, string name)
        {
            if (env != null)
            {
                return env.TryGetValue(name, out var v) ? v : null;
            }
            return Environment.GetEnvironmentVariable(name);
        }

        private static LacerteSDKError Reject(string? dataPath, string reason)
        {
            return new LacerteSDKError(SyntheticStatusCodes.InvalidDataPath,
                new Dictionary<string, object?>
                {
                    ["dataPath"] = dataPath,
                    ["reason"] = reason,
                });
        }
    }
}
