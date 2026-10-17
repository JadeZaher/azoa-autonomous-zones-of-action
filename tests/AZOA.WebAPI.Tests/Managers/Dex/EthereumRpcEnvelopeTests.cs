using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class EthereumRpcEnvelopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualSignatureFieldsReconstructTheExactOriginalSignedEnvelope(bool legacyParityName)
    {
        var (key, unsigned, wire, rpc) = Fixture();
        if (legacyParityName) rpc.Remove("yParity");
        EthereumRpcEnvelope.Read(JsonSerializer.SerializeToElement(rpc), unsigned, key.GetPublicAddress()).Should().Equal(wire);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("nonce")]
    [InlineData("r")]
    [InlineData("accessList")]
    [InlineData("v")]
    [InlineData("maxFeePerGas")]
    [InlineData("from")]
    [InlineData("missing")]
    public void NodeMetadataCannotInventOrAlterTheOriginalSignedCandidate(string mutation)
    {
        var (key, unsigned, _, rpc) = Fixture();
        switch (mutation)
        {
            case "hash": rpc[mutation] = "0x" + new string('a', 64); break;
            case "from": rpc[mutation] = "0x" + new string('b', 40); break;
            case "accessList": rpc[mutation] = new[] { new { address = "0x" + new string('a', 40), storageKeys = Array.Empty<string>() } }; break;
            case "missing": rpc.Remove("s"); break;
            case "v": rpc[mutation] = "0x2"; break;
            default: rpc[mutation] = "0x0"; break;
        }
        var read = () => EthereumRpcEnvelope.Read(JsonSerializer.SerializeToElement(rpc), unsigned, key.GetPublicAddress());
        read.Should().Throw<Exception>();
    }

    private static (EthECKey Key, byte[] Unsigned, byte[] Wire, Dictionary<string, object> Rpc) Fixture()
    {
        var key = EthECKey.GenerateKey();
        var transaction = new Transaction1559(31337, 7, 1, 100, 300000, "0x" + new string('a', 40), 0, "0x12345678", null);
        var unsigned = transaction.GetRLPEncodedRaw();
        using var material = new SigningKeyMaterial(key.GetPrivateKeyAsBytes());
        var signed = new EthereumTransactionSigner().Sign(unsigned, material);
        signed.IsError.Should().BeFalse(signed.Message);
        var wire = signed.Result!; var actual = Transaction1559Encoder.Current.Decode(wire);
        var parity = EthereumHex.Quantity(new BigInteger(actual.Signature.V, true, true));
        return (key, unsigned, wire, new()
        {
            ["type"] = "0x2", ["chainId"] = "0x7a69", ["nonce"] = "0x7", ["maxPriorityFeePerGas"] = "0x1",
            ["maxFeePerGas"] = "0x64", ["gas"] = "0x493e0", ["to"] = "0x" + new string('a', 40), ["value"] = "0x0",
            ["input"] = "0x12345678", ["from"] = key.GetPublicAddress(), ["accessList"] = Array.Empty<object>(),
            ["r"] = EthereumHex.Quantity(new BigInteger(actual.Signature.R, true, true)),
            ["s"] = EthereumHex.Quantity(new BigInteger(actual.Signature.S, true, true)), ["v"] = parity, ["yParity"] = parity,
            ["hash"] = EthereumHex.Data(new Sha3Keccack().CalculateHash(wire))
        });
    }
}
