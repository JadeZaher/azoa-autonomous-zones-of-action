using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Captures actual tenant authority and an independent participant wallet grant.</summary>
public sealed class ExchangeAuthorityFactory(SigningAdmissionAuthorityFactory signing, IUserStore users,
    IWalletStore wallets, ISurrealExecutor executor)
{
    public async Task<AZOAResult<ExchangeParticipantAuthority>> BindAsync(SigningCredentialWitness credential,
        Guid tenantId, Guid participantId, Guid walletId, string chain, string address, CancellationToken ct = default)
    {
        if (credential.Kind != "ApiKey" || credential.TenantId is not null || credential.ActorUserId != tenantId
            || tenantId == Guid.Empty || participantId == Guid.Empty || walletId == Guid.Empty) return Refuse();
        var principal = await signing.BindConsentAsync(credential, [AzoaScopes.SwapSign], ct);
        if (principal.IsError || principal.Result is null) return Refuse();
        var participant = await users.GetByIdAsync(participantId, ct);
        var wallet = await wallets.GetByIdAsync(walletId, ct);
        if (participant.IsError || participant.Result is not { IsActive: true } user || wallet.IsError
            || wallet.Result is not { } account || account.UserId != participantId
            || !account.ChainType.Equals(chain, StringComparison.OrdinalIgnoreCase)
            || !ChainAddressIdentity.Equivalent(chain, account.Address, address)) return Refuse();
        var grantor = SurrealLink.ToLink("user", participantId.ToString("N"))!;
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
        var now = DateTimeOffset.UtcNow;
        var candidates = await executor.QueryAsync<GrantRow>(SurrealQuery<GrantRow>.From()
            .Where(row => row.GrantorUserId == grantor && row.TenantId == tenant
                && row.RevokedAt == null && (row.ExpiresAt == null || row.ExpiresAt > now)).OrderBy(row => row.Id), ct);
        // An explicit swap:sign grant wins; otherwise the tenant-provisioned implied grant (Core/AGENTS.md §consent-scope-coverage).
        var selected = candidates.FirstOrDefault(row => row.Origin == "UserExplicit"
                && ConsentScopeCoverage.SplitScopes(row.Scopes).Contains(AzoaScopes.SwapSign, StringComparer.Ordinal))
            ?? candidates.FirstOrDefault(row => ConsentScopeCoverage.IsTenantProvisioned(row.Origin, row.ParticipationRef)
                && ConsentScopeCoverage.Covers(row.Origin, row.ParticipationRef, row.Scopes, AzoaScopes.SwapSign));
        if (selected is null) return Refuse();
        var participationRef = selected.Origin == "UserExplicit" ? null : selected.ParticipationRef;
        var watermark = user.AuthNotBefore is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        return AZOAResult<ExchangeParticipantAuthority>.Success(new(1, principal.Result, participantId, watermark,
            new(walletId, participantId, account.Address, account.ChainType),
            new(SurrealRecordGuid.Parse(selected.Id), AzoaScopes.SwapSign, selected.Scopes, selected.GrantedAt, selected.ExpiresAt,
                participationRef)));
    }

    private static AZOAResult<ExchangeParticipantAuthority> Refuse()
        => AZOAResult<ExchangeParticipantAuthority>.Failure("The actual tenant credential and exact participant wallet consent are required.");
}
