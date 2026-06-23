using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for NFT aggregates: <see cref="IAvatarNFT"/> plus its
/// <see cref="IHolonNFTBinding"/> and <see cref="IWalletNFTBinding"/> bindings.
/// </summary>
public interface INftStore
{
    // Avatar NFT
    /// <summary>Inserts or updates an avatar NFT.</summary>
    Task<AZOAResult<IAvatarNFT>> UpsertAvatarNFTAsync(IAvatarNFT avatarNFT, CancellationToken ct = default);

    /// <summary>Loads a single avatar NFT by id.</summary>
    Task<AZOAResult<IAvatarNFT>> GetAvatarNFTByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads an avatar NFT by chain/contract/token-id triple.</summary>
    Task<AZOAResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, CancellationToken ct = default);

    /// <summary>Loads all avatar NFTs owned by an avatar.</summary>
    Task<AZOAResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>Deletes an avatar NFT by id.</summary>
    Task<AZOAResult<bool>> DeleteAvatarNFTAsync(Guid id, CancellationToken ct = default);

    // Holon NFT Binding
    /// <summary>Inserts or updates a holon NFT binding.</summary>
    Task<AZOAResult<IHolonNFTBinding>> UpsertHolonNFTBindingAsync(IHolonNFTBinding binding, CancellationToken ct = default);

    /// <summary>Loads a single holon NFT binding by id.</summary>
    Task<AZOAResult<IHolonNFTBinding>> GetHolonNFTBindingByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all holon NFT bindings for an avatar NFT.</summary>
    Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonNFTBindingsByAvatarNFTAsync(Guid avatarNFTId, CancellationToken ct = default);

    /// <summary>Deletes a holon NFT binding by id.</summary>
    Task<AZOAResult<bool>> DeleteHolonNFTBindingAsync(Guid id, CancellationToken ct = default);

    // Wallet NFT Binding
    /// <summary>Inserts or updates a wallet NFT binding.</summary>
    Task<AZOAResult<IWalletNFTBinding>> UpsertWalletNFTBindingAsync(IWalletNFTBinding binding, CancellationToken ct = default);

    /// <summary>Loads a single wallet NFT binding by id.</summary>
    Task<AZOAResult<IWalletNFTBinding>> GetWalletNFTBindingByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all wallet NFT bindings for an avatar NFT.</summary>
    Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletNFTBindingsByAvatarNFTAsync(Guid avatarNFTId, CancellationToken ct = default);

    /// <summary>Deletes a wallet NFT binding by id.</summary>
    Task<AZOAResult<bool>> DeleteWalletNFTBindingAsync(Guid id, CancellationToken ct = default);
}
