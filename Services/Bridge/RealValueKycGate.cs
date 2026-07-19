using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Bridge;

/// <summary>Requires current policy-bound KYC provenance before real-value work.</summary>
public sealed class RealValueKycGate : IRealValueKycGate
{
    private readonly IKycStore _store;
    private readonly IKycProviderService _provider;
    private readonly IKycProviderRegistry? _registry;
    private readonly KycSettings _settings;
    private readonly IHostEnvironment _environment;

    public RealValueKycGate(
        IKycStore store,
        IKycProviderService provider,
        IOptions<KycSettings> settings,
        IHostEnvironment environment)
    {
        _store = store;
        _provider = provider;
        _registry = null;
        _settings = settings.Value;
        _environment = environment;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public RealValueKycGate(
        IKycStore store,
        IKycProviderService provider,
        IKycProviderRegistry registry,
        IOptions<KycSettings> settings,
        IHostEnvironment environment)
    {
        _store = store;
        _provider = provider;
        _registry = registry;
        _settings = settings.Value;
        _environment = environment;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid avatarId,
        CancellationToken ct = default)
        => await RequireCurrentApprovalCoreAsync(avatarId, null, ct);

    public async Task<AZOAResult<bool>> RequireCurrentApprovalAsync(
        Guid avatarId,
        Guid tenantId,
        CancellationToken ct = default)
        => await RequireCurrentApprovalCoreAsync(avatarId, tenantId, ct);

    private async Task<AZOAResult<bool>> RequireCurrentApprovalCoreAsync(
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct)
    {
        if (avatarId == Guid.Empty)
            return Denied();

        var latest = tenantId.HasValue || _registry is not null
            ? await _store.GetLatestSubmissionAsync(avatarId, tenantId, ct)
            : await _store.GetLatestSubmissionByAvatarAsync(avatarId, ct);
        if (latest.IsError)
            return Denied(latest.Exception);

        var authority = await ResolveAuthorityAsync(tenantId, ct);
        if (latest.Result is not null
            && !authority.IsError
            && authority.Result is not null
            && KycApprovalTrust.TryResolveCurrentProfile(
                authority.Result.Provider,
                authority.Result.Settings,
                _environment,
                out _,
                out var profile,
                out _)
            && profile.Provider != KycProvider.MANUAL
            && string.Equals(latest.Result.ProviderKey, authority.Result.Provider.ProviderKey, StringComparison.Ordinal)
            && latest.Result.ProviderSelectionVersion == authority.Result.SelectionVersion
            && latest.Result.ProviderTrustRevision == authority.Result.TrustRevision
            && KycApprovalTrust.MatchesCurrentAttempt(latest.Result, profile, out _)
            && KycApprovalTrust.IsCurrentApproval(
                latest.Result,
                profile,
                DateTimeOffset.UtcNow,
                out _))
        {
            return AZOAResult<bool>.Success(true);
        }

        return Denied();
    }

    private async Task<AZOAResult<KycProviderResolution>> ResolveAuthorityAsync(
        Guid? tenantId,
        CancellationToken ct)
    {
        if (tenantId.HasValue)
            return _registry is null
                ? AZOAResult<KycProviderResolution>.Failure(KycProviderReadinessCodes.SelectionRequired)
                : await _registry.ResolveTenantAsync(tenantId.Value, ct);
        return _registry?.ResolveNodeDefault()
            ?? AZOAResult<KycProviderResolution>.Success(new KycProviderResolution(
                _provider, _settings, null, 0, 0, _provider.ProviderKey));
    }

    private static AZOAResult<bool> Denied(Exception? exception = null) => new()
    {
        IsError = true,
        Result = false,
        Message = KycAuthorizationError.Forbidden
            + KycAuthorizationError.VerificationRequiredMessage,
        Exception = exception,
    };
}
