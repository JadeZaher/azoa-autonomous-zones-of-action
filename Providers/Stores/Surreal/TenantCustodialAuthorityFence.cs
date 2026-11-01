using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Consent;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Serializes current tenant custody access with account claim, grant revocation and membership closure; see AGENTS.md §claimed-custodial-fence.</summary>
public static class TenantCustodialAuthorityFence
{
    public const string FailureMarker = "TENANT_CUSTODIAL_AUTHORITY_CHANGED";
    private const string ParticipationOrigin = "Participation";
    private const string UserExplicitOrigin = "UserExplicit";

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
        return Classify(child, userId, external, tenant) switch
        {
            TenantCustodialKind.Custodial => await CaptureCustodialAsync(executor, child!, userId, external, authority, requiredScope, ct),
            TenantCustodialKind.Claimed => await CaptureClaimedAsync(executor, child!, userId, external, authority, requiredScope, ct),
            _ => null,
        };
    }

    /// <summary>Classifies the member at (tenant, external user) without reading any authority; null when it is not this tenant's.</summary>
    public static async Task<TenantCustodialKind?> OwnershipAsync(ISurrealExecutor executor, Guid userId, string externalUserId,
        Guid tenantId, CancellationToken ct)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty || string.IsNullOrWhiteSpace(externalUserId)) return null;
        var child = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(userId.ToString("N")), ct);
        return Classify(child, userId, externalUserId.Trim(), tenantId);
    }

    public static IReadOnlyList<SurrealQuery>? Create(TenantCustodialWitness witness, string requiredScope)
    {
        var credential = witness.Authority.Credential;
        if (credential.TenantId is not null || witness.UserId == Guid.Empty
            || !witness.Authority.RequiredScopes.Contains(AzoaScopes.TenantProvision, StringComparer.Ordinal)
            || !witness.Authority.RequiredScopes.Contains(requiredScope, StringComparer.Ordinal)
            || !witness.GrantScopes.Split(',').Contains(requiredScope, StringComparer.Ordinal)) return null;
        var member = witness.Kind switch
        {
            TenantCustodialKind.Custodial => CustodialGuards(witness),
            TenantCustodialKind.Claimed => ClaimedGuards(witness),
            _ => null,
        };
        var guards = member is null ? null : SigningAuthorityFence.ForConsent(witness.Authority, witness.Authority.RequiredScopes, "tenant_access_auth");
        return guards is null ? null : [.. guards, .. member!];
    }

    private static TenantCustodialKind? Classify(User? child, Guid userId, string external, Guid tenant)
    {
        if (child is not { IsActive: true } || child.ExternalUserId != external) return null;
        var owner = SurrealRecordGuid.ParseOptional(child.OwnerTenantId);
        if (owner is not null) return owner == tenant ? TenantCustodialKind.Custodial : null;
        return TenantChildCorrelation.IsClaimed(userId, owner, external, child.AuthNotBefore is not null, tenant)
            ? TenantCustodialKind.Claimed : null;
    }

    private static async Task<TenantCustodialWitness?> CaptureCustodialAsync(ISurrealExecutor executor, User child, Guid userId,
        string external, SigningConsentWitness authority, string requiredScope, CancellationToken ct)
    {
        var tenant = authority.Credential.ActorUserId;
        var reference = TenantProvisionedGrant.ParticipationRefFor(tenant, external);
        var membershipId = ConsentLifecycleIdentity.ParticipationId(tenant, reference);
        var membership = await executor.QuerySingleAsync<ConsentParticipationBoundary>(SurrealQuery<ConsentParticipationBoundary>.Key(membershipId), ct);
        if (membership is null || membership.ClosedAt is not null || membership.Version < 0
            || membership.TenantId != tenant.ToString("N") || membership.ParticipationRef != reference) return null;
        var commandId = TenantProvisionedGrant.CommandIdFor(tenant, userId, external);
        var grant = await executor.QuerySingleAsync<ConsentGrant>(SurrealQuery<ConsentGrant>.Key(commandId[..32]), ct);
        if (grant is null || grant.Version < 0 || grant.RevokedAt is not null || grant.CommandId != commandId
            || SurrealRecordGuid.Parse(grant.GrantorUserId) != userId || SurrealRecordGuid.Parse(grant.TenantId) != tenant
            || grant.Origin != ParticipationOrigin || grant.ParticipationRef != reference || grant.AdmissionReceiptJson is null
            || !grant.Scopes.Split(',').Contains(requiredScope, StringComparer.Ordinal)) return null;
        return new(TenantCustodialKind.Custodial, authority, userId, external, child.AuthNotBefore, membershipId, membership.Version,
            grant.Id, grant.Version, grant.Scopes, grant.ExpiresAt, commandId, grant.AdmissionReceiptJson);
    }

    /// <summary>The person's first live UserExplicit grant to this tenant, whatever it covers; reports consent only, never authorizes.</summary>
    public static async Task<ConsentGrant?> FirstLiveClaimedGrantAsync(ISurrealExecutor executor, Guid userId, Guid tenantId, CancellationToken ct)
        => (await LiveClaimedGrantsAsync(executor, userId, tenantId, ct)).FirstOrDefault();

    /// <summary>Selects the first live, receipt-backed UserExplicit grant from the claimed person to this tenant covering the scope.</summary>
    private static async Task<TenantCustodialWitness?> CaptureClaimedAsync(ISurrealExecutor executor, User child, Guid userId,
        string external, SigningConsentWitness authority, string requiredScope, CancellationToken ct)
    {
        var grant = (await LiveClaimedGrantsAsync(executor, userId, authority.Credential.ActorUserId, ct))
            .FirstOrDefault(row => row.Scopes.Split(',').Contains(requiredScope, StringComparer.Ordinal));
        return grant is null ? null : new(TenantCustodialKind.Claimed, authority, userId, external, child.AuthNotBefore, null, null,
            grant.Id, grant.Version, grant.Scopes, grant.ExpiresAt, grant.CommandId!, grant.AdmissionReceiptJson!);
    }

    /// <summary>Live, unexpired, receipt-backed UserExplicit grants from the person to the tenant, in grant-id order.</summary>
    private static async Task<IReadOnlyList<ConsentGrant>> LiveClaimedGrantsAsync(ISurrealExecutor executor, Guid userId, Guid tenantId,
        CancellationToken ct)
    {
        var grantor = SurrealLink.ToLink("user", userId.ToString("N"))!;
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
        var grants = await executor.QueryAsync<ConsentGrant>(SurrealQuery<ConsentGrant>.From()
            .Where(row => row.GrantorUserId == grantor && row.TenantId == tenant && row.Origin == UserExplicitOrigin && row.RevokedAt == null)
            .OrderBy(row => row.Id), ct);
        var now = DateTimeOffset.UtcNow;
        return grants.Where(row => row.Version >= 0 && row.ParticipationRef is null && row.AdmissionReceiptJson is not null
            && row.CommandId is { Length: >= 32 } command && SurrealRecordGuid.BareId(row.Id) == command[..32]
            && (row.ExpiresAt is null || row.ExpiresAt > now)).ToList();
    }

    private static IReadOnlyList<SurrealQuery>? CustodialGuards(TenantCustodialWitness witness)
    {
        var credential = witness.Authority.Credential;
        var tenant = SurrealLink.ToLink("user", credential.ActorUserId.ToString("N"))!;
        var tenantId = credential.ActorUserId.ToString("N");
        var child = SurrealLink.ToLink("user", witness.UserId.ToString("N"))!;
        var reference = TenantProvisionedGrant.ParticipationRefFor(credential.ActorUserId, witness.ExternalUserId);
        var commandId = TenantProvisionedGrant.CommandIdFor(credential.ActorUserId, witness.UserId, witness.ExternalUserId);
        if (witness.MembershipId is not { } membershipId || witness.MembershipVersion is not { } membershipVersion
            || membershipId != ConsentLifecycleIdentity.ParticipationId(credential.ActorUserId, reference)
            || SurrealRecordGuid.BareId(witness.GrantId) != commandId[..32] || witness.GrantCommandId != commandId) return null;
        return
        [
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(witness.UserId.ToString("N"))
                .Where(row => row.IsActive && row.OwnerTenantId == tenant && row.ExternalUserId == witness.ExternalUserId
                    && row.AuthNotBefore == witness.AuthNotBefore).Set(row => row.IsActive, true).Build(), "tenant_access_child", "_tenant_access_child"),
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ConsentParticipationBoundary>(membershipId)
                .Where(row => row.Version == membershipVersion && row.ClosedAt == null
                    && row.TenantId == tenantId && row.ParticipationRef == reference)
                .Set(row => row.Version, membershipVersion).Build(), "tenant_access_member", "_tenant_access_member"),
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ConsentGrant>(SurrealRecordGuid.BareId(witness.GrantId))
                .Where(row => row.Version == witness.GrantVersion && row.RevokedAt == null && row.GrantorUserId == child && row.TenantId == tenant
                    && row.Scopes == witness.GrantScopes && row.ExpiresAt == witness.GrantExpiresAt && row.Origin == ParticipationOrigin
                    && row.ParticipationRef == reference && row.CommandId == witness.GrantCommandId && row.AdmissionReceiptJson == witness.GrantReceiptJson)
                .Set(row => row.Version, witness.GrantVersion).Build(), "tenant_access_grant", "_tenant_access_grant"),
            // raw: child, exact enrollment and current credential share the consuming read or mutation transaction.
            SurrealQuery.Of("""
                IF $_tenant_access_child = NONE OR $_tenant_access_child = [] OR $_tenant_access_member = NONE OR $_tenant_access_member = []
                    OR $_tenant_access_grant = NONE OR $_tenant_access_grant = []
                    OR ($_tenant_access_grant.expires_at != NONE AND $_tenant_access_grant.expires_at <= time::now())
                    { THROW 'TENANT_CUSTODIAL_AUTHORITY_CHANGED' }
                """),
        ];
    }

    /// <summary>Claimed member: still unlinked at the same watermark, and the exact UserExplicit grant version still live.</summary>
    private static IReadOnlyList<SurrealQuery>? ClaimedGuards(TenantCustodialWitness witness)
    {
        var credential = witness.Authority.Credential;
        var tenant = SurrealLink.ToLink("user", credential.ActorUserId.ToString("N"))!;
        var child = SurrealLink.ToLink("user", witness.UserId.ToString("N"))!;
        if (witness.MembershipId is not null || witness.MembershipVersion is not null || witness.AuthNotBefore is not { } watermark
            || !TenantChildCorrelation.IsClaimed(witness.UserId, null, witness.ExternalUserId, claimWatermarked: true, credential.ActorUserId)
            || witness.GrantCommandId.Length < 32 || SurrealRecordGuid.BareId(witness.GrantId) != witness.GrantCommandId[..32]) return null;
        return
        [
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(witness.UserId.ToString("N"))
                .Where(row => row.IsActive && row.OwnerTenantId == null && row.ExternalUserId == witness.ExternalUserId
                    && row.AuthNotBefore == watermark).Set(row => row.IsActive, true).Build(), "tenant_claimed_child", "_tenant_claimed_child"),
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ConsentGrant>(SurrealRecordGuid.BareId(witness.GrantId))
                .Where(row => row.Version == witness.GrantVersion && row.RevokedAt == null && row.GrantorUserId == child && row.TenantId == tenant
                    && row.Scopes == witness.GrantScopes && row.ExpiresAt == witness.GrantExpiresAt && row.Origin == UserExplicitOrigin
                    && row.ParticipationRef == null && row.CommandId == witness.GrantCommandId && row.AdmissionReceiptJson == witness.GrantReceiptJson)
                .Set(row => row.Version, witness.GrantVersion).Build(), "tenant_claimed_grant", "_tenant_claimed_grant"),
            // raw: the unlinked member and the exact live UserExplicit grant version share the consuming transaction.
            SurrealQuery.Of("""
                IF $_tenant_claimed_child = NONE OR $_tenant_claimed_child = [] OR $_tenant_claimed_grant = NONE OR $_tenant_claimed_grant = []
                    OR ($_tenant_claimed_grant.expires_at != NONE AND $_tenant_claimed_grant.expires_at <= time::now())
                    { THROW 'TENANT_CUSTODIAL_AUTHORITY_CHANGED' }
                """),
        ];
    }
}
