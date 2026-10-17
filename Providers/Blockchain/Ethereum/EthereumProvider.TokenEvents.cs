using System.Numerics;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private static bool EthereumTokenEventMatches(JsonElement log, string token, string owner, string? spender, BigInteger amount,
        string transactionId, string blockHash, string eventName)
    {
        if (log.ValueKind != JsonValueKind.Object || !TryReadStringProperty(log, "address", out var address)
            || !EthereumAddress.AreSame(address, token) || !TryReadStringProperty(log, "transactionHash", out var tx) || !SameReceiptHash(tx, transactionId)
            || !TryReadStringProperty(log, "blockHash", out var block) || !SameReceiptHash(block, blockHash)
            || !log.TryGetProperty("removed", out var removed) || removed.ValueKind != JsonValueKind.False
            || !log.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() != (spender is null ? 2 : 3)
            || topics.EnumerateArray().Any(topic => topic.ValueKind != JsonValueKind.String)) return false;
        if (!string.Equals(topics[0].GetString(), "0x" + Keccak.CalculateHash(eventName + (spender is null ? "(address,uint256)" : "(address,address,uint256)")), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(topics[1].GetString(), "0x" + owner[2..].PadLeft(64, '0'), StringComparison.OrdinalIgnoreCase)
            || spender is not null && !string.Equals(topics[2].GetString(), "0x" + spender[2..].PadLeft(64, '0'), StringComparison.OrdinalIgnoreCase)) return false;
        return TryReadStringProperty(log, "data", out var data) && EthereumHex.TryParseData(data, out var bytes) && bytes.Length == 32
            && new BigInteger(bytes, true, true) == amount;
    }
}
