extern alias BCCrypto2;
using System.Text;
using FluentAssertions;

using AZOA.WebAPI.Services.Signing;
using Ed25519PrivateKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// user-sovereign-identity AC1: the ed25519 challenge-signature verifier. Uses a REAL
/// Algorand keypair (Algorand2 owns the address ↔ ed25519 pubkey decode) and a REAL
/// ed25519 signature (Bouncy Castle 2.x) so the round-trip is genuine — a correct
/// signature verifies; a tampered message/signature/address fails closed.
/// </summary>
public class Ed25519SignatureVerifierTests
{
    private readonly WalletSignatureVerifier _verifier = new();

    /// <summary>Generate a real Algorand account and sign <paramref name="message"/> with
    /// its ed25519 private key via Bouncy Castle.</summary>
    private static (string address, byte[] message, byte[] signature) RealSignedChallenge(string message)
    {
        var account = new Algorand.Algod.Model.Account();
        var address = account.Address.EncodeAsString();

        // The Algorand clear-text private key is the 32-byte ed25519 seed (first 32 of
        // the 64-byte expanded key). Build a BC private key from the seed and sign.
        var seed = account.KeyPair.ClearTextPrivateKey;
        var seed32 = seed.Length == 32 ? seed : seed[..32];
        var priv = new Ed25519PrivateKeyParameters(seed32, 0);
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);

        var signer = new Ed25519Signer();
        signer.Init(true, priv);
        signer.BlockUpdate(msgBytes, 0, msgBytes.Length);
        var sig = signer.GenerateSignature();

        return (address, msgBytes, sig);
    }

    [Fact]
    public void VerifyAlgorand_CorrectSignature_ReturnsTrue()
    {
        var (address, message, signature) = RealSignedChallenge("AZOA-AUTH-v1 challenge nonce-abc");
        _verifier.Verify("algorand", address, message, signature).Should().BeTrue();
    }

    [Fact]
    public void VerifyAlgorand_TamperedMessage_ReturnsFalse()
    {
        var (address, _, signature) = RealSignedChallenge("AZOA-AUTH-v1 challenge nonce-abc");
        var tampered = System.Text.Encoding.UTF8.GetBytes("AZOA-AUTH-v1 challenge nonce-XYZ");
        _verifier.Verify("algorand", address, tampered, signature).Should().BeFalse();
    }

    [Fact]
    public void VerifyAlgorand_WrongAddress_ReturnsFalse()
    {
        var (_, message, signature) = RealSignedChallenge("hello");
        var (otherAddress, _, _) = RealSignedChallenge("unrelated");
        _verifier.Verify("algorand", otherAddress, message, signature).Should().BeFalse();
    }

    [Fact]
    public void VerifyAlgorand_WrongLengthSignature_FailsClosed()
    {
        var (address, message, _) = RealSignedChallenge("hello");
        _verifier.Verify("algorand", address, message, new byte[10]).Should().BeFalse();
    }

    [Fact]
    public void VerifyAlgorand_MalformedAddress_FailsClosed()
        => _verifier.Verify("algorand", "NOT-AN-ADDRESS", System.Text.Encoding.UTF8.GetBytes("x"), new byte[64]).Should().BeFalse();

    [Fact]
    public void Verify_UnsupportedChain_FailsClosed()
    {
        _verifier.Verify("unknown", "0xabc", new byte[] { 1 }, new byte[64]).Should().BeFalse();

    }
}
