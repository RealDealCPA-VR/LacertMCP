using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// What a bulk detail-write preview token is bound to: the backend mode,
    /// the active context triple (dataPath, taxYear, module), a SHA-256 hash of
    /// the canonical entries (exactly as they will be written) and
    /// continueOnError. A commit is accepted only when every part matches the
    /// preview's. Kept as parts (not one opaque hash) so a refusal can say WHAT
    /// changed; the entries themselves are only ever stored as a hash.
    /// </summary>
    public sealed record BulkPreviewBinding(
        string Mode,
        string DataPath,
        int Year,
        char Module,
        string EntriesHash,
        int EntryCount,
        bool ContinueOnError);

    /// <summary>Why a preview token was refused (None = accepted).</summary>
    public enum BulkPreviewTokenProblem
    {
        None,
        Missing,
        Unknown,
        Expired,
        Mismatched,
        WrongConnection,
    }

    /// <summary>Outcome of <see cref="BulkPreviewTokenStore.Check"/>.</summary>
    public sealed class BulkPreviewTokenCheck
    {
        public BulkPreviewTokenProblem Problem { get; internal set; }

        /// <summary>The token's expiry (null when the token is missing or unknown).</summary>
        public DateTimeOffset? ExpiresUtc { get; internal set; }

        /// <summary>For <see cref="BulkPreviewTokenProblem.Mismatched"/>: which parts differ (mode, dataPath, taxYear, module, entries, continueOnError).</summary>
        public IReadOnlyList<string> Mismatches { get; internal set; } = Array.Empty<string>();

        public bool Accepted => Problem == BulkPreviewTokenProblem.None;
    }

    /// <summary>
    /// In-process store of preview tokens for <c>lacerte_detail_write_bulk</c>
    /// (filing-preparation decision 2: bulk detail writes require a dry run
    /// first). A dry run whose rows all passed issues a 128-bit random token;
    /// the real call must present it, and it must match the binding.
    ///
    /// <para>
    /// Tokens stay valid for <see cref="Lifetime"/> (30 minutes) and are NOT
    /// single-use, so a half-failed commit can be resubmitted with the same
    /// idempotencyKey without a new preview. Expired tokens are kept for one
    /// extra lifetime so a late commit is told "expired" rather than
    /// "unknown", then pruned opportunistically on every call. The store is
    /// capped at <see cref="Capacity"/>; when full, the oldest token goes.
    /// </para>
    ///
    /// <para>
    /// HTTP transport: a token is usable only by the connection
    /// (<see cref="SessionScope"/>) that created it — the scope object is the
    /// same for every request on one MCP connection, so a reference comparison
    /// is enough. Under stdio there is no scope (null) and the token is bound
    /// to the binding alone. The owner is held weakly so a closed connection's
    /// scope is not kept alive by its tokens.
    /// </para>
    /// </summary>
    public sealed class BulkPreviewTokenStore
    {
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);
        public const int DefaultCapacity = 1000;

        private sealed class Entry
        {
            public Entry(BulkPreviewBinding binding, SessionScope? owner, DateTimeOffset issuedUtc, DateTimeOffset expiresUtc)
            {
                Binding = binding;
                Owner = owner == null ? null : new WeakReference<SessionScope>(owner);
                IssuedUtc = issuedUtc;
                ExpiresUtc = expiresUtc;
            }

            public BulkPreviewBinding Binding { get; }
            public WeakReference<SessionScope>? Owner { get; }
            public DateTimeOffset IssuedUtc { get; }
            public DateTimeOffset ExpiresUtc { get; }
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Test seam: the current time.</summary>
        internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        internal TimeSpan Lifetime { get; set; } = DefaultLifetime;

        internal int Capacity { get; set; } = DefaultCapacity;

        /// <summary>Number of tokens currently held (expired-but-not-yet-pruned included).</summary>
        internal int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>Issues a new token for <paramref name="binding"/>, owned by <paramref name="owner"/> (the caller's connection scope, null under stdio).</summary>
        public (string Token, DateTimeOffset ExpiresUtc) Issue(BulkPreviewBinding binding, SessionScope? owner)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var token = NewToken();
            lock (_lock)
            {
                var now = Clock();
                PruneLocked(now);
                while (_entries.Count >= Math.Max(1, Capacity))
                {
                    EvictOldestLocked();
                }
                var expires = now + Lifetime;
                _entries[token] = new Entry(binding, owner, now, expires);
                return (token, expires);
            }
        }

        /// <summary>
        /// Checks <paramref name="token"/> against <paramref name="binding"/>
        /// for the calling connection <paramref name="caller"/>. Does not
        /// consume the token.
        /// </summary>
        public BulkPreviewTokenCheck Check(string? token, BulkPreviewBinding binding, SessionScope? caller)
        {
            var result = new BulkPreviewTokenCheck();
            var trimmed = token?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                result.Problem = BulkPreviewTokenProblem.Missing;
                return result;
            }

            lock (_lock)
            {
                var now = Clock();
                PruneLocked(now);
                if (!_entries.TryGetValue(trimmed!, out var entry))
                {
                    result.Problem = BulkPreviewTokenProblem.Unknown;
                    return result;
                }

                result.ExpiresUtc = entry.ExpiresUtc;
                if (now >= entry.ExpiresUtc)
                {
                    result.Problem = BulkPreviewTokenProblem.Expired;
                    return result;
                }

                if (!SameOwner(entry.Owner, caller))
                {
                    result.Problem = BulkPreviewTokenProblem.WrongConnection;
                    return result;
                }

                var mismatches = Diff(entry.Binding, binding);
                if (mismatches.Count > 0)
                {
                    result.Problem = BulkPreviewTokenProblem.Mismatched;
                    result.Mismatches = mismatches;
                    return result;
                }

                result.Problem = BulkPreviewTokenProblem.None;
                return result;
            }
        }

        private static bool SameOwner(WeakReference<SessionScope>? owner, SessionScope? caller)
        {
            if (owner == null) return caller == null;
            if (caller == null) return false;
            return owner.TryGetTarget(out var target) && ReferenceEquals(target, caller);
        }

        private static List<string> Diff(BulkPreviewBinding issued, BulkPreviewBinding presented)
        {
            var diffs = new List<string>();
            if (!string.Equals(issued.Mode, presented.Mode, StringComparison.Ordinal)) diffs.Add("mode");
            if (!string.Equals(issued.DataPath, presented.DataPath, StringComparison.Ordinal)) diffs.Add("dataPath");
            if (issued.Year != presented.Year) diffs.Add("taxYear");
            if (issued.Module != presented.Module) diffs.Add("module");
            if (issued.EntryCount != presented.EntryCount
                || !string.Equals(issued.EntriesHash, presented.EntriesHash, StringComparison.Ordinal))
            {
                diffs.Add("entries");
            }
            if (issued.ContinueOnError != presented.ContinueOnError) diffs.Add("continueOnError");
            return diffs;
        }

        // Expired tokens linger for one extra lifetime (so a late commit hears
        // "expired"), then go.
        private void PruneLocked(DateTimeOffset now)
        {
            if (_entries.Count == 0) return;
            var cutoff = now - Lifetime;
            List<string>? dead = null;
            foreach (var kv in _entries)
            {
                if (kv.Value.ExpiresUtc <= cutoff)
                {
                    (dead ??= new List<string>()).Add(kv.Key);
                }
            }
            if (dead == null) return;
            foreach (var k in dead) _entries.Remove(k);
        }

        private void EvictOldestLocked()
        {
            string? oldestKey = null;
            DateTimeOffset oldest = DateTimeOffset.MaxValue;
            foreach (var kv in _entries)
            {
                if (kv.Value.IssuedUtc < oldest)
                {
                    oldest = kv.Value.IssuedUtc;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey != null) _entries.Remove(oldestKey);
        }

        /// <summary>128 random bits, base64url without padding (22 characters).</summary>
        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    public sealed partial class LacerteSessionManager
    {
        /// <summary>
        /// Preview tokens for <c>lacerte_detail_write_bulk</c> (a commit must
        /// present the token its dry run issued). Per process, shared by every
        /// connection; tokens are bound to the issuing connection's scope.
        /// </summary>
        internal BulkPreviewTokenStore DetailBulkPreviews { get; } = new BulkPreviewTokenStore();

        /// <summary>
        /// R7-SRC — the detail-file vocabulary normalization every detail write
        /// applies before validation, fingerprinting and dispatch (both modes):
        /// TSJ T/S/J → Taxpayer/Spouse/Joint; Source case → Source/Nonsource.
        /// Invalid values pass through unchanged so the validator rejects them.
        /// Shared with the bulk tool's preview fingerprint so the token binds
        /// the rows exactly as they will be written.
        /// </summary>
        internal static DetailRow NormalizeDetailVocabulary(DetailRow row) => row with
        {
            TSJ = global::LacertMCP.Server.Bridge.BridgeInputValidation.NormalizeTsj(row.TSJ) ?? row.TSJ,
            Source = global::LacertMCP.Server.Bridge.BridgeInputValidation.NormalizeDetailSource(row.Source) ?? row.Source,
        };
    }
}
