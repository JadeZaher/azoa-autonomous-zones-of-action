using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Nethereum.Signer;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Core;

public sealed class WalletSignatureVerifierTests
{
    [Fact]
    public void Ethereum_personal_sign_uses_exact_UTF8_bytes_and_rejects_other_authorities()
    {
        var signer = EthECKey.GenerateKey();
        var other = EthECKey.GenerateKey();
        var message = System.Text.Encoding.UTF8.GetBytes("AZOA-AUTH-v1\nissuer:azoa\nnonce:unicode-\u00e9");
        var signature = Convert.FromHexString(new EthereumMessageSigner().Sign(message, signer)[2..]);
        var verifier = new WalletSignatureVerifier();
        verifier.Verify("Ethereum", signer.GetPublicAddress(), message, signature).Should().BeTrue();
        verifier.Verify("ethereum", signer.GetPublicAddress().ToLowerInvariant(), message, signature).Should().BeTrue();
        verifier.Verify("ethereum", other.GetPublicAddress(), message, signature).Should().BeFalse();
        verifier.Verify("ethereum", signer.GetPublicAddress(), [..message, 10], signature).Should().BeFalse();
        verifier.Verify("solana", signer.GetPublicAddress(), message, signature).Should().BeFalse();
        signature[64] = 35;
        verifier.Verify("ethereum", signer.GetPublicAddress(), message, signature).Should().BeFalse();
    }

    [Fact]
    public void Solana_signs_raw_message_and_preserves_address_case()
    {
        var account = new Account();
        var message = System.Text.Encoding.UTF8.GetBytes("AZOA-AUTH-v1\nissuer:azoa\nchain:solana\nnonce:one");
        var signature = account.Sign(message);
        var verifier = new WalletSignatureVerifier();
        verifier.Verify("solana", account.PublicKey.Key, message, signature).Should().BeTrue();
        verifier.Verify("solana", account.PublicKey.Key, [..message, 0], signature).Should().BeFalse();
        verifier.Verify("algorand", account.PublicKey.Key, message, signature).Should().BeFalse();
        WalletAuthIdentity.TryCreate(account.PublicKey.Key, "SOLANA", out var normalized, out var chain).Should().BeTrue();
        normalized.Should().Be(account.PublicKey.Key);
        chain.Should().Be("solana");
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void Signature_hex_is_not_misread_as_base64(int size)
    {
        var original = Enumerable.Repeat((byte)0xab, size).ToArray();
        AZOA.WebAPI.Helpers.Encoding.TryDecodeSignature(Convert.ToHexString(original), out var bare).Should().BeTrue();
        AZOA.WebAPI.Helpers.Encoding.TryDecodeSignature("0x" + Convert.ToHexString(original), out var prefixed).Should().BeTrue();
        AZOA.WebAPI.Helpers.Encoding.TryDecodeSignature(Convert.ToBase64String(original), out var encoded).Should().BeTrue();
        bare.Should().Equal(original);
        prefixed.Should().Equal(original);
        encoded.Should().Equal(original);
    }
}
