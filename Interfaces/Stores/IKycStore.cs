// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the KYC aggregate (<see cref="KycSubmission"/> +
/// <see cref="KycDocument"/>). Hand-authored SurrealDB store; no AutoMapper, no
/// EF. Owner-scoped queries (by <c>avatar_id</c>) are the IDOR-safe primitives
/// the manager builds on.
/// </summary>
public interface IKycStore
{
    /// <summary>Loads a single submission by id, or <c>Result == null</c> when none exists.</summary>
    Task<AZOAResult<KycSubmission>> GetSubmissionByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads the most-recent submission (by <c>submitted_at</c>) owned by the
    /// avatar, or <c>Result == null</c> with no error when the avatar has none.
    /// </summary>
    Task<AZOAResult<KycSubmission>> GetLatestSubmissionByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>
    /// Loads the avatar's single active (PENDING or IN_REVIEW) submission if one
    /// exists, else <c>Result == null</c>. The manager uses this to reject a
    /// second concurrent submission.
    /// </summary>
    Task<AZOAResult<KycSubmission>> GetActiveSubmissionByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>Admin review queue: every PENDING or IN_REVIEW submission.</summary>
    Task<AZOAResult<IEnumerable<KycSubmission>>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates a submission.</summary>
    Task<AZOAResult<KycSubmission>> UpsertSubmissionAsync(KycSubmission submission, CancellationToken ct = default);

    /// <summary>Lists the documents attached to a submission.</summary>
    Task<AZOAResult<IEnumerable<KycDocument>>> GetDocumentsBySubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Inserts a batch of documents for a submission.</summary>
    Task<AZOAResult<bool>> AddDocumentsAsync(IEnumerable<KycDocument> documents, CancellationToken ct = default);
}
