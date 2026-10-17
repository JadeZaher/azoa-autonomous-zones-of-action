using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Validates provider-independent authorization and exactly-once logical fee terms.</summary>
public static class NftTransactionAuthorization
{
    public static bool IsValid(NftTransactionIntent intent)
    {
        var grant = intent.Authorization;
        if (grant is null || !Guid.TryParseExact(grant.LifecycleId, "N", out _) || grant.ActorUserId == Guid.Empty
            || grant.ActorSigningContext.IsPlatform || grant.ActorSigningContext.UserId != grant.ActorUserId
            || grant.ActorSigningContext.WalletId == Guid.Empty || grant.Stage != intent.Kind || grant.PreparationGeneration < 0
            || intent.PreparationKey != $"{grant.LifecycleId}:{grant.Stage}:{grant.PreparationGeneration}"
            || grant.FeeQuote.ChainType != intent.ChainType || grant.FeeQuote.Network != intent.Network
            || grant.FeeQuote.PayerUserId != grant.ActorUserId || grant.FeeQuote.Operation != grant.Operation
            || grant.Operation is not ("Mint" or "Transfer" or "Burn" or "Revoke" or "Refund")) return false;
        var stageAllowed = grant.Operation switch
        {
            "Mint" => intent.Kind is NftTransactionKinds.Create or NftTransactionKinds.Accept or NftTransactionKinds.Deliver,
            "Burn" => intent.Kind is NftTransactionKinds.Accept or NftTransactionKinds.ReturnForBurn or NftTransactionKinds.Burn,
            "Revoke" => intent.Kind is NftTransactionKinds.Accept or NftTransactionKinds.Revoke,
            _ => intent.Kind is NftTransactionKinds.Accept or NftTransactionKinds.Transfer,
        };
        if (!stageAllowed) return false;
        var final = intent.Kind is NftTransactionKinds.Transfer or NftTransactionKinds.Burn or NftTransactionKinds.Revoke or NftTransactionKinds.Deliver
            || (intent.Kind == NftTransactionKinds.Create && !intent.Soulbound);
        if (grant.IsFinalEffect != final || (intent.Soulbound && (grant.IssuerUserId != grant.ActorUserId
            || grant.IssuerAddress != grant.ActorAddress || grant.IssuerSigningContext != grant.ActorSigningContext))) return false;
        if (!final || grant.FeeQuote.NativeBaseUnits == "0") return intent.Fee is null;
        return intent.Fee is { } fee && fee.Quote == grant.FeeQuote && fee.PayerAddress == grant.ActorAddress
            && fee.SigningContext == grant.ActorSigningContext;
    }
}
