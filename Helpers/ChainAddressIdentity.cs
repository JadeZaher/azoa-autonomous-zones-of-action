using AZOA.WebAPI.Providers.Blockchain.Ethereum;

namespace AZOA.WebAPI.Helpers;

/// <summary>Compares chain addresses without folding case-sensitive public-key encodings.</summary>
public static class ChainAddressIdentity
{
    public static bool Equivalent(string chain, string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (!chain.Equals("Ethereum", StringComparison.OrdinalIgnoreCase))
            return string.Equals(left, right, StringComparison.Ordinal);
        return EthereumAddress.AreSame(left, right);
    }
}
