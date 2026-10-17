using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealEthereumPreparationStore(ISurrealExecutor executor) : IEthereumPreparationStore
{
    /// <inheritdoc/>
    public Task<EthereumPreparation?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<EthereumPreparation>(SurrealQuery<EthereumPreparation>.Key(id), ct);

    /// <inheritdoc/>
    public Task<EthereumPreparation?> GetHighestNonceAsync(string protocol, string sender, CancellationToken ct = default)
        => executor.QuerySingleAsync<EthereumPreparation>(SurrealQuery<EthereumPreparation>.From().Where(row => row.ProtocolIdentity == protocol && row.Sender == sender)
            .OrderByDescending(row => row.Nonce).Limit(1), ct);

    /// <inheritdoc/>
    public async Task<EthereumPreparation?> TryClaimAsync(EthereumPreparation candidate, CancellationToken ct = default)
    {
        try
        {
            var result = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            result.EnsureAllOk();
            return result.GetValues<EthereumPreparation>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return await GetAsync(candidate.Id, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<EthereumPreparation?> UpdateAsync(EthereumPreparation row, long expectedVersion, CancellationToken ct = default)
    {
        var update = SurrealWriter.UpdateOnly<EthereumPreparation>(row.Id).Where(item => item.Version == expectedVersion)
            .Set(item => item.State, row.State).Set(item => item.RecoveryCandidatesJson, row.RecoveryCandidatesJson)
            .Set(item => item.RecoveryAuditJson, row.RecoveryAuditJson).Set(item => item.Version, expectedVersion + 1)
            .Set(item => item.UpdatedAt, row.UpdatedAt);
        update = row.SignedTransactionBase64 is null ? update.Unset(item => item.SignedTransactionBase64) : update.Set(item => item.SignedTransactionBase64, row.SignedTransactionBase64);
        update = row.TransactionId is null ? update.Unset(item => item.TransactionId) : update.Set(item => item.TransactionId, row.TransactionId);
        var result = await executor.ExecuteAsync(update, ct);
        result.EnsureAllOk();
        return result.GetValues<EthereumPreparation>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EthereumPreparation>> ListOwnerAsync(string owner, int limit, CancellationToken ct = default)
        => executor.QueryAsync<EthereumPreparation>(SurrealQuery<EthereumPreparation>.From().Where(row => row.OwnerUserId == owner)
            .OrderByDescending(row => row.CreatedAt).Limit(Math.Clamp(limit, 1, 100)), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<EthereumPreparation>> ListAuthorizedAsync(string network, CancellationToken ct = default)
        => executor.QueryAsync<EthereumPreparation>(SurrealQuery<EthereumPreparation>.From()
            .Where(row => row.Network == network && row.State == "SubmissionAuthorized").OrderBy(row => row.UpdatedAt).Limit(50), ct);

    /// <inheritdoc/>
    public Task<EthereumSignerCheckpoint?> GetCheckpointAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<EthereumSignerCheckpoint>(SurrealQuery<EthereumSignerCheckpoint>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<EthereumSignerCheckpoint> ClaimCheckpointAsync(EthereumSignerCheckpoint candidate, CancellationToken ct = default)
    {
        try
        {
            var result = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            result.EnsureAllOk();
            return result.GetValues<EthereumSignerCheckpoint>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return await GetCheckpointAsync(candidate.Id, ct) ?? throw new InvalidOperationException("Ethereum activation checkpoint disappeared.");
        }
    }
}
