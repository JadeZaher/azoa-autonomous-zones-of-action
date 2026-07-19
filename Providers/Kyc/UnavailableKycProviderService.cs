// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>Fail-closed provider used for unknown KYC configuration values.</summary>
public sealed class UnavailableKycProviderService : IKycProviderService
{
    private const string Message =
        "Kyc:Provider is unknown. Select an explicitly supported provider.";

    public KycProvider Provider => KycProvider.UNAVAILABLE;
    public string ProviderKey => "unavailable";

    public KycProviderCapabilitiesModel GetCapabilities() => new()
    {
        Provider = Provider,
        ProviderKey = ProviderKey,
        Available = false,
        HostedVerification = false,
        AcceptsDocumentReferences = false,
        UnavailableReason = Message
    };

    public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(Guid avatarId, CancellationToken ct = default)
        => Unavailable<KycSessionStartModel>();

    public Task<AZOAResult<string>> CreateSessionAsync(
        Guid avatarId,
        IReadOnlyList<KycDocumentModel> documents,
        CancellationToken ct = default)
        => Unavailable<string>();

    public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(
        string providerSessionId,
        CancellationToken ct = default)
        => Unavailable<KycStatus>();

    public Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default)
        => Unavailable<KycStatus>();

    public Task<AZOAResult<bool>> ValidateDocumentsAsync(
        IReadOnlyList<SubmitKycDocumentModel> documents,
        CancellationToken ct = default)
        => Unavailable<bool>();

    private static Task<AZOAResult<T>> Unavailable<T>()
        => Task.FromResult(AZOAResult<T>.Failure(Message));
}
