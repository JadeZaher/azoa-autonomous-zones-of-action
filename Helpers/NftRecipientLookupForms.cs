using AZOA.WebAPI.Providers.Blockchain.Ethereum;

namespace AZOA.WebAPI.Helpers;

/// <summary>Stored (chain, address) spellings an exact indexed recipient lookup must probe; see Services/Users/AGENTS.md §NFT recipient resolution.</summary>
public static class NftRecipientLookupForms
{
    /// <summary>Chain: as given, canonical display name, lowercase. Address: as given plus its per-chain canonical form (never blind case-folding).</summary>
    public static IReadOnlyList<(string Chain, string Address)> For(string chainType, string address)
    {
        if (string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(address)) return [];
        var trimmed = address.Trim();
        var chains = new[] { chainType.Trim(), WalletBootstrapIdentity.CanonicalChain(chainType), ChainNames.NormalizeChain(chainType) }
            .Where(chain => !string.IsNullOrEmpty(chain)).Select(chain => chain!).Distinct(StringComparer.Ordinal).ToArray();
        var addresses = new HashSet<string>(StringComparer.Ordinal) { trimmed };
        if (WalletAuthIdentity.TryCreate(trimmed, chainType, out var canonical, out _)) addresses.Add(canonical);
        if (ChainNames.NormalizeChain(chainType) == "ethereum" && EthereumAddress.TryParse(trimmed, out var checksummed))
            addresses.Add(checksummed);
        return chains.SelectMany(chain => addresses.Select(form => (chain, form))).ToArray();
    }
}
