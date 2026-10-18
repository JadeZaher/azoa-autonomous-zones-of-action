using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Blockchain.ClientSigning;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// One in-memory <c>operation_log</c> behind both the generic operation store and the client-signing CAS store, mirroring
/// the production status compare-and-swap (<c>SurrealOperationLogClientSigningStore</c>).
/// </summary>
internal sealed class InMemoryOperationLog : IBlockchainOperationStore, IClientSignedOperationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, BlockchainOperation> _rows = new();

    internal int Transitions { get; private set; }

    /// <summary>Runs inside the CAS before it commits, standing in for a concurrent writer.</summary>
    internal Action<Guid>? BeforeTransition { get; set; }

    internal BlockchainOperation Row(Guid id)
    {
        lock (_sync) return Copy(_rows[id]);
    }

    public Task<AZOAResult<IBlockchainOperation>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_rows.TryGetValue(id, out var row)
                ? AZOAResult<IBlockchainOperation>.Success(Copy(row))
                : AZOAResult<IBlockchainOperation>.Failure("Operation not found."));
    }

    public Task<AZOAResult<IBlockchainOperation>> UpsertAsync(IBlockchainOperation operation, CancellationToken ct = default)
    {
        lock (_sync)
        {
            _rows[operation.Id] = Copy((BlockchainOperation)operation);
            return Task.FromResult(AZOAResult<IBlockchainOperation>.Success(Copy(_rows[operation.Id])));
        }
    }

    public Task<bool> TryTransitionAsync(Guid operationId, string expectedStatus, string nextStatus,
        IReadOnlyDictionary<string, string> parameters, DateTimeOffset? completedAt, CancellationToken ct = default)
    {
        BeforeTransition?.Invoke(operationId);
        lock (_sync)
        {
            if (!_rows.TryGetValue(operationId, out var row) || row.Status != expectedStatus) return Task.FromResult(false);
            row.Status = nextStatus;
            row.Parameters = new Dictionary<string, string>(parameters);
            row.CompletedDate = completedAt?.UtcDateTime;
            Transitions++;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<Guid>> ListStuckAsync(DateTimeOffset awaitingBefore, int limit, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<Guid>>(_rows.Values
                .Where(row => row.Status is (OperationStatus.AwaitingSignature or OperationStatus.PendingConfirmation) && row.CreatedDate < awaitingBefore.UtcDateTime
                    || row.Status == ClientSigningStatus.NeedsOperator)
                .OrderBy(row => row.CreatedDate).Take(limit).Select(row => row.Id).ToArray());
    }

    public Task<AZOAResult<IBlockchainOperation>> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AZOAResult<IReadOnlyList<IBlockchainOperation>>> QueryAsync(BlockchainOperationQuery query, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

    private static BlockchainOperation Copy(BlockchainOperation row) => new()
    {
        Id = row.Id, UserId = row.UserId, WalletId = row.WalletId, IdempotencyKey = row.IdempotencyKey,
        InitiatorUserId = row.InitiatorUserId, InitiatorApiKeyId = row.InitiatorApiKeyId, ActingTenantId = row.ActingTenantId,
        SigningScope = row.SigningScope, OperationType = row.OperationType, Status = row.Status,
        Parameters = new Dictionary<string, string>(row.Parameters), CreatedDate = row.CreatedDate, CompletedDate = row.CompletedDate,
        TokenUri = row.TokenUri, Amount = row.Amount, AssetType = row.AssetType, ValueStamp = row.ValueStamp,
        SourceHolonId = row.SourceHolonId, TargetHolonId = row.TargetHolonId, ExchangeRate = row.ExchangeRate,
        RecipientAddress = row.RecipientAddress,
    };
}
