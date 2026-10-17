// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// The node's single KYC value gate: current policy-bound identity approval AND a clear AML
/// verdict for the same attempt lineage. Serves both <see cref="IKycGateService"/> and
/// <see cref="IRealValueKycGate"/>; see <c>Services/Kyc/AGENTS.md</c> §value gate.
/// </summary>
public sealed class KycGateService : IKycGateService, IRealValueKycGate
{
    private readonly IKycStore _store;
    private readonly IKycScreeningStore _screeningStore;
    private readonly IKycProviderService _provider;
    private readonly IKycProviderRegistry? _registry;
    private readonly KycSettings _settings;
    private readonly IHostEnvironment _environment;

    public KycGateService(
        IKycStore store,
        IKycScreeningStore screeningStore,
        IKycProviderService provider,
        IOptions<KycSettings> settings,
        IHostEnvironment environment)
    {
        _store = store;
        _screeningStore = screeningStore;
        _provider = provider;
        _registry = null;
        _settings = settings.Value;
        _environment = environment;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public KycGateService(
        IKycStore store,
        IKycScreeningStore screeningStore,
        IKycProviderService provider,
        IKycProviderRegistry registry,
        IOptions<KycSettings> settings,
        IHostEnvironment environment)
    {
        _store = store;
        _screeningStore = screeningStore;
        _provider = provider;
        _registry = registry;
        _settings = settings.Value;
        _environment = environment;
    }

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> RequireVerifiedAsync(Guid userId)
        => RequireCoreAsync(userId, null, CancellationToken.None);

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> RequireVerifiedAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
        => RequireCoreAsync(userId, tenantId, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, CancellationToken ct = default)
        => RequireCoreAsync(userId, null, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
        => RequireCoreAsync(userId, tenantId, ct);

    private async Task<AZOAResult<bool>> RequireCoreAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return Denied();

        var latest = await LatestAsync(userId, tenantId, ct);
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
            && MatchesAuthority(latest.Result, authority.Result, profile)
            && KycApprovalTrust.IsCurrentApproval(latest.Result, profile, DateTimeOffset.UtcNow, out _)
            && await IsAmlClearAsync(userId, tenantId, latest.Result.AmlLineageKey, ct))
        {
            return AZOAResult<bool>.Success(true);
        }

        return Denied();
    }

    // See Services/Kyc/AGENTS.md: AML attempt lineage.
    private async Task<bool> IsAmlClearAsync(Guid userId, Guid? tenantId, string? lineageKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineageKey)) return false;
        var screening = await _screeningStore.GetLatestResultAsync(userId, tenantId, ct, lineageKey);
        if (screening.IsError || screening.Result is null)
            return false;

        return string.Equals(screening.Result.LineageKey, lineageKey, StringComparison.Ordinal)
            && KycAmlClearance.IsClear(screening.Result);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycStatus>> GetKycStatusAsync(Guid userId)
        => GetKycStatusCoreAsync(userId, null, CancellationToken.None);

    /// <inheritdoc/>
    public Task<AZOAResult<KycStatus>> GetKycStatusAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
        => GetKycStatusCoreAsync(userId, tenantId, ct);

    private async Task<AZOAResult<KycStatus>> GetKycStatusCoreAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        var latest = await LatestAsync(userId, tenantId, ct);
        if (latest.IsError)
            return new AZOAResult<KycStatus> { IsError = true, Message = latest.Message, Exception = latest.Exception };

        if (latest.Result is null)
            return new AZOAResult<KycStatus>
            {
                IsError = true,
                Message = $"{KycAuthorizationError.NotFound}No KYC submission found for this user."
            };

        var now = DateTimeOffset.UtcNow;
        var status = latest.Result.Status;
        if (latest.Result.ExpiresAt is not { } expiresAt || expiresAt <= now)
        {
            status = KycStatus.EXPIRED;
        }
        else if (status == KycStatus.APPROVED)
        {
            var authority = await ResolveAuthorityAsync(tenantId, ct);
            if (authority.IsError
                || authority.Result is null
                || !KycApprovalTrust.TryResolveCurrentProfile(
                    authority.Result.Provider,
                    authority.Result.Settings,
                    _environment,
                    out _,
                    out var profile,
                    out _)
                || !MatchesAuthority(latest.Result, authority.Result, profile)
                || !KycApprovalTrust.IsCurrentApproval(latest.Result, profile, now, out _))
            {
                status = KycStatus.EXPIRED;
            }
        }

        return new AZOAResult<KycStatus> { Result = status, Message = "Success" };
    }

    private Task<AZOAResult<KycSubmission>> LatestAsync(Guid userId, Guid? tenantId, CancellationToken ct)
        => tenantId.HasValue || _registry is not null
            ? _store.GetLatestSubmissionAsync(userId, tenantId, ct)
            : _store.GetLatestSubmissionByUserAsync(userId, ct);

    private async Task<AZOAResult<KycProviderResolution>> ResolveAuthorityAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId.HasValue)
            return _registry is null
                ? AZOAResult<KycProviderResolution>.Failure(KycProviderReadinessCodes.SelectionRequired)
                : await _registry.ResolveTenantAsync(tenantId.Value, ct);
        return _registry?.ResolveNodeDefault()
            ?? AZOAResult<KycProviderResolution>.Success(new KycProviderResolution(
                _provider, _settings, null, 0, 0, _provider.ProviderKey));
    }

    private static bool MatchesAuthority(KycSubmission submission, KycProviderResolution resolution, KycApprovalProfile profile)
        => string.Equals(submission.ProviderKey, resolution.Provider.ProviderKey, StringComparison.Ordinal)
            && submission.ProviderSelectionVersion == resolution.SelectionVersion
            && submission.ProviderTrustRevision == resolution.TrustRevision
            && KycApprovalTrust.MatchesCurrentAttempt(submission, profile, out _);

    private static AZOAResult<bool> Denied(Exception? exception = null) => new()
    {
        IsError = true,
        Result = false,
        Message = KycAuthorizationError.Forbidden + KycAuthorizationError.VerificationRequiredMessage,
        Exception = exception,
    };
}
