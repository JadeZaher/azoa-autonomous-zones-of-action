using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Owns one network's reviewed DEX grammar, immutable stage plans and authoritative settlement.</summary>
public interface IDexTransactionModule : IBlockchainProviderModule
{
    /// <summary>Names the immutable concrete network profile used by this module instance.</summary>
    ChainNetwork ActiveNetwork { get; }

    /// <summary>Describes the actual supported protocol grammar without implying configured readiness.</summary>
    DexCapability DexCapability { get; }

    /// <summary>Reads the authoritative physical network identity that binds quotes, without signing or sending.</summary>
    Task<AZOAResult<DexNetwork>> DescribeDexNetworkAsync(CancellationToken ct = default);

    /// <summary>Checks public terms and returns fresh protocol evidence without signing or sending.</summary>
    Task<AZOAResult<DexQuoteTerms>> QuoteSwapAsync(DexQuoteTerms requested, CancellationToken ct = default);

    /// <summary>Chooses a bounded stage from confirmed history; no observation may infer missing signatures.</summary>
    Task<AZOAResult<SwapNextStage>> GetNextSwapStageAsync(SwapIntent intent,
        IReadOnlyList<SwapStageHistory> history, CancellationToken ct = default);

    /// <summary>Claims the immutable unsigned plan before custody; the same preparation key retains original bytes.</summary>
    Task<AZOAResult<SwapStagePlan>> PlanSwapStageAsync(SwapIntent intent, string stage,
        int generation, CancellationToken ct = default);

    /// <summary>Plans separately consented cleanup using the saved terminal lineage and authoritative rent attribution.</summary>
    Task<AZOAResult<SwapStagePlan>> PlanSwapCleanupAsync(SwapIntent intent, IReadOnlyList<SwapStageHistory> history,
        SwapCleanupConsent consent, string stage, int generation, CancellationToken ct = default);

    /// <summary>Signs the exact claimed plan under current caller authority without authorizing broadcast.</summary>
    Task<AZOAResult<PreparedSwapStage>> SignSwapStageAsync(SwapIntent intent, SwapStagePlan plan,
        IReadOnlyList<SwapSigningAuthority> authorities, CancellationToken ct = default);

    /// <summary>Validates expected signatures and the complete canonical browser envelope against the saved plan.</summary>
    AZOAResult<PreparedSwapStage> ValidateSignedSwapStage(SwapIntent intent, SwapStagePlan plan,
        string signedPayloadJson);

    /// <summary>Derives any signature-independent transaction identities from the exact plan before browser exposure.</summary>
    AZOAResult<IReadOnlyList<string>> GetExposedSwapTransactionIds(SwapIntent intent, SwapStagePlan plan);

    /// <summary>Observes browser wire using only the prior persisted provider observation; candidates contain actual signatures.</summary>
    Task<AZOAResult<SwapExternalStageObservation>> ObserveExposedSwapStageAsync(SwapIntent intent,
        SwapStagePlan plan, CancellationToken ct = default, SwapStageObservation? previous = null);

    /// <summary>Validates saved plan-bound no-effect evidence without manufacturing a signed candidate.</summary>
    bool ValidateExposedSwapNoEffect(SwapIntent intent, SwapStagePlan plan, SwapStageObservation observation);

    /// <summary>Revalidates intent and replays exact saved wire; caller must hold durable first-send authorization.</summary>
    Task<AZOAResult<IReadOnlyList<string>>> SubmitSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default);

    /// <summary>Observes original wire using prior persisted proof; no signing or caller-supplied recovery cursor.</summary>
    Task<AZOAResult<SwapStageObservation>> ObserveSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default, SwapStageObservation? previous = null);

    /// <summary>Resolves previously unknown incremental rent from authoritative history without changing original effect evidence.</summary>
    Task<AZOAResult<SwapStageRentAccounting>> ResolveSwapStageAccountingAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default);

    /// <summary>Advances one bounded, persisted history page toward attributing release of an original allocation.</summary>
    Task<AZOAResult<SwapRentReleaseProgress>> ScanSwapRentReleaseAsync(SwapIntent intent,
        PreparedSwapStage allocation, PreparedSwapStage cleanup, string? checkpointJson, CancellationToken ct = default);
}
