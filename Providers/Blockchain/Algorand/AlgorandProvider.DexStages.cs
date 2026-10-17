using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IDexTransactionModule.DexCapability"/>
    public DexCapability DexCapability { get; } = new("Tinyman", "2", ExactInput: true, ExactOutput: false,
        NativeInput: true, NativeOutput: true, SelfRecipientOnly: true,
        ["One pinned Tinyman V2 validator pool proven from the pool logic signature; ASA/ASA or ASA/ALGO fixed-input swaps only.",
         "Self-recipient: the validator pays the app-call sender, so a different recipient is not offered.",
         "A missing output opt-in is a separate signed prerequisite whose minimum balance stays disclosed until separately consented cleanup.",
         "Rekeyed or delegated owner accounts are outside the single-key grammar."],
        CleanupSemantics: [SwapCleanupContract.CloseCurrentZeroHolding]);

    /// <inheritdoc cref="IDexTransactionModule.DescribeDexNetworkAsync"/>
    public async Task<AZOAResult<DexNetwork>> DescribeDexNetworkAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct);
            return parameters is { LastRound: > 0, GenesisHashB64.Length: > 0 }
                ? AZOAResult<DexNetwork>.Success(new(ChainType, ActiveNetwork.ToString(), "algorand:" + parameters.GenesisHashB64))
                : AZOAResult<DexNetwork>.FailureWithCode("Algorand genesis identity is unavailable.", AzoaErrorCodes.DependencyUnavailable);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        { return AZOAResult<DexNetwork>.FailureWithCode("Algorand genesis identity is unavailable.", AzoaErrorCodes.DependencyUnavailable); }
    }

    /// <inheritdoc cref="IDexTransactionModule.GetNextSwapStageAsync"/>
    public async Task<AZOAResult<SwapNextStage>> GetNextSwapStageAsync(SwapIntent intent,
        IReadOnlyList<SwapStageHistory> history, CancellationToken ct = default)
    {
        var retained = new List<DexEffectDisclosure>();
        var preparations = new HashSet<string>(StringComparer.Ordinal);
        SwapStageHistory? last = null;
        var confirmedSwap = false;
        var confirmedOptIn = false;
        foreach (var item in history)
        {
            ct.ThrowIfCancellationRequested();
            var plan = item.Plan;
            if (!ValidTinymanHistory(intent, item) ||
                !preparations.Add(plan.PreparationKey) ||
                !SwapObservationContract.IsTerminal(item.Observation.Verdict) || confirmedSwap ||
                (last is not null && plan.Stage == last.Plan.Stage &&
                    (last.Observation.Verdict == "Confirmed" || plan.Generation != last.Plan.Generation + 1)) ||
                (last is not null && plan.Stage != last.Plan.Stage &&
                    (last.Plan.Stage != "OptIn" || last.Observation.Verdict != "Confirmed" || plan.Stage != "Swap" || plan.Generation != 0)) ||
                (last is null && plan.Generation != 0))
                return AZOAResult<SwapNextStage>.Failure("Tinyman stage history is not an exact terminal lineage.");
            if (item.Observation.Verdict == "Confirmed")
            {
                if (plan.Stage == "OptIn")
                {
                    if (confirmedOptIn || item.Observation.ActualInput != "0" || item.Observation.ActualOutput != "0" ||
                        item.Observation.PlatformFeePaid != "0")
                        return AZOAResult<SwapNextStage>.Failure("Opt-in history contains contradictory economic evidence.");
                    confirmedOptIn = true;
                    retained.Add(new("OptIn", intent.Terms.OutputAsset.Id, "0", intent.Terms.Owner.Address,
                        intent.Terms.Owner.Address, true));
                }
                else if (plan.Stage == "Swap" && item.Observation.ActualInput == intent.Terms.GrossInput &&
                    item.Observation.PlatformFeePaid == intent.Terms.PlatformFee.Amount &&
                    UnsignedIntegerText.TryParse(item.Observation.ActualOutput, out var output) &&
                    UnsignedIntegerText.TryParse(intent.Terms.MinimumOutput, out var minimum) && output >= minimum)
                    confirmedSwap = true;
                else return AZOAResult<SwapNextStage>.Failure("Swap history does not prove the consented economic settlement.");
            }
            last = item;
        }
        if (confirmedSwap) return AZOAResult<SwapNextStage>.Success(new(null, true, retained));
        if (last is not null)
            return AZOAResult<SwapNextStage>.Success(new(last.Observation.Verdict == "Confirmed" ? "Swap" : last.Plan.Stage,
                false, retained));
        var fresh = await QuoteSwapAsync(intent.Terms, ct);
        if (fresh.IsError || fresh.Result is null)
            return AZOAResult<SwapNextStage>.FailureWithCode(fresh.Message, fresh.Code ?? "DependencyUnavailable");
        return AZOAResult<SwapNextStage>.Success(new(fresh.Result.Prerequisites.Any(effect => effect.Kind == "OptIn") ? "OptIn" : "Swap",
            false, retained));
    }
    private bool ValidTinymanHistory(SwapIntent intent, SwapStageHistory item)
    {
        if (item.Prepared is not { } prepared)
            return item.ExposedPlan is { } exposed && ValidateExposedSwapNoEffect(intent, exposed, item.Observation);
        if (item.ExposedPlan is not null) return false;
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        return !validated.IsError && validated.Result is { } candidate &&
            candidate.TransactionIds.SequenceEqual(prepared.TransactionIds) &&
            SwapObservationContract.IsValid(intent, prepared, item.Observation);
    }
}
