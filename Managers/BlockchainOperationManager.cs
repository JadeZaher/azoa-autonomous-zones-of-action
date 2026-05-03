using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

public class BlockchainOperationManager : IBlockchainOperationManager
{
    private readonly ProviderContext _providerContext;
    private readonly IBlockchainProviderFactory _chainFactory;

    public BlockchainOperationManager(ProviderContext providerContext, IBlockchainProviderFactory chainFactory)
    {
        _providerContext = providerContext;
        _chainFactory = chainFactory;
    }

    public async Task<OASISResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IBlockchainOperation> { IsError = true, Message = activation.Message };

        var result = await _providerContext.CurrentProvider.SaveBlockchainOperationAsync(operation);
        if (result.IsError) return result;

        var chainType = operation.Parameters.GetValueOrDefault("ChainType", _chainFactory.GetDefaultProvider().ChainType);
        var networkStr = operation.Parameters.GetValueOrDefault("ChainNetwork", "Devnet");
        var network = Enum.TryParse<ChainNetwork>(networkStr, true, out var parsed) ? parsed : ChainNetwork.Devnet;

        var chainProvider = _chainFactory.GetProvider(chainType, network);

        try
        {
            switch (operation.OperationType)
            {
                case "Mint":
                    await ExecuteMintAsync(operation, chainProvider);
                    break;
                case "Burn":
                    await ExecuteBurnAsync(operation, chainProvider);
                    break;
                case "Exchange":
                    await ExecuteExchangeAsync(operation, chainProvider);
                    break;
                case "Swap":
                    await ExecuteSwapAsync(operation, chainProvider);
                    break;
                case "Transfer":
                    await ExecuteTransferAsync(operation, chainProvider);
                    break;
                case "DeployContract":
                    await ExecuteDeployContractAsync(operation, chainProvider);
                    break;
                case "CallContract":
                    await ExecuteCallContractAsync(operation, chainProvider);
                    break;
                case "Composite":
                    break;
                default:
                    operation.Status = "Unknown";
                    break;
            }
        }
        catch (Exception ex)
        {
            operation.Status = "Failed";
            operation.Parameters["Error"] = ex.Message;
        }

        if (operation.Status == "Pending")
        {
            operation.Status = "Completed";
            operation.CompletedDate = DateTime.UtcNow;
        }

        return await _providerContext.CurrentProvider.SaveBlockchainOperationAsync(operation);
    }

    public async Task<OASISResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, OASISRequest? request = null)
    {
        var builder = new BlockchainOperationBuilder();
        var operation = build(builder);
        return await ExecuteAsync(operation, request);
    }

    public async Task<OASISResult<IBlockchainOperation>> GetAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IBlockchainOperation> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadBlockchainOperationAsync(id);
    }

    public async Task<OASISResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IBlockchainOperation>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadBlockchainOperationsByAvatarAsync(avatarId);
    }

    private async Task ExecuteMintAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not IMintOperation mint) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.MintAsync(
            mint.TokenUri ?? string.Empty, mint.Amount, mint.AssetType ?? string.Empty, walletAddress);
        ApplyChainResult(operation, result, "Minted");
    }

    private async Task ExecuteBurnAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var tokenId = operation.Parameters.GetValueOrDefault("TokenId", string.Empty);
        var amount = int.Parse(operation.Parameters.GetValueOrDefault("Amount", "0"));
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.BurnAsync(tokenId, amount, walletAddress);
        ApplyChainResult(operation, result, "Burned");
    }

    private async Task ExecuteExchangeAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not IExchangeOperation exchange) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var sourceTokenId = operation.Parameters.GetValueOrDefault("SourceTokenId", exchange.SourceHolonId?.ToString() ?? string.Empty);
        var targetTokenId = operation.Parameters.GetValueOrDefault("TargetTokenId", exchange.TargetHolonId?.ToString() ?? string.Empty);
        var result = await chainProvider.ExchangeAsync(sourceTokenId, targetTokenId, exchange.ExchangeRate ?? string.Empty, walletAddress);
        ApplyChainResult(operation, result, "Exchanged");
    }

    private async Task ExecuteSwapAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var tokenIn = operation.Parameters.GetValueOrDefault("TokenIn", string.Empty);
        var tokenOut = operation.Parameters.GetValueOrDefault("TokenOut", string.Empty);
        var amountIn = decimal.Parse(operation.Parameters.GetValueOrDefault("AmountIn", "0"));
        var minAmountOut = decimal.Parse(operation.Parameters.GetValueOrDefault("MinAmountOut", "0"));
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.SwapAsync(tokenIn, tokenOut, amountIn, minAmountOut, walletAddress);
        ApplyChainResult(operation, result, "Swapped");
    }

    private async Task ExecuteTransferAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not ITransferOperation transfer) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var sourceTokenId = operation.Parameters.GetValueOrDefault("SourceTokenId", transfer.SourceHolonId?.ToString() ?? string.Empty);
        var result = await chainProvider.TransferAsync(sourceTokenId, walletAddress, transfer.RecipientAddress ?? string.Empty, 1);
        ApplyChainResult(operation, result, "Transferred");
    }

    private async Task ExecuteDeployContractAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var contractCode = Convert.FromBase64String(operation.Parameters.GetValueOrDefault("ContractCode", string.Empty));
        var args = operation.Parameters.GetValueOrDefault("Args") is string s && !string.IsNullOrEmpty(s)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(s)
            : null;
        var result = await chainProvider.DeployContractAsync(contractCode, walletAddress, args);
        ApplyChainResult(operation, result, "Deployed");
    }

    private async Task ExecuteCallContractAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var contractAddress = operation.Parameters.GetValueOrDefault("ContractAddress", string.Empty);
        var method = operation.Parameters.GetValueOrDefault("Method", string.Empty);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            operation.Parameters.GetValueOrDefault("Args", "{}")) ?? new();
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.CallContractAsync(contractAddress, method, args, walletAddress);
        ApplyChainResult(operation, result, "Called");
    }

    private static void ApplyChainResult<T>(IBlockchainOperation operation, OASISResult<T> chainResult, string successStatus)
    {
        operation.Status = chainResult.IsError ? "Failed" : successStatus;
        if (!chainResult.IsError && chainResult.Result != null)
            operation.Parameters["TxHash"] = chainResult.Result?.ToString() ?? string.Empty;
        else if (chainResult.IsError)
            operation.Parameters["Error"] = chainResult.Message;
    }
}
