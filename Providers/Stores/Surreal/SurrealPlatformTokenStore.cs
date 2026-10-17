using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Signing;
using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealPlatformTokenStore(ISurrealExecutor executor) : IPlatformTokenStore
{
    /// <inheritdoc/>
    public async Task<bool> HasVerifiedIssuanceAsync(PlatformToken row, CancellationToken ct = default)
    {
        if (!row.IsActive || string.IsNullOrWhiteSpace(row.CreationOperationId)) return false;
        var operation = await executor.QuerySingleAsync<FungibleCreationOperation>(SurrealQuery<FungibleCreationOperation>.Key(row.CreationOperationId), ct);
        if (operation is not { State: "Completed" } || operation.AssetId != row.AssetId) return false;
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(operation.RequestJson);
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(operation.IntentJson);
        var proof = operation.ObservationJson is null ? null : JsonSerializer.Deserialize<FungibleCreationObservation>(operation.ObservationJson);
        if (command?.GenesisAuthority is not { } purpose || intent is null || proof is not { Verdict: "Confirmed" }
            || !PlatformGenesisIdentity.Matches(command, purpose) || CanonicalJson.Hash(command) != operation.RequestHash
            || operation.Id != FungibleCreationIdentity.OperationId(command.ActorUserId, null, command.RequestKey)
            || operation.ActorUserId != command.ActorUserId.ToString("N") || operation.TenantId is not null
            || SurrealRecordGuid.ParseOptional(row.GenesisUserId) != command.ActorUserId
            || intent.Generation != operation.Generation
            || FungibleCreationIdentity.IntentHash(intent with { Generation = 0 }) != FungibleCreationIdentity.IntentHash(command.Intent)
            || purpose.PlatformTokenId != row.Id || purpose.GenesisOperationKey != row.GenesisOperationKey
            || command.Intent.ChainType != row.Chain || command.Intent.Network.ToString() != row.Network
            || command.Intent.Name != row.Name || command.Intent.UnitName != row.UnitName || command.Intent.Decimals != row.Decimals
            || command.Intent.TotalBaseUnits != row.TotalSupply || command.Intent.CreatorAddress != row.ReserveAddress
            || proof.AssetId != row.AssetId || proof.ProtocolIdentity != purpose.ProtocolIdentity) return false;
        var projection = await executor.QuerySingleAsync<FungibleCreationProjection>(SurrealQuery<FungibleCreationProjection>.Key(operation.Id), ct);
        if (projection is null || projection.RequestHash != operation.RequestHash || projection.InitialAttachmentState != "NotRequested") return false;
        var authority = await executor.QuerySingleAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.Key(projection.AuthorityId), ct);
        var reserve = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(PlatformReserveLedger.RecordIdFor(row.Chain, row.Network, row.AssetId!)), ct);
        return authority is not null && reserve is not null && authority.OperationId == operation.Id && authority.AssetId == row.AssetId
            && authority.IntentJson == operation.IntentJson && authority.IntentHash == CanonicalJson.Hash(intent)
            && authority.IssuerUserId == operation.ActorUserId && authority.TenantId is null && authority.CreatorAddress == row.ReserveAddress
            && authority.ChainType == row.Chain && authority.Network == row.Network && authority.ProtocolIdentity == purpose.ProtocolIdentity
            && authority.CreationTransactionId == proof.TransactionId && authority.EvidenceJson == proof.EvidenceJson
            && !string.IsNullOrWhiteSpace(authority.EvidenceJson) && reserve.AssetId == row.AssetId && reserve.Chain == row.Chain
            && reserve.Network == row.Network && reserve.ProtocolIdentity == purpose.ProtocolIdentity && reserve.ReserveAddress == row.ReserveAddress
            && reserve.TotalSupply == row.TotalSupply && reserve.Decimals == row.Decimals && reserve.UnitName == row.UnitName && reserve.TenantId is null;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<PlatformToken>>> ListForNetworkAsync(string network, CancellationToken ct = default)
        => AZOAResult<IReadOnlyList<PlatformToken>>.Success(await executor.QueryAsync<PlatformToken>(
            SurrealQuery<PlatformToken>.From().Where(row => row.Network == network), ct));

    /// <inheritdoc/>
    public async Task<AZOAResult<PlatformTokenClaim>> ClaimIfAbsentAsync(PlatformToken row, CancellationToken ct = default, SigningCredentialWitness? credential = null)
    {
        if (row.Id != PlatformToken.RecordIdFor(row.Chain, row.Network) || row.Status != PlatformToken.StatusPending
            || row.AssetId is not null || row.CreationOperationId is not null || row.Version != 0)
            return AZOAResult<PlatformTokenClaim>.Failure("A fresh frozen platform definition is required.");
        var authority = credential is null ? null : PlatformGenesisFence.Operator(credential);
        if (authority is null || SurrealRecordGuid.ParseOptional(row.GenesisUserId) != credential!.ActorUserId)
            return AZOAResult<PlatformTokenClaim>.Failure("A current recent node-operator credential must authorize the frozen definition.");
        row.GenesisUserId = SurrealLink.ToLink("user", SurrealRecordGuid.Parse(row.GenesisUserId).ToString("N"))!;
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN TRANSACTION") };
        statements.AddRange(authority);
        var createdIndex = statements.Count;
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(row), "genesis_claim"));
        statements.Add(SurrealQuery.Of("COMMIT TRANSACTION"));
        // raw: the unique immutable definition and current recent operator authorization commit together.
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(query, ct);
                result.EnsureAllOk();
                return result;
            }, ct);
            var created = response.GetValues<PlatformToken>(createdIndex).SingleOrDefault();
            return created is null ? AZOAResult<PlatformTokenClaim>.Failure("Genesis claim returned no durable definition.")
                : AZOAResult<PlatformTokenClaim>.Success(new(created, true));
        }
        catch (SurrealStatementException error) when (error.Message.Contains(PlatformGenesisFence.FailureMarker, StringComparison.Ordinal)
            || error.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return AZOAResult<PlatformTokenClaim>.Failure("The operator session changed before the genesis definition was claimed.");
        }
        catch (SurrealStatementException error) when (SurrealIdempotencyLedger.IsUniqueViolation(error.Message))
        {
            var prior = await executor.QuerySingleAsync<PlatformToken>(SurrealQuery<PlatformToken>.Key(row.Id), ct);
            return prior is null ? AZOAResult<PlatformTokenClaim>.Failure("A conflicting platform definition owns this network.")
                : AZOAResult<PlatformTokenClaim>.Success(new(prior, false));
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<PlatformToken>> UpdateIfVersionAsync(PlatformToken row, long expectedVersion, CancellationToken ct = default)
    {
        if (row.Status != PlatformToken.StatusPending || row.AssetId is not null)
            return AZOAResult<PlatformToken>.Failure("Platform activation belongs to finalized fungible projection.");
        var mutation = SurrealWriter.UpdateOnly<PlatformToken>(row.Id)
            .Where(current => current.Version == expectedVersion && current.Status == "Pending" && current.AssetId == null
                && current.GenesisOperationKey == row.GenesisOperationKey)
            .Set(current => current.Version, expectedVersion + 1).Set(current => current.UpdatedAt, row.UpdatedAt);
        mutation = row.LastFailureReason is null ? mutation.Unset(current => current.LastFailureReason)
            : mutation.Set(current => current.LastFailureReason, row.LastFailureReason);
        var query = mutation.Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        var saved = response.GetValues<PlatformToken>(0).SingleOrDefault();
        return saved is null ? AZOAResult<PlatformToken>.Failure("The platform definition changed before its pending diagnostic was saved.")
            : AZOAResult<PlatformToken>.Success(saved);
    }
}
