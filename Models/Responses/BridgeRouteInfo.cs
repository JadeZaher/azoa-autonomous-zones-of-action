using AZOA.WebAPI.Core.Blockchain.Wormhole;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Information about a supported cross-chain bridge route.
/// </summary>
public class BridgeRouteInfo
{
    public string SourceChain { get; set; } = string.Empty;
    public string TargetChain { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? EstimatedTime { get; set; }
    public List<string> SupportedAssetTypes { get; set; } = new();
    public string? MinAmount { get; set; }
    public string? FeeInfo { get; set; }

    /// <summary>Available bridge modes for this route.</summary>
    public List<BridgeMode> AvailableModes { get; set; } = new() { BridgeMode.Trusted };

    /// <summary>Whether this route supports trustless Wormhole bridging.</summary>
    public bool WormholeSupported { get; set; }

    /// <summary>Wormhole chain IDs for source/target (null if not Wormhole-supported).</summary>
    public int? WormholeSourceChainId { get; set; }
    public int? WormholeTargetChainId { get; set; }

    /// <summary>Whether the server kill switch permits real-value movement on this route.</summary>
    public bool RealValueEnabled { get; set; }
}
