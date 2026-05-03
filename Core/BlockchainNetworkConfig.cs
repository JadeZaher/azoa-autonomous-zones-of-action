namespace OASIS.WebAPI.Core;

public class BlockchainNetworkConfig
{
    public string NodeUrl { get; set; } = string.Empty;
    public string? ApiToken { get; set; }
    public string? ApiKey { get; set; }
    public string? PrivateKey { get; set; }
    public int? TimeoutMs { get; set; }
    public int? RetryCount { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class BlockchainChainConfig
{
    public string ChainType { get; set; } = string.Empty;
    public BlockchainNetworkConfig Devnet { get; set; } = new();
    public BlockchainNetworkConfig Testnet { get; set; } = new();
    public BlockchainNetworkConfig Mainnet { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class BlockchainConfig
{
    public string DefaultChain { get; set; } = "Algorand";
    public ChainNetwork DefaultNetwork { get; set; } = ChainNetwork.Devnet;
    public List<BlockchainChainConfig> Chains { get; set; } = new();
}
