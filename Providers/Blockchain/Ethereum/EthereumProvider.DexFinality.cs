using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private sealed record DexEvmFinalized(PreparedSwapStage Candidate, EthereumFinalizedReceipt Receipt);

    private async Task<DexEvmFinalized?> ReadDexFinalizedAsync(SwapIntent intent, SwapStagePlan plan,
        string transactionId, CancellationToken ct)
    {
        var found = await ReadDexFinalizedCoreAsync(intent, plan, transactionId, sameCallEnvelope: false, ct);
        return found is { Candidate: { } candidate } exact ? new(candidate, exact.Receipt) : null;
    }

    /// <summary>Full finality proof (genesis, block body, code pins, stable receipt) for the exact plan or, for operator settlement, its same call.</summary>
    private async Task<(PreparedSwapStage? Candidate, EthereumFinalizedReceipt Receipt)?> ReadDexFinalizedCoreAsync(SwapIntent intent,
        SwapStagePlan plan, string transactionId, bool sameCallEnvelope, CancellationToken ct)
    {
        var saved = ReadEthereumDexPlan(intent, plan);
        var quote = ReadDexAcceptedIntent(intent);
        var chain = await GetChainIdAsync(ct);
        var genesis = await RpcReadAsync("eth_getBlockByNumber", ["0x0", false], ct);
        if (chain.IsError || "eip155:" + Text(chain.Result) != saved.ProtocolIdentity || genesis.IsError || genesis.IsNullResult ||
            !TryReadStringProperty(genesis.Value, "hash", out var actualGenesis) || !SameDexHash(actualGenesis, quote.Deployment.GenesisHash))
            return null;
        var response = await RpcReadAsync("eth_getTransactionByHash", [transactionId], ct);
        if (response.IsError || response.IsNullResult) return null;
        PreparedSwapStage? prepared = null;
        if (sameCallEnvelope)
        {
            // The owner's own signature over the same to/value/input at the reserved nonce; fees and gas are the wallet's choice.
            EthereumRpcEnvelope.ReadOwnEnvelope(response.Value, intent.Terms.Owner.Address);
            if (!TryReadStringProperty(response.Value, "hash", out var reported) || !SameDexHash(reported, transactionId) ||
                !SameDexCall(response.Value, saved) || !TryReadQuantityProperty(response.Value, "nonce", out var nonce) ||
                nonce != EthereumDexExecutionCodec.Amount(saved.Nonce) || !TryReadQuantityProperty(response.Value, "chainId", out var signedChain) ||
                signedChain != chain.Result) return null;
        }
        else
        {
            var wire = EthereumRpcEnvelope.Read(response.Value, DexUnsigned(saved), intent.Terms.Owner.Address);
            var candidate = ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(Convert.ToBase64String(wire)));
            if (candidate.IsError || candidate.Result is not { } exact || !SameDexHash(exact.TransactionIds[0], transactionId)) return null;
            prepared = exact;
        }
        var receipt = await ReadFinalizedReceiptAsync(transactionId, ct);
        if (receipt is null || !sameCallEnvelope && (
                EthereumDexExecutionCodec.Amount(receipt.NetworkFee) > EthereumDexExecutionCodec.Amount(plan.ReservedNetworkFee) ||
                !TryReadQuantityProperty(receipt.Receipt, "gasUsed", out var gas) || gas > EthereumDexExecutionCodec.Amount(saved.Call.GasLimit) ||
                !TryReadQuantityProperty(receipt.Receipt, "effectiveGasPrice", out var price) || price > EthereumDexExecutionCodec.Amount(saved.Call.MaxFee)) ||
            !TryReadQuantityProperty(receipt.Receipt, "transactionIndex", out var index) || index > int.MaxValue ||
            !TryReadQuantityProperty(response.Value, "transactionIndex", out var txIndex) || txIndex != index ||
            !TryReadQuantityProperty(response.Value, "blockNumber", out var txBlock) || txBlock != receipt.BlockNumber ||
            !TryReadStringProperty(response.Value, "blockHash", out var txHash) || !SameDexHash(txHash, receipt.BlockHash)) return null;
        var block = await RpcReadAsync("eth_getBlockByHash", [receipt.BlockHash, false], ct);
        if (block.IsError || block.IsNullResult || !TryReadStringProperty(block.Value, "hash", out var blockHash) || !SameDexHash(blockHash, receipt.BlockHash) ||
            !TryReadQuantityProperty(block.Value, "number", out var number) || number != receipt.BlockNumber ||
            !block.Value.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array ||
            index >= transactions.GetArrayLength() || transactions[(int)index].ValueKind != JsonValueKind.String ||
            !SameDexHash(transactions[(int)index].GetString()!, transactionId) ||
            transactions.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.String && SameDexHash(item.GetString()!, transactionId)) != 1)
            return null;
        foreach (var position in new[] { receipt.ParentHash, receipt.BlockHash })
        {
            var at = new { blockHash = position, requireCanonical = true };
            foreach (var (address, hash) in new[]
            {
                (quote.Deployment.Executor, quote.Deployment.ExecutorHash), (quote.Deployment.Router, quote.Deployment.RouterHash),
                (quote.Deployment.Factory, quote.Deployment.FactoryHash), (quote.Deployment.WrappedNative, quote.Deployment.WrappedHash),
                (quote.Input.Address, quote.Input.CodeHash), (quote.Output.Address, quote.Output.CodeHash)
            }) await RequireCodeAtBlockAsync(address, hash, at, ct);
        }
        var repeated = await ReadFinalizedReceiptAsync(transactionId, ct);
        if (repeated is null || repeated.BlockHash != receipt.BlockHash || repeated.BlockNumber != receipt.BlockNumber ||
            repeated.Succeeded != receipt.Succeeded || repeated.NetworkFee != receipt.NetworkFee ||
            CanonicalJson.Hash(repeated.Receipt) != CanonicalJson.Hash(receipt.Receipt)) return null;
        return (prepared, receipt);
    }
}
