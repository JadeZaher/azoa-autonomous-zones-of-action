using System.Globalization;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IWttTargetModule.ObserveWttTargetAsync"/>
    public async Task<AZOAResult<WttStageObservation>> ObserveWttTargetAsync(WttOperationIntent intent,
        WttTargetProofs proofs, WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct = default)
    {
        try
        {
            var (plan, wire) = await ReadWttAlgoSavedTargetAsync(intent, proofs, quote, prepared, ct);
            var step = plan.Steps.Single(item => item.Stage == prepared.Stage);
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
                intent.OperationId, "Target", prepared.Stage, prepared.Generation, quote.IntentHash, quote.QuoteId);
            var id = WttPreparationIdentity.Id(binding);
            var expected = BuildWttAlgoTargetStep(intent, plan, step, wire.FirstValid, id, wire.Asset);
            var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
            var head = WttAlgoUnsigned(parameters, "last-round");
            if (head == 0 || WttAlgoText(parameters, "genesis-hash") != plan.GenesisHash
                || WttAlgoText(parameters, "genesis-id") != plan.GenesisId)
                return Error<WttStageObservation>("Target observation endpoint differs from its saved physical network.");
            var pending = new WttStageObservation("Pending", prepared.TransactionId, prepared.ProtocolIdentity,
                Detail: "Exact target group, protocol execution and recipient effects remain under reconciliation.");
            var indexed = await ReadWttAlgoIndexedTransactionAsync(prepared.TransactionId, ct);
            if (indexed is null) return Ok(pending);
            if (indexed.Value.Transaction is not { } first)
                return await ObserveWttAlgoAbsenceAsync(intent, quote, prepared, plan.GenesisHash, wire.FirstValid,
                    expected, head, indexed.Value.Round, pending, ct);
            var round = WttAlgoUnsigned(first, "confirmed-round");
            if (round < wire.FirstValid || round > checked(wire.FirstValid + 1000) || indexed.Value.Round < round || head < round)
                return Ok(pending);
            var block = await ReadWttAlgoIndexerAsync("/v2/blocks/" + round.ToString(CultureInfo.InvariantCulture), ct);
            var receipt = block is null ? null : AlgorandGroupReceipt.Read(expected, block.Value);
            if (receipt is null || receipt.Round != round) return Ok(pending);
            var identity = await ReadWttAlgoFinalizedBlockIdentityAsync(plan.GenesisHash, round, receipt.Timestamp, ct);
            if (identity is null) return Ok(pending);
            var programs = true;
            for (var index = 0; index < expected.Count; index++)
            {
                if (expected[index] is not ApplicationCallTransaction app) continue;
                if (AlgorandApplicationCall.Id(app) is not { } application) return Error<WttStageObservation>("Unknown application in the target group.");
                var core = application == plan.CoreApplication;
                if (!core && application != plan.TokenApplication) return Error<WttStageObservation>("Unknown application in the target group.");
                programs &= await ProveWttAlgoProgramAsync(new(1, id, plan.GenesisHash, application, expected[index].TxID(), "",
                    round, WttAlgoUnsigned(receipt.Transactions[index], "intra-round-offset"),
                    core ? plan.CoreApprovalHash : plan.TokenApprovalHash, core ? plan.CoreClearHash : plan.TokenClearHash), ct);
            }
            if (!programs) return Ok(pending);
            var asset = WttAlgorandTargetEffects.Read(intent, plan, step.Stage, wire.Asset, receipt.Transactions);
            var repeated = await ReadWttAlgoIndexerAsync("/v2/blocks/" + round.ToString(CultureInfo.InvariantCulture), ct);
            if (repeated is null || CanonicalJson.Hash(repeated.Value) != receipt.BlockDigest) return Ok(pending);
            var proof = step.Purpose == "Attestation" ? plan.Attestation! : plan.Transfer;
            var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                format = "wormhole-algorand-target/1", preparationId = id, quote.IntentHash, quote.QuoteId,
                round = round.ToString(CultureInfo.InvariantCulture), blockIdentity = identity, receipt.BlockDigest,
                transactionIds = expected.Select(transaction => transaction.TxID()).ToArray(),
                bodyDigest = proof.Vaa.BodyDigest, proof.Vaa.EmitterChain, proof.Vaa.EmitterAddress, proof.Vaa.Sequence,
                asset = asset.ToString(CultureInfo.InvariantCulture), intent.RecipientAddress,
                recipientAmount = step.Stage == "Redeem" ? WttPayloadCodec.Denormalize(intent.Amount.RecipientAmount, plan.Decimals) : "0",
                coreApproval = plan.CoreApprovalHash, coreClear = plan.CoreClearHash,
                tokenApproval = plan.TokenApprovalHash, tokenClear = plan.TokenClearHash,
            }));
            return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                evidence, NetworkFeePaid: receipt.NetworkFee.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Error<WttStageObservation>(exception.Message); }
    }
}
