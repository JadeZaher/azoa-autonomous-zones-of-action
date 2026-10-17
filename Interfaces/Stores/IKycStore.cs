// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the KYC aggregate (<see cref="KycSubmission"/> +
/// <see cref="KycDocument"/>). Hand-authored SurrealDB store; no AutoMapper, no
/// EF. Owner-scoped queries (by <c>user_id</c>) are the IDOR-safe primitives
/// the manager builds on.
/// </summary>
public interface IKycStore
{
    /// <summary>Loads a single submission by id, or <c>Result == null</c> when none exists.</summary>
    Task<AZOAResult<KycSubmission>> GetSubmissionByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads the most-recent submission (by <c>submitted_at</c>) owned by the
    /// user, or <c>Result == null</c> with no error when the user has none.
    /// </summary>
    Task<AZOAResult<KycSubmission>> GetLatestSubmissionByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Loads latest provenance for exactly one user/tenant authority tuple.</summary>
    Task<AZOAResult<KycSubmission>> GetLatestSubmissionAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the user's single active (PENDING or IN_REVIEW) submission if one
    /// exists, else <c>Result == null</c>. The manager uses this to reject a
    /// second concurrent submission.
    /// </summary>
    Task<AZOAResult<KycSubmission>> GetActiveSubmissionByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Loads the active attempt for exactly one user/tenant authority tuple.</summary>
    Task<AZOAResult<KycSubmission>> GetActiveSubmissionAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>Admin review queue: every PENDING or IN_REVIEW submission.</summary>
    Task<AZOAResult<IEnumerable<KycSubmission>>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>Returns one bounded oldest-first operator review page.</summary>
    Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetPendingPageAsync(
        int offset,
        int limit,
        CancellationToken ct = default);

    /// <summary>Returns one bounded operator page for an explicit status set.</summary>
    Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetOperatorPageAsync(
        IReadOnlyCollection<KycStatus> statuses,
        int offset,
        int limit,
        CancellationToken ct = default);

    /// <summary>Returns a page by effective status, including expiry-derived transitions.</summary>
    Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetEffectiveOperatorPageAsync(
        string effectiveStatus,
        int offset,
        int limit,
        CancellationToken ct = default);

    Task<AZOAResult<long>> CountByStatusesAsync(
        IReadOnlyCollection<KycStatus> statuses,
        CancellationToken ct = default);

    Task<AZOAResult<long>> CountEffectiveStatusAsync(
        string effectiveStatus,
        CancellationToken ct = default);

    /// <summary>Inserts or updates a submission.</summary>
    Task<AZOAResult<KycSubmission>> UpsertSubmissionAsync(KycSubmission submission, CancellationToken ct = default);

    /// <summary>Retires the observed active attempt without overwriting a concurrent decision.</summary>
    Task<AZOAResult<KycSubmission>> TryRetireAttemptAsync(KycSubmission expected, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Attaches the redirect only while this exact hosted attempt remains active.</summary>
    Task<AZOAResult<KycSubmission>> TryAttachHostedRedirectAsync(KycSubmission expected, string redirectUrl, DateTimeOffset? expiresAt, CancellationToken ct = default);

    /// <summary>Completes the exact active hosted attempt if its correlation, provenance, version and expiry still match; contention returns no row.</summary>
    Task<AZOAResult<KycSubmission>> TryCompleteHostedAsync(
        KycSubmission expected,
        HostedSession lease,
        KycStatus status,
        DateTimeOffset reviewedAt,
        DateTimeOffset? expiresAt,
        string? rejectionReason,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically creates one active submission and its documents. The storage
    /// transaction rejects a second active submission for the user.
    /// </summary>
    Task<AZOAResult<KycSubmission>> CreateSubmissionAsync(
        KycSubmission submission,
        IReadOnlyList<KycDocument> documents,
        CancellationToken ct = default);

    Task<AZOAResult<KycSubmission>> AttachDocumentsIfAbsentAsync(
        KycSubmission submission,
        IReadOnlyList<KycDocument> documents,
        CancellationToken ct = default);

    /// <summary>
    /// Conditionally commits one manual review decision while the row is still
    /// MANUAL, active, and unexpired. A lost reviewer race returns no result.
    /// </summary>
    Task<AZOAResult<KycSubmission>> TryReviewAsync(
        KycSubmission decision,
        CancellationToken ct = default);

    /// <summary>Lists the documents attached to a submission.</summary>
    Task<AZOAResult<IEnumerable<KycDocument>>> GetDocumentsBySubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Inserts a batch of documents for a submission.</summary>
    Task<AZOAResult<bool>> AddDocumentsAsync(IEnumerable<KycDocument> documents, CancellationToken ct = default);
}
