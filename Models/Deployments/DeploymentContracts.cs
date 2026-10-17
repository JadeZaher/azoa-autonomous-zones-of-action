using System.Text.Json;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Deployments;

public sealed record DeploymentTarget(string Kind, string ChainType, string Network);
public sealed record DeploymentSource(Guid ActionId, string Occurrence, long Version, string PublishedHash, ActionDefinition Snapshot);
public sealed record DeploymentReference(Guid HolonId, string ContentHash);
public sealed record RuntimeArtifact(IReadOnlyList<DeploymentSource> Sources, ActionDefinition Graph,
    IReadOnlyList<DeploymentReference> References, string RuntimeVersion = "azoa-actions/2",
    IReadOnlyList<DeploymentInternalDependency>? InternalDependencies = null,
    IReadOnlyList<DeploymentInputMapping>? InputMappings = null,
    IReadOnlyList<DeploymentBundleReference>? BundleReferences = null,
    IReadOnlyList<DeploymentOccurrenceBoundary>? OccurrenceBoundaries = null);
public sealed record DeploymentOccurrenceBoundary(Guid EntryNodeId, IReadOnlyList<Guid> RequiredNodeIds);
public sealed record DeploymentBundleReference(Guid BundleId, string ArtifactHash);
public sealed record DeploymentInternalDependency(Guid EntryNodeId, IReadOnlyList<Guid> RequiredNodeIds,
    ActionDependencyType DependencyType, string SourceOccurrence, string TargetOccurrence);
public sealed record NativeArtifact(string Format, JsonElement Content, string CompilerIdentity);
public sealed record DeploymentArtifactPayload(int Version, Guid BundleId, Guid OwnerUserId, Guid? TenantId,
    DeploymentTarget Target, RuntimeArtifact? Runtime, NativeArtifact? Native);
public sealed record DeploymentArtifactView(string ArtifactId, string PayloadHash, string ArtifactHash,
    DeploymentTarget Target, string? Loader, string FormatVersion = "2", NativeArtifact? Native = null);
public sealed record DeploymentGenerateRequest(DeploymentTarget Target, IReadOnlyList<Guid>? ActionIds = null,
    NativeArtifact? Native = null, IReadOnlyList<DeploymentInputMapping>? Mappings = null);
public sealed record DeploymentInputMapping(string SourceOccurrence, Guid SourceNodeId, string SourceField,
    string TargetOccurrence, Guid TargetNodeId, string TargetField);
public sealed record NativeBudget(string MaximumNetworkFee, string MaximumLockedRent, string MaximumValue,
    string QuoteId);
public sealed record NativeAuthority(string Address, SigningContext SigningContext);
public sealed record NativeOperationIntent(int Version, string OperationKey, string Kind, string ChainType,
    string Network, string ProtocolIdentity, string ArtifactHash, JsonElement Input,
    IReadOnlyList<NativeAuthority> Authorities, NativeBudget Budget, NativeOperationQuote? Quote = null);
public sealed record NativeOperationQuote(string QuoteId, string IntentHash, string ProtocolIdentity,
    string NetworkFee, string LockedRent, string Value, string PlanJson, DateTimeOffset ExpiresAt,
    NativeOperationDisclosure? Disclosure = null);
public sealed record NativeOperationDisclosure(string Effect, string? RefundDestination = null,
    string? EstimatedReturnedBaseUnits = null, JsonElement? ResultingMetadata = null, string? CleanupRefundDestination = null);
public sealed record NativeStagePreparation(string Stage, string TransactionId, string SignedTransactionBase64,
    string ProtocolIdentity, string RecoveryMetadata, string ReservedFee, string ReservedRent,
    string? AdditionalSignedTransactionsJson = null, string ReservedValue = "0");
public sealed record NativeStageObservation(string Verdict, string TransactionId, string ProtocolIdentity,
    string? ResourceId = null, string? EvidenceJson = null, string? Detail = null, NativeStageAccounting? Accounting = null,
    NativeResourceEffect? ResourceEffect = null, NativeResourceEffect? CleanupEffect = null);
public sealed record NativeBalanceChange(string Address, string BeforeBaseUnits, string AfterBaseUnits);
public sealed record NativeStageAccounting(string NetworkFeePaid, IReadOnlyList<NativeBalanceChange> NativeBalances);
public sealed record DeploymentEconomics(string ReservedNetworkFee, string ReservedRent, string ReservedValue,
    string ObservedNetworkFeePaid, IReadOnlyList<NativeStageAccounting> ConfirmedAccounting, bool NetworkFeeAccountingComplete);
public sealed record NativeAuthorizationSummary(string Kind, NativeBudget Budget, NativeOperationQuote Quote,
    IReadOnlyList<NativeAuthority> Authorities, JsonElement Input);
public sealed record NativeStageReservation(string NetworkFee, string LockedRent, string Value);
public sealed record NativeNextStage(string? Stage, bool Complete, string? ResourceId = null, string? EvidenceJson = null,
    string Disposition = "Normal", NativeStageReservation? Reservation = null);
public sealed record DeploymentOriginalBudgetRecoveryRequest(long ExpectedVersion, string Mode, bool AcknowledgeRecovery);
public sealed record NativeStageHistory(NativeStagePreparation Prepared, NativeStageObservation Observation);
public sealed record NativeRecoveryInput(NativeOperationIntent OriginalIntent, IReadOnlyList<NativeStageHistory> ResolvedHistory,
    string Mode, string PayerAddress, long? ParentVersion = null, NativeRecoveryPredecessor? Predecessor = null);
public sealed record NativeRecoveryPredecessor(string OperationId, long Version, string IntentHash, string HistoryHash, string ProofKind,
    string? AncestryDigest = null);
public sealed record DeploymentRecoveryAncestry(string HeadOperationId, string State, long InspectedCount, string Digest, string? Detail);
public sealed record DeploymentRecoveryQuoteRequest(long ExpectedVersion, string RequestKey, string Mode, string PayerAddress,
    IReadOnlyList<NativeAuthority> Authorities, NativeBudget Budget);
public sealed record DeploymentRecoveryQuote(long ParentVersion, string BindingHash, NativeOperationIntent Intent);
public sealed record DeploymentRecoveryStartRequest(string RequestKey, DeploymentRecoveryQuote Quote, bool AcknowledgeCosts);
public sealed record NativeResourceEffect(string Kind, string? ResourceId, string TransactionId, string? EvidenceJson);
public sealed record DeploymentRecoverySummary(string Mode, string? ParentOperationId, string? ChildOperationId,
    string? EvidenceJson, bool Projected, string? ReplacesOperationId = null, string? ReplacedByOperationId = null);
public sealed record DeploymentStartRequest(string ArtifactId, string ArtifactHash, DeploymentTarget Target,
    NativeOperationIntent? NativeIntent = null);
public sealed record DeploymentQuoteRequest(string ArtifactId, string RequestKey, NativeOperationIntent Intent);
public sealed record DeploymentRequestCancellation(string RequestKey, DeploymentStartRequest Request);
public sealed record DeploymentReceipt(string OperationId, string ArtifactId, string ArtifactHash,
    DeploymentTarget Target, string State, string Stage, string? ResourceId, string? InvokeUrl,
    IReadOnlyList<string> TransactionIds, string? Detail, long Version, DeploymentEconomics? Economics = null,
    NativeAuthorizationSummary? Authorization = null, NativeResourceEffect? ResourceEffect = null, NativeResourceEffect? CleanupEffect = null,
    DeploymentRecoverySummary? Recovery = null);
public sealed record DeploymentInvokeRequest(string ExpectedPayloadHash, JsonElement Input,
    bool AcknowledgeEconomicEffects);
public sealed record DeploymentPage(IReadOnlyList<DeploymentReceipt> Items, string? NextCursor);
