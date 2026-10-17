using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWttBodyClaimStore(ISurrealExecutor executor) : IWttBodyClaimStore
{
    /// <inheritdoc/>
    public Task<WttBodyClaim?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<WttBodyClaim>(SurrealQuery<WttBodyClaim>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<WttBodyClaim?> ClaimAsync(WttBodyClaim candidate, CancellationToken ct = default)
    {
        if (candidate.TargetProtocolChain is < 1 or > ushort.MaxValue
            || candidate.TargetPartition != WttBodyClaimIdentity.Partition(new WttDeploymentIdentity((ushort)candidate.TargetProtocolChain,
                candidate.TargetGenesisHex, candidate.TargetCoreBridgeHex, candidate.TargetTokenBridgeHex))
            || candidate.Id != WttBodyClaimIdentity.Id(candidate.TargetPartition, candidate.BodyDigest)
            || string.IsNullOrWhiteSpace(candidate.TargetProtocolIdentity) || string.IsNullOrWhiteSpace(candidate.SourceProtocolIdentity)
            || string.IsNullOrWhiteSpace(candidate.TargetCoreBridge) || string.IsNullOrWhiteSpace(candidate.TargetTokenBridge)
            || string.IsNullOrWhiteSpace(candidate.OperationId) || string.IsNullOrWhiteSpace(candidate.IntentHash)
            || string.IsNullOrWhiteSpace(candidate.ActorUserId) || candidate.EmitterChain is < 1 or > ushort.MaxValue
            || !UInt64Text.TryParse(candidate.Sequence, out _) || !WttBodyClaimIdentity.ValidPublication(candidate))
            throw new ArgumentException("WTT reservation requires exact immutable operation and publication identity.", nameof(candidate));
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            return response.GetValues<WttBodyClaim>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(candidate.Id, ct);
            return existing is not null && WttBodyClaimIdentity.SameOwner(existing, candidate) ? existing : null;
        }
    }
}
