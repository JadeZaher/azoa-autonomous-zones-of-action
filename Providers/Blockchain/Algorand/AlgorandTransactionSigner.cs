using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>
/// Real Ed25519 Algorand transaction signer (signing-core-keystone Phase 2).
/// Ed25519 comes from the already-referenced <c>Algorand2</c> package (no curve math here).
/// <para>Signs the provider's exact canonical bytes (<c>"TX" || txn</c>) and frames the canonical
/// <c>{sig, txn}</c> envelope; see <c>Providers/Blockchain/Algorand/AGENTS.md</c> §exact-bytes signing.</para>
/// </summary>
public sealed class AlgorandTransactionSigner : ITransactionSigner
{
    public string ChainType => "Algorand";

    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        if (canonicalTxn is null || canonicalTxn.Length == 0)
            return Fail("Canonical transaction bytes are required for signing.");
        if (key is null)
            return Fail("Signing key material is required.");

        try
        {
            // Decode only to prove the bytes are an Algorand transaction; the SIGNATURE covers the
            // provider's exact bytes. The SDK cannot round-trip every shape (a zero-amount axfer decodes
            // as an opt-in and loses aclose), so signing the decoded object could sign a different txn.
            if (Encoder.DecodeFromMsgPack<Transaction>(canonicalTxn) is null)
                return Fail("Failed to decode canonical Algorand transaction bytes.");

            // Algorand2's Account(byte[]) treats the input as the 32-byte Ed25519 seed
            // (the same value WalletKeyService stores as the private key hex).
            var account = new Account(key.PrivateKey);
            var signature = account.SignRawBytes([.. "TX"u8, .. canonicalTxn]).Bytes;

            return new AZOAResult<byte[]>
            {
                IsError = false,
                Result = [.. EnvelopePrefix, .. signature, .. EnvelopeTxnKey, .. canonicalTxn],
                Message = "Signed Algorand transaction."
            };
        }
        catch (Exception ex)
        {
            return Fail($"Algorand signing failed: {ex.Message}", ex);
        }
    }

    /// <summary>Canonical (key-sorted) signed envelope: fixmap(2) "sig" bin8(64) … "txn" &lt;txn map&gt;.</summary>
    private static readonly byte[] EnvelopePrefix = [0x82, 0xa3, (byte)'s', (byte)'i', (byte)'g', 0xc4, 0x40];
    private static readonly byte[] EnvelopeTxnKey = [0xa3, (byte)'t', (byte)'x', (byte)'n'];

    private static AZOAResult<byte[]> Fail(string message, Exception? ex = null) =>
        new() { IsError = true, Message = message, Exception = ex };
}
