using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Helpers.Dex;

/// <summary>Validates separately consented cleanup against its immutable original owner and cost ceiling.</summary>
public static class SwapCleanupContract
{
    /// <summary>Closes the owner's current zero holding of the original output asset.</summary>
    public const string CloseCurrentZeroHolding = "CloseCurrentZeroHolding";

    /// <summary>Sets the owner's current allowance for the original input token and quoted spender to zero.</summary>
    public const string RevokeCurrentAllowance = "RevokeCurrentAllowance";

    public static bool IsValid(SwapIntent intent, SwapCleanupConsent consent)
        => consent.Version == 1 && consent.OperationId == intent.OperationId && consent.QuoteHash == intent.QuoteHash &&
            consent.ActorUserId == intent.Terms.ActorUserId && consent.Owner == intent.Terms.Owner &&
            consent.TenantId != Guid.Empty && (consent.TenantId is null || consent.TenantId == intent.Terms.TenantId) &&
            consent.RequestKeyHash is { Length: 64 } && consent.RequestKeyHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            SemanticsMatch(intent, consent) &&
            UnsignedIntegerText.TryParse(consent.MaximumNetworkFee, out var cap) &&
            UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumCleanupFee, out var originalCap) && cap <= originalCap;

    /// <summary>Derives the single affirmative semantic; both or neither consent flags is no consent.</summary>
    public static string? Semantics(bool acceptCurrentZeroHoldingReplacement, bool acceptCurrentAllowanceRevocation)
        => acceptCurrentZeroHoldingReplacement == acceptCurrentAllowanceRevocation ? null
            : acceptCurrentZeroHoldingReplacement ? CloseCurrentZeroHolding : RevokeCurrentAllowance;

    private static bool SemanticsMatch(SwapIntent intent, SwapCleanupConsent consent) => consent.Semantics switch
    {
        CloseCurrentZeroHolding => consent.AcceptCurrentZeroHoldingReplacement && !consent.AcceptCurrentAllowanceRevocation &&
            consent.AssetId == intent.Terms.OutputAsset.Id,
        RevokeCurrentAllowance => consent.AcceptCurrentAllowanceRevocation && !consent.AcceptCurrentZeroHoldingReplacement &&
            !intent.Terms.InputAsset.Native && consent.AssetId == intent.Terms.InputAsset.Id,
        _ => false,
    };
}
