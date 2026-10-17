// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>Records the vendor's manual review on a hosted KYC attempt; see Services/Kyc/AGENTS.md §manual review.</summary>
public interface IKycHostedProgressStore
{
    /// <summary>
    /// Moves a live hosted attempt bound to <paramref name="session"/> to IN_REVIEW and extends its expiry so the
    /// pending human decision can still land. CAS on the observed row; a changed row returns null.
    /// </summary>
    Task<AZOAResult<KycSubmission>> TryMarkHostedInReviewAsync(
        KycSubmission expected, HostedSession session, DateTimeOffset now, DateTimeOffset extendedExpiry, CancellationToken ct = default);
}
