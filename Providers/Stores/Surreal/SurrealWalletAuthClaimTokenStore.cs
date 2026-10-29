using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using WalletAuthClaimTokenPoco = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthClaimToken;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWalletAuthClaimTokenStore(ISurrealExecutor executor) : IWalletAuthClaimTokenStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthClaimToken>> CreateAsync(WalletAuthClaimToken token, CancellationToken ct = default)
    {
        if (!OpaqueTokenDigest.IsV1Digest(token.TokenHash))
            return AZOAResult<WalletAuthClaimToken>.Failure("A claim invite persists only the v1 digest of its token.");
        if (token.Id == Guid.Empty) token.Id = Guid.NewGuid();
        var result = await executor.ExecuteAsync(SurrealWriter.Create(FromDomain(token)), ct);
        result.EnsureAllOk();
        return AZOAResult<WalletAuthClaimToken>.Success(ToDomain(result.GetValues<WalletAuthClaimTokenPoco>(0).Single()));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthClaimToken>> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        if (!OpaqueTokenDigest.IsV1Digest(tokenHash)) return new AZOAResult<WalletAuthClaimToken>();
        var query = SurrealGeneratedStatementScope.Apply(SurrealQuery<WalletAuthClaimTokenPoco>.From()
            .Where(row => row.Token == tokenHash).Limit(1), "claim_lookup");
        var row = await executor.QuerySingleAsync<WalletAuthClaimTokenPoco>(query, ct);
        return new AZOAResult<WalletAuthClaimToken> { Result = row is null ? null : ToDomain(row) };
    }

    private static WalletAuthClaimTokenPoco FromDomain(WalletAuthClaimToken t) => new()
    {
        Id             = SurrealId.ToSurrealId(t.Id),
        Token          = t.TokenHash,
        TargetUserId = SurrealLink.ToLink("user", SurrealId.ToSurrealId(t.TargetUserId)) ?? string.Empty,
        TenantId       = SurrealLink.ToLink("user", SurrealId.ToSurrealId(t.TenantId)) ?? string.Empty,
        ExpiresAt      = new DateTimeOffset(DateTime.SpecifyKind(t.ExpiresAt, DateTimeKind.Utc)),
        ConsumedAt     = t.ConsumedAt.HasValue
                         ? new DateTimeOffset(DateTime.SpecifyKind(t.ConsumedAt.Value, DateTimeKind.Utc))
                         : null,
        CreatedAt      = new DateTimeOffset(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
    };

    private static WalletAuthClaimToken ToDomain(WalletAuthClaimTokenPoco p) => new()
    {
        Id             = SurrealId.FromSurrealId(p.Id),
        TokenHash      = p.Token,
        TargetUserId = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TargetUserId)!),
        TenantId       = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        ExpiresAt      = p.ExpiresAt.UtcDateTime,
        ConsumedAt     = p.ConsumedAt?.UtcDateTime,
        CreatedAt      = p.CreatedAt.UtcDateTime,
    };
}
