// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Discriminator the controller uses to translate a manager auth failure into
/// the right HTTP status without parsing a free-text <c>Message</c>. Carried via
/// the <see cref="AZOAResult{T}.Message"/> prefix — the same pattern STARODK
/// uses (<see cref="STARODKAuthorizationError"/>).
/// </summary>
public static class KycAuthorizationError
{
    /// <summary>Submission exists but is owned by a different avatar — surfaces as 403.</summary>
    public const string Forbidden = "KYC_FORBIDDEN: ";

    /// <summary>Id from the route did not match any submission — surfaces as 404.</summary>
    public const string NotFound = "KYC_NOT_FOUND: ";

    /// <summary>
    /// The generic, brand-free message returned by the gate / manager when an
    /// avatar is not verified. Carried after the <see cref="Forbidden"/> prefix.
    /// </summary>
    public const string VerificationRequiredMessage =
        "KYC verification required. Complete identity verification to unlock this feature.";
}

/// <summary>
/// KYC manager surface. All methods return <see cref="AZOAResult{T}"/> and are
/// Avatar-scoped — ownership is enforced inside the manager, never trusted from
/// a request body. Admin operations take the reviewer's avatar id as an explicit
/// parameter (claim-sourced by the controller), never a body field.
/// </summary>
public interface IKycManager
{
    /// <summary>
    /// Submits a KYC request for <paramref name="avatarId"/>. Rejects when an
    /// active (PENDING/IN_REVIEW) submission already exists; validates documents
    /// via the active provider; persists the submission + its documents; opens a
    /// provider session and stamps the provider session id.
    /// </summary>
    Task<AZOAResult<KycSubmissionModel>> SubmitAsync(SubmitKycModel model, Guid avatarId, CancellationToken ct = default);

    /// <summary>The most-recent submission for the avatar (status read).</summary>
    Task<AZOAResult<KycSubmissionModel>> GetStatusAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>
    /// IDOR-scoped get-by-id: loads the submission then requires it to be owned by
    /// <paramref name="avatarId"/>, else <see cref="KycAuthorizationError.Forbidden"/>.
    /// </summary>
    Task<AZOAResult<KycSubmissionModel>> GetByIdAsync(Guid submissionId, Guid avatarId, CancellationToken ct = default);

    /// <summary>IDOR-scoped: documents for a submission the avatar owns.</summary>
    Task<AZOAResult<IEnumerable<KycDocumentModel>>> ListDocumentsAsync(Guid submissionId, Guid avatarId, CancellationToken ct = default);

    // ── Admin surface ─────────────────────────────────────────────────────────

    /// <summary>Admin review queue (PENDING + IN_REVIEW).</summary>
    Task<AZOAResult<IEnumerable<KycSubmissionModel>>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Approves a submission: status → APPROVED and the owning avatar's
    /// <c>IsVerified</c> flips true. <paramref name="reviewerAvatarId"/> is the
    /// admin's claim-sourced id, never a body field.
    /// </summary>
    Task<AZOAResult<KycSubmissionModel>> ApproveAsync(Guid submissionId, Guid reviewerAvatarId, string? notes, CancellationToken ct = default);

    /// <summary>Rejects a submission: status → REJECTED with an optional reason.</summary>
    Task<AZOAResult<KycSubmissionModel>> RejectAsync(Guid submissionId, Guid reviewerAvatarId, string? notes, string? rejectionReason, CancellationToken ct = default);
}
