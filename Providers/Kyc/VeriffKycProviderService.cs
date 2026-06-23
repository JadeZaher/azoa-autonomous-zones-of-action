// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// Config-gated external KYC provider adapter. This is a deploy-stub: it is
/// registered only when <c>Kyc:Provider == "veriff"</c> and its secrets are
/// provisioned out-of-band (see <c>conductor/DEPLOY-STEPS-TODO.md</c>, P4).
/// Until the real integration lands every method throws
/// <see cref="NotImplementedException"/> with a generic message so a
/// mis-configured deployment fails loudly rather than silently passing KYC.
/// </summary>
public sealed class VeriffKycProviderService : IKycProviderService
{
    private const string NotImplementedMessage =
        "External KYC provider integration is not yet configured. " +
        "Set Kyc:Provider=manual to use the built-in manual review provider.";

    public Task<AZOAResult<string>> CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(string providerSessionId, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<AZOAResult<bool>> ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);
}
