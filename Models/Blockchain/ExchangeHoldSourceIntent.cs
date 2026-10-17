using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Immutable exchange purchase or sale and the exact reserve commitment selected before any signing.</summary>
/// <remarks>Exactly one of <see cref="Opening"/> (held purchase) or <see cref="Units"/> (direct delivery or sale) is present.</remarks>
public sealed record ExchangeHoldSourceIntent(int Version, string RequestKey, Guid ApiKeyId,
    Guid TenantUserId, Guid ParticipantUserId, string OriginalRequestJson, HoldCommandIntent? Opening,
    string PlatformAssetId, string PlatformQuantityBaseUnits, string ProjectReference,
    string TreasuryAddress, SigningWalletWitness ParticipantWallet, string MaximumNetworkFeeBaseUnits = "0",
    ExchangeReleaseConsent? ReleaseConsent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExchangeUnitsCommitment? Units = null);

/// <summary>Project-unit leg settled directly against the reserve: <c>Issue</c> delivers to the participant, <c>Return</c> receives a sale.</summary>
/// <remarks>A non-null <paramref name="RecipientFundingBaseUnits"/> folds the participant's asset acceptance (and that much reserve-paid native funding) into the same atomic group; see Services/Reserve/AGENTS.md §recipient-readiness.</remarks>
public sealed record ExchangeUnitsCommitment(string Kind, HoldAssetIdentity Asset, string QuantityBaseUnits,
    string ReservePartition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecipientFundingBaseUnits = null);

/// <summary>Exact accepted future release disclosure and separately scoped actual tenant authority.</summary>
public sealed record ExchangeReleaseConsent(Guid RequestId, HoldReleaseMandateTerms Terms, ValueCustodyQuote Quote,
    SigningConsentWitness Authority, string DisclosureHash);

/// <summary>Original completed purchase and opening receipt that constrain a delayed release.</summary>
public sealed record HoldReleaseExchangeSource(int Version, string SourceId, string SourceIntentHash,
    string OpeningCommandId, string OpeningOutcomeHash, ExchangeParticipantAuthority Authority,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HoldReleaseLineage? Lineage = null);

/// <summary>Remainder liability produced by an exact partial recovery, and the receipt that created it.</summary>
public sealed record HoldReleaseLineage(string OriginalHoldRecordId, string RecoveryCommandId, string RecoveryOutcomeHash);

/// <summary>
/// Fiat-reversal recovery from one settled held purchase: <paramref name="AttributedSourceUnits"/> is this reversal's
/// cumulative want from the purchase and <paramref name="ClaimSourceUnits"/> the lot's whole attributed share of it.
/// </summary>
public sealed record ExchangePurchaseRecovery(int Version, string ReversalId, string LotId, Guid SubjectUserId,
    string SourceId, string AttributedSourceUnits, string Reason, string ClaimSourceUnits);

/// <summary>
/// Immutable outcome of one purchase recovery step; <paramref name="CreditedSourceUnits"/> is that step's own delta.
/// A <paramref name="Clawback"/> step reserves <paramref name="RecoveredHoldUnits"/> delivered units to claw back.
/// </summary>
public sealed record ExchangePurchaseRecoveryResult(string ReceiptId, string SourceId, string RecoveredHoldUnits,
    string CreditedSourceUnits, string LiabilityRecordId, string? RemainderRecordId, bool Clawback = false);

/// <summary>A settled held purchase's current liability (after any recovery split) and, once released, who received it.</summary>
public sealed record ExchangePurchaseLiability(string LiabilityRecordId, string State, string QuantityBaseUnits,
    string? ReleasedToAddress);

/// <summary>Explicit same-key replacement of an uncommitted release disclosure, bound to the source version it supersedes.</summary>
public sealed record ExchangeReleaseRevision(Guid RequestId, long ExpectedVersion, ExchangeReleaseConsent Consent);

/// <summary>Server-authored automatic-release terms and current quote for a held purchase, identified by its canonical hash.</summary>
public sealed record ExchangeReleaseDisclosure(HoldReleaseMandateTerms Terms, ValueCustodyQuote Quote, string DisclosureHash);
