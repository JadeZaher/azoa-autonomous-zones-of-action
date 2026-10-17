using System.Numerics;
using System.Text.Json;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

internal sealed record EthereumDexLog(string Address, IReadOnlyList<byte[]> Topics, byte[] Data);

internal sealed record EthereumDexSwapExpectation(string Executor, string Router, string Pool, string Owner, string Recipient,
    string TokenIn, string TokenOut, string Treasury, BigInteger Gross, BigInteger Fee, string Operation, string QuoteHash,
    BigInteger FeeRevision, bool NativeInput, bool NativeOutput);

/// <summary>Reads the closed finalized log grammar of one executor swap or one exact allowance change.</summary>
internal static class EthereumDexReceipt
{
    private static readonly byte[] Transfer = Topic("Transfer(address,address,uint256)");
    private static readonly byte[] Approval = Topic("Approval(address,address,uint256)");
    private static readonly byte[] Deposit = Topic("Deposit(address,uint256)");
    private static readonly byte[] Withdrawal = Topic("Withdrawal(address,uint256)");
    private static readonly byte[] PoolSwap = Topic("Swap(address,address,int256,int256,uint160,uint128,int24)");
    private static readonly byte[] SwapExecuted = Topic(
        "SwapExecuted(bytes32,bytes32,address,address,address,address,address,uint256,uint256,uint256,address,uint256)");

    /// <summary>Parses every receipt log bound to the exact transaction and block; malformed or removed logs refuse the whole set.</summary>
    internal static IReadOnlyList<EthereumDexLog>? Logs(JsonElement receipt, string transactionId, string blockHash)
    {
        if (!receipt.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array || logs.GetArrayLength() > 64) return null;
        var parsed = new List<EthereumDexLog>();
        foreach (var log in logs.EnumerateArray())
        {
            if (log.ValueKind != JsonValueKind.Object || !Text(log, "address", out var address) || !EthereumAddress.TryParse(address, out var canonical) ||
                !Text(log, "transactionHash", out var tx) || !tx.Equals(transactionId, StringComparison.OrdinalIgnoreCase) ||
                !Text(log, "blockHash", out var block) || !block.Equals(blockHash, StringComparison.OrdinalIgnoreCase) ||
                !log.TryGetProperty("removed", out var removed) || removed.ValueKind != JsonValueKind.False ||
                !log.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() is 0 or > 4 ||
                !Text(log, "data", out var dataText) || !EthereumHex.TryParseData(dataText, out var data) || data.Length % 32 != 0)
                return null;
            var words = new List<byte[]>();
            foreach (var topic in topics.EnumerateArray())
            {
                if (topic.ValueKind != JsonValueKind.String || !EthereumHex.TryParseData(topic.GetString(), out var bytes) || bytes.Length != 32) return null;
                words.Add(bytes);
            }
            parsed.Add(new(canonical.ToLowerInvariant(), words, data));
        }
        return parsed;
    }

    /// <summary>Returns actual output only when executor, pool and every token value movement match the reviewed atomic swap exactly.</summary>
    internal static BigInteger? SwapOutput(IReadOnlyList<EthereumDexLog> logs, EthereumDexSwapExpectation e)
    {
        var executor = e.Executor.ToLowerInvariant(); var pool = e.Pool.ToLowerInvariant();
        var tokenIn = e.TokenIn.ToLowerInvariant(); var tokenOut = e.TokenOut.ToLowerInvariant();
        var executed = logs.Where(log => log.Address == executor).ToArray();
        var swaps = logs.Where(log => log.Address == pool).ToArray();
        if (executed.Length != 1 || swaps.Length != 1 || logs.Any(log => log.Address != executor && log.Address != pool &&
            log.Address != tokenIn && log.Address != tokenOut)) return null;
        var report = executed[0];
        if (report.Topics.Count != 4 || !report.Topics[0].AsSpan().SequenceEqual(SwapExecuted) || report.Data.Length != 9 * 32 ||
            !report.Topics[1].AsSpan().SequenceEqual(Convert.FromHexString(e.Operation)) ||
            !report.Topics[2].AsSpan().SequenceEqual(Convert.FromHexString(e.QuoteHash)) || !AddressWord(report.Topics[3], e.Owner) ||
            !AddressWord(Word(report.Data, 0), e.Recipient) || !AddressWord(Word(report.Data, 1), e.Pool) ||
            !AddressWord(Word(report.Data, 2), e.TokenIn) || !AddressWord(Word(report.Data, 3), e.TokenOut) ||
            Unsigned(Word(report.Data, 4)) != e.Gross || Unsigned(Word(report.Data, 5)) != e.Fee ||
            !AddressWord(Word(report.Data, 7), e.Treasury) || Unsigned(Word(report.Data, 8)) != e.FeeRevision) return null;
        var output = Unsigned(Word(report.Data, 6));
        var net = e.Gross - e.Fee;
        var swap = swaps[0];
        var zeroForOne = new BigInteger(Convert.FromHexString(tokenIn[2..]), true, true) < new BigInteger(Convert.FromHexString(tokenOut[2..]), true, true);
        if (output <= 0 || net <= 0 || swap.Topics.Count != 3 || !swap.Topics[0].AsSpan().SequenceEqual(PoolSwap) || swap.Data.Length != 5 * 32 ||
            !AddressWord(swap.Topics[1], e.Router) || !AddressWord(swap.Topics[2], e.Executor) ||
            Signed(Word(swap.Data, zeroForOne ? 0 : 1)) != net || Signed(Word(swap.Data, zeroForOne ? 1 : 0)) != -output) return null;
        var expected = new List<string>();
        if (e.NativeInput)
        {
            expected.Add(Value(tokenIn, "Deposit", executor, null, e.Gross));
            if (e.Fee > 0) expected.Add(Value(tokenIn, "Withdrawal", executor, null, e.Fee));
        }
        else
        {
            expected.Add(Value(tokenIn, "Transfer", e.Owner, executor, e.Gross));
            if (e.Fee > 0) expected.Add(Value(tokenIn, "Transfer", executor, e.Treasury, e.Fee));
        }
        expected.Add(Value(tokenIn, "Transfer", executor, pool, net));
        expected.Add(Value(tokenOut, "Transfer", pool, executor, output));
        expected.Add(e.NativeOutput ? Value(tokenOut, "Withdrawal", executor, null, output) : Value(tokenOut, "Transfer", executor, e.Recipient, output));
        var actual = new List<string>();
        foreach (var log in logs.Where(log => log.Address == tokenIn || log.Address == tokenOut))
        {
            var kind = Kind(log);
            if (kind is null) return null;
            if (kind == "Approval") continue;
            if (kind == "Transfer" ? log.Topics.Count != 3 : log.Topics.Count != 2) return null;
            if (log.Data.Length != 32) return null;
            actual.Add(Value(log.Address, kind, Address(log.Topics[1]), kind == "Transfer" ? Address(log.Topics[2]) : null, Unsigned(log.Data)));
        }
        expected.Sort(StringComparer.Ordinal); actual.Sort(StringComparer.Ordinal);
        return expected.SequenceEqual(actual, StringComparer.Ordinal) ? output : null;
    }

    /// <summary>True only for exactly one owner-to-spender Approval of the exact value and no value movement or foreign log.</summary>
    internal static bool ExactApproval(IReadOnlyList<EthereumDexLog> logs, string token, string owner, string spender, BigInteger value)
    {
        var address = token.ToLowerInvariant();
        return logs.Count == 1 && logs[0].Address == address && Kind(logs[0]) == "Approval" && logs[0].Topics.Count == 3 &&
            logs[0].Data.Length == 32 && AddressWord(logs[0].Topics[1], owner) && AddressWord(logs[0].Topics[2], spender) &&
            Unsigned(logs[0].Data) == value;
    }

    private static string? Kind(EthereumDexLog log)
    {
        var topic = log.Topics[0];
        return topic.AsSpan().SequenceEqual(Transfer) ? "Transfer" : topic.AsSpan().SequenceEqual(Approval) ? "Approval"
            : topic.AsSpan().SequenceEqual(Deposit) ? "Deposit" : topic.AsSpan().SequenceEqual(Withdrawal) ? "Withdrawal" : null;
    }

    private static string Value(string token, string kind, string from, string? to, BigInteger amount)
        => string.Join('|', token.ToLowerInvariant(), kind, from.ToLowerInvariant(), to?.ToLowerInvariant() ?? "", amount.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static byte[] Word(byte[] data, int index) => data.AsSpan(index * 32, 32).ToArray();
    private static BigInteger Unsigned(byte[] word) => new(word, true, true);
    private static BigInteger Signed(byte[] word) => new(word, false, true);
    private static string Address(byte[] word) => "0x" + Convert.ToHexStringLower(word.AsSpan(12));
    private static bool AddressWord(byte[] word, string address)
        => word.Length == 32 && word.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && EthereumAddress.AreSame(Address(word), address);
    private static bool Text(JsonElement value, string name, out string text)
    {
        text = value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        return text.Length > 0;
    }
    private static byte[] Topic(string signature) => new Sha3Keccack().CalculateHash(System.Text.Encoding.UTF8.GetBytes(signature));
}
