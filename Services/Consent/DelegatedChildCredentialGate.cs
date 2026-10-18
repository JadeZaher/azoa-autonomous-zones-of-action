using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>Per-request admission of tenant-issued child JWTs; see Services/Consent/AGENTS.md §Delegated child credentials.</summary>
public sealed class DelegatedChildCredentialGate(IConfiguration configuration, IConsentGrantStore grants, IUserStore users, IApiKeyStore apiKeys)
{
    /// <summary>Node switch; off by default and in production until the sweep proves the tenant-branch flows (D-5).</summary>
    public const string EnabledKey = "Tenant:DelegatedChildCredentials:Enabled";

    /// <summary>The <c>JwtBearerEvents.OnTokenValidated</c> step: fails a child JWT the gate rejects; false when the context was failed.</summary>
    public static async Task<bool> AdmitAsync(TokenValidatedContext context)
    {
        if (!AzoaClaims.IsChildCredential(context.Principal)) return true;
        var gate = ActivatorUtilities.CreateInstance<DelegatedChildCredentialGate>(context.HttpContext.RequestServices);
        var rejection = await gate.RejectAsync(context.Principal!, context.HttpContext.GetEndpoint(), context.HttpContext.RequestAborted);
        if (rejection is null) return true;
        context.Fail(rejection);
        return false;
    }

    /// <summary>Returns a rejection reason, or null when the child credential may reach <paramref name="endpoint"/>.
    /// On admission the principal gains a <see cref="AzoaClaims.LiveRoleIdentity"/> identity holding the subject's store-loaded dApp role.</summary>
    public async Task<string?> RejectAsync(ClaimsPrincipal principal, Endpoint? endpoint, CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>(EnabledKey)) return "Delegated child credentials are disabled on this node.";
        var policy = endpoint?.Metadata.GetMetadata<DelegatedChildCredentialAttribute>();
        if (policy is null) return "This endpoint does not accept delegated child credentials.";
        if (!principal.HasScope(policy.Scope)) return $"The child credential lacks the '{policy.Scope}' scope this endpoint requires.";
        if (principal.GetActingTenantId() is not { } tenant || !AzoaClaims.TryGetSubjectId(principal, out var child)
            || child == Guid.Empty || child == tenant
            || !Guid.TryParse(principal.FindFirst(AzoaClaims.IssuingKeyId)?.Value, out var issuingKey))
            return "The child credential is malformed.";
        var issuer = await users.GetByIdAsync(tenant, ct);
        if (issuer.IsError || issuer.Result is not { IsActive: true }) return "The issuing tenant is no longer active.";
        var now = DateTime.UtcNow;
        var key = await apiKeys.GetByIdForUserAsync(issuingKey, tenant, ct);
        if (key is not { IsActive: true, RevokedAt: null } || key.ExpiresAt <= now)
            return "The tenant API key that issued this credential is no longer active.";
        if (!(key.Scopes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(policy.Scope, StringComparer.Ordinal))
            return $"The tenant API key that issued this credential no longer carries the '{policy.Scope}' scope.";
        var subject = await users.GetByIdAsync(child, ct);
        if (subject.IsError || subject.Result is not { IsActive: true }) return "The credential's user is no longer active.";
        var liveRole = AzoaDappRoles.Normalize(subject.Result.DappRole);
        if (!AzoaDappRoles.GrantsScope(liveRole, policy.Scope))
            return $"The user's current DApp role does not grant the '{policy.Scope}' scope this endpoint requires.";
        var consent = await grants.ListByGrantorAsync(child, ct);
        if (consent.IsError || consent.Result is null) return "The user's consent could not be verified.";
        if (!consent.Result.Any(grant => grant.TenantId == tenant && grant.Covers(policy.Scope, now)))
            return "The user's consent to the issuing tenant no longer covers this endpoint.";
        principal.AddIdentity(new ClaimsIdentity([new Claim("dapp_role", liveRole)], AzoaClaims.LiveRoleIdentity));
        return null;
    }
}
