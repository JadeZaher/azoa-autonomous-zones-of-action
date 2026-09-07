using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// A bounded window over one user's operation history, shaped to the
/// <c>operation_log_user_created</c> index: equality on the user, a range on the
/// creation date, ordered newest first.
/// </summary>
public sealed record BlockchainOperationQuery
{
    /// <summary>The subject whose history is read. Never caller-supplied on a self route.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Inclusive lower bound on the creation date; null means unbounded.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper bound on the creation date; null means unbounded.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Rows to skip. Must be non-negative.</summary>
    public int Skip { get; init; }

    /// <summary>Rows to return. Must be positive and within the store's page cap.</summary>
    public required int Take { get; init; }
}

/// <summary>Persistence boundary for <see cref="IBlockchainOperation"/> aggregates.</summary>
public interface IBlockchainOperationStore
{
    /// <summary>Loads a single blockchain operation by id.</summary>
    Task<AZOAResult<IBlockchainOperation>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads a single operation by its durable, public-safe idempotency correlation.
    /// A missing correlation returns an error result; callers must separately enforce
    /// their initiator-ownership boundary before exposing the operation.
    /// </summary>
    Task<AZOAResult<IBlockchainOperation>> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    /// <summary>Loads all blockchain operations for a user.</summary>
    Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads one page of a user's operations within a date window, newest first.
    /// <see cref="GetByUserAsync"/> returns everything unbounded, which cannot serve a
    /// history surface: a long-lived account's whole log would be materialised per request.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<IBlockchainOperation>>> QueryAsync(
        BlockchainOperationQuery query,
        CancellationToken ct = default);

    /// <summary>Inserts or updates a blockchain operation.</summary>
    Task<AZOAResult<IBlockchainOperation>> UpsertAsync(IBlockchainOperation operation, CancellationToken ct = default);

    /// <summary>Deletes a blockchain operation by id.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
