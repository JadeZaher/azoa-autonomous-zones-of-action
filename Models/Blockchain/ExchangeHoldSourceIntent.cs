using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Immutable exchange purchase and reserve liability selected before any commitment or signing.</summary>
public sealed record ExchangeHoldSourceIntent(int Version, string RequestKey, Guid ApiKeyId,
    Guid TenantUserId, Guid ParticipantUserId, string OriginalRequestJson, HoldCommandIntent Opening,
    string PlatformAssetId, string PlatformQuantityBaseUnits, string ProjectReference,
    string TreasuryAddress, SigningWalletWitness ParticipantWallet, string MaximumNetworkFeeBaseUnits = "0",
    ExchangeReleaseConsent? ReleaseConsent = null);

/// <summary>Exact accepted future release disclosure and separately scoped actual tenant authority.</summary>
public sealed record ExchangeReleaseConsent(Guid RequestId, HoldReleaseMandateTerms Terms, ValueCustodyQuote Quote,
    SigningConsentWitness Authority, string DisclosureHash);

/// <summary>Original completed purchase and opening receipt that constrain a delayed release.</summary>
public sealed record HoldReleaseExchangeSource(int Version, string SourceId, string SourceIntentHash,
    string OpeningCommandId, string OpeningOutcomeHash, ExchangeParticipantAuthority Authority);
