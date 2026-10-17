using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Actual token-bridge source stages with provider-owned asset, wire, custody and publication semantics.</summary>
public interface IWttSourceModule : IBlockchainProviderModule
{
    /// <summary>Reads trusted deployment and asset origin/behavior without signing or reserving transaction identity.</summary>
    Task<AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>> InspectWttSourceAsync(
        string tokenAddress, bool nativeAsset, CancellationToken ct = default);

    /// <summary>Quotes every required source prerequisite and exact publication under the frozen caller caps, without custody or egress.</summary>
    Task<AZOAResult<WttStageQuote>> QuoteWttSourceAsync(WttOperationIntent intent, CancellationToken ct = default);

    /// <summary>Claims and signs one exact stage/generation under live source custody; retry returns its durable winning wire without submitting.</summary>
    Task<AZOAResult<WttStagePreparation>> PrepareWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        string stage, int generation, CancellationToken ct = default);

    /// <summary>Revalidates and submits only the saved source wire; caller must have durably authorized this first send.</summary>
    Task SubmitWttSourceAsync(WttOperationIntent intent, WttStageQuote quote, WttStagePreparation prepared,
        CancellationToken ct = default);

    /// <summary>Reconciles that exact wire and uniquely attributed finalized core publication, never a generic lock or mint receipt.</summary>
    Task<AZOAResult<WttStageObservation>> ObserveWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default);
}
