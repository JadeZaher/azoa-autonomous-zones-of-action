// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>
/// Lost-callback and orphan recovery over <c>hosted_session</c>: outcome-poll scheduling, stalled
/// listings, orphan supersession and audit. See <c>Services/HostedSessions/AGENTS.md</c> §recovery.
/// </summary>
public interface IHostedSessionRecoveryStore
{
    /// <summary>Attached, non-terminal KYC/KYB sessions whose next poll is due (NONE counts as due).</summary>
    Task<IReadOnlyList<HostedSession>> ListPollDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>
    /// Claims one poll by moving <c>next_poll_at</c> from its observed value; a concurrent claimer
    /// (another replica) gets false and skips the row.
    /// </summary>
    Task<bool> TryClaimPollAsync(HostedSession observed, DateTimeOffset nextPollAt, CancellationToken ct = default);

    /// <summary>Attached sessions still non-terminal and untouched since <paramref name="before"/>, for the operator queue.</summary>
    Task<IReadOnlyList<HostedSession>> ListStalledAsync(DateTimeOffset before, int limit, CancellationToken ct = default);

    /// <summary>
    /// Abandons an unattached, possibly-sent admission so a fresh attempt may start. CAS on the
    /// observed version; the admission key is retired so the subject's next claim is new.
    /// Returns null when the row changed.
    /// </summary>
    Task<AZOAResult<HostedSession>> TrySupersedeOrphanAsync(HostedSession orphan, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Orphans superseded for this subject and flow since <paramref name="since"/> (the supersede budget).</summary>
    Task<int> CountSupersededSinceAsync(string flowKind, Guid subjectUserId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Appends one audit entry (e.g. a late provider session deleted) without changing state.</summary>
    Task<bool> AppendAuditAsync(Guid id, string actor, string action, string reason, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Outcome detail stamped on a superseded orphan admission.</summary>
public static class HostedSessionRecoveryOutcomes
{
    public const string SupersededOrphan = "SUPERSEDED_ORPHAN";
    public const string PollExpired = "POLL_EXPIRED";
}
