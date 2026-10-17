using System.Globalization;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private static WttAlgorandTargetPlan PriceWttAlgoTargetPlan(WttOperationIntent intent, WttAlgorandTargetPlan input,
        ulong byteFee, ulong round)
    {
        var plan = input;
        for (var pass = 0; pass < 8; pass++)
        {
            var regular = plan.RegularFee; var storage = plan.StorageFee;
            var registrationVerification = plan.RegistrationVerificationFee; var registrationCompletion = plan.RegistrationCompletionFee;
            var redemptionVerification = plan.RedemptionVerificationFee; var redemptionCompletion = plan.RedemptionCompletionFee;
            foreach (var step in plan.Steps)
            {
                var key = "quote:" + plan.IntentHash + ":" + step.Stage;
                var transactions = BuildWttAlgoTargetStep(intent, plan, step, round, key);
                var batches = step.Stage is "Register" or "Redeem"
                    ? ((step.Stage == "Register" ? plan.Attestation! : plan.Transfer).Vaa.Signatures.Count + 5) / 6 : 0;
                ulong pooled = 0;
                for (var index = 0; index < transactions.Count; index++)
                {
                    var envelope = step.StorageApplication != 0 && index == 1
                        ? WttAlgorandStorageWire.ProtocolEnvelope(WttAlgoTargetStorageParameters(intent, plan, step, round, key))
                        : index < batches ? WttAlgorandTargetWire.ProtocolEnvelope(WttAlgoTargetParameters(intent, plan, step, round, key), index)
                            : new SignedTransaction(transactions[index], new Signature(Enumerable.Repeat((byte)1, 64).ToArray()));
                    var byteCost = checked(byteFee * checked((ulong)Encoder.EncodeToMsgPackOrdered(envelope).Length + 9));
                    if (step.StorageApplication != 0) { pooled = checked(pooled + byteCost); continue; }
                    if (index <= batches && batches > 0) { pooled = checked(pooled + byteCost); continue; }
                    if (index == transactions.Count - 1 && step.Stage == "Register") registrationCompletion = Math.Max(registrationCompletion, byteCost);
                    else if (index == transactions.Count - 1 && step.Stage == "Redeem") redemptionCompletion = Math.Max(redemptionCompletion, byteCost);
                    else regular = Math.Max(regular, byteCost);
                }
                if (step.StorageApplication != 0) storage = Math.Max(storage, pooled);
                if (step.Stage == "Register") registrationVerification = Math.Max(registrationVerification, pooled);
                if (step.Stage == "Redeem") redemptionVerification = Math.Max(redemptionVerification, pooled);
            }
            var following = plan with { RegularFee = regular, StorageFee = storage,
                RegistrationVerificationFee = registrationVerification, RegistrationCompletionFee = registrationCompletion,
                RedemptionVerificationFee = redemptionVerification, RedemptionCompletionFee = redemptionCompletion };
            if (following == plan)
            {
                return plan with { Steps = plan.Steps.Select(step => step with { Reservation = step.Reservation with
                {
                    NetworkFee = BuildWttAlgoTargetStep(intent, plan, step, round, "quote:" + plan.IntentHash + ":" + step.Stage)
                        .Aggregate(0UL, (sum, transaction) => checked(sum + transaction.Fee!.Value)).ToString(CultureInfo.InvariantCulture)
                } }).ToArray() };
            }
            plan = following;
        }
        throw new InvalidDataException("Target fee encoding did not converge within the bounded canonical plan.");
    }

    private static IReadOnlyList<Transaction> BuildWttAlgoTargetStep(WttOperationIntent intent, WttAlgorandTargetPlan plan,
        WttAlgorandTargetStep step, ulong round, string key, ulong? resolvedAsset = null)
        => step.Stage switch
        {
            "AttestationSequenceStorage" or "TransferSequenceStorage" or "WrappedStorage"
                => WttAlgorandStorageWire.Build(WttAlgoTargetStorageParameters(intent, plan, step, round, key)),
            "Register" or "Redeem" => WttAlgorandTargetWire.Build(WttAlgoTargetParameters(intent, plan, step, round, key, resolvedAsset)),
            "RecipientOptIn" => WttAlgorandHoldingWire.Build(new("RecipientOptIn", key, intent.RecipientAddress,
                plan.TokenApplication, resolvedAsset ?? plan.Asset ?? ulong.MaxValue, plan.MinimumFee, plan.RegularFee, 0,
                round, checked(round + 1000), plan.GenesisHash, plan.GenesisId)),
            _ => throw new InvalidDataException("Unknown Algorand target prerequisite or settlement stage.")
        };

    private static WttAlgorandStorageParameters WttAlgoTargetStorageParameters(WttOperationIntent intent,
        WttAlgorandTargetPlan plan, WttAlgorandTargetStep step, ulong round, string key)
        => new(key, intent.TargetAuthority.Address, step.StorageApplication, step.StorageIndex, step.StorageEmitterHex,
            WttAlgorandStorageWire.ProtocolSeedDeposit, plan.StorageFee, plan.MinimumFee,
            round, checked(round + 1000), plan.GenesisHash, plan.GenesisId);

    private static WttAlgorandTargetParameters WttAlgoTargetParameters(WttOperationIntent intent,
        WttAlgorandTargetPlan plan, WttAlgorandTargetStep step, ulong round, string key, ulong? resolvedAsset = null)
        => new(step.Stage, key, intent.TargetAuthority.Address, plan.CoreApplication, plan.TokenApplication,
            resolvedAsset ?? plan.Asset ?? (step.Stage == "Register" ? 0 : ulong.MaxValue), intent.RecipientAddress,
            step.Stage == "Register" ? plan.Attestation ?? throw new InvalidDataException("Registration lacks attestation.") : plan.Transfer,
            plan.MinimumFee, plan.RegularFee, step.Stage == "Register" ? plan.RegistrationVerificationFee : plan.RedemptionVerificationFee,
            step.Stage == "Register" ? plan.RegistrationCompletionFee : plan.RedemptionCompletionFee,
            round, checked(round + 1000), plan.GenesisHash, plan.GenesisId);
}
