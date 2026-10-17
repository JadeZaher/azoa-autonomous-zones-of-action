using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private WttAlgorandTargetPlan ReadWttAlgoTargetQuote(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote)
    {
        if (!ValidWttAlgoTargetIntent(intent) || proofs?.Transfer?.Vaa is null || quote is null || quote.Phase != "Target"
            || quote.IntentHash != CanonicalJson.Hash(intent) || quote.ProtocolIdentity != intent.Target.ProtocolIdentity)
            throw new InvalidDataException("WTT quote differs from the exact target intent.");
        var plan = JsonSerializer.Deserialize<WttAlgorandTargetPlan>(quote.PlanJson)
            ?? throw new InvalidDataException("WTT target plan is missing.");
        if (plan.Version != 1 || plan.IntentHash != quote.IntentHash || plan.Transfer?.Vaa is null
            || plan.Steps is null || plan.Steps.Count is < 1 or > 6 || plan.Steps.Any(step => step?.Reservation is null)
            || plan.CoreApplication.ToString(CultureInfo.InvariantCulture) != intent.Target.CoreBridge
            || plan.TokenApplication.ToString(CultureInfo.InvariantCulture) != intent.Target.TokenBridge
            || CanonicalJson.Hash(new WttAlgoDeployment(plan.CoreApplication, plan.TokenApplication, plan.GenesisHash,
                plan.CoreApprovalHash, plan.CoreClearHash, plan.TokenApprovalHash, plan.TokenClearHash)) != intent.Target.CodeIdentity
            || "algorand:" + plan.GenesisHash != quote.ProtocolIdentity || plan.Decimals != intent.TargetDecimals
            || intent.TargetAssetAddress is not null && plan.Asset?.ToString(CultureInfo.InvariantCulture) != intent.TargetAssetAddress
            || plan.MinimumFee == 0 || plan.RegularFee < plan.MinimumFee || plan.StorageFee < checked(plan.MinimumFee * 2)
            || quote.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan))
            || quote.QuoteId != CanonicalJson.Hash(new { Version = 1, IntentHash = quote.IntentHash, Plan = quote.PlanJson, quote.ExpiresAt })
            || quote.Stages is null || !quote.Stages.SequenceEqual(plan.Steps.Select(step => step.Reservation)))
            throw new InvalidDataException("WTT target quote identity, deployment or reservations changed.");
        var transfer = WttProofBinding.Transfer(intent, plan.Transfer);
        if (CanonicalJson.Hash(transfer) != CanonicalJson.Hash(WttProofBinding.Transfer(intent, proofs.Transfer)))
            throw new InvalidDataException("Target quote changed its exact transfer proof envelope.");
        if (plan.Attestation is not null && (proofs.Attestation is null
            || CanonicalJson.Hash(WttProofBinding.Attestation(intent, plan.Attestation))
                != CanonicalJson.Hash(WttProofBinding.Attestation(intent, proofs.Attestation))))
            throw new InvalidDataException("Target quote changed its exact registration proof envelope.");
        var custodyIndex = intent.Asset.OriginChain == 8
            ? BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(intent.Asset.OriginAddress).AsSpan(24)) : intent.Asset.OriginChain;
        var custodyEmitter = intent.Asset.OriginChain == 8 ? "native"u8.ToArray() : Convert.FromHexString(intent.Asset.OriginAddress);
        if (plan.CustodyAddress != WttAlgorandStorage.Address(plan.TokenApplication, custodyIndex, custodyEmitter)
            || intent.Asset.OriginChain == 8 && plan.Asset != custodyIndex)
            throw new InvalidDataException("Target custody or original asset identity changed.");
        var rank = -1;
        foreach (var step in plan.Steps)
        {
            var current = step.Stage switch { "AttestationSequenceStorage" => 0, "TransferSequenceStorage" => 1,
                "WrappedStorage" => 2, "Register" => 3, "RecipientOptIn" => 4, "Redeem" => 5, _ => -1 };
            var attestation = current is 0 or 2 or 3;
            var participant = current == 4 ? intent.RecipientAddress : intent.TargetAuthority.Address;
            if (current <= rank || step.Purpose != (attestation ? "Attestation" : "Transfer")
                || attestation && plan.Attestation is null || step.Reservation.Stage != step.Stage
                || step.Reservation.ParticipantAddress != participant || current == 4 && plan.Asset == 0)
                throw new InvalidDataException("Target stages changed order, purpose or required participant.");
            rank = current;
            if (current < 3)
            {
                var index = custodyIndex; var emitter = custodyEmitter;
                if (current < 2)
                {
                    var proof = current == 0 ? plan.Attestation!.Vaa : transfer;
                    index = UInt64Text.Parse(proof.Sequence) / (127 * 15 * 8);
                    emitter = new byte[34]; BinaryPrimitives.WriteUInt16BigEndian(emitter, proof.EmitterChain);
                    Convert.FromHexString(proof.EmitterAddress).CopyTo(emitter, 2);
                }
                if (step.StorageApplication != plan.TokenApplication || step.StorageIndex != index
                    || step.StorageEmitterHex != Convert.ToHexStringLower(emitter))
                    throw new InvalidDataException("Target prerequisite changed its exact storage role.");
            }
            else if (step.StorageApplication != 0 || step.StorageIndex != 0 || step.StorageEmitterHex != "")
                throw new InvalidDataException("Target economic stage contains a foreign storage role.");
            var fees = BuildWttAlgoTargetStep(intent, plan, step, 1, "quote:" + plan.IntentHash + ":" + step.Stage)
                .Aggregate(0UL, (sum, transaction) => checked(sum + transaction.Fee!.Value));
            if (step.Reservation.NetworkFee != fees.ToString(CultureInfo.InvariantCulture) || step.Reservation.Value != "0"
                || step.Reservation.LockedDeposit != (current < 3 ? "1002000" : current is 3 or 4 ? "100000" : "0"))
                throw new InvalidDataException("Target reservation differs from the exact protocol transaction costs.");
        }
        if (rank != 5 || plan.Steps.Any(step => step.Stage == "Register") != (plan.Attestation is not null)
            || plan.Asset is null && plan.Attestation is null)
            throw new InvalidDataException("Target registration or redemption stages are incomplete.");
        RequireWttAlgoBudget(plan.Steps.Select(step => step.Reservation), intent.TargetBudget);
        return plan;
    }
}
