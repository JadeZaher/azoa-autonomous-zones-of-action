// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// Config-gated external KYC provider adapter. This is a deploy-stub: it is
/// registered only when <c>Kyc:Provider == "veriff"</c> and its secrets are
/// provisioned out-of-band (see <c>docs/NODE-HOST.md</c> §8, KYC provisioning).
/// Until the real integration lands every operation returns an unavailable
/// result so a mis-configured deployment fails closed without an opaque 500.
/// </summary>
public sealed class VeriffKycProviderService : IKycProviderService
{
    private const string NotImplementedMessage =
        "External KYC provider integration is not yet configured. " +
        "Set Kyc:Provider=manual to use the built-in manual review provider.";

    public KycProvider Provider => KycProvider.VERIFF;
    public string ProviderKey => "veriff";

    public KycProviderCapabilitiesModel GetCapabilities() => new()
    {
        Provider = Provider,
        ProviderKey = ProviderKey,
        Available = false,
        HostedVerification = true,
        AcceptsDocumentReferences = false,
        UnavailableReason = NotImplementedMessage
    };

    public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(Guid avatarId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<KycSessionStartModel>.Failure(NotImplementedMessage));

    public Task<AZOAResult<string>> CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<string>.Failure(NotImplementedMessage));

    public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(string providerSessionId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<KycStatus>.Failure(NotImplementedMessage));

    public Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<KycStatus>.Failure(NotImplementedMessage));

    public Task<AZOAResult<bool>> ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Failure(NotImplementedMessage));
}
