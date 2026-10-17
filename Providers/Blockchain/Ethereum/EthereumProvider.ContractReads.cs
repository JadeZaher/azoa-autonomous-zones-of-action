using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private async Task RequireCodeAtBlockAsync(string address, string expectedHash, object block, CancellationToken ct)
    {
        var response = await RpcReadAsync("eth_getCode", [address, block], ct);
        if (!EthereumHex.TryParseData(expectedHash, out var expected) || expected.Length != 32 || response.IsError ||
            !response.TryGetString(out var value) || !EthereumHex.TryParseData(value, out var code) || code.Length == 0 ||
            !Keccak.CalculateHash(code).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Ethereum deployed runtime code differs from the pinned deployment.");
    }

    private async Task<string> ReadContractCallAtBlockAsync(string contract, string signature, object[] values,
        object block, CancellationToken ct)
    {
        var response = await RpcReadAsync("eth_call", [new { to = contract, data = EthereumAbi.EncodeCall(signature, values) }, block], ct);
        if (response.IsError || !response.TryGetString(out var result))
            throw new InvalidDataException("Authoritative Ethereum contract state is unavailable.");
        return result;
    }

    private async Task<BigInteger> ReadUInt256AtBlockAsync(string contract, string signature, object[] values,
        object block, CancellationToken ct)
    {
        var value = await ReadContractCallAtBlockAsync(contract, signature, values, block, ct);
        if (!EthereumHex.TryParseData(value, out var bytes) || bytes.Length != 32 || !EthereumAbi.TryDecodeUInt256(value, out var number))
            throw new InvalidDataException("Ethereum contract returned a malformed integer.");
        return number;
    }
}
