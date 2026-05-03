using OASIS.WebAPI.Core;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces;

public interface IBlockchainProvider
{
    string ChainType { get; }
    ChainNetwork ActiveNetwork { get; }

    void Initialize(BlockchainNetworkConfig config, ChainNetwork network);

    // ─── Account / Wallet ───
    Task<OASISResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default);
    Task<OASISResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default);

    // ─── Token / Asset Lifecycle ───
    Task<OASISResult<string>> MintAsync(
        string tokenUri,
        int amount,
        string assetType,
        string walletAddress,
        CancellationToken ct = default);

    Task<OASISResult<string>> BurnAsync(
        string tokenId,
        int amount,
        string walletAddress,
        CancellationToken ct = default);

    Task<OASISResult<string>> TransferAsync(
        string tokenId,
        string fromAddress,
        string toAddress,
        int amount,
        CancellationToken ct = default);

    // ─── Exchange / Swap ───
    Task<OASISResult<string>> ExchangeAsync(
        string sourceTokenId,
        string targetTokenId,
        string exchangeRate,
        string walletAddress,
        CancellationToken ct = default);

    Task<OASISResult<string>> SwapAsync(
        string tokenIn,
        string tokenOut,
        decimal amountIn,
        decimal minAmountOut,
        string walletAddress,
        CancellationToken ct = default);

    // ─── Query / Metadata ───
    Task<OASISResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId,
        CancellationToken ct = default);

    Task<OASISResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress,
        CancellationToken ct = default);

    Task<OASISResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash,
        CancellationToken ct = default);

    // ─── Smart Contract / Program ───
    Task<OASISResult<string>> DeployContractAsync(
        byte[] contractCode,
        string walletAddress,
        Dictionary<string, object>? args = null,
        CancellationToken ct = default);

    Task<OASISResult<object>> CallContractAsync(
        string contractAddress,
        string method,
        Dictionary<string, object> args,
        string walletAddress,
        CancellationToken ct = default);

    // ─── Chain Info ───
    Task<OASISResult<Dictionary<string, object>>> GetChainInfoAsync(CancellationToken ct = default);
}
