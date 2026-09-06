using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for NFT aggregates: <see cref="IUserNFT"/> plus its
/// <see cref="IHolonNFTBinding"/> and <see cref="IWalletNFTBinding"/> bindings.
/// </summary>
public interface INftStore
{
    // User NFT
    /// <summary>Inserts or updates an user NFT.</summary>
    Task<AZOAResult<IUserNFT>> UpsertUserNFTAsync(IUserNFT userNFT, CancellationToken ct = default);

    /// <summary>Loads a single user NFT by id.</summary>
    Task<AZOAResult<IUserNFT>> GetUserNFTByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads an user NFT by chain/contract/token-id triple.</summary>
    Task<AZOAResult<IUserNFT>> GetUserNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, CancellationToken ct = default);

    /// <summary>Loads all user NFTs owned by an user.</summary>
    Task<AZOAResult<IEnumerable<IUserNFT>>> GetUserNFTsByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Deletes an user NFT by id.</summary>
    Task<AZOAResult<bool>> DeleteUserNFTAsync(Guid id, CancellationToken ct = default);

    // Holon NFT Binding
    /// <summary>Inserts or updates a holon NFT binding.</summary>
    Task<AZOAResult<IHolonNFTBinding>> UpsertHolonNFTBindingAsync(IHolonNFTBinding binding, CancellationToken ct = default);

    /// <summary>Loads a single holon NFT binding by id.</summary>
    Task<AZOAResult<IHolonNFTBinding>> GetHolonNFTBindingByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all holon NFT bindings for an user NFT.</summary>
    Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonNFTBindingsByUserNFTAsync(Guid userNFTId, CancellationToken ct = default);

    /// <summary>Deletes a holon NFT binding by id.</summary>
    Task<AZOAResult<bool>> DeleteHolonNFTBindingAsync(Guid id, CancellationToken ct = default);

    // Wallet NFT Binding
    /// <summary>Inserts or updates a wallet NFT binding.</summary>
    Task<AZOAResult<IWalletNFTBinding>> UpsertWalletNFTBindingAsync(IWalletNFTBinding binding, CancellationToken ct = default);

    /// <summary>Loads a single wallet NFT binding by id.</summary>
    Task<AZOAResult<IWalletNFTBinding>> GetWalletNFTBindingByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all wallet NFT bindings for an user NFT.</summary>
    Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletNFTBindingsByUserNFTAsync(Guid userNFTId, CancellationToken ct = default);

    /// <summary>Deletes a wallet NFT binding by id.</summary>
    Task<AZOAResult<bool>> DeleteWalletNFTBindingAsync(Guid id, CancellationToken ct = default);
}
