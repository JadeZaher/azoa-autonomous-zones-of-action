using SurrealForge.Client;
using SurrealForge.Client.Query;
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
        if (token.Id == Guid.Empty) token.Id = Guid.NewGuid();
        var result = await executor.ExecuteAsync(SurrealWriter.Create(FromDomain(token)), ct);
        result.EnsureAllOk();
        return AZOAResult<WalletAuthClaimToken>.Success(ToDomain(result.GetValues<WalletAuthClaimTokenPoco>(0).Single()));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthClaimToken>> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        var claimToken = token;
        var row = await executor.QuerySingleAsync<WalletAuthClaimTokenPoco>(SurrealQuery<WalletAuthClaimTokenPoco>.From()
            .Where(row => row.Token == claimToken).Limit(1), ct);
        return new AZOAResult<WalletAuthClaimToken> { Result = row is null ? null : ToDomain(row) };
    }

    private static WalletAuthClaimTokenPoco FromDomain(WalletAuthClaimToken t) => new()
    {
        Id             = SurrealId.ToSurrealId(t.Id),
        Token          = t.Token,
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
        Token          = p.Token,
        TargetUserId = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TargetUserId)!),
        TenantId       = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        ExpiresAt      = p.ExpiresAt.UtcDateTime,
        ConsumedAt     = p.ConsumedAt?.UtcDateTime,
        CreatedAt      = p.CreatedAt.UtcDateTime,
    };
}
