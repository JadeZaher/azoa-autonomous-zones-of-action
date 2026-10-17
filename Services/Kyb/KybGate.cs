// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyb;

namespace AZOA.WebAPI.Services.Kyb;

/// <summary>Tenant capabilities that require a current business verification (owner decision D-6).</summary>
public enum KybCapability
{
    /// <summary>Tenant-driven signing on a user's behalf, enforced at <c>ITenantConsentGate</c>.</summary>
    TenantOnBehalfValue,

    /// <summary>Custodial accounts and custodial KYC for tenant-provisioned users.</summary>
    CustodialAccess,

    /// <summary>Publishing or activating a tenant payout price schedule (consumed by the money lane).</summary>
    PayoutPriceSchedule,
}

/// <summary>
/// The single KYB enforcement seam: succeeds only for a tenant whose current business verification
/// is APPROVED, unexpired and bound to the node's current KYB authority revision.
/// See <c>Services/Kyb/AGENTS.md</c> §KYB gate (D-6).
/// </summary>
public interface IKybGate
{
    /// <summary>
    /// Fails closed with <see cref="KybGate.RequiredCode"/> unless the tenant holds a current approval.
    /// Store faults bubble to the HTTP/worker boundary, so they also never allow.
    /// </summary>
    Task<AZOAResult<bool>> RequireVerifiedTenantAsync(Guid tenantId, KybCapability capability, CancellationToken ct = default);
}

/// <inheritdoc/>
public sealed class KybGate(IKybVerificationStore store, IdentomatKybProvider provider, TimeProvider clock,
    ILogger<KybGate> logger) : IKybGate
{
    /// <summary>Stable refusal code; callers map it to HTTP 403.</summary>
    public const string RequiredCode = "KYB_REQUIRED";

    /// <summary>Generic refusal text; never says why (expired, rejected, absent).</summary>
    public const string RequiredMessage = "KYB_REQUIRED: this tenant must complete business verification first.";

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> RequireVerifiedTenantAsync(Guid tenantId, KybCapability capability, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return Denied();
        // Approval already requires at least one beneficiary (KybHostedSessionOutcomeHandler), so the row is the proof.
        var current = await store.GetAsync(tenantId, ct);
        var now = clock.GetUtcNow();
        if (current is { Status: "APPROVED", BusinessEvidenceJson: not null }
            && current.ExpiresAt > now
            && string.Equals(current.AuthorityRevision, provider.AuthorityRevision, StringComparison.Ordinal))
            return AZOAResult<bool>.Success(true);
        // An early renewal in flight keeps the prior approval effective until it is decided (RV-L2 L2-7).
        if (current is { Status: "PENDING", EffectiveApprovalEvidenceJson: not null, EffectiveApprovalExpiresAt: { } until }
            && until > now
            && string.Equals(current.EffectiveApprovalAuthorityRevision, provider.AuthorityRevision, StringComparison.Ordinal))
            return AZOAResult<bool>.Success(true);

        logger.LogInformation("KYB gate denied {Capability} for tenant {TenantId}.", capability, tenantId);
        return Denied();
    }

    private static AZOAResult<bool> Denied()
        => new() { IsError = true, Result = false, Code = RequiredCode, Message = RequiredMessage };
}
