using System.Text.Json;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Dex;

public sealed record DexAsset(string Id, string Symbol, int Decimals, bool Native);
public sealed record DexNetwork(string ChainType, string Network, string ProtocolIdentity);
public sealed record DexParticipant(Guid UserId, Guid WalletId, string Address);
public sealed record DexFeeQuote(string Denomination, long ScheduleVersion, string FlatBaseUnits,
    int BasisPoints, string Amount, string TreasuryAddress, string PolicyKeyVersion = "");
public sealed record DexBudget(string MaximumNetworkFee, string MaximumLockedBalance,
    string MaximumCleanupFee);
public sealed record DexQuoteTerms(int Version, Guid ActorUserId, Guid? TenantId, DexParticipant Owner,
    DexParticipant Payer, string RecipientAddress, DexNetwork Network, DexAsset InputAsset,
    DexAsset OutputAsset, string GrossInput, string SwapInput, string ExpectedOutput, string MinimumOutput,
    int SlippageBps, DexFeeQuote PlatformFee, DexBudget Budget, string Provider,
    string ProtocolVersion, JsonElement ProtocolEvidence, DateTimeOffset ExpiresAt,
    IReadOnlyList<DexEffectDisclosure> Prerequisites);
public sealed record DexQuoteView(string QuoteId, string QuoteHash, DexQuoteTerms Terms);
public sealed record DexEffectDisclosure(string Kind, string AssetId, string Amount, string OwnerAddress,
    string DestinationAddress, bool RetainedOnSwapFailure);
public sealed record SwapIntent(int Version, string OperationId, string QuoteId, string QuoteHash,
    DexQuoteTerms Terms, string RequestHash);
public sealed record SwapSigningAuthority(DexParticipant Participant, SigningContext Context);
public sealed record SwapStagePlan(string Stage, int Generation, string PreparationKey, string IntentHash,
    IReadOnlyList<string> RequiredSignerAddresses, string UnsignedPayloadJson, string RecoveryMetadata,
    string ReservedNetworkFee, string ReservedLockedBalance, string Purpose = "Effect", SwapCleanupConsent? CleanupConsent = null);
public sealed record PreparedSwapStage(SwapStagePlan Plan, IReadOnlyList<string> TransactionIds,
    string SignedPayloadJson);
public sealed record SwapExternalStageObservation(SwapStageObservation Observation, PreparedSwapStage? RecoveredCandidate = null);
public sealed record SwapStageObservation(string Verdict, IReadOnlyList<string> TransactionIds,
    string ProtocolIdentity, string? ActualInput = null, string? ActualOutput = null,
    string? PlatformFeePaid = null, string? NetworkFeePaid = null, string? LockedBalance = null,
    string? EvidenceJson = null, string? Detail = null, SwapFinalizedCandidate? FinalizedCandidate = null);
public sealed record SwapFinalizedCandidate(PreparedSwapStage Candidate, string OriginalPreparedHash, string PlanHash);
public sealed record SwapStageHistory(PreparedSwapStage? Prepared, SwapStageObservation Observation,
    SwapStageRentAccounting? RentAccounting = null, SwapStagePlan? ExposedPlan = null)
{
    public SwapStagePlan Plan => Prepared?.Plan ?? ExposedPlan ?? throw new InvalidOperationException("Swap history has no immutable plan.");
}
public sealed record SwapStageRentAccounting(string ActualLockedBalance, string EvidenceJson);
public sealed record SwapRentReleaseProgress(string CheckpointJson, bool Complete,
    string? ReleasedLockedBalance = null, string? EvidenceJson = null);
public sealed record SwapCleanupConsent(int Version, string OperationId, string QuoteHash, string RequestKeyHash,
    Guid ActorUserId, Guid? TenantId, DexParticipant Owner, string AssetId, string Semantics,
    string MaximumNetworkFee, bool AcceptCurrentZeroHoldingReplacement);
public sealed record SwapNextStage(string? Stage, bool Complete,
    IReadOnlyList<DexEffectDisclosure> RetainedEffects, string? Detail = null);
public sealed record SwapOperationView(string OperationId, string QuoteId, string QuoteHash,
    string State, string Stage, long Version, DexNetwork Network,
    IReadOnlyList<string> TransactionIds, string? ActualInput, string? ActualOutput,
    IReadOnlyList<DexEffectDisclosure> RetainedEffects, string? Detail, SwapAccountingView? Accounting = null,
    bool RetentionReconciled = false, SwapStageReceipt? CurrentStage = null);
public sealed record SwapStageReceipt(string StageId, string Stage, long Version, string State, string Purpose,
    IReadOnlyList<string> TransactionIds, string? ActualInput, string? ActualOutput, string? PlatformFeePaid,
    string? NetworkFeePaid, string? LockedBalance, bool SubmissionAuthorized,
    bool ExternalSigningExposed = false, bool ServerSubmissionWithdrawn = false);
public sealed record SwapAccountingView(bool Complete, string ConfirmedExecutionNetworkFee,
    string ConfirmedCleanupNetworkFee, string ConfirmedRentAllocated, string UnresolvedRentReservation,
    long UnresolvedRentStages, string ConfirmedRentReleased = "0");
public sealed record SwapSettlementReceipt(string OperationId, string QuoteHash, string FinalStageId,
    DexNetwork Network, IReadOnlyList<string> TransactionIds, string ActualInput, string ActualOutput,
    string PlatformFeePaid, IReadOnlyList<DexEffectDisclosure> RetainedEffects,
    string? OriginalPreparedHash = null, string? PlanHash = null, IReadOnlyList<string>? OriginalTransactionIds = null);
public sealed record SwapCleanupReceipt(string OperationId, string QuoteHash, string FinalStageId,
    string CleanupConsentHash, IReadOnlyList<string> TransactionIds, string ReleasedRent,
    bool OriginalEconomicSettlementPreserved);
public sealed record DexCapability(string Provider, string ProtocolVersion, bool ExactInput,
    bool ExactOutput, bool NativeInput, bool NativeOutput, bool SelfRecipientOnly,
    IReadOnlyList<string> Restrictions);
