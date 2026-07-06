using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using Solnet.Rpc.Models;
using SolanaAccount = Solnet.Wallet.Account;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Real Ed25519 Solana transaction signer (final-hardening B1). See Providers/Blockchain/Solana/AGENTS.md §signer.</summary>
public sealed class SolanaTransactionSigner : ITransactionSigner
{
    /// <summary>Length of the raw Solana secret key (32-byte seed ++ 32-byte public key).</summary>
    private const int SolanaSecretKeyLength = 64;

    public string ChainType => "Solana";

    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        if (canonicalTxn is null || canonicalTxn.Length == 0)
            return Fail("Canonical transaction bytes are required for signing.");
        if (key is null)
            return Fail("Signing key material is required.");

        // Fail-closed on a wrong-shaped key BEFORE constructing a signer: a Solana
        // secret is exactly 64 bytes (seed ++ pubkey). Never sign with an empty or
        // malformed key.
        byte[] secret = key.PrivateKey;
        if (secret is null || secret.Length != SolanaSecretKeyLength)
            return Fail($"Solana signing key must be {SolanaSecretKeyLength} bytes (seed ++ public key).");

        try
        {
            // Decode the canonical unsigned message the provider compiled
            // (TransactionBuilder.CompileMessage()). AccountKeys[0] is the
            // fee-payer / first required signer.
            var message = Message.Deserialize(canonicalTxn);
            if (message is null || message.AccountKeys is null || message.AccountKeys.Count == 0)
                return Fail("Failed to decode canonical Solana message bytes.");

            // Reconstruct the signing account from the raw secret. The 32-byte public
            // key lives in secret[32..64]; passing it explicitly avoids re-deriving.
            var publicKey = new byte[32];
            Array.Copy(secret, SolanaSecretKeyLength - 32, publicKey, 0, 32);
            var account = new SolanaAccount(secret, publicKey);

            // The signer must be the message's first required signer (the fee payer).
            // Verifying this fails closed if custody handed us the wrong key rather
            // than emitting a transaction that would be rejected on-chain.
            if (!message.AccountKeys[0].KeyBytes.AsSpan().SequenceEqual(publicKey))
                return Fail("Signing key does not match the transaction's required signer (fee payer).");

            // Ed25519 sign over the canonical message bytes (Solnet owns the curve math).
            byte[] signature = account.Sign(canonicalTxn);
            if (signature is null || signature.Length != 64)
                return Fail("Ed25519 signing produced an invalid signature length.");

            // Assemble the submittable wire transaction: [compact-u16 sig count]
            // [64-byte signatures...][message bytes]. Populate + Serialize is the
            // canonical Solnet path and byte-matches TransactionBuilder.Build(signer).
            var transaction = Transaction.Populate(message, new List<byte[]> { signature });
            byte[] submittable = transaction.Serialize();
            if (submittable is null || submittable.Length == 0)
                return Fail("Signed transaction serialized to empty bytes.");

            // Defence in depth: reject if the assembled signature does not verify.
            if (!transaction.VerifySignatures())
                return Fail("Assembled Solana transaction failed signature verification.");

            return new AZOAResult<byte[]>
            {
                IsError = false,
                Result = submittable,
                Message = $"Signed Solana transaction for signer {message.AccountKeys[0].Key}"
            };
        }
        catch (Exception ex)
        {
            // Message never includes key bytes.
            return Fail($"Solana signing failed: {ex.Message}", ex);
        }
    }

    private static AZOAResult<byte[]> Fail(string message, Exception? ex = null) =>
        new() { IsError = true, Message = message, Exception = ex };
}
