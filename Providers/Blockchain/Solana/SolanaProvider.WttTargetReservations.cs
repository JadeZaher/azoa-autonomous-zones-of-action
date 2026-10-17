using System.Numerics;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private static void RequireWttTargetReservations(WttOperationIntent intent, WttSolanaTargetPlan plan)
    {
        var mint = intent.Asset.OriginChain == 1 ? new PublicKey(Convert.FromHexString(intent.Asset.OriginAddress))
            : WttSolanaAccounts.WrappedMint(new PublicKey(plan.TokenBridge), intent.Asset.OriginChain, Convert.FromHexString(intent.Asset.OriginAddress));
        if (plan.Mint != mint.Key || (plan.Proofs.Count == 2) != (plan.MetadataPolicy is not null)
            || plan.Proofs.Any(value => value is null || value.VerificationGeneration < 0 || value.Guardians is null)
            || plan.Steps.Any(value => value is null || value.Reservation is null))
            throw new InvalidDataException("Target plan changed its mint, reviewed metadata policy or proof generation.");
        var rent = Convert.FromBase64String(plan.Guard.RentBase64);
        ulong Minimum(int size) => WttSolanaCostGuard.MinimumBalance(rent, size);
        var position = 0;
        void Require(string stage, string kind, WttSolanaTargetProof proof, int start, int count, ulong accountRent, ulong levy = 0)
        {
            if (position + 1 >= plan.Steps.Count) throw new InvalidDataException("Target stage or cleanup reservation is missing.");
            var step = plan.Steps[position++]; var cleanup = plan.Steps[position++];
            var retained = checked(Minimum(0) + accountRent);
            var ata = checked((step.CreateRecipientAta ? Minimum(165) : 0)
                + (step.CreateFeeAta && intent.TargetAuthority.Address != intent.RecipientAddress ? Minimum(165) : 0));
            if (step.Stage != stage || step.Kind != kind || step.ProofLabel != proof.Label || step.SignatureStart != start || step.SignatureCount != count
                || step.ComputeUnits is 0 or > 1400000 || step.Funding != checked(retained + levy)
                || kind != "Redeem" && (step.CreateRecipientAta || step.CreateFeeAta)
                || step.Reservation.Stage != stage || step.Reservation.ParticipantAddress != plan.Sender
                || UInt64Text.Parse(step.Reservation.LockedDeposit) != checked(retained + ata) || UInt64Text.Parse(step.Reservation.Value) != levy
                || cleanup.Stage != "Cleanup:" + stage || cleanup.Kind != "Cleanup" || cleanup.ProofLabel != proof.Label
                || cleanup.SignatureStart != 0 || cleanup.SignatureCount != 0 || cleanup.Funding != 0
                || cleanup.CreateRecipientAta || cleanup.CreateFeeAta || cleanup.ComputeUnits != 0 || cleanup.ComputePrice != 0
                || cleanup.Reservation.Stage != cleanup.Stage || cleanup.Reservation.ParticipantAddress != plan.Sender
                || cleanup.Reservation.LockedDeposit != "0" || cleanup.Reservation.Value != "0")
                throw new InvalidDataException("Target stage, protected funding or separate fee/rent/value categories differ from the closed protocol plan.");
            _ = UInt64Text.Parse(step.Reservation.NetworkFee); _ = UInt64Text.Parse(cleanup.Reservation.NetworkFee);
        }
        foreach (var proof in plan.Proofs)
        {
            WttSolanaCoreInstructions.RequireCanonicalVaa(proof.Vaa);
            if (!proof.AlreadyPosted)
            {
                if (proof.Guardians.Count is < 1 or > 19 || proof.PostedSignatureSet is not null)
                    throw new InvalidDataException("Unposted proof requires its exact bounded guardian membership.");
                for (var offset = 0; offset < proof.Vaa.Signatures.Count; offset += 3)
                    Require("Verify:" + proof.Label + ":" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture), "Verify", proof,
                        offset, Math.Min(3, proof.Vaa.Signatures.Count - offset), Minimum(40 + proof.Guardians.Count));
                Require("Post:" + proof.Label, "Post", proof, 0, 0, Minimum(95 + proof.Vaa.PayloadHex.Length / 2));
            }
            else if (proof.Guardians.Count != 0 || string.IsNullOrWhiteSpace(proof.PostedSignatureSet))
                throw new InvalidDataException("Already-posted proof requires exact persisted signature-set evidence.");
            if (proof.Label == "Attestation")
            {
                var costs = plan.MetadataPolicy!.Costs(rent);
                Require("Register", "Register", proof, 0, 0, checked(Minimum(1) + Minimum(82) + Minimum(35) + costs.RetainedRent), costs.ProtocolLevy);
            }
            else Require("Redeem", "Redeem", proof, 0, 0, Minimum(1));
        }
        var budget = intent.TargetBudget;
        if (position != plan.Steps.Count
            || plan.Steps.Aggregate(BigInteger.Zero, (sum, value) => sum + WttPayloadCodec.ParseAmount(value.Reservation.NetworkFee)) > WttPayloadCodec.ParseAmount(budget.MaximumNetworkFee)
            || plan.Steps.Aggregate(BigInteger.Zero, (sum, value) => sum + WttPayloadCodec.ParseAmount(value.Reservation.LockedDeposit)) > WttPayloadCodec.ParseAmount(budget.MaximumLockedDeposit)
            || plan.Steps.Aggregate(BigInteger.Zero, (sum, value) => sum + WttPayloadCodec.ParseAmount(value.Reservation.Value)) > WttPayloadCodec.ParseAmount(budget.MaximumValue))
            throw new InvalidDataException("Target stage sequence or total costs differ from frozen caller limits.");
    }
}
