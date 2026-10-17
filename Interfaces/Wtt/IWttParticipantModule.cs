using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Optional exact-message wallet participation for prerequisites requiring the recipient's own signature.</summary>
public interface IWttParticipantModule : IBlockchainProviderModule
{
    /// <summary>Claims one unsigned participant message bound to operation, account, asset, fees and genesis without impersonating the wallet.</summary>
    Task<AZOAResult<WttParticipantPreparation>> PrepareWttParticipantAsync(WttOperationIntent intent,
        WttStageQuote quote, string stage, int generation, CancellationToken ct = default);

    /// <summary>Verifies exact message and expected participant signature, returning a durable candidate for persistence before authorized submission.</summary>
    Task<AZOAResult<WttStagePreparation>> AcceptWttParticipantSignatureAsync(WttOperationIntent intent,
        WttStageQuote quote, WttParticipantPreparation prepared, string signedTransactionBase64, CancellationToken ct = default);
}
