using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using SolanaAccount = Solnet.Wallet.Account;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <inheritdoc/>
public sealed class SolanaTransactionSigner : ITransactionSigner, IPartialTransactionSigner
{
    /// <inheritdoc/>
    public string ChainType => "Solana";

    /// <inheritdoc/>
    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        if (key?.PrivateKey is not { Length: 64 }) return Fail("Solana signing key must be 64 bytes.");
        if (!TryMessage(canonicalTxn, out var message) || message.RequiredSignatures != 1)
            return Fail("A canonical single-signer Solana message is required.");
        var signature = SignPartial(canonicalTxn, message.StaticKeys[0].Key, key);
        if (signature.IsError || signature.Result is null) return signature;
        return AZOAResult<byte[]>.Success(SolanaMessageEnvelope.Populate(canonicalTxn, [signature.Result]));
    }

    /// <inheritdoc/>
    public AZOAResult<byte[]> SignPartial(byte[] canonicalMessage, string expectedSigner, SigningKeyMaterial key)
    {
        if (!TryMessage(canonicalMessage, out var message)) return Fail("A canonical Solana message is required.");
        if (key?.PrivateKey is not { Length: 64 } secret) return Fail("Solana signing requires a 64-byte secret.");
        var required = message.RequiredKeys
            .FirstOrDefault(publicKey => publicKey.Key == expectedSigner);
        if (required is null || !required.KeyBytes.AsSpan().SequenceEqual(secret.AsSpan(32, 32)))
            return Fail("Custody key does not match the declared required signer.");
        var account = new SolanaAccount(secret, required.KeyBytes);
        var signature = account.Sign(canonicalMessage);
        return signature.Length == 64 && required.Verify(canonicalMessage, signature)
            ? AZOAResult<byte[]>.Success(signature)
            : Fail("Detached Solana signature failed verification.");
    }

    private static bool TryMessage(byte[]? bytes, out SolanaMessageEnvelope.Parsed message)
    {
        message = null!;
        if (bytes is null || bytes.Length < 4) return false;
        try
        {
            message = SolanaMessageEnvelope.Read(bytes);
            return 1 + message.RequiredSignatures * 64 + bytes.Length <= 1232;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static AZOAResult<byte[]> Fail(string message) => AZOAResult<byte[]>.Failure(message);
}
