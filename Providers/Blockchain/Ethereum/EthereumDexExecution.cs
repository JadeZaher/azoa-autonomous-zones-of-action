using System.Numerics;
using AZOA.WebAPI.Helpers;
using Nethereum.ABI;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

internal sealed record EthereumDexExecution(string Operation, string QuoteHash, string Owner, string Recipient,
    string TokenIn, string TokenOut, string Pool, string Treasury, string GrossInput, string PlatformFee,
    string MinimumOutput, uint PoolFee, string Deadline, long FeeRevision, string PolicyKeyVersion,
    string InputCodeHash, string OutputCodeHash, bool NativeInput, bool NativeOutput);

/// <summary>Encodes the executor's exact static tuple with canonical uint256 and identity bounds.</summary>
internal static class EthereumDexExecutionCodec
{
    internal static byte[] Encode(EthereumDexExecution execution)
    {
        var gross = Amount(execution.GrossInput); var fee = Amount(execution.PlatformFee);
        var minimum = Amount(execution.MinimumOutput); var deadline = Amount(execution.Deadline);
        if (gross == 0 || fee >= gross || minimum == 0 || deadline == 0 || execution.FeeRevision <= 0 ||
            execution.PoolFee is 0 or > 0xffffff || execution.NativeInput && execution.NativeOutput ||
            EthereumAddress.AreSame(execution.TokenIn, execution.TokenOut))
            throw new InvalidDataException("Executor economics or exact-input token identities are invalid.");
        foreach (var address in new[] { execution.Owner, execution.Recipient, execution.TokenIn, execution.TokenOut, execution.Pool })
            if (!EthereumAddress.IsNonZero(address)) throw new InvalidDataException("Executor requires exact nonzero addresses.");
        if (!EthereumAddress.TryParse(execution.Treasury, out _) || fee > 0 && !EthereumAddress.IsNonZero(execution.Treasury))
            throw new InvalidDataException("The executor fee treasury is invalid.");
        return new ABIEncode().GetABIEncoded(
            new ABIValue("bytes32", Hash(execution.Operation)), new ABIValue("bytes32", Hash(execution.QuoteHash)),
            new ABIValue("address", execution.Owner), new ABIValue("address", execution.Recipient),
            new ABIValue("address", execution.TokenIn), new ABIValue("address", execution.TokenOut),
            new ABIValue("address", execution.Pool), new ABIValue("address", execution.Treasury),
            new ABIValue("uint256", gross), new ABIValue("uint256", fee), new ABIValue("uint256", minimum),
            new ABIValue("uint24", execution.PoolFee), new ABIValue("uint256", deadline),
            new ABIValue("uint256", execution.FeeRevision), new ABIValue("bytes32", Hash(execution.PolicyKeyVersion)),
            new ABIValue("bytes32", Hash(execution.InputCodeHash)), new ABIValue("bytes32", Hash(execution.OutputCodeHash)),
            new ABIValue("bool", execution.NativeInput), new ABIValue("bool", execution.NativeOutput));
    }

    internal static string OperationIdentity(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) throw new InvalidDataException("Executor operation identity is required.");
        return Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(System.Text.Encoding.UTF8.GetBytes("azoa:dex-operation/1:" + operationId)));
    }

    internal static BigInteger Amount(string text)
        => UnsignedIntegerText.TryParse(text, out var value) && value < (BigInteger.One << 256) ? value
            : throw new InvalidDataException("Executor amount must be canonical uint256 decimal text.");

    private static byte[] Hash(string text)
    {
        if (text is not { Length: 64 } || text.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Executor hash must be exactly 32 canonical bytes.");
        var bytes = Convert.FromHexString(text);
        if (bytes.All(value => value == 0)) throw new InvalidDataException("Executor identity cannot be zero.");
        return bytes;
    }
}
