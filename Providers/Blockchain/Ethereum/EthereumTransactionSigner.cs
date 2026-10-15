using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>
/// Real secp256k1 signer for EIP-1559 (EIP-2718 type-2) Ethereum transactions.
/// </summary>
/// <remarks>
/// Canonical-byte contract (see <c>Providers/Blockchain/Ethereum/AGENTS.md</c> §signer):
/// <list type="bullet">
/// <item><c>canonicalTxn</c> = the type-2 SIGNING payload
/// <c>0x02 ‖ rlp([chainId, nonce, maxPriorityFeePerGas, maxFeePerGas, gasLimit, to, value, data, accessList])</c>,
/// i.e. exactly <c>Transaction1559.GetRLPEncodedRaw()</c>.</item>
/// <item>The return value is the fully framed submittable envelope
/// <c>0x02 ‖ rlp([…, yParity, r, s])</c>, i.e. <c>Transaction1559.GetRLPEncoded()</c> —
/// the exact bytes <c>eth_sendRawTransaction</c> takes. The provider hex-encodes and
/// submits them unmodified; re-encoding would invalidate the signature.</item>
/// </list>
/// The signer never resolves keys and never touches the network.
/// </remarks>
public sealed class EthereumTransactionSigner : ITransactionSigner
{
    /// <summary>Byte length of a raw secp256k1 private key.</summary>
    public const int PrivateKeyLength = 32;

    /// <summary>EIP-2718 type byte for an EIP-1559 fee-market transaction.</summary>
    public const byte Eip1559TransactionType = 0x02;

    private static readonly Sha3Keccack Keccak = new();

    public string ChainType => "Ethereum";

    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        if (canonicalTxn is null || canonicalTxn.Length == 0)
            return Fail("Canonical transaction bytes are required for signing.");
        if (key is null)
            return Fail("Signing key material is required.");

        // Fail closed on a wrong-shaped key BEFORE constructing an EC key: an Ethereum
        // secret is exactly 32 bytes. Never sign with an empty or malformed key.
        var secret = key.PrivateKey;
        if (secret is null || secret.Length != PrivateKeyLength)
            return Fail($"Ethereum signing key must be {PrivateKeyLength} bytes (a raw secp256k1 private key).");

        // Only EIP-1559 typed transactions are signable here. A legacy or type-1
        // payload would need different V/chain-id handling; refusing is safer than
        // producing a signature over a shape this signer did not validate.
        if (canonicalTxn[0] != Eip1559TransactionType)
            return Fail(
                $"Canonical Ethereum bytes must be an EIP-1559 (type 0x02) signing payload but start with "
                + $"0x{canonicalTxn[0]:x2}. This signer does not produce legacy-transaction signatures.");

        try
        {
            // Round-trip the canonical bytes back into the typed transaction so the
            // signed envelope is re-framed by Nethereum's RLP encoder rather than by
            // hand-splicing signature fields into the payload.
            var transaction = Transaction1559Encoder.Current.Decode(canonicalTxn);
            if (transaction is null)
                return Fail("Failed to decode the canonical EIP-1559 signing payload.");

            EthECKey signingKey;
            try
            {
                signingKey = new EthECKey(secret, isPrivate: true);
            }
            catch (Exception ex)
            {
                // Never echo key bytes.
                return Fail("The resolved Ethereum signing key is not a valid secp256k1 scalar.", ex);
            }

            // EIP-1559 signs keccak256 over the type-prefixed unsigned payload and
            // carries y-parity (0/1) in place of the legacy chain-encoded V.
            var signature = signingKey.SignAndCalculateYParityV(Keccak.CalculateHash(canonicalTxn));
            if (signature is null)
                return Fail("secp256k1 signing produced no signature.");

            transaction.SetSignature(signature);

            var submittable = transaction.GetRLPEncoded();
            if (submittable is null || submittable.Length == 0)
                return Fail("The signed Ethereum transaction serialized to empty bytes.");

            // Defence in depth: recover the sender from the assembled envelope and
            // require it to be the key we just signed with. A framing mistake that
            // silently changed the recovered sender would send value from an account
            // the caller never authorized.
            var recovered = TransactionVerificationAndRecovery.GetSenderAddress(transaction);
            if (!EthereumAddress.AreSame(recovered, signingKey.GetPublicAddress()))
                return Fail("The assembled Ethereum transaction does not recover to the signing key's address.");

            return new AZOAResult<byte[]>
            {
                IsError = false,
                Result = submittable,
                Message = $"Signed EIP-1559 transaction for signer {signingKey.GetPublicAddress()}",
            };
        }
        catch (Exception ex)
        {
            // Message never includes key bytes.
            return Fail($"Ethereum signing failed: {ex.Message}", ex);
        }
    }

    private static AZOAResult<byte[]> Fail(string message, Exception? ex = null) =>
        new() { IsError = true, Message = message, Exception = ex };
}
