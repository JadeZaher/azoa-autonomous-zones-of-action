using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>Per-request admission of tenant-issued child JWTs; see Services/Consent/AGENTS.md §Delegated child credentials.</summary>
public sealed class DelegatedChildCredentialGate(IConfiguration configuration, IConsentGrantStore grants, IUserStore users)
{
    /// <summary>Node switch; off by default and in production until the sweep proves the tenant-branch flows (D-5).</summary>
    public const string EnabledKey = "Tenant:DelegatedChildCredentials:Enabled";

    /// <summary>Returns a rejection reason, or null when the child credential may reach <paramref name="endpoint"/>.</summary>
    public async Task<string?> RejectAsync(ClaimsPrincipal principal, Endpoint? endpoint, CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>(EnabledKey)) return "Delegated child credentials are disabled on this node.";
        var policy = endpoint?.Metadata.GetMetadata<DelegatedChildCredentialAttribute>();
        if (policy is null) return "This endpoint does not accept delegated child credentials.";
        if (!principal.HasScope(policy.Scope)) return $"The child credential lacks the '{policy.Scope}' scope this endpoint requires.";
        if (principal.GetActingTenantId() is not { } tenant || !AzoaClaims.TryGetSubjectId(principal, out var child)
            || child == Guid.Empty || child == tenant)
            return "The child credential is malformed.";
        var issuer = await users.GetByIdAsync(tenant, ct);
        if (issuer.IsError || issuer.Result is not { IsActive: true }) return "The issuing tenant is no longer active.";
        var consent = await grants.ListByGrantorAsync(child, ct);
        if (consent.IsError || consent.Result is null) return "The user's consent could not be verified.";
        var now = DateTime.UtcNow;
        return consent.Result.Any(grant => grant.TenantId == tenant && grant.Covers(policy.Scope, now))
            ? null : "The user's consent to the issuing tenant no longer covers this endpoint.";
    }
}
