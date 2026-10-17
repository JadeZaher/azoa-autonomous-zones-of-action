namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Recipient's exact native-output offer after the original token redemption is finalized.</summary>
public sealed record WttNativeOutputQuote(int Version, string QuoteId, string IntentHash, string RedemptionPreparationHash,
    string RedemptionObservationHash, string ProtocolIdentity, string RecipientAddress, string MintAddress,
    string SourceTokenAccount, string NativeAmount, string NetworkFee, string TemporaryRent, string PlanJson,
    DateTimeOffset ExpiresAt);

/// <summary>Fresh recipient acceptance of the exact output and temporary funding ceilings.</summary>
public sealed record WttNativeOutputConsent(int Version, string RequestKey, string QuoteId,
    string MaximumNetworkFee, string MaximumTemporaryRent);

/// <summary>Finalized native output is distinct from token redemption and temporary rent return.</summary>
public sealed record WttNativeOutputObservation(WttStageObservation Stage, string? NativeAmountReceived,
    string? TemporaryRentReturned);
