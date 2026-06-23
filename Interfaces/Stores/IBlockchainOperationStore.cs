using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="IBlockchainOperation"/> aggregates.</summary>
public interface IBlockchainOperationStore
{
    /// <summary>Loads a single blockchain operation by id.</summary>
    Task<AZOAResult<IBlockchainOperation>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all blockchain operations for an avatar.</summary>
    Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>Inserts or updates a blockchain operation.</summary>
    Task<AZOAResult<IBlockchainOperation>> UpsertAsync(IBlockchainOperation operation, CancellationToken ct = default);

    /// <summary>Deletes a blockchain operation by id.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
