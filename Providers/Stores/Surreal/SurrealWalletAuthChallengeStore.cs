using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using System.Security.Cryptography;
using System.Text.Json;
using WalletAuthChallenge = AZOA.WebAPI.Models.WalletAuthChallenge;
using WalletAuthChallengePoco = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthChallenge;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWalletAuthChallengeStore(ISurrealExecutor executor) : IWalletAuthChallengeStore
{
    private sealed record Slot(string Nonce, DateTimeOffset ExpiresAt);

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthChallenge>> CreateAsync(WalletAuthChallenge challenge, CancellationToken ct = default)
    {
        if (!await WalletAuthIdentityGate.IsReadyAsync(executor, ct))
            return AZOAResult<WalletAuthChallenge>.FailureWithCode("Wallet identity migration is not ready.", AzoaErrorCodes.DependencyUnavailable);
        if (!WalletAuthIdentity.TryCreate(challenge.Address, challenge.ChainType, out var address, out var chain)
            || address != challenge.Address || chain != challenge.ChainType)
            return AZOAResult<WalletAuthChallenge>.Failure("A canonical wallet identity is required.");
        if (!await ReserveAsync(challenge, ct))
            return AZOAResult<WalletAuthChallenge>.FailureWithCode("Too many active challenges for this wallet. Try again shortly.", AzoaErrorCodes.Conflict);
        if (challenge.Id == Guid.Empty) challenge.Id = Guid.NewGuid();
        var response = await executor.ExecuteAsync(SurrealWriter.Create(FromDomain(challenge)), ct);
        response.EnsureAllOk();
        return AZOAResult<WalletAuthChallenge>.Success(ToDomain(response.GetValues<WalletAuthChallengePoco>(0).Single()));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthChallenge>> GetByNonceAsync(string nonce, CancellationToken ct = default)
    {
        var row = await executor.QuerySingleAsync<WalletAuthChallengePoco>(
            SurrealQuery<WalletAuthChallengePoco>.From().Where(row => row.Nonce == nonce).Limit(1), ct);
        return new AZOAResult<WalletAuthChallenge> { Result = row is null ? null : ToDomain(row) };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthChallenge>> GetLatestLiveByAddressAsync(string address, string chainType, DateTime now, CancellationToken ct = default)
    {
        if (!await WalletAuthIdentityGate.IsReadyAsync(executor, ct))
            return AZOAResult<WalletAuthChallenge>.FailureWithCode("Wallet identity migration is not ready.", AzoaErrorCodes.DependencyUnavailable);
        var instant = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        var row = await executor.QuerySingleAsync<WalletAuthChallengePoco>(SurrealQuery<WalletAuthChallengePoco>.From()
            .Where(row => row.Address == address && row.ChainType == chainType && row.ConsumedAt == null && row.ExpiresAt > instant)
            .OrderByDescending(row => row.CreatedAt).Limit(1), ct);
        return new AZOAResult<WalletAuthChallenge> { Result = row is null ? null : ToDomain(row) };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TryConsumeAsync(string nonce, DateTime now, CancellationToken ct = default)
    {
        if (!await WalletAuthIdentityGate.IsReadyAsync(executor, ct)) return AZOAResult<bool>.Success(false);
        var found = await GetByNonceAsync(nonce, ct);
        if (found.Result is null) return AZOAResult<bool>.Success(false);
        var instant = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WalletAuthChallengePoco>(SurrealId.ToSurrealId(found.Result.Id))
            .Where(row => row.Nonce == nonce && row.ConsumedAt == null && row.ExpiresAt > instant)
            .Set(row => row.ConsumedAt, instant).Build(), ct);
        response.EnsureAllOk();
        return AZOAResult<bool>.Success(response.GetValues<WalletAuthChallengePoco>(0).Count() == 1);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<int>> CountLiveByAddressAsync(string address, string chainType, DateTime now, CancellationToken ct = default)
    {
        var instant = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        var rows = await executor.QueryAsync<WalletAuthChallengePoco>(SurrealQuery<WalletAuthChallengePoco>.From()
            .Where(row => row.Address == address && row.ChainType == chainType && row.ConsumedAt == null && row.ExpiresAt > instant), ct);
        return AZOAResult<int>.Success(rows.Count);
    }

    private async Task<bool> ReserveAsync(WalletAuthChallenge challenge, CancellationToken ct)
    {
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{challenge.ChainType}:{challenge.Address}"))).ToLowerInvariant();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var row = await executor.QuerySingleAsync<WalletAuthChallengeBudget>(SurrealQuery<WalletAuthChallengeBudget>.Key(id), ct);
            if (row is null)
            {
                try
                {
                    await SurrealTransientConflict.RetryOnConflictAsync(async () =>
                    {
                        var creation = await executor.ExecuteAsync(SurrealWriter.Create(new WalletAuthChallengeBudget { Id = id }), ct);
                        creation.EnsureAllOk();
                        return true;
                    }, ct);
                }
                catch (SurrealStatementException ex) when (SurrealIdempotencyLedger.IsUniqueViolation(ex.Message)) { }
                continue;
            }
            var slots = JsonSerializer.Deserialize<List<Slot>>(row.SlotsJson)!;
            var now = DateTimeOffset.UtcNow;
            var legacy = await executor.QueryAsync<WalletAuthChallengePoco>(SurrealQuery<WalletAuthChallengePoco>.From()
                .Where(item => item.Address == challenge.Address && item.ChainType == challenge.ChainType && item.ConsumedAt == null && item.ExpiresAt > now)
                .Limit(5), ct);
            foreach (var old in legacy)
                if (slots.All(slot => slot.Nonce != old.Nonce)) slots.Add(new(old.Nonce, old.ExpiresAt));
            var live = new List<Slot>();
            foreach (var slot in slots.Where(item => item.ExpiresAt > DateTimeOffset.UtcNow))
            {
                var existing = await GetByNonceAsync(slot.Nonce, ct);
                if (existing.Result?.ConsumedAt is null) live.Add(slot);
            }
            if (live.Count >= 5) return false;
            live.Add(new(challenge.Nonce, new DateTimeOffset(DateTime.SpecifyKind(challenge.ExpiresAt, DateTimeKind.Utc))));
            // A contended CAS can surface SurrealDB 3.x's retryable conflict; the retried loser misses its version guard and re-reads.
            var won = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WalletAuthChallengeBudget>(id)
                    .Where(item => item.Version == row.Version).Set(item => item.SlotsJson, JsonSerializer.Serialize(live))
                    .Set(item => item.Version, row.Version + 1).Build(), ct);
                result.EnsureAllOk();
                return result.GetValues<WalletAuthChallengeBudget>(0).Any();
            }, ct);
            if (won) return true;
        }
        return false;
    }

    private static WalletAuthChallengePoco FromDomain(WalletAuthChallenge c) => new()
    {
        Id            = SurrealId.ToSurrealId(c.Id),
        Nonce         = c.Nonce,
        Address       = c.Address,
        ChainType     = c.ChainType,
        DomainMessage = c.DomainMessage,
        ExpiresAt     = new DateTimeOffset(DateTime.SpecifyKind(c.ExpiresAt, DateTimeKind.Utc)),
        ConsumedAt    = c.ConsumedAt.HasValue
                        ? new DateTimeOffset(DateTime.SpecifyKind(c.ConsumedAt.Value, DateTimeKind.Utc))
                        : null,
        CreatedAt     = new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc)),
    };

    private static WalletAuthChallenge ToDomain(WalletAuthChallengePoco p) => new()
    {
        Id            = SurrealId.FromSurrealId(p.Id),
        Nonce         = p.Nonce,
        Address       = p.Address,
        ChainType     = p.ChainType,
        DomainMessage = p.DomainMessage,
        ExpiresAt     = p.ExpiresAt.UtcDateTime,
        ConsumedAt    = p.ConsumedAt?.UtcDateTime,
        CreatedAt     = p.CreatedAt.UtcDateTime,
    };
}
