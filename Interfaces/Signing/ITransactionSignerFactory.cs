namespace OASIS.WebAPI.Interfaces.Signing;

/// <summary>
/// Selects an <see cref="ITransactionSigner"/> by chain type, mirroring
/// <c>IBlockchainProviderFactory</c> (signing-core-keystone D3). Keeping the
/// signer seam independent of provider construction lets the
/// <c>db-only-null-provider</c> (no signer) and future chains compose cleanly —
/// adding a chain is one signer implementation + one DI registration.
/// </summary>
public interface ITransactionSignerFactory
{
    /// <summary>
    /// Resolve the signer for <paramref name="chainType"/> (case-insensitive).
    /// Throws <see cref="InvalidOperationException"/> when no signer is registered.
    /// </summary>
    ITransactionSigner GetSigner(string chainType);

    /// <summary>Non-throwing variant; returns false when no signer is registered.</summary>
    bool TryGetSigner(string chainType, out ITransactionSigner? signer);
}
