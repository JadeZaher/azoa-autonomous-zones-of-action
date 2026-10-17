using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private WttAlgorandSourcePlan ReadWttAlgoSourceQuote(WttOperationIntent intent, WttStageQuote quote)
    {
        if (!ValidWttAlgoSourceIntent(intent) || quote is null || quote.Phase != "Source"
            || quote.IntentHash != CanonicalJson.Hash(intent) || quote.ProtocolIdentity != intent.Source.ProtocolIdentity)
            throw new InvalidDataException("WTT quote is not bound to the exact source intent.");
        var plan = JsonSerializer.Deserialize<WttAlgorandSourcePlan>(quote.PlanJson)
            ?? throw new InvalidDataException("WTT source plan is missing.");
        if (plan.Version != 1 || plan.IntentHash != quote.IntentHash || plan.Steps is null || plan.Steps.Count is < 1 or > 5
            || plan.Steps.Any(step => step is null || step.Reservation is null)
            || plan.CoreApplication.ToString(CultureInfo.InvariantCulture) != intent.Source.CoreBridge
            || plan.TokenApplication.ToString(CultureInfo.InvariantCulture) != intent.Source.TokenBridge
            || CanonicalJson.Hash(new WttAlgoDeployment(plan.CoreApplication, plan.TokenApplication, plan.GenesisHash,
                plan.CoreApprovalHash, plan.CoreClearHash, plan.TokenApprovalHash, plan.TokenClearHash)) != intent.Source.CodeIdentity
            || "algorand:" + plan.GenesisHash != quote.ProtocolIdentity || plan.Asset != UInt64Text.Parse(intent.Asset.TokenAddress)
            || plan.MinimumFee == 0 || plan.RegularFee < plan.MinimumFee || plan.ApplicationFee < plan.MinimumFee
            || plan.StorageFee < checked(plan.MinimumFee * 2)
            || quote.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan))
            || quote.QuoteId != CanonicalJson.Hash(new { Version = 1, IntentHash = quote.IntentHash, Plan = quote.PlanJson, quote.ExpiresAt })
            || quote.Stages is null || !quote.Stages.SequenceEqual(plan.Steps.Select(step => step.Reservation)))
            throw new InvalidDataException("WTT source quote identity, deployment or reservation changed.");
        var rank = -1;
        foreach (var step in plan.Steps)
        {
            var currentRank = step.Stage switch { "EmitterStorage" => 0, "CustodyStorage" => 1, "CustodyOptIn" => 2, "Attest" => 3, "Transfer" => 4, _ => -1 };
            if (currentRank <= rank || step.Reservation.Stage != step.Stage || step.Reservation.ParticipantAddress != intent.SourceAuthority.Address)
                throw new InvalidDataException("WTT source stages are duplicated, reordered or assigned to another payer.");
            rank = currentRank;
            var storage = currentRank < 2;
            if (storage && (step.StorageApplication != (currentRank == 0 ? plan.CoreApplication : plan.TokenApplication)
                || step.StorageIndex != (currentRank == 0 ? 0 : plan.Asset)
                || step.StorageEmitterHex != Convert.ToHexStringLower(currentRank == 0 ? Address.ForApplication(plan.TokenApplication).Bytes : "native"u8.ToArray()))
                || !storage && (step.StorageApplication != 0 || step.StorageIndex != 0 || step.StorageEmitterHex != "")
                || currentRank is 1 or 2 && intent.Asset.OriginChain != 8 || currentRank == 2 && plan.Asset == 0)
                throw new InvalidDataException("WTT prerequisite account identity differs from its protocol role.");
            var deposit = storage ? "1002000" : currentRank == 2 ? "100000" : "0";
            var value = currentRank >= 3 ? checked(plan.MessageFee + (currentRank == 4 && intent.Asset.NativeAsset ? UInt64Text.Parse(intent.Amount.Debited) : 0)) : 0;
            var totalFee = BuildWttAlgoSourceStep(intent, plan, step, 1, "quote:" + plan.IntentHash + ":" + step.Stage)
                .Aggregate(0UL, (sum, transaction) => checked(sum + transaction.Fee!.Value));
            if (step.Reservation.LockedDeposit != deposit || step.Reservation.Value != value.ToString(CultureInfo.InvariantCulture)
                || step.Reservation.NetworkFee != totalFee.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("WTT source reservation differs from the exact transaction costs.");
        }
        if (rank != 4 || plan.Steps.Any(step => step.Stage == "Attest") != intent.RequiresAttestation)
            throw new InvalidDataException("WTT source publication stages are incomplete.");
        RequireWttAlgoBudget(plan.Steps.Select(step => step.Reservation), intent.SourceBudget);
        return plan;
    }

    private static IReadOnlyList<Transaction> BuildWttAlgoSourceStep(WttOperationIntent intent,
        WttAlgorandSourcePlan plan, WttAlgorandSourceStep step, ulong round, string key)
        => step.Stage switch
        {
            "EmitterStorage" or "CustodyStorage" => WttAlgorandStorageWire.Build(WttAlgoStorageParameters(intent, plan, step, round, key)),
            "CustodyOptIn" => WttAlgorandHoldingWire.Build(new("CustodyOptIn", key, intent.SourceAuthority.Address,
                plan.TokenApplication, plan.Asset, plan.MinimumFee, plan.RegularFee, Math.Max(plan.ApplicationFee, checked(plan.MinimumFee * 2)),
                round, checked(round + 1000), plan.GenesisHash, plan.GenesisId)),
            "Attest" or "Transfer" => WttAlgorandSourceWire.Build(WttAlgoPublicationParameters(intent, plan, step.Stage, round, key)),
            _ => throw new InvalidDataException("Unreviewed Algorand source stage.")
        };

    private static WttAlgorandStorageParameters WttAlgoStorageParameters(WttOperationIntent intent,
        WttAlgorandSourcePlan plan, WttAlgorandSourceStep step, ulong round, string key)
        => new(key, intent.SourceAuthority.Address, step.StorageApplication, step.StorageIndex, step.StorageEmitterHex,
            WttAlgorandStorageWire.ProtocolSeedDeposit, plan.StorageFee, plan.MinimumFee, round, checked(round + 1000),
            plan.GenesisHash, plan.GenesisId);

    private static WttAlgorandSourceParameters WttAlgoPublicationParameters(WttOperationIntent intent,
        WttAlgorandSourcePlan plan, string stage, ulong round, string key)
    {
        var fee = WttPayloadCodec.ParseAmount(intent.Amount.Fee) * BigInteger.Pow(10, Math.Max(0, intent.Asset.Decimals - 8));
        if (fee > ulong.MaxValue) throw new InvalidDataException("Algorand relayer fee exceeds its wire range.");
        return new(stage, key, intent.SourceAuthority.Address, plan.CoreApplication, plan.TokenApplication,
            plan.Asset, UInt64Text.Parse(intent.Amount.Debited), (ulong)fee, intent.Target.ProtocolChain,
            intent.RecipientProtocolAddress, intent.Asset.OriginChain != 8, intent.Asset.OriginChain,
            intent.Asset.OriginAddress, plan.AssetCreator, plan.MessageFee, round, checked(round + 1000),
            plan.GenesisHash, plan.GenesisId, plan.MinimumFee, plan.RegularFee, plan.ApplicationFee);
    }

    private static WttAlgorandSourcePlan PriceWttAlgoSourcePlan(WttOperationIntent intent,
        WttAlgorandSourcePlan plan, ulong byteFee, ulong round)
    {
        var regular = plan.RegularFee; var application = plan.ApplicationFee; var storage = plan.StorageFee;
        foreach (var step in plan.Steps)
        {
            var key = "quote:" + plan.IntentHash + ":" + step.Stage;
            var transactions = BuildWttAlgoSourceStep(intent, plan, step, round, key);
            if (step.Stage is "EmitterStorage" or "CustodyStorage")
            {
                var payment = new SignedTransaction(transactions[0], new Signature(Enumerable.Repeat((byte)1, 64).ToArray()));
                var logic = WttAlgorandStorageWire.ProtocolEnvelope(WttAlgoStorageParameters(intent, plan, step, round, key));
                var size = checked((ulong)Encoder.EncodeToMsgPackOrdered(payment).Length + (ulong)Encoder.EncodeToMsgPackOrdered(logic).Length + 9);
                storage = Math.Max(storage, checked(byteFee * size));
                continue;
            }
            foreach (var transaction in transactions)
            {
                var envelope = new SignedTransaction(transaction, new Signature(Enumerable.Repeat((byte)1, 64).ToArray()));
                // MessagePack uint64 fee encoding can grow by at most nine bytes.
                var amount = checked(byteFee * ((ulong)Encoder.EncodeToMsgPackOrdered(envelope).Length + 9));
                if (transaction is ApplicationCallTransaction)
                {
                    var inner = step.Stage == "CustodyOptIn" || plan.MessageFee == 0 ? 1UL : 2UL;
                    application = Math.Max(application, checked(amount + inner * plan.MinimumFee));
                }
                else regular = Math.Max(regular, amount);
            }
        }
        plan = plan with { RegularFee = regular, ApplicationFee = application, StorageFee = storage };
        return plan with { Steps = plan.Steps.Select(step => step with { Reservation = step.Reservation with {
            NetworkFee = BuildWttAlgoSourceStep(intent, plan, step, round, "quote:" + plan.IntentHash + ":" + step.Stage)
                .Aggregate(0UL, (sum, transaction) => checked(sum + transaction.Fee!.Value)).ToString(CultureInfo.InvariantCulture)
        } }).ToArray() };
    }
}
