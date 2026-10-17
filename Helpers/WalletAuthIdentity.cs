using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using Solnet.Wallet;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical chain-aware identity used by wallet authentication and unique bindings.</summary>
public static class WalletAuthIdentity
{
    public static bool TryCreate(string? address, string? chainType, out string canonicalAddress, out string canonicalChain)
    {
        canonicalAddress = string.Empty;
        canonicalChain = chainType?.Trim().ToLowerInvariant() ?? string.Empty;
        var value = address?.Trim() ?? string.Empty;
        switch (canonicalChain)
        {
            case "ethereum":
                if (!EthereumAddress.TryParse(value, out var parsed) || parsed == EthereumAddress.Zero) return false;
                canonicalAddress = parsed.ToLowerInvariant();
                return true;
            case "algorand":
                if (!Algorand.Address.IsValid(value)) return false;
                canonicalAddress = new Algorand.Address(value).ToString();
                return true;
            case "solana":
                try
                {
                    var key = new PublicKey(value);
                    if (key.KeyBytes.Length != 32 || key.Key != value) return false;
                    canonicalAddress = key.Key;
                    return true;
                }
                catch (ArgumentException) { return false; }
                catch (FormatException) { return false; }
            default:
                return false;
        }
    }
}
