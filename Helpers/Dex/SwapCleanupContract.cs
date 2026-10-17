using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Helpers.Dex;

/// <summary>Validates separately consented cleanup against its immutable original owner and cost ceiling.</summary>
public static class SwapCleanupContract
{
    public static bool IsValid(SwapIntent intent, SwapCleanupConsent consent)
        => consent.Version == 1 && consent.OperationId == intent.OperationId && consent.QuoteHash == intent.QuoteHash &&
            consent.ActorUserId == intent.Terms.ActorUserId && consent.Owner == intent.Terms.Owner &&
            consent.TenantId != Guid.Empty && (consent.TenantId is null || consent.TenantId == intent.Terms.TenantId) &&
            consent.RequestKeyHash is { Length: 64 } && consent.RequestKeyHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            consent.AssetId == intent.Terms.OutputAsset.Id && consent.Semantics == "CloseCurrentZeroHolding" &&
            consent.AcceptCurrentZeroHoldingReplacement &&
            UnsignedIntegerText.TryParse(consent.MaximumNetworkFee, out var cap) &&
            UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumCleanupFee, out var originalCap) && cap <= originalCap;
}
