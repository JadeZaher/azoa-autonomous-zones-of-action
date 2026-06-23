using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="IWallet"/> aggregates.</summary>
public interface IWalletStore
{
    /// <summary>Loads a single wallet by id.</summary>
    Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads every wallet.</summary>
    Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Loads all wallets owned by an avatar.</summary>
    Task<AZOAResult<IEnumerable<IWallet>>> GetByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>Inserts or updates a wallet.</summary>
    Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default);

    /// <summary>Deletes a wallet by id.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
