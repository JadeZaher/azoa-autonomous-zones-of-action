// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client.Query;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Hosted manual-review progress; see Services/Kyc/AGENTS.md §manual review.</summary>
public sealed partial class SurrealKycStore : IKycHostedProgressStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> TryMarkHostedInReviewAsync(
        KycSubmission expected, HostedSession session, DateTimeOffset now, DateTimeOffset extendedExpiry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(session);
        if (expected.ExpiresAt is not { } currentExpiry || currentExpiry <= now
            || expected.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW)
            || !string.Equals(expected.ProviderSessionId, session.Id, StringComparison.Ordinal))
            return AZOAResult<KycSubmission>.Success(null!);

        var newExpiry = extendedExpiry > currentExpiry ? extendedExpiry : currentExpiry;
        var mutation = SurrealWriter.UpdateOnly<KycSubmission>(expected.Id)
            .Where(row => row.ProviderSessionId == expected.ProviderSessionId && row.Status == expected.Status)
            .Where(row => row.ExpiresAt == expected.ExpiresAt && row.ExpiresAt > now)
            .Set(row => row.Status, KycStatus.IN_REVIEW)
            .Set(row => row.ExpiresAt, (DateTimeOffset?)newExpiry)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)now);
        mutation = expected.ModifiedDate is null
            ? mutation.Where(row => row.ModifiedDate == null)
            : mutation.Where(row => row.ModifiedDate == expected.ModifiedDate);
        var response = await KycEvidenceFence.ExecuteAsync(_executor,
            KycEvidenceFence.Mutation(expected.UserId, expected.TenantId, mutation.Build()), ct);
        var row = response.GetValues<KycSubmission>(2).SingleOrDefault();
        return AZOAResult<KycSubmission>.Success(row is null ? null! : FromStorage(row));
    }
}
