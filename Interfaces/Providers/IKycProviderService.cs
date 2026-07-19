// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Providers;

/// <summary>
/// Provider-agnostic verification seam. This is the swap point between the
/// in-house manual reviewer and an external KYC vendor — selected by the
/// <c>Kyc:Provider</c> configuration value. The manager depends only on this
/// interface, never on a concrete provider.
/// </summary>
public interface IKycProviderService
{
    /// <summary>The authoritative provider represented by this implementation.</summary>
    KycProvider Provider { get; }

    /// <summary>Stable public provider key used in provider-neutral API metadata.</summary>
    string ProviderKey { get; }

    /// <summary>Reports whether this provider can begin and accept verification work.</summary>
    KycProviderCapabilitiesModel GetCapabilities();

    /// <summary>
    /// Begins a safe verification flow. Hosted providers return an HTTPS redirect;
    /// the manual provider returns document-reference instructions instead.
    /// </summary>
    Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>
    /// Begins a provider session for the avatar's documents and returns the
    /// provider session id. The manual provider has no external session and
    /// returns the avatar id as a pseudo-session id.
    /// </summary>
    Task<AZOAResult<string>> CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default);

    /// <summary>Polls the current status of a provider session.</summary>
    Task<AZOAResult<KycStatus>> GetSessionStatusAsync(string providerSessionId, CancellationToken ct = default);

    /// <summary>Processes an inbound provider webhook payload.</summary>
    Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default);

    /// <summary>
    /// Validates the supplied documents (MIME allow-list, size cap, required
    /// fields) before any submission row is written.
    /// </summary>
    Task<AZOAResult<bool>> ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default);
}
