using AZOA.WebAPI.Core.Blockchain.Bridge;
using AZOA.WebAPI.Core.Blockchain.Wormhole;

namespace AZOA.WebAPI.Services.Bridge;

/// <summary>
/// Registers the cross-chain bridge PROTOCOLS. A protocol is chosen at composition time,
/// which is exactly why <see cref="IBridgeProtocol{TProof}"/> can be generic over its proof
/// type: the binding is static here, so <c>CrossChainBridgeService</c> gets a compile-time
/// proof type rather than a reflected one.
/// </summary>
/// <remarks>
/// Composition root: call this from <c>Program.cs</c> after <c>IWormholeAdapter</c> is
/// registered and before <c>ICrossChainBridgeService</c>.
/// </remarks>
public static class BridgeProtocolServiceCollectionExtensions
{
    /// <summary>Registers every shipped bridge protocol against its proof type.</summary>
    public static IServiceCollection AddCrossChainBridgeProtocols(this IServiceCollection services)
    {
        // Wormhole is the only shipped protocol. Guardian-quorum VAA verification is reachable
        // ONLY through it, and it is fail-closed at every step.
        services.AddScoped<IBridgeProtocol<WormholeVAA>, WormholeProtocol>();
        return services;
    }
}
