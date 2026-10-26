using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Nethereum.Model;
using Nethereum.Signer;

namespace AZOA.WebAPI.IntegrationTests;

/// <summary>A leading-zero fixture key must reach the provider as 32 bytes and still sign as itself.</summary>
public sealed class EthereumFixtureKeyTests
{
    // First byte 0x00: the 1-in-256 shape a random Hardhat key takes.
    private const string LeadingZeroKeyHex = "0x000883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    [Fact]
    public void LeadingZeroKey_ExportsAs32Bytes_AndSignsAsTheSameAddress()
    {
        var key = new EthECKey(LeadingZeroKeyHex);
        key.GetPrivateKeyAsBytes().Should().HaveCount(31, "the premise: Nethereum's minimal export drops the zero byte");

        var bytes = EthereumFixtureKey.PrivateKeyBytes(key);
        Convert.ToHexStringLower(bytes).Should().Be(LeadingZeroKeyHex[2..]);
        EthereumFixtureKey.PrivateKeyHex(key).Should().Be(LeadingZeroKeyHex);

        using var material = new SigningKeyMaterial(bytes);
        var transaction = new Transaction1559(31337, 0, 1, 2, 21_000, "0x5B38Da6a701c568545dCfcB03FcB875f56beddC4", 1, "0x", null);
        var signed = new EthereumTransactionSigner().Sign(transaction.GetRLPEncodedRaw(), material);

        signed.IsError.Should().BeFalse(signed.Message);
        var sender = TransactionVerificationAndRecovery.GetSenderAddress(Transaction1559Encoder.Current.Decode(signed.Result!));
        EthereumAddress.AreSame(sender, key.GetPublicAddress()).Should().BeTrue();
    }
}
