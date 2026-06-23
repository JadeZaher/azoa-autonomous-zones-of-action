namespace AZOA.WebAPI.Core;

public class BlockchainNetworkConfig
{
    public string NodeUrl { get; set; } = string.Empty;
    public string? IndexerUrl { get; set; }
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

    /// <summary>
    /// Global blockchain settlement mode. <c>"Live"</c> (default) routes every
    /// chain to its real provider. <c>"Simulated"</c> short-circuits the factory
    /// to the <c>SimulatedBlockchainProvider</c> regardless of requested chain —
    /// mint/transfer/burn/balance are satisfied with deterministic, clearly
    /// marked (<c>sim:</c> prefixed) synthetic results and NO network I/O. The
    /// dev/test/demo and "no-chain" tenant default. Per-chain mode is a
    /// documented follow-up (db-only-null-provider plan D2).
    /// </summary>
    public string Mode { get; set; } = "Live";
}
