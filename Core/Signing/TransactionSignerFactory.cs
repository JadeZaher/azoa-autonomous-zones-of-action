using AZOA.WebAPI.Interfaces.Signing;

namespace AZOA.WebAPI.Core.Signing;

/// <summary>
/// Resolves <see cref="ITransactionSigner"/> instances by chain type, mirroring
/// <c>BlockchainProviderFactory</c> (signing-core-keystone D3). All registered
/// signers are injected; the factory indexes them by their <c>ChainType</c>
/// (case-insensitive). Adding a chain is one new signer + one DI registration.
/// </summary>
public sealed class TransactionSignerFactory : ITransactionSignerFactory
{
    private readonly IReadOnlyDictionary<string, ITransactionSigner> _signers;

    public TransactionSignerFactory(IEnumerable<ITransactionSigner> registeredSigners)
    {
        ArgumentNullException.ThrowIfNull(registeredSigners);
        var map = new Dictionary<string, ITransactionSigner>(StringComparer.OrdinalIgnoreCase);
        foreach (var signer in registeredSigners)
            map[signer.ChainType] = signer;
        _signers = map;
    }

    public ITransactionSigner GetSigner(string chainType)
    {
        if (!TryGetSigner(chainType, out var signer) || signer is null)
            throw new InvalidOperationException($"No transaction signer registered for chain type: {chainType}");
        return signer;
    }

    public bool TryGetSigner(string chainType, out ITransactionSigner? signer)
    {
        if (string.IsNullOrWhiteSpace(chainType))
        {
            signer = null;
            return false;
        }
        return _signers.TryGetValue(chainType, out signer);
    }
}
