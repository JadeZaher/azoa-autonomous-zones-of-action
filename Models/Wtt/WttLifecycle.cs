using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Wtt;

/// <summary>One chain endpoint chosen by the authenticated participant; the wallet must be owned by that participant.</summary>
public sealed record WttEndpoint(string ChainType, string Network, Guid WalletId);

/// <summary>New WTT transfer request; the request key fixes the operation identity before any provider quote.</summary>
public sealed record WttQuoteRequest(string RequestKey, WttEndpoint Source, string TokenAddress, bool NativeAsset,
    string Amount, string? RelayerFee, WttEndpoint Target, string RecipientAddress, bool UnwrapNative,
    WttChainBudget SourceBudget, WttChainBudget TargetBudget, string? ReturnOfOperationId = null);

/// <summary>Public stage plan disclosure; provider plan bytes stay server-side.</summary>
public sealed record WttStageQuoteView(string QuoteId, string ProtocolIdentity, string Phase,
    IReadOnlyList<WttStageReservation> Stages, DateTimeOffset ExpiresAt);

/// <summary>Immutable offer the participant explicitly accepts at start.</summary>
public sealed record WttOfferView(string OfferId, string OfferHash, string OperationId, WttOperationIntent Intent,
    WttStageQuoteView SourceQuote, WttTargetInspection Target, DateTimeOffset ExpiresAt, string? ReturnOfOperationId);

public sealed record WttStartRequest(string OfferId, string OfferHash, bool AcceptEconomicEffects);
public sealed record WttCancelRequest(string OfferId, string OfferHash);
public sealed record WttVersionCommand(long ExpectedVersion);
public sealed record WttContinueCommand(long ExpectedVersion, bool AuthorizeSubmission);
public sealed record WttStageCommand(long ExpectedVersion, string StageId, long ExpectedStageVersion);
public sealed record WttParticipantSignatureCommand(long ExpectedVersion, string StageId, long ExpectedStageVersion,
    string SignedTransactionBase64, bool AuthorizeSubmission);
public sealed record WttNativeOutputRequest(long ExpectedVersion, string RequestKey, string MaximumNetworkFee,
    string MaximumTemporaryRent, bool AcceptNativeOutput);
public sealed record WttCleanupRequest(long ExpectedVersion, string StageId, string RequestKey, string MaximumNetworkFee,
    bool AcceptReturn);

/// <summary>Server-captured authority for one stage transition; exactly one of signing or consent applies to its scope.</summary>
public sealed record WttStageAuthority(string Scope, SigningAdmissionAuthority? Signing, SigningConsentWitness? Consent);

/// <summary>Operation-only journal transition; evidence fields are set once and never replaced.</summary>
public sealed record WttOperationPatch(string? State = null, string? Phase = null, string? Detail = null,
    string? TransferPublicationJson = null, string? AttestationPublicationJson = null,
    string? TransferEnvelopeBase64 = null, string? AttestationEnvelopeBase64 = null, string? ProofsJson = null,
    string? BodyClaimId = null, string? TargetQuoteJson = null, string? SettlementJson = null,
    DateTimeOffset? NextAttemptAt = null);

/// <summary>Attributed publications recorded atomically with the confirming source Transfer observation.</summary>
public sealed record WttSourceCompletion(string TransferPublicationJson, string? AttestationPublicationJson);

/// <summary>What a stage's durable-nonce preparation row holds: nothing, an unsigned reservation, or a signed/exposed/unknown nonce.</summary>
public enum WttNonceHold { None, Unsigned, Held }

/// <summary>Public stage projection; signed and unsigned wire stay private.</summary>
public sealed record WttStageView(string StageId, string Phase, string Stage, int Generation, long Version, string State,
    WttStageReservation? Reservation, string? TransactionId, string? NetworkFeePaid, string? Detail,
    bool SubmissionAuthorized, bool Exposed, string? ParticipantAddress);

/// <summary>Public operation receipt with every transaction identity and the next participant action.</summary>
public sealed record WttOperationView(string OperationId, string OfferId, long Version, string State, string PublicState,
    string Phase, WttOperationIntent Intent, IReadOnlyList<WttStageView> Stages, string? CurrentStageId,
    string? TransferSequence, string? AttestationSequence, bool ProofVerified, string NextAction, string? Detail,
    WttSettlement? Settlement, string? ReturnOfOperationId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WttOperationPage(IReadOnlyList<WttOperationView> Items, string? NextCursor);

/// <summary>Exact unsigned participant message, disclosed only after exposure is durably recorded.</summary>
public sealed record WttParticipantSigningView(string OperationId, string StageId, long OperationVersion, long StageVersion,
    string ParticipantAddress, string ProtocolIdentity, string UnsignedTransactionBase64, WttStageReservation Reservation);

/// <summary>Read-only native-output disclosure; preparation re-quotes and requires the participant's ceilings.</summary>
public sealed record WttNativeOutputOffer(string NativeAmount, string NetworkFee, string TemporaryRent,
    string RecipientAddress, DateTimeOffset ExpiresAt);

/// <summary>Read-only residual-return disclosure for one resolved original stage.</summary>
public sealed record WttCleanupOffer(string StageId, string ReturnAmount, string OriginalAttributedDepositReturn,
    string NetworkFee, string DestinationAddress, DateTimeOffset ExpiresAt);

/// <summary>Final settled projection derived from finalized target evidence; never from submission alone.</summary>
public sealed record WttSettlement(int Version, string TargetTransactionId, string TargetProtocolIdentity, string BodyDigest,
    string RecipientAddress, string RecipientAmount, string RedemptionEvidenceJson, string? NativeOutputTransactionId = null,
    string? NativeAmountReceived = null);
