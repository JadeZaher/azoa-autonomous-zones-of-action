using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Consent;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using Membership = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentParticipationBoundary;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealWalletStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> BootstrapTenantAsync(IWallet wallet, string externalUserId,
        SigningConsentWitness authority, CancellationToken ct = default)
    {
        const string changed = "TENANT_BOOTSTRAP_CHANGED";
        var credential = authority.Credential;
        var guards = SigningAuthorityFence.ForConsent(authority,
            [AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.KycRead], "tenant_wallet");
        if (guards is null || credential.TenantId is not null || wallet.UserId == Guid.Empty
            || string.IsNullOrWhiteSpace(externalUserId) || externalUserId.Trim().Length > 128
            || string.IsNullOrWhiteSpace(wallet.Address) || string.IsNullOrWhiteSpace(wallet.EncryptedPrivateKey)
            || wallet.WalletType != WalletType.Platform || WalletBootstrapIdentity.CanonicalChain(wallet.ChainType) is not { } chain
            || wallet.Id != WalletBootstrapIdentity.For(wallet.UserId, chain))
            return AZOAResult<IWallet>.Failure("Current tenant wallet provisioning authority is required.");
        var tenant = SurrealLink.ToLink("user", credential.ActorUserId.ToString("N"))!;
        var childId = wallet.UserId.ToString("N");
        var childLink = SurrealLink.ToLink("user", childId)!;
        var child = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(childId), ct);
        var external = externalUserId.Trim();
        if (child is not { IsActive: true } || child.OwnerTenantId != tenant || child.ExternalUserId != external)
            return AZOAResult<IWallet>.Failure("The account is no longer owned by this tenant.");
        var reference = TenantProvisionedGrant.ParticipationRefFor(credential.ActorUserId, external);
        var membershipId = ConsentLifecycleIdentity.ParticipationId(credential.ActorUserId, reference);
        var membership = await executor.QuerySingleAsync<Membership>(SurrealQuery<Membership>.Key(membershipId), ct);
        if (membership is null || membership.ClosedAt is not null || membership.TenantId != credential.ActorUserId.ToString("N")
            || membership.ParticipationRef != reference)
            return AZOAResult<IWallet>.Failure("Provisioning membership is closed or unavailable.");
        var commandId = TenantProvisionedGrant.CommandIdFor(credential.ActorUserId, wallet.UserId, external);
        var grant = await executor.QuerySingleAsync<Grant>(SurrealQuery<Grant>.Key(commandId[..32]), ct);
        if (grant is null || grant.GrantorUserId != childLink || grant.TenantId != tenant || grant.Origin != "Participation"
            || grant.ParticipationRef != reference || grant.RevokedAt is not null || grant.AdmissionReceiptJson is null || grant.CommandId != commandId
            || !grant.Scopes.Split(',').Contains(AzoaScopes.WalletManage, StringComparer.Ordinal)
            || !grant.Scopes.Split(',').Contains(AzoaScopes.KycRead, StringComparer.Ordinal))
            return AZOAResult<IWallet>.Failure("Live provisioning consent for wallet management is required.");
        var id = wallet.Id.ToString("N");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(id), ct);
            if (existing is not null && (existing.UserId != childLink || existing.ChainType != chain || existing.WalletType != Row.WalletTypeKind.Platform))
                return AZOAResult<IWallet>.Failure("The wallet identity is already bound to another wallet.");
            var defaults = await executor.QueryAsync<Row>(SurrealQuery<Row>.From()
                .Where(row => row.UserId == childLink && row.ChainType == chain && row.IsDefault).Limit(1), ct);
            var candidate = ToPoco(wallet);
            candidate.IsDefault = wallet.IsDefault && defaults.Count == 0;
            // raw: current actor, child ownership, membership, consent and create-only wallet share one authority transaction.
            var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
            statements.AddRange(guards);
            statements.Add(LocalEntityMutationFence.Wallets());
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<User>(childId)
                .Where(row => row.IsActive && row.OwnerTenantId == tenant && row.ExternalUserId == external && row.AuthNotBefore == child.AuthNotBefore)
                .Set(row => row.IsActive, true).Build(), "bootstrap_child", "_bootstrap_child"));
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Membership>(membershipId)
                .Where(row => row.Version == membership.Version && row.ClosedAt == null)
                .Set(row => row.Version, membership.Version).Build(), "bootstrap_member", "_bootstrap_member"));
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Grant>(SurrealRecordGuid.BareId(grant.Id))
                .Where(row => row.Version == grant.Version && row.RevokedAt == null && row.GrantorUserId == childLink
                    && row.TenantId == tenant && row.Scopes == grant.Scopes && row.ParticipationRef == reference && row.Origin == "Participation"
                    && row.ExpiresAt == grant.ExpiresAt && row.CommandId == grant.CommandId && row.AdmissionReceiptJson == grant.AdmissionReceiptJson)
                .Set(row => row.Version, grant.Version).Build(), "bootstrap_grant", "_bootstrap_grant"));
            statements.Add(SurrealQuery.Of("""
                IF $_bootstrap_child = NONE OR $_bootstrap_child = [] OR $_bootstrap_member = NONE OR $_bootstrap_member = []
                    OR $_bootstrap_grant = NONE OR $_bootstrap_grant = []
                    OR ($_bootstrap_grant.expires_at != NONE AND $_bootstrap_grant.expires_at <= time::now())
                    { THROW 'TENANT_BOOTSTRAP_CHANGED' }
                """));
            if (existing is null)
            {
                if (candidate.IsDefault)
                {
                    statements.Add(SurrealGeneratedStatementScope.Assign(SurrealQuery<Row>.From()
                        .Where(row => row.UserId == childLink && row.ChainType == chain && row.IsDefault).Limit(1), "bootstrap_defaults", "_bootstrap_defaults"));
                    statements.Add(SurrealQuery.Of("IF array::len($_bootstrap_defaults) != 0 { THROW 'TENANT_BOOTSTRAP_CHANGED' }"));
                }
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.Create(candidate), "bootstrap_create", "_bootstrap_wallet"));
            }
            else
            {
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Row>(id)
                    .Where(row => row.UserId == childLink && row.ChainType == chain && row.WalletType == Row.WalletTypeKind.Platform
                        && row.Address == existing.Address && row.EncryptedPrivateKey == existing.EncryptedPrivateKey && row.IsDefault == existing.IsDefault)
                    .Set(row => row.IsDefault, existing.IsDefault).Build(), "bootstrap_existing", "_bootstrap_wallet"));
                statements.Add(SurrealQuery.Of("IF $_bootstrap_wallet = NONE OR $_bootstrap_wallet = [] { THROW 'TENANT_BOOTSTRAP_CHANGED' }"));
            }
            statements.Add(SurrealQuery.Of("RETURN $_bootstrap_wallet"));
            statements.Add(SurrealQuery.Of("COMMIT"));
            var query = SurrealQuery.Combine(statements.ToArray());
            try
            {
                var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
                {
                    var result = await executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
                }, ct);
                return AZOAResult<IWallet>.Success(FromPoco(response.GetValues<Row>(response.Statements.Count - 2).Single()));
            }
            catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
            {
                if (attempt != 0) throw;
            }
            catch (SurrealStatementException exception) when (exception.Message.Contains(changed, StringComparison.Ordinal)
                || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
            {
                return AZOAResult<IWallet>.Failure("Tenant ownership or provisioning consent changed concurrently.");
            }
        }
        return AZOAResult<IWallet>.Failure("The wallet could not be provisioned under current authority.");
    }
}
