using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Actual token-bridge target registration, participant prerequisites and canonical-body redemption.</summary>
public interface IWttTargetModule : IBlockchainProviderModule
{
    /// <summary>Inspects exact target representation and participant prerequisites against authoritative registered source origin.</summary>
    Task<AZOAResult<WttTargetInspection>> InspectWttTargetAsync(
        WttAssetDescriptor origin, string recipientAddress, bool unwrapNative, CancellationToken ct = default);

    /// <summary>Quotes required target stages for the exact verified source body under target payer limits, without signing.</summary>
    Task<AZOAResult<WttStageQuote>> QuoteWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        CancellationToken ct = default);

    /// <summary>Claims and signs one target stage/generation; current participant custody and the exact proof-body claim are required.</summary>
    Task<AZOAResult<WttStagePreparation>> PrepareWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote quote, string stage, int generation, CancellationToken ct = default);

    /// <summary>Submits only the persisted target wire after durable body-claim ownership and first-send authorization.</summary>
    Task SubmitWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default);

    /// <summary>Reconciles exact target wire and protocol completion for the canonical source body, including recipient and asset effects.</summary>
    Task<AZOAResult<WttStageObservation>> ObserveWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct = default);
}
