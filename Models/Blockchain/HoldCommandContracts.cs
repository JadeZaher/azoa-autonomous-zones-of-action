using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Signing;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Immutable network-qualified custody identity for one hold.</summary>
public sealed record HoldAssetIdentity(string ChainType, ChainNetwork Network, string ProtocolIdentity,
    string AssetId, string HoldingAddress);

/// <summary>Original custody liability and permitted release terms.</summary>
public sealed record HoldDefinition(HoldAssetIdentity Asset, string QuantityBaseUnits, string FundingSource,
    Guid BeneficiaryUserId, Guid? FunderUserId, string? ReservePartition, DateTimeOffset? ReleaseNotBefore,
    bool AutoRelease, string? Purpose);

/// <summary>Original keyed command; an aggregate's later state cannot rewrite its outcome.</summary>
public sealed record HoldCommandIntent(int Version, string RequestKey, string Kind, string HoldId,
    Guid ActorUserId, Guid OwnerUserId, Guid? TenantId, Guid? CustodyTenantId, HoldAssetIdentity Asset, HoldDefinition? Definition = null,
    string? NextHoldId = null, Guid? NextBeneficiaryUserId = null, DateTimeOffset? NextReleaseNotBefore = null,
    bool NextAutoRelease = false, string? Reason = null);

/// <summary>Server-captured authority for a command, with mutually exclusive execution sources.</summary>
public sealed record HoldCommandAuthority(int Version, SigningConsentWitness? Direct = null,
    WorkflowSigningWitness? Workflow = null, HoldEconomicSource? EconomicSource = null,
    SigningAdmissionAuthority? Sender = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReleaseMandateId = null);

/// <summary>Names an existing economic source whose full terms must be verified atomically.</summary>
public sealed record HoldEconomicSource(string Kind, string SourceId, string SourceIntentHash);

/// <summary>Participant-safe immutable command progress and resulting hold identity.</summary>
public sealed record HoldCommandView(string OperationId, long Version, string Kind, string State,
    string HoldRecordId, string HoldId, HoldAssetIdentity Asset, string QuantityBaseUnits,
    Guid ActorUserId, Guid OwnerUserId, Guid? TenantId, Guid BeneficiaryUserId, string? TransactionId,
    string? Detail, string? NextHoldRecordId = null, Guid? CustodyTenantId = null,
    ValueCustodyQuote? CostQuote = null, string? AcceptanceState = null, long Generation = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReleaseMandateId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HoldReleaseMandateTerms? MandateTerms = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MandateDraftHash = null);

/// <summary>Immutable custody movement and participant accounts reserved before accepting a cost quote.</summary>
public sealed record HoldRailPlan(int Version, string Kind, string CommandId, long Generation,
    ValueCustodyQuote Quote, SigningWalletWitness? SenderWallet, SigningWalletWitness? RecipientWallet,
    SigningContext SenderSigningContext);

/// <summary>Explicit consent to an unchanged displayed operation version and full quote identity.</summary>
public sealed record HoldContinuationRequest(Guid RequestId, long Version, string QuoteId,
    string MaximumNetworkFeeBaseUnits, string? Reason = null);

/// <summary>Authoritative adapter observation bound to one exact persisted transaction.</summary>
public sealed record HoldTransferEvidence(int Version, string PreparedHash, string TransactionId,
    string ProtocolIdentity, ChainConfirmation Confirmation, DateTimeOffset ObservedAt);

/// <summary>Exact participant request for an audited no-effect recovery transition.</summary>
public sealed record HoldRecoveryRequest(Guid RequestId, long Version, string Reason);

/// <summary>Exact keyed hold admission and selected participant accounts; authority comes from the authenticated principal.</summary>
public sealed record HoldStartRequest(HoldCommandIntent Intent, Guid? SenderWalletId = null, Guid? RecipientWalletId = null);

/// <summary>Participant-safe bounded discovery page; the cursor advances across every inspected row.</summary>
public sealed record HoldCommandPage(IReadOnlyList<HoldCommandView> Items, int? NextOffset);

/// <summary>Immutable permission for one future platform-custody release to an exact beneficiary account.</summary>
public sealed record HoldReleaseMandateTerms(int Version, string HoldRecordId, string DefinitionHash,
    HoldAssetIdentity Asset, string QuantityBaseUnits, SigningWalletWitness Recipient,
    DateTimeOffset NotBefore, DateTimeOffset ExpiresAt, string NativeCurrency, int NativeDecimals,
    ValueCustodyCostBreakdown MaximumCosts);

/// <summary>Explicit owner request to bind an already displayed quote and complete release mandate.</summary>
public sealed record HoldReleaseMandateRequest(Guid RequestId, long HoldVersion, HoldReleaseMandateTerms Terms,
    ValueCustodyQuote Quote);

/// <summary>Owner-selected mandate terms for obtaining a server-authored quote before consent.</summary>
public sealed record HoldReleaseMandateOfferRequest(Guid RequestId, long HoldVersion, HoldReleaseMandateTerms Terms);

/// <summary>Explicit acceptance of the exact persisted mandate disclosure.</summary>
public sealed record HoldReleaseMandateAcceptance(string OfferId, string RequestHash);

/// <summary>Original mandate disclosure; no permission exists until the matching explicit acceptance.</summary>
public sealed record HoldReleaseMandateOfferView(string Id, string RequestHash, HoldReleaseMandateRequest Request);

/// <summary>Participant-safe release mandate status, separate from actual settlement.</summary>
public sealed record HoldReleaseMandateView(string Id, long Version, string Status, HoldReleaseMandateTerms Terms,
    string? OperationId, string? Detail);

/// <summary>Owner-scoped mandate discovery page.</summary>
public sealed record HoldReleaseMandatePage(IReadOnlyList<HoldReleaseMandateView> Items, int? NextOffset);

/// <summary>Original saved mandate management request to resolve after a lost response.</summary>
public sealed record HoldMandateRecoveryLookup(string Kind, HoldRecoveryRequest Request);

/// <summary>Exact receipt or monotonic version proof for retiring a saved management request.</summary>
public sealed record HoldMandateRecoveryResolution(string State, HoldReleaseMandateView? Mandate);

/// <summary>Immutable completed opening execution and accounting receipt behind a workflow release mandate.</summary>
public sealed record HoldReleaseWorkflowSource(int Version, string ExecutionRecordId, string OpeningCommandId,
    string OpeningIntentHash, string OpeningOutcomeHash, WorkflowSigningWitness Witness);

/// <summary>Server-authored delayed release disclosure for one frozen workflow opening, before liability creation.</summary>
public sealed record HoldWorkflowMandateDraft(int Version, string ExecutionRecordId,
    HoldReleaseMandateTerms Terms, ValueCustodyQuote Quote);

/// <summary>Actual actor acceptance of one immutable parked workflow opening disclosure.</summary>
public sealed record HoldWorkflowOpeningAcceptance(Guid RequestId, long Version, string DraftHash);

/// <summary>Exact acceptance receipt or monotonic version proof for a saved opening request.</summary>
public sealed record HoldWorkflowOpeningResolution(string State, HoldCommandView? Command);

/// <summary>Original replacement or withdrawal command whose outcome must be reconciled before retry.</summary>
public sealed record HoldWorkflowOpeningRecoveryLookup(string Kind, HoldRecoveryRequest Request);

/// <summary>Frozen automatic-release deadline and independent cost caps; recipient wallet is selected separately.</summary>
public sealed record HoldWorkflowMandateSelection(DateTimeOffset ExpiresAt, ValueCustodyCostBreakdown MaximumCosts);

/// <summary>One original saved transfer, acceptance, replacement or cancellation request.</summary>
public sealed record HoldCommandRequestLookup(string Kind, HoldContinuationRequest? Consent = null, HoldRecoveryRequest? Recovery = null);

/// <summary>Exact receipt or ordered version evidence for safely retiring a saved command.</summary>
public sealed record HoldCommandRequestResolution(string State, HoldCommandView? Command);
