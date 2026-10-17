using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>
/// F-M13: operator settle-review re-proves a same-call winner the owner's wallet signed in its own envelope. Every reviewed
/// envelope (type 2 or 1 with an access list, EIP-155 legacy) is reconstructed and its signer and hash re-proven locally;
/// a replayable or foreign signature never is.
/// </summary>
public sealed class EthereumOwnEnvelopeTests
{
    private const long Chain = 31337;
    private static readonly string Target = "0x" + new string('a', 40);

    public static TheoryData<string> Accepted => new() { "type2-access-list", "type1-access-list", "legacy-eip155" };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void The_owners_own_envelope_is_reconstructed_to_its_exact_signed_bytes(string envelope)
    {
        var key = EthECKey.GenerateKey();
        var (json, wire) = Signed(envelope, key);

        var read = EthereumRpcEnvelope.ReadOwnEnvelope(json, key.GetPublicAddress());

        read.Should().Equal(wire);
    }

    [Theory]
    [InlineData("pre-eip155-legacy")]
    [InlineData("other-signer")]
    [InlineData("altered-payload")]
    public void A_replayable_foreign_or_altered_envelope_is_refused(string mutation)
    {
        var key = EthECKey.GenerateKey();
        var (json, _) = Signed(mutation == "pre-eip155-legacy" ? "legacy-pre155" : "type1-access-list", key);
        var claimed = mutation == "other-signer" ? EthECKey.GenerateKey().GetPublicAddress() : key.GetPublicAddress();
        if (mutation == "altered-payload") json = With(json, "value", "0x2");

        var read = () => EthereumRpcEnvelope.ReadOwnEnvelope(json, claimed);

        read.Should().Throw<InvalidDataException>();
    }

    private static (JsonElement Json, byte[] Wire) Signed(string envelope, EthECKey key)
    {
        var access = new List<AccessListItem> { new(Target, [new byte[32]]) };
        var fields = new Dictionary<string, object?>
        {
            ["from"] = key.GetPublicAddress(), ["to"] = Target, ["value"] = "0x1", ["nonce"] = "0x7", ["gas"] = "0x5208",
            ["input"] = "0x12345678", ["chainId"] = Hex(Chain),
        };
        byte[] wire;
        if (envelope.StartsWith("legacy", StringComparison.Ordinal))
        {
            var unsigned = new LegacyTransactionChainId(Target, 1, 7, 1_000_000_000, 21000, "0x12345678", Chain);
            var hash = new Sha3Keccack().CalculateHash(unsigned.GetRLPEncodedRaw());
            var signature = envelope == "legacy-eip155" ? key.SignAndCalculateV(hash, Chain) : key.SignAndCalculateV(hash);
            var v = new BigInteger(signature.V, true, true);
            wire = new LegacyTransactionChainId(Target, 1, 7, 1_000_000_000, 21000, "0x12345678", Chain, signature.R, signature.S, signature.V)
                .GetRLPEncoded();
            fields["type"] = "0x0"; fields["gasPrice"] = Hex(1_000_000_000); fields["v"] = "0x" + v.ToString("x").TrimStart('0');
            fields["r"] = Hex(signature.R); fields["s"] = Hex(signature.S);
        }
        else
        {
            SignedTypeTransaction transaction = envelope == "type1-access-list"
                ? new Transaction2930(Chain, 7, 1_000_000_000, 21000, Target, 1, "0x12345678", access)
                : new Transaction1559(Chain, 7, 1, 1_000_000_000, 21000, Target, 1, "0x12345678", access);
            var signature = key.SignAndCalculateYParityV(new Sha3Keccack().CalculateHash(transaction.GetRLPEncodedRaw()));
            transaction.SetSignature(new Signature(signature.R, signature.S, signature.V));
            wire = transaction.GetRLPEncoded();
            fields["type"] = envelope == "type1-access-list" ? "0x1" : "0x2";
            if (envelope == "type1-access-list") fields["gasPrice"] = Hex(1_000_000_000);
            else { fields["maxPriorityFeePerGas"] = "0x1"; fields["maxFeePerGas"] = Hex(1_000_000_000); }
            fields["accessList"] = new[] { new { address = Target, storageKeys = new[] { "0x" + new string('0', 64) } } };
            fields["yParity"] = signature.V.Length == 0 || signature.V[0] == 0 ? "0x0" : "0x1";
            fields["r"] = Hex(signature.R); fields["s"] = Hex(signature.S);
        }
        fields["hash"] = "0x" + Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(wire));
        return (JsonSerializer.SerializeToElement(fields), wire);
    }

    private static JsonElement With(JsonElement json, string field, string value)
    {
        var copy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        copy[field] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.SerializeToElement(copy);
    }

    private static string Hex(long value) => "0x" + value.ToString("x");
    private static string Hex(byte[] value) => "0x" + new BigInteger(value, true, true).ToString("x").TrimStart('0');
}
