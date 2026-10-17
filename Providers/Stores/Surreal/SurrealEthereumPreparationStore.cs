using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealEthereumPreparationStore(ISurrealExecutor executor) : IEthereumPreparationStore
{
    private const string ReleasedConflict = "ETHEREUM_RELEASED_NONCE_CONFLICT";

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
    public Task<IReadOnlyList<EthereumPreparation>> ListReleasedAsync(string protocol, string sender, int limit, CancellationToken ct = default)
        => executor.QueryAsync<EthereumPreparation>(SurrealQuery<EthereumPreparation>.From()
            .Where(row => row.ProtocolIdentity == protocol && row.Sender == sender && row.State == "Released")
            .OrderBy(row => row.Nonce).Limit(Math.Clamp(limit, 1, 50)), ct);

    /// <inheritdoc/>
    public async Task<EthereumPreparation?> TryClaimReleasedAsync(EthereumPreparation released, EthereumPreparation candidate, CancellationToken ct = default)
    {
        if (released.State != "Released" || released.SignedTransactionBase64 is not null || released.Nonce != candidate.Nonce ||
            released.Sender != candidate.Sender || released.ProtocolIdentity != candidate.ProtocolIdentity ||
            released.NonceSlot.Length != 0 || candidate.NonceSlot.Length != 0 || released.Id == candidate.Id) return null;
        // The released row stays as a tombstone: its id keeps the preparation-key lock, its own slot frees the live nonce.
        var tombstone = SurrealWriter.UpdateOnly<EthereumPreparation>(released.Id)
            .Where(row => row.Version == released.Version && row.State == "Released" && row.Nonce == released.Nonce && row.NonceSlot == "")
            .Set(row => row.State, "ReleasedRefilled").Set(row => row.NonceSlot, released.Id)
            .Set(row => row.Version, released.Version + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        // raw: retiring the unchanged released reservation and claiming its nonce commit together or not at all.
        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(tombstone, "released_guard", "released_nonce"),
            SurrealGeneratedStatementScope.RequireResult("released_nonce", ReleasedConflict),
            SurrealGeneratedStatementScope.Assign(SurrealWriter.Create(candidate), "released_claim", "released_successor"),
        };
        var resultIndex = statements.Count;
        statements.Add(SurrealQuery.Of("RETURN $released_successor"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var query = SurrealQuery.Combine(statements.ToArray());
            var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return result.GetValues<EthereumPreparation>(resultIndex).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message) ||
            exception.Message.Contains(ReleasedConflict, StringComparison.Ordinal))
        {
            return null;
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
        var result = await executor.ExecuteAsync(update.Build(), ct);
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
