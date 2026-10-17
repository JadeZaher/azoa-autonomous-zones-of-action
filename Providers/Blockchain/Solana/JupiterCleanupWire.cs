using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Builds the exact separately consented empty-account guard and owner-only close.</summary>
internal static class JupiterCleanupWire
{
    internal static IReadOnlyList<TransactionInstruction> Build(SwapIntent intent, SwapCleanupConsent consent,
        string stage, int generation, string preparationKey, string account)
    {
        if (!SwapCleanupContract.IsValid(intent, consent) || stage != "CloseAta" || generation < 0 ||
            preparationKey != SwapOperationIdentity.CleanupPreparation(intent.OperationId, stage, generation,
                CanonicalJson.Hash(intent), consent) || intent.Terms.OutputAsset.Native)
            throw new InvalidDataException("Jupiter cleanup requires its own exact retained-account consent.");
        var owner = new PublicKey(intent.Terms.Owner.Address);
        var mint = new PublicKey(consent.AssetId);
        if (AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint).Key != account ||
            !intent.Terms.Prerequisites.Any(effect => effect.Kind == "CreateAta" && effect.AssetId == mint.Key &&
                effect.OwnerAddress == owner.Key && effect.DestinationAddress == account && effect.RetainedOnSwapFailure))
            throw new InvalidDataException("Jupiter cleanup account was not an original disclosed owner ATA.");
        var pins = JupiterAcceptedQuote.Read(intent.Terms);
        return
        [
            SolanaComputeBudget.Limit(pins.ComputeUnits), SolanaComputeBudget.Price(pins.ComputePrice),
            MemoProgram.NewMemoV2(CanonicalJson.Hash(new { Domain = "azoa:jupiter-stage/1", Identity = preparationKey }), owner),
            JupiterZeroHoldingGuard.Build(new PublicKey(pins.GuardProgram), new PublicKey(account), mint, owner),
            TokenProgram.CloseAccount(new PublicKey(account), owner, owner)
        ];
    }
}
