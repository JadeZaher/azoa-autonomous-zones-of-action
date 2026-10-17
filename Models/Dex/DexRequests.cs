namespace AZOA.WebAPI.Models.Dex;

public sealed record DexQuoteRequest(Guid WalletId, DexNetwork Network, DexAsset InputAsset,
    DexAsset OutputAsset, string GrossInput, int SlippageBps, DexBudget Budget);
public sealed record DexQuoteAvailability(bool Unavailable, DexQuoteView? Quote = null, string? Reason = null);
public sealed record SwapAdmissionRequest(string QuoteId, string QuoteHash, string RequestKey, bool AcceptEconomicEffects);
public sealed record SwapVersionCommand(long ExpectedVersion);
public sealed record SwapCleanupStageCommand(long ExpectedVersion, string Stage);
public sealed record SwapStageCommand(long ExpectedVersion, string StageId, long ExpectedStageVersion);
public sealed record SwapBrowserStageCommand(long ExpectedVersion, string StageId, long ExpectedStageVersion, string SignedPayloadJson);
public sealed record SwapExposureCommand(long ExpectedVersion, string StageId, long ExpectedStageVersion,
    bool AcceptPotentialExternalSubmission);
public sealed record SwapExternalSigningView(string OperationId, string StageId, long OperationVersion, long StageVersion,
    DexNetwork Network, string PlanHash, string ExposureHash, IReadOnlyList<string> RequiredSignerAddresses,
    string UnsignedPayloadJson, string ReservedNetworkFee, string ReservedLockedBalance);
public sealed record SwapCleanupRequest(long ExpectedVersion, string RequestKey, string AssetId,
    string MaximumNetworkFee, bool AcceptCurrentZeroHoldingReplacement);
