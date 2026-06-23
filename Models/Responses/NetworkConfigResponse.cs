namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Public, read-only view of the backend's blockchain RPC/network configuration.
/// Secrets (ApiToken, ApiKey, PrivateKey) are deliberately excluded.
/// </summary>
public class NetworkConfigResponse
{
    /// <summary>Default network name, lowercased ("devnet" | "testnet" | "mainnet").</summary>
    public string DefaultNetwork { get; set; } = string.Empty;

    /// <summary>Per-network chain endpoint configuration, keyed by "devnet"/"testnet"/"mainnet".</summary>
    public Dictionary<string, NetworkChainsConfig> Networks { get; set; } = new();
}

/// <summary>
/// Chain endpoints available for a single network.
/// </summary>
public class NetworkChainsConfig
{
    public ChainEndpointConfig? Algorand { get; set; }
    public ChainEndpointConfig? Solana { get; set; }
    public ChainEndpointConfig? Ethereum { get; set; }
}

/// <summary>
/// Public endpoint/metadata for a single chain on a single network.
/// </summary>
public class ChainEndpointConfig
{
    public string NodeUrl { get; set; } = string.Empty;
    public string? IndexerUrl { get; set; }
    public bool IsEnabled { get; set; }
    public string? ExplorerUrl { get; set; }
    public string? NativeToken { get; set; }
    public int? Decimals { get; set; }
}
