using AZOA.WebAPI.Core.Signing;
using System.Numerics;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class EthereumSignedEnvelopeTests
{
    [Fact]
    public void TwoCanonicalRealSignaturesCanCoverTheSameCompleteUnsignedTransaction()
    {
        var key = EthECKey.GenerateKey(); var unsigned = Transaction().GetRLPEncodedRaw();
        var original = Sign(key, unsigned); var alternate = EthereumAlternateSignature.Sign(unsigned, key);
        alternate.Should().NotEqual(original);
        var first = EthereumSignedEnvelope.Verify(original, unsigned, key.GetPublicAddress());
        var second = EthereumSignedEnvelope.Verify(alternate, unsigned, key.GetPublicAddress());
        second.Should().NotBe(first);
        Transaction1559Encoder.Current.Decode(alternate).GetRLPEncodedRaw().Should().Equal(unsigned);
    }

    [Theory]
    [InlineData("high-s")]
    [InlineData("zero-r")]
    [InlineData("zero-s")]
    [InlineData("parity")]
    public void NoncanonicalSignatureScalarsNeverPassFullWireValidation(string mutation)
    {
        var key = EthECKey.GenerateKey(); var unsigned = Transaction().GetRLPEncodedRaw();
        var transaction = Transaction1559Encoder.Current.Decode(Sign(key, unsigned));
        var signature = transaction.Signature;
        var order = new BigInteger(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"), true, true);
        var r = mutation == "zero-r" ? new byte[] { 0 } : signature.R;
        var s = mutation == "zero-s" ? new byte[] { 0 } : mutation == "high-s"
            ? (order - new BigInteger(signature.S, true, true)).ToByteArray(true, true) : signature.S;
        var parity = mutation == "parity" ? new byte[] { 2 } : signature.V;
        var wire = SignedWire(unsigned, parity, r, s);
        var read = () => EthereumSignedEnvelope.Verify(wire, unsigned, key.GetPublicAddress());
        read.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void CompleteOriginalWireAndRealOwnerSignatureProduceItsActualHash()
    {
        var key = EthECKey.GenerateKey(); var unsigned = Transaction().GetRLPEncodedRaw();
        var wire = Sign(key, unsigned);
        EthereumSignedEnvelope.Verify(wire, unsigned, key.GetPublicAddress())
            .Should().Be("0x" + Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(wire)));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("sender")]
    [InlineData("chain")]
    [InlineData("fee")]
    [InlineData("target")]
    [InlineData("trailing")]
    [InlineData("unsigned")]
    public void BrowserMutationCannotChangeAnyOriginalSigningBytes(string mutation)
    {
        var key = EthECKey.GenerateKey(); var unsigned = Transaction().GetRLPEncodedRaw();
        var altered = mutation switch
        {
            "nonce" => Transaction(nonce: 8), "chain" => Transaction(chain: 1),
            "fee" => Transaction(fee: 200), "target" => Transaction(target: "0x" + new string('b', 40)),
            _ => Transaction()
        };
        var wire = Sign(mutation == "sender" ? EthECKey.GenerateKey() : key, altered.GetRLPEncodedRaw());
        if (mutation == "trailing") wire = [.. wire, 0];
        if (mutation == "unsigned") wire = unsigned;
        var read = () => EthereumSignedEnvelope.Verify(wire, unsigned, key.GetPublicAddress());
        read.Should().Throw<Exception>();
    }

    private static Transaction1559 Transaction(int nonce = 7, int chain = 31337, int fee = 100, string? target = null)
        => new(chain, nonce, 1, fee, 300000, target ?? "0x" + new string('a', 40), 0, "0x12345678", null);

    /// <summary>Hand-encodes 0x02 || rlp([...unsigned fields, yParity, r, s]); Nethereum's encoder cannot emit a zero scalar.</summary>
    private static byte[] SignedWire(byte[] unsigned, byte[] parity, byte[] r, byte[] s)
    {
        var header = unsigned[1];
        var bodyStart = header >= 0xf8 ? 2 + (header - 0xf7) : 2;
        var body = unsigned[bodyStart..].Concat(Item(parity)).Concat(Item(r)).Concat(Item(s)).ToArray();
        var length = body.Length < 56 ? [(byte)(0xc0 + body.Length)] : LongHeader(0xf7, body.Length);
        return new byte[] { 0x02 }.Concat(length).Concat(body).ToArray();
        static byte[] Item(byte[] value)
        {
            var trimmed = value.SkipWhile(b => b == 0).ToArray();
            return trimmed.Length == 1 && trimmed[0] < 0x80 ? trimmed : [(byte)(0x80 + trimmed.Length), .. trimmed];
        }
        static byte[] LongHeader(int offset, int length)
        {
            var bytes = BitConverter.GetBytes(length).Reverse().SkipWhile(b => b == 0).ToArray();
            return [(byte)(offset + bytes.Length), .. bytes];
        }
    }

    private static byte[] Sign(EthECKey key, byte[] unsigned)
    {
        using var material = new SigningKeyMaterial(key.GetPrivateKeyAsBytes());
        var result = new EthereumTransactionSigner().Sign(unsigned, material);
        result.IsError.Should().BeFalse(result.Message);
        return result.Result!;
    }
}
