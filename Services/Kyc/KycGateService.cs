// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Validates the latest KYC ledger entry against the active operator trust profile,
/// AND the latest AML screening verdict for the same (user, tenant) pair.
///
/// <para>Identity verification and sanctions screening are two independent
/// questions. Passing identity says the person is who they claim; it says nothing
/// about whether that person is sanctioned. This gate requires BOTH, and requires
/// the screening answer to exist: an approval with no screening result is an
/// unanswered question, not a clean subject.</para>
/// </summary>
public sealed class KycGateService : IKycGateService
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

    public Task<AZOAResult<bool>> RequireVerifiedAsync(Guid userId)
        => RequireVerifiedCoreAsync(userId, null, CancellationToken.None);

    public Task<AZOAResult<bool>> RequireVerifiedAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
        => RequireVerifiedCoreAsync(userId, tenantId, ct);

    private async Task<AZOAResult<bool>> RequireVerifiedCoreAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
    {
        var latest = tenantId.HasValue || _registry is not null
            ? await _store.GetLatestSubmissionAsync(userId, tenantId, ct)
            : await _store.GetLatestSubmissionByUserAsync(userId, ct);
        if (latest.IsError)
            return new AZOAResult<bool> { IsError = true, Result = false, Message = latest.Message, Exception = latest.Exception };

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
            && KycApprovalTrust.IsCurrentApproval(
                latest.Result,
                profile,
                DateTimeOffset.UtcNow,
                out _))
        {
            // Identity holds. The AML answer is a separate question and is required.
            if (await IsAmlClearAsync(userId, tenantId, ct))
                return new AZOAResult<bool> { Result = true, Message = "Success" };
        }

        // No submission, the latest is not APPROVED, or AML is not clear — gate closed.
        return new AZOAResult<bool>
        {
            IsError = true,
            Result  = false,
            Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
        };
    }

    /// <summary>
    /// Fail-closed AML read. Only <see cref="KycScreeningDecision.NO_HIT"/> on a
    /// non-truncated, un-erased result for this exact (user, tenant) pair opens the
    /// gate. A MISSING result denies, a store error denies, MANUAL_REVIEW denies, and
    /// a truncated hit list denies — an unread sanctions list is not a clean one, and
    /// the whole point of screening is that an approved identity who is sanctioned
    /// must not reach value.
    /// </summary>
    private async Task<bool> IsAmlClearAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        var screening = await _screeningStore.GetLatestResultAsync(userId, tenantId, ct);
        if (screening.IsError || screening.Result is null)
            return false;

        var result = screening.Result;
        return result.Decision == KycScreeningDecision.NO_HIT && !result.ResultsTruncated;
    }

    public Task<AZOAResult<KycStatus>> GetKycStatusAsync(Guid userId)
        => GetKycStatusCoreAsync(userId, null, CancellationToken.None);

    public Task<AZOAResult<KycStatus>> GetKycStatusAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
        => GetKycStatusCoreAsync(userId, tenantId, ct);

    private async Task<AZOAResult<KycStatus>> GetKycStatusCoreAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
    {
        var latest = tenantId.HasValue || _registry is not null
            ? await _store.GetLatestSubmissionAsync(userId, tenantId, ct)
            : await _store.GetLatestSubmissionByUserAsync(userId, ct);
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

    private static bool MatchesAuthority(
        AZOA.WebAPI.Persistence.SurrealDb.Models.KycSubmission submission,
        KycProviderResolution resolution,
        KycApprovalProfile profile)
        => string.Equals(submission.ProviderKey, resolution.Provider.ProviderKey, StringComparison.Ordinal)
            && submission.ProviderSelectionVersion == resolution.SelectionVersion
            && submission.ProviderTrustRevision == resolution.TrustRevision
            && KycApprovalTrust.MatchesCurrentAttempt(submission, profile, out _);
}
