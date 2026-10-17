using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Prepares, observes and replays caller-authorized typed native operations without implicit authority.</summary>
public interface INativeOperationModule : IBlockchainProviderModule
{
    /// <summary>Validates and normalizes native artifacts without signing or sending.</summary>
    Task<AZOAResult<NativeArtifact>> ValidateArtifactAsync(NativeArtifact artifact, CancellationToken ct = default);
    /// <summary>Reads current protocol resources and quotes the complete operation before any signer or nonce allocation.</summary>
    Task<AZOAResult<NativeOperationQuote>> QuoteNativeOperationAsync(NativeOperationIntent intent, CancellationToken ct = default);
    /// <summary>Resolves the next bounded stage from exact confirmed history and authoritative chain state.</summary>
    Task<AZOAResult<NativeNextStage>> GetNextStageAsync(NativeOperationIntent intent, IReadOnlyList<NativeStageHistory> history, CancellationToken ct = default);
    /// <summary>Prepares exact bytes for one stage under the supplied current custody authority.</summary>
    Task<AZOAResult<NativeStagePreparation>> PrepareNativeStageAsync(NativeOperationIntent intent, string stage, int generation, CancellationToken ct = default);
    /// <summary>Revalidates intent and canonical wire before replaying the same authorized transaction.</summary>
    Task<AZOAResult<string>> SubmitNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default);
    /// <summary>Returns Confirmed, FailedOnChain, ExpiredNoEffect, Pending or Unknown from exact chain evidence.</summary>
    Task<AZOAResult<NativeStageObservation>> ObserveNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default);
}
