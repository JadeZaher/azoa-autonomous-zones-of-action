using System.Numerics;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers.Dex;

/// <summary>Remaining consented cost caps for one more stage, counting unresolved rent at its full reservation.</summary>
public static class SwapBudget
{
    public static bool WithinRemaining(SwapIntent intent, SwapOperationRecord operation, SwapStagePlan next)
    {
        if (!UnsignedIntegerText.TryParse(next.ReservedNetworkFee, out var nextFee) ||
            !UnsignedIntegerText.TryParse(next.ReservedLockedBalance, out var nextRent) ||
            !UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumNetworkFee, out var effectCap) ||
            !UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumCleanupFee, out var cleanupCap) ||
            !UnsignedIntegerText.TryParse(intent.Terms.Budget.MaximumLockedBalance, out var rentCap) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedEffectNetworkFee, out var effectFee) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedCleanupNetworkFee, out var cleanupFee) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedRetainedRent, out var rent) ||
            !UnsignedIntegerText.TryParse(operation.ConfirmedReleasedRent, out var released) || released > rent ||
            !UnsignedIntegerText.TryParse(operation.UnresolvedRentReservation, out var unresolvedRent)) return false;
        if (next.Purpose == "Effect") effectFee += nextFee;
        else
        {
            if (next.CleanupConsent is not { } consent || !UnsignedIntegerText.TryParse(consent.MaximumNetworkFee, out var acceptedCleanupCap)) return false;
            cleanupCap = BigInteger.Min(cleanupCap, acceptedCleanupCap);
            cleanupFee += nextFee;
        }
        return effectFee <= effectCap && cleanupFee <= cleanupCap && rent - released + unresolvedRent + nextRent <= rentCap;
    }
}
