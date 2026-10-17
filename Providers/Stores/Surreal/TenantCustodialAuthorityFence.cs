using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Consent;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Serializes current tenant custody access with account claim, grant revocation and membership closure.</summary>
public static class TenantCustodialAuthorityFence
{
    public const string FailureMarker = "TENANT_CUSTODIAL_AUTHORITY_CHANGED";

    public static async Task<TenantCustodialWitness?> CaptureAsync(ISurrealExecutor executor, Guid userId, string externalUserId,
        SigningConsentWitness authority, string requiredScope, CancellationToken ct)
    {
        var credential = authority.Credential;
        if (userId == Guid.Empty || credential.TenantId is not null || string.IsNullOrWhiteSpace(externalUserId)
            || externalUserId.Length > 128 || externalUserId.Any(char.IsControl)
            || !authority.RequiredScopes.Contains(AzoaScopes.TenantProvision, StringComparer.Ordinal)
            || !authority.RequiredScopes.Contains(requiredScope, StringComparer.Ordinal)) return null;
        var tenant = credential.ActorUserId;
        var external = externalUserId.Trim();
        var child = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(userId.ToString("N")), ct);
        if (child is not { IsActive: true } || SurrealRecordGuid.ParseOptional(child.OwnerTenantId) != tenant
            || child.ExternalUserId != external) return null;
        var reference = TenantProvisionedGrant.ParticipationRefFor(tenant, external);
        var membershipId = ConsentLifecycleIdentity.ParticipationId(tenant, reference);
        var membership = await executor.QuerySingleAsync<ConsentParticipationBoundary>(SurrealQuery<ConsentParticipationBoundary>.Key(membershipId), ct);
        if (membership is null || membership.ClosedAt is not null || membership.Version < 0
            || membership.TenantId != tenant.ToString("N") || membership.ParticipationRef != reference) return null;
        var commandId = TenantProvisionedGrant.CommandIdFor(tenant, userId, external);
        var grant = await executor.QuerySingleAsync<ConsentGrant>(SurrealQuery<ConsentGrant>.Key(commandId[..32]), ct);
        if (grant is null || grant.Version < 0 || grant.RevokedAt is not null || grant.CommandId != commandId
            || SurrealRecordGuid.Parse(grant.GrantorUserId) != userId || SurrealRecordGuid.Parse(grant.TenantId) != tenant
            || grant.Origin != "Participation" || grant.ParticipationRef != reference || grant.AdmissionReceiptJson is null
            || !grant.Scopes.Split(',').Contains(requiredScope, StringComparer.Ordinal)) return null;
        return new(authority, userId, external, child.AuthNotBefore, membershipId, membership.Version, grant.Id,
            grant.Version, grant.Scopes, grant.ExpiresAt, commandId, grant.AdmissionReceiptJson);
    }

    public static IReadOnlyList<SurrealQuery>? Create(TenantCustodialWitness witness, string requiredScope)
    {
        var credential = witness.Authority.Credential;
        if (credential.TenantId is not null || witness.UserId == Guid.Empty
            || !witness.Authority.RequiredScopes.Contains(AzoaScopes.TenantProvision, StringComparer.Ordinal)
            || !witness.Authority.RequiredScopes.Contains(requiredScope, StringComparer.Ordinal)
            || !witness.GrantScopes.Split(',').Contains(requiredScope, StringComparer.Ordinal)) return null;
        var guards = SigningAuthorityFence.ForConsent(witness.Authority, witness.Authority.RequiredScopes, "tenant_access_auth");
        if (guards is null) return null;
        var tenant = SurrealLink.ToLink("user", credential.ActorUserId.ToString("N"))!;
        var tenantId = credential.ActorUserId.ToString("N");
        var child = SurrealLink.ToLink("user", witness.UserId.ToString("N"))!;
        var reference = TenantProvisionedGrant.ParticipationRefFor(credential.ActorUserId, witness.ExternalUserId);
        var commandId = TenantProvisionedGrant.CommandIdFor(credential.ActorUserId, witness.UserId, witness.ExternalUserId);
        if (witness.MembershipId != ConsentLifecycleIdentity.ParticipationId(credential.ActorUserId, reference)
            || SurrealRecordGuid.BareId(witness.GrantId) != commandId[..32] || witness.GrantCommandId != commandId) return null;
        var result = guards.ToList();
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(witness.UserId.ToString("N"))
            .Where(row => row.IsActive && row.OwnerTenantId == tenant && row.ExternalUserId == witness.ExternalUserId
                && row.AuthNotBefore == witness.AuthNotBefore).Set(row => row.IsActive, true).Build(), "tenant_access_child", "_tenant_access_child"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ConsentParticipationBoundary>(witness.MembershipId)
            .Where(row => row.Version == witness.MembershipVersion && row.ClosedAt == null
                && row.TenantId == tenantId && row.ParticipationRef == reference)
            .Set(row => row.Version, witness.MembershipVersion).Build(), "tenant_access_member", "_tenant_access_member"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ConsentGrant>(SurrealRecordGuid.BareId(witness.GrantId))
            .Where(row => row.Version == witness.GrantVersion && row.RevokedAt == null && row.GrantorUserId == child && row.TenantId == tenant
                && row.Scopes == witness.GrantScopes && row.ExpiresAt == witness.GrantExpiresAt && row.Origin == "Participation"
                && row.ParticipationRef == reference && row.CommandId == witness.GrantCommandId && row.AdmissionReceiptJson == witness.GrantReceiptJson)
            .Set(row => row.Version, witness.GrantVersion).Build(), "tenant_access_grant", "_tenant_access_grant"));
        // raw: child, exact enrollment and current credential share the consuming read or mutation transaction.
        result.Add(SurrealQuery.Of("""
            IF $_tenant_access_child = NONE OR $_tenant_access_child = [] OR $_tenant_access_member = NONE OR $_tenant_access_member = []
                OR $_tenant_access_grant = NONE OR $_tenant_access_grant = []
                OR ($_tenant_access_grant.expires_at != NONE AND $_tenant_access_grant.expires_at <= time::now())
                { THROW 'TENANT_CUSTODIAL_AUTHORITY_CHANGED' }
            """));
        return result;
    }
}
