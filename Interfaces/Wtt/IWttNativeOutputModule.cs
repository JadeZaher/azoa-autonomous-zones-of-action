using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Explicit recipient-authorized native output for an originally accepted native-output bridge intent.</summary>
public interface IWttNativeOutputModule : IBlockchainProviderModule
{
    /// <summary>Reobserves original redemption and discloses exact net output, temporary rent and network costs without signing.</summary>
    Task<AZOAResult<WttNativeOutputQuote>> QuoteWttNativeOutputAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote targetQuote, WttStagePreparation redemption, CancellationToken ct = default);

    /// <summary>Persists one exact recipient message; the journal must fence and record exposure before disclosure.</summary>
    Task<AZOAResult<WttParticipantPreparation>> PrepareWttNativeOutputAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, int generation, CancellationToken ct = default);

    /// <summary>Accepts only the exact real recipient signature and fills the existing durable candidate once.</summary>
    Task<AZOAResult<WttStagePreparation>> AcceptWttNativeOutputSignatureAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, WttParticipantPreparation prepared,
        string signedTransactionBase64, CancellationToken ct = default);

    /// <summary>Replays only the saved recipient candidate after separate first-send or exposed-wire authorization.</summary>
    Task SubmitWttNativeOutputAsync(WttOperationIntent intent, WttNativeOutputQuote quote, WttNativeOutputConsent consent,
        WttStagePreparation prepared, CancellationToken ct = default);

    /// <summary>Proves exact token debit and native credit while preserving the original recipient ATA and unrelated funds.</summary>
    Task<AZOAResult<WttNativeOutputObservation>> ObserveWttNativeOutputAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, WttStagePreparation prepared, CancellationToken ct = default);

    /// <summary>Observes a disclosed recipient message the server never authorized; see
    /// <see cref="IWttParticipantModule.ObserveExposedWttParticipantAsync"/>.</summary>
    Task<AZOAResult<WttExposedObservation>> ObserveExposedWttNativeOutputAsync(WttOperationIntent intent, WttNativeOutputQuote quote,
        WttNativeOutputConsent consent, WttParticipantPreparation participant, string? previousEvidenceJson, CancellationToken ct = default);
}
