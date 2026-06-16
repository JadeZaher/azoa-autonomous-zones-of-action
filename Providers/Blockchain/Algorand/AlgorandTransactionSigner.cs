using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using OASIS.WebAPI.Core.Signing;
using OASIS.WebAPI.Interfaces.Signing;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Blockchain.Algorand;

/// <summary>
/// Real Ed25519 Algorand transaction signer (signing-core-keystone Phase 2).
/// Crypto comes ENTIRELY from the already-referenced <c>Algorand2</c> package —
/// no hand-rolled msgpack or curve math.
/// <para>
/// Seam contract: the provider builds a typed <see cref="Transaction"/> (it owns
/// the chain-specific shape), encodes it canonically with
/// <see cref="Encoder.EncodeToMsgPackOrdered(object)"/>, and hands those opaque
/// bytes here. This signer decodes them back to a <see cref="Transaction"/>,
/// reconstructs an <see cref="Account"/> from the supplied private-key bytes,
/// signs (canonical msgpack via <c>Transaction.Sign</c>), and returns the
/// submittable signed-envelope bytes (msgpack-ordered) ready for Algod
/// <c>POST /v2/transactions</c>. The five asset shapes (acfg-create,
/// acfg-destroy, axfer-transfer, axfer-clawback, axfer-opt-in) all derive from
/// <see cref="Transaction"/>, so a single decode→sign→encode path covers them.
/// </para>
/// </summary>
public sealed class AlgorandTransactionSigner : ITransactionSigner
{
    public string ChainType => "Algorand";

    public OASISResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        if (canonicalTxn is null || canonicalTxn.Length == 0)
            return Fail("Canonical transaction bytes are required for signing.");
        if (key is null)
            return Fail("Signing key material is required.");

        try
        {
            // Decode the canonical unsigned transaction the provider built.
            var txn = Encoder.DecodeFromMsgPack<Transaction>(canonicalTxn);
            if (txn is null)
                return Fail("Failed to decode canonical Algorand transaction bytes.");

            // Reconstruct the signing account from the raw private-key bytes.
            // Algorand2's Account(byte[]) treats the input as the 32-byte Ed25519
            // seed (the same value WalletKeyService stores as the private key hex).
            var account = new Account(key.PrivateKey);

            // Canonical msgpack sign — Algorand2 owns the domain-prefix + ordering.
            var signed = txn.Sign(account);

            var submittable = Encoder.EncodeToMsgPackOrdered(signed);
            if (submittable is null || submittable.Length == 0)
                return Fail("Signed transaction encoded to empty bytes.");

            return new OASISResult<byte[]>
            {
                IsError = false,
                Result = submittable,
                Message = $"Signed Algorand transaction {txn.TxID()}"
            };
        }
        catch (Exception ex)
        {
            return Fail($"Algorand signing failed: {ex.Message}", ex);
        }
    }

    private static OASISResult<byte[]> Fail(string message, Exception? ex = null) =>
        new() { IsError = true, Message = message, Exception = ex };
}
