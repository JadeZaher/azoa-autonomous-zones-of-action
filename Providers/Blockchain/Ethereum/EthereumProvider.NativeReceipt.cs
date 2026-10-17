using System.Text.Json;
using System.Globalization;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private sealed record NativeFinalizedReceipt(JsonElement Receipt, string BlockHash, string BlockNumber, string NetworkFee);

    private async Task<NativeFinalizedReceipt?> ReadNativeFinalizedReceiptAsync(string transactionId,
        ChainConfirmation confirmation, CancellationToken ct)
    {
        var receipt = await RpcCallAsync("eth_getTransactionReceipt", new object?[] { transactionId }, ct);
        if (receipt.IsError || receipt.IsNullResult
            || !TryReadStringProperty(receipt.Value, "transactionHash", out var actualId)
            || !string.Equals(actualId, transactionId, StringComparison.OrdinalIgnoreCase)
            || !TryReadStringProperty(receipt.Value, "blockHash", out var blockHash)
            || !TryReadQuantityProperty(receipt.Value, "blockNumber", out var blockNumber)
            || !TryReadQuantityProperty(receipt.Value, "status", out var status)
            || status != (confirmation == ChainConfirmation.Confirmed ? 1 : 0)
            || !TryReadQuantityProperty(receipt.Value, "gasUsed", out var gas) || gas <= 0
            || !TryReadQuantityProperty(receipt.Value, "effectiveGasPrice", out var price) || price < 0) return null;
        var block = await RpcCallAsync("eth_getBlockByNumber", new object?[] { EthereumHex.Quantity(blockNumber), false }, ct);
        var finalized = await RpcCallAsync("eth_getBlockByNumber", new object?[] { "finalized", false }, ct);
        if (block.IsError || block.IsNullResult || finalized.IsError || finalized.IsNullResult
            || !TryReadStringProperty(block.Value, "hash", out var canonicalHash)
            || !string.Equals(canonicalHash, blockHash, StringComparison.OrdinalIgnoreCase)
            || !TryReadQuantityProperty(block.Value, "number", out var canonicalNumber) || canonicalNumber != blockNumber
            || !TryReadQuantityProperty(finalized.Value, "number", out var finalizedNumber) || finalizedNumber < blockNumber) return null;
        return new(receipt.Value.Clone(), blockHash, EthereumHex.Quantity(blockNumber), (gas * price).ToString(CultureInfo.InvariantCulture));
    }
}
