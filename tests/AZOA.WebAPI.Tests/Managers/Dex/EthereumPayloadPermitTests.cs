using System.Numerics;
using System.Text;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Nethereum.Util;
using Nethereum.Signer;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class EthereumPayloadPermitTests
{
    [Fact]
    public void RetainedPermitRecoversAgainstExactPublicDomainWithoutItsPrivateKey()
    {
        var key = EthECKey.GenerateKey();
        var digest = EthereumPayloadPermit.Digest("AZOA DEX", "1", 31337, "0x" + new string('a', 40), [1, 2, 3]);
        var permit = EthereumPayloadPermit.Sign(key, digest);
        EthereumPayloadPermit.RequireSigner(digest, permit, key.GetPublicAddress());
        var otherDomain = EthereumPayloadPermit.Digest("AZOA NFT", "1", 31337, "0x" + new string('a', 40), [1, 2, 3]);
        var read = () => EthereumPayloadPermit.RequireSigner(otherDomain, permit, key.GetPublicAddress());
        read.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("AZOA NFT")]
    [InlineData("AZOA DEX")]
    public void Digest_matches_independently_assembled_static_domain_and_payload_words(string name)
    {
        const string contract = "0x1122334455667788990011223344556677889900";
        byte[] payload = [0, 1, 2, 255];
        var hash = new Sha3Keccack();
        byte[] Text(string text) => hash.CalculateHash(Encoding.UTF8.GetBytes(text));
        var chain = new byte[32]; chain[30] = 0x7a; chain[31] = 0x69;
        var address = new byte[32]; Convert.FromHexString(contract[2..]).CopyTo(address, 12);
        var domain = hash.CalculateHash(Text("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)")
            .Concat(Text(name)).Concat(Text("1")).Concat(chain).Concat(address).ToArray());
        var message = hash.CalculateHash(Text("Execution(bytes32 payloadHash)").Concat(hash.CalculateHash(payload)).ToArray());
        var expected = hash.CalculateHash(new byte[] { 0x19, 0x01 }.Concat(domain).Concat(message).ToArray());
        var actual = EthereumPayloadPermit.Digest(name, "1", new BigInteger(31337), contract, payload);
        actual.Should().Equal(expected);
        EthereumPayloadPermit.Digest(name, "1", 1, contract, payload).Should().NotEqual(actual);
        EthereumPayloadPermit.Digest(name, "1", 31337, "0x1122334455667788990011223344556677889901", payload).Should().NotEqual(actual);
        EthereumPayloadPermit.Digest(name == "AZOA NFT" ? "AZOA DEX" : "AZOA NFT", "1", 31337, contract, payload).Should().NotEqual(actual);
    }
}
