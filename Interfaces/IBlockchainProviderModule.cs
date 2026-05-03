using OASIS.WebAPI.Core;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces;

/// <summary>
/// Extension module pattern for chain-specific features that are not part of the standard cross-chain interface.
/// Each module is keyed by a capability name (e.g., "Algorand.ASA", "Solana.Metaplex", "Ethereum.ERC721").
/// The base provider exposes a generic module resolution method; consumers cast to the specific module interface.
/// </summary>
public interface IBlockchainProviderModule
{
    string CapabilityName { get; }
    string ChainType { get; }
}

// Example chain-specific capability interfaces — consumers check for presence via IBlockchainProvider.TryGetModule<T>()

public interface IAlgorandASAModule : IBlockchainProviderModule
{
    Task<OASISResult<string>> CreateASAAsync(
        string name, string unitName, int total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress, string clawbackAddress,
        string walletAddress, CancellationToken ct = default);

    Task<OASISResult<bool>> OptInAsync(string assetId, string walletAddress, CancellationToken ct = default);
    Task<OASISResult<string>> GetAssetHoldingAsync(string assetId, string address, CancellationToken ct = default);
}

public interface ISolanaMetaplexModule : IBlockchainProviderModule
{
    Task<OASISResult<string>> CreateMetadataAccountAsync(
        string mint, string name, string symbol, string uri,
        int sellerFeeBasisPoints, string walletAddress, CancellationToken ct = default);

    Task<OASISResult<bool>> UpdateMetadataAsync(
        string mint, string? newUri, string? newName, string walletAddress, CancellationToken ct = default);
}

public interface ISolanaSPLModule : IBlockchainProviderModule
{
    Task<OASISResult<string>> CreateTokenAccountAsync(string mint, string owner, CancellationToken ct = default);
    Task<OASISResult<string>> CloseTokenAccountAsync(string tokenAccount, string owner, CancellationToken ct = default);
}
