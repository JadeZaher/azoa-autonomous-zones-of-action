using OASIS.WebAPI.Core.Signing;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Signing;

/// <summary>
/// Chain-agnostic server-side transaction signer (signing-core-keystone D2/D3).
/// Implementations take canonical, chain-native UNSIGNED transaction bytes plus
/// the key material the custody layer resolves, and return the submittable SIGNED
/// envelope bytes for that chain's broadcast endpoint.
/// <para>
/// Nothing chain-specific leaks through this contract: the canonical bytes are
/// opaque to callers, and the signer is selected by <see cref="ChainType"/> via
/// <see cref="ITransactionSignerFactory"/>. A signer NEVER reaches into wallet
/// storage or <c>WalletKeyService</c> — custody resolution is the caller's job.
/// </para>
/// </summary>
public interface ITransactionSigner
{
    /// <summary>Chain key this signer serves, e.g. "Algorand". Matches the provider's ChainType.</summary>
    string ChainType { get; }

    /// <summary>
    /// Sign the supplied canonical unsigned transaction bytes with <paramref name="key"/>
    /// and return the submittable signed envelope bytes.
    /// </summary>
    /// <param name="canonicalTxn">
    /// Canonical chain-native UNSIGNED transaction bytes (for Algorand, the
    /// msgpack-ordered encoding of the transaction, i.e. <c>BytesToSign</c> input).
    /// </param>
    /// <param name="key">Decrypted key material; the caller owns its lifetime/zeroing.</param>
    OASISResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key);
}
