using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Operator settlement of a same-call nonce winner; see Providers/Blockchain/Ethereum/AGENTS.md §Operator settlement.</summary>
public partial class EthereumProvider : IDexOperatorSettlementModule
{
    private sealed record DexSameCallReview(string TransactionId, string BlockHash, string BlockNumber, JsonElement Evidence);

    /// <inheritdoc/>
    public async Task<AZOAResult<SwapStageObservation>> ProveOperatorSettlementAsync(SwapIntent intent, SwapStagePlan plan,
        SwapStageObservation review, SwapOperatorSettlement audit, CancellationToken ct = default)
    {
        try
        {
            var saved = ReadEthereumDexPlan(intent, plan);
            if (review.Verdict != "NeedsOperator" || ReadSameCallReview(review.EvidenceJson, plan, saved) is not { } winner)
                return Error<SwapStageObservation>("Only a recorded same-call nonce winner can be settled by an operator.");
            var found = await ReadDexFinalizedCoreAsync(intent, plan, winner.TransactionId, sameCallEnvelope: true, ct);
            if (found is not { } finalized || !SameDexHash(finalized.Receipt.BlockHash, winner.BlockHash) ||
                Text(finalized.Receipt.BlockNumber) != winner.BlockNumber)
                return AZOAResult<SwapStageObservation>.FailureWithCode(
                    "The same-call winner's finalized receipt is not proven at its recorded block.", AzoaErrorCodes.DependencyUnavailable);
            var settled = await DexReceiptObservationAsync(intent, plan, [winner.TransactionId], finalized.Receipt,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["SameCallWinner"] = winner.Evidence, [SwapOperatorSettlementContract.AuditMember] = audit,
                }, ct);
            return settled.Verdict is "Confirmed" or "FailedOnChain" && ValidateOperatorSettlement(intent, plan, settled) ? Ok(settled)
                : Error<SwapStageObservation>(settled.Detail ?? "The same-call receipt does not prove an exact stage outcome.");
        }
        catch (HttpRequestException)
        {
            return AZOAResult<SwapStageObservation>.FailureWithCode("Ethereum finality evidence is unavailable.", AzoaErrorCodes.DependencyUnavailable);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            return Error<SwapStageObservation>("Ethereum evidence differs from the reviewed same-call winner, receipt or code grammar.");
        }
    }

    /// <inheritdoc/>
    public bool ValidateOperatorSettlement(SwapIntent intent, SwapStagePlan plan, SwapStageObservation settled)
    {
        if (!SwapOperatorSettlementContract.IsValid(intent, plan, settled)) return false;
        try
        {
            var saved = ReadEthereumDexPlan(intent, plan);
            using var document = JsonDocument.Parse(settled.EvidenceJson!);
            var root = document.RootElement;
            var receipt = root.GetProperty("Receipt");
            var id = settled.TransactionIds[0];
            if (ReadSameCallReview(root.GetProperty("SameCallWinner").GetRawText(), plan, saved) is not { } winner ||
                winner.TransactionId != id || receipt.GetProperty("Format").GetString() != DexReceiptFormat ||
                receipt.GetProperty("Stage").GetString() != plan.Stage || receipt.GetProperty("Generation").GetInt32() != plan.Generation ||
                receipt.GetProperty("PlanHash").GetString() != CanonicalJson.Hash(plan) || receipt.GetProperty("TransactionId").GetString() != id ||
                !SameDexHash(receipt.GetProperty("BlockHash").GetString() ?? "", winner.BlockHash) ||
                receipt.GetProperty("BlockNumber").GetString() != winner.BlockNumber ||
                receipt.GetProperty("NetworkFee").GetString() != settled.NetworkFeePaid ||
                receipt.GetProperty("Succeeded").GetBoolean() != (settled.Verdict == "Confirmed") || settled.LockedBalance != "0") return false;
            if (settled.Verdict != "Confirmed") return true;
            if (plan.Stage != "Swap") return settled.ActualInput == "0" && settled.ActualOutput == "0" && settled.PlatformFeePaid == "0";
            var settlement = root.GetProperty("Settlement");
            return settlement.GetProperty("GrossInput").GetString() == settled.ActualInput &&
                settlement.GetProperty("PlatformFee").GetString() == settled.PlatformFeePaid &&
                settlement.GetProperty("Output").GetString() == settled.ActualOutput;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { return false; }
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseSettledResourcesAsync(SwapIntent intent, string stageId, SwapStagePlan plan,
        SwapStageObservation settled, CancellationToken ct = default)
        => ValidateOperatorSettlement(intent, plan, settled) && await ReleaseAllowanceAsync(intent, stageId, plan.Stage, settled, ct);

    /// <summary>Reads the saved same-call evidence bound to exactly this plan, nonce and sender.</summary>
    private static DexSameCallReview? ReadSameCallReview(string? evidenceJson, SwapStagePlan plan, DexEvmPlan saved)
    {
        if (string.IsNullOrEmpty(evidenceJson)) return null;
        using var document = JsonDocument.Parse(evidenceJson);
        var review = document.RootElement;
        if (review.ValueKind != JsonValueKind.Object ||
            !review.TryGetProperty("Format", out var format) || format.GetString() != DexSameCallWinnerFormat ||
            review.GetProperty("PlanHash").GetString() != CanonicalJson.Hash(plan) ||
            review.GetProperty("ProtocolIdentity").GetString() != saved.ProtocolIdentity ||
            review.GetProperty("Sender").GetString() != saved.Call.Sender || review.GetProperty("Nonce").GetString() != saved.Nonce ||
            review.GetProperty("WinnerTransactionId").GetString() is not { } transaction || !IsDexHash(transaction) ||
            transaction != transaction.ToLowerInvariant() ||
            review.GetProperty("WinnerBlockHash").GetString() is not { } block || !IsDexHash(block) ||
            review.GetProperty("WinnerBlockNumber").GetString() is not { } number || !UnsignedIntegerText.TryParse(number, out _)) return null;
        return new(transaction, block, number, review.Clone());
    }
}
