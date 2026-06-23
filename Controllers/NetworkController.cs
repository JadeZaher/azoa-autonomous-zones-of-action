using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Public, read-only blockchain RPC/network configuration.
/// Lets the frontend resolve RPC endpoints from the backend (single source of truth)
/// instead of hardcoding them. Secrets are never exposed.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class NetworkController : ControllerBase
{
    private readonly IConfiguration _config;

    public NetworkController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet]
    [AllowAnonymous]
    public ActionResult<AZOAResult<NetworkConfigResponse>> Get()
    {
        var configManager = new BlockchainConfigurationManager(_config);

        var defaultNetwork = (_config.GetValue<string>("Blockchain:DefaultNetwork")
            ?? "Devnet").ToLowerInvariant();

        var response = new NetworkConfigResponse
        {
            DefaultNetwork = defaultNetwork,
            Networks = new Dictionary<string, NetworkChainsConfig>()
        };

        var chains = configManager.GetAvailableChains();
        if (chains.Count == 0)
        {
            // No Blockchain section / no chains configured: succeed with empty Networks.
            return Ok(new AZOAResult<NetworkConfigResponse>
            {
                Result = response,
                Message = "Network configuration."
            });
        }

        foreach (var network in new[] { ChainNetwork.Devnet, ChainNetwork.Testnet, ChainNetwork.Mainnet })
        {
            var key = network.ToString().ToLowerInvariant();
            response.Networks[key] = new NetworkChainsConfig
            {
                Algorand = BuildEndpoint(chains, "Algorand", network),
                Solana = BuildEndpoint(chains, "Solana", network),
                Ethereum = BuildEndpoint(chains, "Ethereum", network)
            };
        }

        return Ok(new AZOAResult<NetworkConfigResponse>
        {
            Result = response,
            Message = "Network configuration."
        });
    }

    private static ChainEndpointConfig? BuildEndpoint(
        List<BlockchainChainConfig> chains, string chainType, ChainNetwork network)
    {
        var chain = chains.FirstOrDefault(c =>
            c.ChainType.Equals(chainType, StringComparison.OrdinalIgnoreCase));
        if (chain == null) return null;

        var net = network switch
        {
            ChainNetwork.Devnet => chain.Devnet,
            ChainNetwork.Testnet => chain.Testnet,
            ChainNetwork.Mainnet => chain.Mainnet,
            _ => chain.Devnet
        };
        if (net == null) return null;

        int? decimals = null;
        if (chain.Metadata.TryGetValue("Decimals", out var decimalsRaw)
            && int.TryParse(decimalsRaw, out var parsedDecimals))
        {
            decimals = parsedDecimals;
        }

        // SECURITY: only public endpoint/metadata fields are copied.
        // ApiToken, ApiKey, PrivateKey are intentionally never included.
        return new ChainEndpointConfig
        {
            NodeUrl = net.NodeUrl,
            IndexerUrl = net.IndexerUrl,
            IsEnabled = net.IsEnabled,
            ExplorerUrl = chain.Metadata.TryGetValue("ExplorerUrl", out var explorer) ? explorer : null,
            NativeToken = chain.Metadata.TryGetValue("NativeToken", out var token) ? token : null,
            Decimals = decimals
        };
    }
}
