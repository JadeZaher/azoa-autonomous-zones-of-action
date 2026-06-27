// Bind ALL Bouncy Castle types in this file to the vetted 2.x assembly
// (BouncyCastle.Cryptography) via its csproj alias, never the legacy 1.8.8
// BouncyCastle.Crypto transitively bundled by the Algorand2/Solana SDKs.
// Mirrors the alias+using pattern of Services/Wormhole/Secp256k1VaaSignatureVerifier.cs.
extern alias BCCrypto2;
using AZOA.WebAPI.Interfaces;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;
using Ed25519PublicKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters;

namespace AZOA.WebAPI.Services.Signing;

// ─── DI registration (orchestrator applies to Program.cs — do NOT edit here) ───
//
//   builder.Services.AddSingleton<IWalletSignatureVerifier, Ed25519SignatureVerifier>();
//
// Stateless + thread-safe (no captured state), so Singleton is correct and cheapest.
// Register near the other crypto/auth services (e.g. WalletKeyService, Program.cs:370).

/// <summary>
/// Wallet-challenge signature verifier (user-sovereign-identity §1, AC1).
/// Dispatches on chain type:
/// <list type="bullet">
///   <item><c>algorand</c> → ed25519: decode the Algorand address to its 32-byte
///   ed25519 public key (the Algorand2 <see cref="Algorand.Address"/> owns the
///   canonical base32 + SHA-512/256 checksum decode), then verify the signature over
///   the raw message bytes with Bouncy Castle 2.x's <see cref="Ed25519Signer"/>.</item>
///   <item>any other chain → <see cref="NotSupportedException"/> (spec out-of-scope).</item>
/// </list>
///
/// <para><b>Crypto provenance:</b> address decode/checksum is the vetted Algorand2
/// package; ed25519 point arithmetic is the vetted Bouncy Castle 2.x library. This
/// class only wires the standard verify call around that arithmetic — no homebake
/// curve math. The challenge message is consumed AS-IS (the same bytes the server
/// issued in <c>WalletAuthChallenge.DomainMessage</c>); it is NOT re-hashed (ed25519
/// hashes internally).</para>
///
/// <para><b>Fail-closed:</b> a malformed address, wrong-length signature, or any
/// unexpected exception yields <c>false</c> — never an exception a caller could
/// mistake for "valid". Only the explicitly-unsupported-chain path throws.</para>
/// </summary>
public sealed class Ed25519SignatureVerifier : IWalletSignatureVerifier
{
    public bool Verify(string chainType, string address, byte[] message, byte[] signature)
    {
        if (string.IsNullOrWhiteSpace(chainType))
            return false;

        return chainType.Trim().ToLowerInvariant() switch
        {
            "algorand" => VerifyAlgorand(address, message, signature),
            // EVM/Solana signature verify is an explicit follow-up (spec out-of-scope).
            _ => throw new NotSupportedException(
                     $"Wallet-challenge signature verification is not supported for chain type '{chainType}'.")
        };
    }

    /// <summary>
    /// Verify an ed25519 signature over <paramref name="message"/> against the 32-byte
    /// public key decoded from an Algorand <paramref name="address"/>. Fail-closed on
    /// any malformed input.
    /// </summary>
    public bool VerifyAlgorand(string address, byte[] message, byte[] signature)
    {
        try
        {
            // ─── 0. Input sanity (fail-closed) ───
            if (string.IsNullOrWhiteSpace(address))
                return false;
            if (message is null || message.Length == 0)
                return false;
            // ed25519 signatures are always exactly 64 bytes.
            if (signature is null || signature.Length != 64)
                return false;

            // Reject a structurally-invalid address before touching crypto — the
            // Algorand2 checksum guard (same one AlgorandProvider.IsValid uses).
            if (!Algorand.Address.IsValid(address))
                return false;

            // ─── 1. Decode the address → 32-byte ed25519 public key ───
            // Algorand2's Address.Bytes is exactly the 32-byte raw ed25519 public key
            // (the base32 payload minus the 4-byte checksum). No hand-rolled base32.
            var publicKey = new Algorand.Address(address).Bytes;
            if (publicKey is null || publicKey.Length != 32)
                return false;

            // ─── 2. ed25519 verify over the raw message bytes (BC 2.x) ───
            var keyParams = new Ed25519PublicKeyParameters(publicKey, 0);
            var verifier = new Ed25519Signer();
            verifier.Init(false, keyParams);            // forSigning: false → verify mode
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch
        {
            // Fail-closed: a thrown verifier is "not valid", never success.
            return false;
        }
    }
}
