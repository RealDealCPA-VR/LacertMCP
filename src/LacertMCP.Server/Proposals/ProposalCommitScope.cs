using System;
using System.Threading;

namespace LacertMCP.Server.Proposals
{
    /// <summary>
    /// Marks the async flow of an approved proposal's commit. With
    /// <c>LACERTE_REQUIRE_APPROVAL=1</c> the session's write chokepoints refuse
    /// every real (non-dry-run) field / detail write unless it runs inside this
    /// scope (<see cref="Session.LacerteSessionManager.ThrowIfApprovalRequired"/>).
    /// Internal: only <see cref="ProposalService.CommitAsync"/> enters it, after
    /// checking the proposal is approved and unchanged.
    /// </summary>
    internal static class ProposalCommitScope
    {
        private static readonly AsyncLocal<string?> s_proposalId = new AsyncLocal<string?>();

        /// <summary>True inside a proposal commit.</summary>
        public static bool IsActive => s_proposalId.Value != null;

        /// <summary>The proposal being committed, or null.</summary>
        public static string? ProposalId => s_proposalId.Value;

        public static IDisposable Enter(string proposalId)
        {
            var prior = s_proposalId.Value;
            s_proposalId.Value = proposalId;
            return new Exit(prior);
        }

        private sealed class Exit : IDisposable
        {
            private readonly string? _prior;
            private bool _done;
            public Exit(string? prior) { _prior = prior; }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                s_proposalId.Value = _prior;
            }
        }
    }
}
