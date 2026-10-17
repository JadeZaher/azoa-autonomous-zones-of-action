using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using UserRow = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using WalletRow = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Fences both actual tenant credentials and the separately consenting exchange participant.</summary>
public static class ExchangeAuthorityFence
{
    public const string FailureMarker = "EXCHANGE_PARTICIPANT_AUTHORITY_CHANGED";

    public static IReadOnlyList<SurrealQuery>? Create(ExchangeParticipantAuthority authority, string prefix)
    {
        var credential = authority.Tenant.Credential;
        var grant = authority.Grant;
        if (authority.Version != 1 || credential.Kind != "ApiKey" || credential.TenantId is not null
            || authority.ParticipantUserId == Guid.Empty || authority.Wallet.Id == Guid.Empty
            || authority.Wallet.OwnerUserId != authority.ParticipantUserId || grant.Id == Guid.Empty
            || grant.RequiredScope != AzoaScopes.SwapSign
            || !grant.StoredScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(AzoaScopes.SwapSign, StringComparer.Ordinal)) return null;
        var tenantFence = SigningAuthorityFence.ForConsent(authority.Tenant, [AzoaScopes.SwapSign], prefix + "_tenant");
        if (tenantFence is null) return null;
        var participantId = authority.ParticipantUserId.ToString("N");
        var participantLink = SurrealLink.ToLink("user", participantId)!;
        var tenantLink = SurrealLink.ToLink("user", credential.ActorUserId.ToString("N"))!;
        // raw: actual tenant, participant watermark, wallet and exact selected grant locks join one source authorization.
        var statements = tenantFence.ToList();
        var user = SurrealWriter.UpdateOnly<UserRow>(participantId);
        user = authority.ParticipantAuthNotBefore is { } watermark
            ? user.Where(row => row.IsActive && row.AuthNotBefore == watermark)
            : user.Where(row => row.IsActive && row.AuthNotBefore == null);
        statements.Add(SurrealGeneratedStatementScope.Assign(user.Set(row => row.IsActive, true).Build(),
            prefix + "_participant", "_" + prefix + "_participant"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<WalletRow>(authority.Wallet.Id.ToString("N"))
            .Where(row => row.UserId == participantLink && row.ChainType == authority.Wallet.ChainType && row.Address == authority.Wallet.Address)
            .Set(row => row.Address, authority.Wallet.Address).Build(), prefix + "_wallet", "_" + prefix + "_wallet"));
        var selected = SurrealWriter.UpdateOnly<GrantRow>(grant.Id.ToString("N"));
        selected = grant.ExpiresAt is { } expiry
            ? selected.Where(row => row.GrantorUserId == participantLink && row.TenantId == tenantLink && row.Origin == "UserExplicit"
                && row.Scopes == grant.StoredScopes && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == expiry && row.RevokedAt == null)
            : selected.Where(row => row.GrantorUserId == participantLink && row.TenantId == tenantLink && row.Origin == "UserExplicit"
                && row.Scopes == grant.StoredScopes && row.GrantedAt == grant.GrantedAt && row.ExpiresAt == null && row.RevokedAt == null);
        statements.Add(SurrealGeneratedStatementScope.Assign(selected.Set(row => row.Scopes, grant.StoredScopes).Build(),
            prefix + "_grant", "_" + prefix + "_grant"));
        statements.Add(SurrealQuery.Of($"""
            IF $_{prefix}_participant = NONE OR $_{prefix}_participant = [] OR $_{prefix}_wallet = NONE OR $_{prefix}_wallet = []
                OR $_{prefix}_grant = NONE OR $_{prefix}_grant = [] {{ THROW 'EXCHANGE_PARTICIPANT_AUTHORITY_CHANGED' }}
            """));
        statements.Add(SurrealQuery.Of($"""
            IF $_{prefix}_grant.expires_at != NONE AND $_{prefix}_grant.expires_at <= time::now()
                {{ THROW 'EXCHANGE_PARTICIPANT_AUTHORITY_CHANGED' }}
            """));
        return statements;
    }
}
