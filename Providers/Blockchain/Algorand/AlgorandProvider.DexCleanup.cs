using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IDexTransactionModule.PlanSwapCleanupAsync"/>
    public async Task<AZOAResult<SwapStagePlan>> PlanSwapCleanupAsync(SwapIntent intent,
        IReadOnlyList<SwapStageHistory> history, SwapCleanupConsent consent, string stage, int generation,
        CancellationToken ct = default)
    {
        if (stage != "CloseOptIn" || generation < 0 || !SwapCleanupContract.IsValid(intent, consent) ||
            !UInt64Text.TryParse(intent.Terms.OutputAsset.Id, out var assetId) || assetId == 0 || history.Count == 0 ||
            history.Any(item => item.Plan.Purpose is not ("Effect" or "Cleanup")))
            return AZOAResult<SwapStagePlan>.Failure("Explicit current-zero-holding cleanup consent and original opt-in history are required.");
        var original = history.Where(item => item.Plan.Purpose == "Effect").ToArray();
        var lineage = await GetNextSwapStageAsync(intent, original, ct);
        if (lineage.IsError) return AZOAResult<SwapStagePlan>.Failure(lineage.Message);
        var allocations = original.Where(item => item.Plan.Stage == "OptIn" && item.Observation.Verdict == "Confirmed").ToArray();
        if (allocations.Length != 1 || allocations[0].Prepared is not { } preparedAllocation)
            return AZOAResult<SwapStagePlan>.Failure("Cleanup requires one confirmed opt-in attributable to this operation.");
        foreach (var prior in history.Where(item => item.Plan.Purpose == "Cleanup"))
        {
            if (!ValidTinymanHistory(intent, prior) ||
                prior.Observation.Verdict is not ("FailedOnChain" or "ExpiredAbsent") ||
                prior.Plan.CleanupConsent != consent || prior.Plan.Generation >= generation)
                return AZOAResult<SwapStagePlan>.Failure("An earlier cleanup is unresolved, already confirmed or differently authorized.");
        }
        var allocation = await ResolveSwapStageAccountingAsync(intent, preparedAllocation, ct);
        if (allocation.IsError || allocation.Result?.ActualLockedBalance != "100000")
            return AZOAResult<SwapStagePlan>.Failure("Authoritative original opt-in rent attribution is not available for cleanup.");
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new JsonException("Algod cleanup parameters are missing.");
        if (parameters.LastRound <= 0 || parameters.MinFee <= 0 || parameters.Fee < 0 ||
            intent.Terms.Network.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return AZOAResult<SwapStagePlan>.Failure("Cleanup round, network or fee evidence is invalid.");
        var owner = intent.Terms.Owner.Address;
        var account = await ReadTinymanJsonAsync("/v2/accounts/" + Uri.EscapeDataString(owner), ct);
        var asset = await ReadTinymanJsonAsync($"/v2/assets/{assetId}", ct);
        if (account is null || !ZeroCleanupHolding(account.Value, owner, assetId) || asset is null ||
            !asset.Value.TryGetProperty("params", out var assetParameters) ||
            !assetParameters.TryGetProperty("creator", out var creator) || creator.ValueKind != JsonValueKind.String ||
            !ValidateAddressFormat(creator.GetString()!) || creator.GetString() == owner ||
            !TinymanHolding(account.Value, owner, 0, out var available, out _))
            return AZOAResult<SwapStagePlan>.Failure("Cleanup requires the non-creator owner's current zero asset holding and spendable fee balance.");
        var intentHash = CanonicalJson.Hash(intent);
        var key = SwapOperationIdentity.CleanupPreparation(intent.OperationId, stage, generation, intentHash, consent);
        var wire = ReadTinymanPlan(intent, allocations[0].Plan) with
        {
            Stage = stage, PreparationKey = key, FirstValid = (ulong)parameters.LastRound,
            LastValid = checked((ulong)parameters.LastRound + 1000), GenesisHash = parameters.GenesisHashB64!,
            GenesisId = parameters.GenesisId ?? "", MinimumFee = (ulong)parameters.MinFee,
            RegularFee = (ulong)parameters.MinFee, ApplicationFee = checked((ulong)parameters.MinFee * 2),
        };
        var unsigned = TinymanSwapWire.Build(wire);
        var byteFee = checked((ulong)parameters.Fee * ((ulong)Encoder.EncodeToMsgPackOrdered(unsigned[0]).Length + 88));
        wire = wire with { RegularFee = Math.Max(wire.RegularFee, byteFee) };
        if (!UInt64Text.TryParse(consent.MaximumNetworkFee, out var cap) || wire.RegularFee > cap || available < wire.RegularFee)
            return AZOAResult<SwapStagePlan>.Failure("Cleanup network fee exceeds its explicit cap or current spendable balance.");
        return AZOAResult<SwapStagePlan>.Success(new(stage, generation, key, intentHash, [owner],
            TinymanUnsignedPayload(TinymanSwapWire.Build(wire)), CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire)),
            wire.RegularFee.ToString(CultureInfo.InvariantCulture), "0", "Cleanup", consent));
    }

    private static bool ZeroCleanupHolding(JsonElement account, string owner, ulong asset)
    {
        if (!TinymanSwapReceipt.Text(account, "address", owner) ||
            (account.TryGetProperty("auth-addr", out var auth) && (auth.ValueKind != JsonValueKind.String || auth.GetString() != owner)) ||
            !account.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return false;
        var seen = new HashSet<ulong>();
        var found = false;
        foreach (var holding in assets.EnumerateArray())
        {
            if (!TinymanSwapReceipt.Number(holding, "asset-id", out var id) || !seen.Add(id)) return false;
            if (id != asset) continue;
            if (!TinymanSwapReceipt.Number(holding, "amount", out var amount) || amount != 0) return false;
            found = true;
        }
        return found;
    }
}
