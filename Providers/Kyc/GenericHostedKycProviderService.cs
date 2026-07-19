// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>Fail-closed scaffold for an operator-configured hosted KYC adapter.</summary>
public sealed class GenericHostedKycProviderService : IKycProviderService
{
    private const string IncompleteMessage =
        "The generic hosted KYC provider configuration is incomplete or invalid.";
    private const string ScaffoldMessage =
        "The generic hosted KYC provider adapter is a scaffold and is not production-ready.";
    private readonly HostedKycSettings _settings;

    public GenericHostedKycProviderService(IOptions<KycSettings> options)
    {
        _settings = options.Value.Hosted;
    }

    public KycProvider Provider => KycProvider.GENERIC_HOSTED;
    public string ProviderKey => ValidProviderName(_settings.ProviderName)
        ? _settings.ProviderName!.Trim().ToLowerInvariant()
        : "hosted";

    public KycProviderCapabilitiesModel GetCapabilities() => new()
    {
        Provider = Provider,
        ProviderKey = ProviderKey,
        Available = false,
        HostedVerification = true,
        AcceptsDocumentReferences = false,
        UnavailableReason = ConfigurationComplete() ? ScaffoldMessage : IncompleteMessage
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

    private bool ConfigurationComplete()
        => ValidProviderName(_settings.ProviderName)
            && ValidHttpsOrigin(_settings.BaseUrl)
            && !string.IsNullOrWhiteSpace(_settings.ApiKey)
            && !string.IsNullOrWhiteSpace(_settings.WebhookSecret)
            && ValidRelativePath(_settings.SessionPath)
            && ValidRelativePath(_settings.StatusPath)
            && _settings.StatusPath.Contains("{sessionId}", StringComparison.Ordinal);

    private static bool ValidProviderName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 64
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool ValidHttpsOrigin(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && !uri.IsLoopback;

    private static bool ValidRelativePath(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains('?')
            && !value.Contains('#');

    private Task<AZOAResult<T>> Unavailable<T>()
    {
        var reason = GetCapabilities().UnavailableReason
            ?? "The configured KYC provider is unavailable.";
        return Task.FromResult(AZOAResult<T>.Failure(reason));
    }
}
