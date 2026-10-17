using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private sealed record EthereumFinalizedReceipt(JsonElement Receipt, string BlockHash, BigInteger BlockNumber,
        string ParentHash, uint Timestamp, string NetworkFee, bool Succeeded);

    private async Task<EthereumFinalizedReceipt?> ReadFinalizedReceiptAsync(string transactionId, CancellationToken ct)
    {
        var response = await RpcReadAsync("eth_getTransactionReceipt", [transactionId], ct);
        if (response.IsError || response.IsNullResult || !TryReadStringProperty(response.Value, "transactionHash", out var tx)
            || !SameReceiptHash(tx, transactionId) || !TryReadStringProperty(response.Value, "blockHash", out var hash) || !ReceiptHash(hash)
            || !TryReadQuantityProperty(response.Value, "blockNumber", out var number)
            || !TryReadQuantityProperty(response.Value, "status", out var status) || status < 0 || status > 1
            || !TryReadQuantityProperty(response.Value, "gasUsed", out var gas) || gas <= 0
            || !TryReadQuantityProperty(response.Value, "effectiveGasPrice", out var price) || price < 0) return null;
        var block = await RpcReadAsync("eth_getBlockByNumber", [EthereumHex.Quantity(number), false], ct);
        var finalized = await RpcReadAsync("eth_getBlockByNumber", ["finalized", false], ct);
        if (block.IsError || block.IsNullResult || finalized.IsError || finalized.IsNullResult
            || !TryReadStringProperty(block.Value, "hash", out var actual) || !SameReceiptHash(actual, hash)
            || !TryReadQuantityProperty(block.Value, "number", out var actualNumber) || actualNumber != number
            || !TryReadStringProperty(block.Value, "parentHash", out var parent) || !ReceiptHash(parent)
            || !TryReadQuantityProperty(block.Value, "timestamp", out var timestamp) || timestamp < 0 || timestamp > uint.MaxValue
            || !TryReadQuantityProperty(finalized.Value, "number", out var finalNumber) || finalNumber < number) return null;
        return new(response.Value.Clone(), hash.ToLowerInvariant(), number, parent.ToLowerInvariant(), (uint)timestamp,
            (gas * price).ToString(CultureInfo.InvariantCulture), status == 1);
    }

    private static bool ReceiptHash(string value) => EthereumHex.TryParseData(value, out var bytes) && bytes.Length == 32;
    private static bool SameReceiptHash(string left, string right)
        => ReceiptHash(left) && ReceiptHash(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
