using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Fresh owner-authorized residual return after every original attempt is resolved and durably fenced.</summary>
public interface IWttCleanupModule : IBlockchainProviderModule
{
    /// <summary>Reobserves original exact wire and discloses a bounded return without signing or reviving its workflow.</summary>
    Task<AZOAResult<WttCleanupQuote>> QuoteWttCleanupAsync(WttOperationIntent intent, WttStageQuote originalQuote,
        WttTargetProofs? proofs, WttStagePreparation original, CancellationToken ct = default);

    /// <summary>Persists exact cleanup bytes under fresh direct original-owner custody after the outer atomic attempt fence.</summary>
    Task<AZOAResult<WttStagePreparation>> PrepareWttCleanupAsync(WttOperationIntent intent, WttStageQuote originalQuote,
        WttTargetProofs? proofs, WttStagePreparation original, WttCleanupQuote quote, WttCleanupConsent consent,
        WttSigningAuthority authority, int generation, CancellationToken ct = default);

    /// <summary>Replays only the exact persisted cleanup candidate after separate first-send authorization.</summary>
    Task SubmitWttCleanupAsync(WttOperationIntent intent, WttCleanupQuote quote, WttCleanupConsent consent,
        WttStagePreparation prepared, CancellationToken ct = default);

    /// <summary>Proves the actual original-wallet return without crediting unsolicited funds as original deposited rent.</summary>
    Task<AZOAResult<WttCleanupObservation>> ObserveWttCleanupAsync(WttOperationIntent intent, WttCleanupQuote quote,
        WttCleanupConsent consent, WttStagePreparation prepared, CancellationToken ct = default);
}
