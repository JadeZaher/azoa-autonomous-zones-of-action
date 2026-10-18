using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyb;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyb;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Kyb;

/// <inheritdoc/>
public sealed class KybService(IKybVerificationStore store, IdentomatKybProvider provider,
    IHostedSessionFlow flow, IKycTenantQuota quota, IOptions<KybSettings> options,
    TimeProvider clock, IUserStore users, IApiKeyStore apiKeys, IHostedSessionStore sessions,
    IHostedSessionRecoveryStore? recovery = null, IOptions<KycSettings>? kycOptions = null) : IKybService
{
    private readonly IdentomatKycSettings _identomat = kycOptions?.Value.Identomat ?? new IdentomatKycSettings();

    /// <inheritdoc/>
    public HostedSessionCapabilities GetCapabilities() => provider.GetHostedCapabilities();

    /// <inheritdoc/>
    public async Task<AZOAResult<KybSessionResponse>> BeginAsync(Guid tenantId, string requestKey, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 200)
            return AZOAResult<KybSessionResponse>.FailureWithCode("A valid Idempotency-Key is required.", AzoaErrorCodes.InvalidRequest);
        var authority = await TenantAuthority.CheckAsync(tenantId, users, apiKeys, clock.GetUtcNow(), ct);
        if (authority.IsError || !authority.Result)
            return AZOAResult<KybSessionResponse>.FailureWithCode("Tenant business verification is not authorized.", authority.Code ?? AzoaErrorCodes.Forbidden);
        if (!GetCapabilities().Available)
            return AZOAResult<KybSessionResponse>.FailureWithCode("Business verification is not configured.", AzoaErrorCodes.DependencyUnavailable);
        var now = clock.GetUtcNow();
        var requestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestKey)));
        var current = await store.GetAsync(tenantId, ct);
        if (current is null || current.ExpiresAt <= now || current.AuthorityRevision != provider.AuthorityRevision
            || (current.Status != "PENDING" && current.RequestKeyHash != requestHash))
        {
            if (!quota.TryConsume(tenantId, KycQuotaOperation.BeginSession))
                return AZOAResult<KybSessionResponse>.FailureWithCode("Business verification request limit reached.", AzoaErrorCodes.OperationNotAllowed);
            var attempt = Guid.NewGuid().ToString("N");
            var candidate = new KybVerification
            {
                Id = tenantId.ToString("N"), AttemptId = attempt,
                AdmissionKey = $"kyb:{tenantId:N}:{attempt}", RequestKeyHash = requestHash,
                AuthorityRevision = provider.AuthorityRevision, CreatedAt = now,
                ExpiresAt = now.AddHours(Math.Clamp(options.Value.AttemptLifetimeHours, 1, 720)),
                Version = (current?.Version ?? -1) + 1,
            };
            CarryEffectiveApproval(current, candidate, now);
            current = await store.TryStartAsync(current, candidate, ct) ?? await store.GetAsync(tenantId, ct);
        }
        if (current is null || current.Status != "PENDING"
            || current.AuthorityRevision != provider.AuthorityRevision || current.ExpiresAt <= now)
            return AZOAResult<KybSessionResponse>.FailureWithCode(
                "A different or completed business verification attempt exists. Read its status before starting another.", AzoaErrorCodes.Conflict);

        var superseded = await SupersedeOrphanAsync(tenantId, current, requestHash, now, ct);
        if (superseded.IsError || superseded.Result is null)
            return AZOAResult<KybSessionResponse>.FailureWithCode(superseded.Message, superseded.Code ?? AzoaErrorCodes.Conflict);
        current = superseded.Result;

        var started = await flow.BeginAsync(provider, new HostedSessionRequest
        {
            FlowKind = HostedSessionFlowKinds.Kyb, TenantId = tenantId, SubjectUserId = tenantId,
            AdmissionKey = current.AdmissionKey,
        }, ct);
        if (started.IsError || started.Result is null)
            return AZOAResult<KybSessionResponse>.FailureWithCode(started.Message, started.Code ?? AzoaErrorCodes.DependencyUnavailable);
        if (!await store.AttachSessionAsync(tenantId, current.AttemptId, started.Result.SessionId, ct))
            return AZOAResult<KybSessionResponse>.FailureWithCode("Business verification changed. Read its current status.", AzoaErrorCodes.Conflict);
        return AZOAResult<KybSessionResponse>.Success(new(Project(current), started.Result.RedirectUrl, started.Result.Resumed));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KybStatusResponse?>> GetStatusAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return AZOAResult<KybStatusResponse?>.FailureWithCode("Tenant identity is required.", AzoaErrorCodes.Forbidden);
        var authority = await TenantAuthority.CheckAsync(tenantId, users, apiKeys, clock.GetUtcNow(), ct);
        if (authority.IsError || !authority.Result)
            return AZOAResult<KybStatusResponse?>.FailureWithCode("Tenant business verification is not authorized.", authority.Code ?? AzoaErrorCodes.Forbidden);
        var current = await store.GetAsync(tenantId, ct);
        if (current is null) return AZOAResult<KybStatusResponse?>.Success(null);
        var projected = Project(current);
        if (projected.Status == "PENDING")
        {
            var hosted = await sessions.GetByAdmissionAsync(HostedSessionFlowKinds.Kyb, provider.ProviderKey, current.AdmissionKey, ct);
            if (hosted.IsError)
                return AZOAResult<KybStatusResponse?>.FailureWithCode("Business verification status is unavailable.", AzoaErrorCodes.DependencyUnavailable);
            if (hosted.Result is { State: HostedSessionStates.Claimed, ProviderSessionId: null } admission
                && (admission.CreationQuarantinedAt is not null || (admission.CreationClaimedAt ?? admission.CreatedDate) <= clock.GetUtcNow().AddMinutes(-5)))
                projected = projected with { Status = "NEEDS_OPERATOR" };
        }
        return AZOAResult<KybStatusResponse?>.Success(projected);
    }

    /// <inheritdoc/>
    public async Task HandleCallbackAsync(string rawBody, CancellationToken ct = default)
        => _ = await flow.HandleCallbackAsync(provider, rawBody, ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<KybStatusResponse?>> RefreshAsync(Guid tenantId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(tenantId, ct);
        if (status.IsError || status.Result?.Status != "PENDING") return status;
        var verification = await store.GetAsync(tenantId, ct);
        if (verification is null) return status;
        var session = Guid.TryParse(verification.HostedSessionId, out var id)
            ? await sessions.GetByIdAsync(id, ct)
            : await sessions.GetByAdmissionAsync(HostedSessionFlowKinds.Kyb, provider.ProviderKey, verification.AdmissionKey, ct);
        if (session.IsError || session.Result is null || session.Result.TenantId != tenantId.ToString("N")
            || session.Result.AdmissionKey != verification.AdmissionKey)
            return AZOAResult<KybStatusResponse?>.FailureWithCode("Business verification could not be refreshed.", AzoaErrorCodes.DependencyUnavailable);
        // No vendor session is attached yet (creation in flight or awaiting operator reconciliation): nothing to re-fetch.
        if (string.IsNullOrWhiteSpace(session.Result.ProviderSessionId))
            return status;
        id = Guid.Parse(session.Result.Id);
        var refreshed = await flow.RefreshOnDemandAsync(provider, id, _identomat.OutcomePoll.RefreshCooldown, ct);
        if (refreshed.IsError && refreshed.Code == HostedSessionFlow.RequestNotSentCode)
            return AZOAResult<KybStatusResponse?>.FailureWithCode("Business verification could not be refreshed.", AzoaErrorCodes.DependencyUnavailable);
        return await GetStatusAsync(tenantId, ct);
    }

    /// <summary>
    /// An early renewal must not suspend a current approval (RV-L2 L2-7): the replacement attempt carries the
    /// still-effective approval until it is decided. See Services/Kyb/AGENTS.md §renewal.
    /// </summary>
    private void CarryEffectiveApproval(KybVerification? current, KybVerification candidate, DateTimeOffset now)
    {
        if (current is { Status: "APPROVED", BusinessEvidenceJson: not null } approved && approved.ExpiresAt > now
            && approved.AuthorityRevision == provider.AuthorityRevision)
        {
            candidate.EffectiveApprovalExpiresAt = approved.ExpiresAt;
            candidate.EffectiveApprovalEvidenceJson = approved.BusinessEvidenceJson;
            candidate.EffectiveApprovalAuthorityRevision = approved.AuthorityRevision;
        }
        else if (current is not null && KybGate.HasEffectiveCarriedApproval(current, provider.AuthorityRevision, now))
        {
            candidate.EffectiveApprovalExpiresAt = current.EffectiveApprovalExpiresAt;
            candidate.EffectiveApprovalEvidenceJson = current.EffectiveApprovalEvidenceJson;
            candidate.EffectiveApprovalAuthorityRevision = current.EffectiveApprovalAuthorityRevision;
        }
    }

    /// <summary>
    /// IDENTOMAT-RECOVERY for KYB: a possibly-sent create past the bounded wait is superseded by a fresh
    /// attempt, at most <c>OrphanSupersedesPerDay</c> per tenant. Returns <paramref name="current"/> unchanged
    /// when its admission is not an orphan. See Services/HostedSessions/AGENTS.md §recovery.
    /// </summary>
    private async Task<AZOAResult<KybVerification>> SupersedeOrphanAsync(Guid tenantId, KybVerification current,
        string requestHash, DateTimeOffset now, CancellationToken ct)
    {
        if (recovery is null)
            return AZOAResult<KybVerification>.Success(current);
        var admission = await sessions.GetByAdmissionAsync(HostedSessionFlowKinds.Kyb, provider.ProviderKey, current.AdmissionKey, ct);
        if (admission.IsError || admission.Result is not { State: HostedSessionStates.Claimed, ProviderSessionId: null, CreationStartedAt: not null } orphan
            || (orphan.CreationQuarantinedAt is null
                && (orphan.CreationClaimedAt ?? orphan.CreatedDate) > now.AddMinutes(-Math.Clamp(_identomat.OrphanWaitMinutes, 1, 1440))))
            return AZOAResult<KybVerification>.Success(current);

        var used = await recovery.CountSupersededSinceAsync(HostedSessionFlowKinds.Kyb, tenantId, now.AddDays(-1), ct);
        if (used >= Math.Clamp(_identomat.OrphanSupersedesPerDay, 0, 10))
            return AZOAResult<KybVerification>.FailureWithCode(
                "NeedsOperator: the previous business verification request is unresolved; retry tomorrow or contact support.",
                AzoaErrorCodes.Conflict);
        var closed = await recovery.TrySupersedeOrphanAsync(orphan, now, ct);
        if (closed.IsError || closed.Result is null)
            return AZOAResult<KybVerification>.FailureWithCode("Business verification changed. Read its current status.", AzoaErrorCodes.Conflict);

        var attempt = Guid.NewGuid().ToString("N");
        var candidate = new KybVerification
        {
            Id = tenantId.ToString("N"), AttemptId = attempt, AdmissionKey = $"kyb:{tenantId:N}:{attempt}",
            RequestKeyHash = requestHash, AuthorityRevision = provider.AuthorityRevision, CreatedAt = now,
            ExpiresAt = now.AddHours(Math.Clamp(options.Value.AttemptLifetimeHours, 1, 720)), Version = current.Version + 1,
        };
        CarryEffectiveApproval(current, candidate, now);
        var replacement = await store.TryStartAsync(current, candidate, ct);
        return replacement is { Status: "PENDING" } && replacement.AttemptId == attempt
            ? AZOAResult<KybVerification>.Success(replacement)
            : AZOAResult<KybVerification>.FailureWithCode("Business verification changed. Read its current status.", AzoaErrorCodes.Conflict);
    }

    private KybStatusResponse Project(KybVerification verification)
    {
        var now = clock.GetUtcNow();
        var fresh = verification.ExpiresAt > now
            && verification.AuthorityRevision == provider.AuthorityRevision && GetCapabilities().Available;
        var carried = GetCapabilities().Available && KybGate.HasEffectiveCarriedApproval(verification, provider.AuthorityRevision, now);
        var evidence = verification.BusinessEvidenceJson is null ? null
            : JsonSerializer.Deserialize<KybBusinessEvidence>(verification.BusinessEvidenceJson);
        return new KybStatusResponse(Guid.Parse(verification.AttemptId), fresh ? verification.Status : "EXPIRED",
            (fresh && verification.Status == "APPROVED" && evidence is { Beneficiaries.Count: > 0 }) || carried,
            verification.ExpiresAt, verification.ReviewedAt, evidence?.Beneficiaries.Count ?? 0);
    }
}

