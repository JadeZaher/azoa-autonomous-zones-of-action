namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Fresh exact residual-return offer, separate from original workflow signing authority.</summary>
public sealed record WttCleanupQuote(int Version, string QuoteId, string IntentHash, string OriginalQuoteId,
    string Phase, string OriginalStage, int OriginalGeneration, string OriginalPreparationHash,
    string ProtocolIdentity, string StageAccount, string DestinationAddress, string ReturnAmount,
    string OriginalAttributedDepositReturn, string NetworkFee, string OriginalObservationHash,
    string PlanJson, DateTimeOffset ExpiresAt);

/// <summary>Original user's explicit acceptance of a separately disclosed cleanup offer.</summary>
public sealed record WttCleanupConsent(int Version, string RequestKey, string QuoteId, string MaximumNetworkFee);

/// <summary>Exact finalized return and original allocation accounting are deliberately distinct.</summary>
public sealed record WttCleanupObservation(WttStageObservation Stage, string? ReturnedAmount,
    string? OriginalAttributedDepositReturned, string? RemainingStageBalance = null);
