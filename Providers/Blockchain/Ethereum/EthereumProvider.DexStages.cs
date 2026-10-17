using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IDexRetainedResourceModule
{
    /// <inheritdoc cref="IDexTransactionModule.DexCapability"/>
    public DexCapability DexCapability { get; } = new("UniswapV3", "classic-exact-input/1", ExactInput: true, ExactOutput: false,
        NativeInput: true, NativeOutput: true, SelfRecipientOnly: true,
        ["One factory-proven classic Uniswap V3 pool at the configured fee tier, settled atomically by the pinned AzoaDexExecutor.",
         "Only reviewed immutable ERC20 token policies; rebasing, transfer-tax and callback tokens are excluded.",
         "ERC20 input requires a finalized exact-gross allowance; a different nonzero allowance is reset to zero first; unlimited approval is never requested.",
         "The recipient is the owner; native ETH and WETH are distinct asset choices."],
        CleanupSemantics: [SwapCleanupContract.RevokeCurrentAllowance]);

    /// <inheritdoc cref="IDexTransactionModule.DescribeDexNetworkAsync"/>
    public async Task<AZOAResult<DexNetwork>> DescribeDexNetworkAsync(CancellationToken ct = default)
    {
        var chain = await GetChainIdAsync(ct);
        return chain.IsError
            ? AZOAResult<DexNetwork>.FailureWithCode("Ethereum chain identity is unavailable or differs from the configured network.",
                AzoaErrorCodes.DependencyUnavailable)
            : Ok(new DexNetwork(ChainType, ActiveNetwork.ToString(), "eip155:" + Text(chain.Result)));
    }

    /// <inheritdoc cref="IDexTransactionModule.GetNextSwapStageAsync"/>
    public async Task<AZOAResult<SwapNextStage>> GetNextSwapStageAsync(SwapIntent intent,
        IReadOnlyList<SwapStageHistory> history, CancellationToken ct = default)
    {
        DexEvmQuoteEvidence quote;
        try { quote = ReadDexAcceptedIntent(intent); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return Error<SwapNextStage>("The original accepted Ethereum quote is required."); }
        var order = intent.Terms.Prerequisites.Select(effect => effect.Kind).Where(kind => kind is "ResetAllowance" or "ApproveExact")
            .Append("Swap").ToArray();
        var retained = new List<DexEffectDisclosure>();
        var preparations = new HashSet<string>(StringComparer.Ordinal);
        SwapStageHistory? last = null;
        var confirmedSwap = false;
        foreach (var item in history)
        {
            ct.ThrowIfCancellationRequested();
            var plan = item.Plan;
            var position = Array.IndexOf(order, plan.Stage);
            if (plan.Purpose != "Effect" || position < 0 || !ValidEthereumHistory(intent, item) || !preparations.Add(plan.PreparationKey) ||
                !SwapObservationContract.IsTerminal(item.Observation.Verdict) || confirmedSwap ||
                (last is null && plan.Generation != 0) ||
                (last is not null && plan.Stage == last.Plan.Stage &&
                    (last.Observation.Verdict == "Confirmed" || plan.Generation != last.Plan.Generation + 1)) ||
                (last is not null && plan.Stage != last.Plan.Stage &&
                    (last.Observation.Verdict != "Confirmed" || position != Array.IndexOf(order, last.Plan.Stage) + 1 || plan.Generation != 0)))
                return Error<SwapNextStage>("Ethereum stage history is not an exact terminal lineage.");
            if (item.Observation.Verdict == "Confirmed")
            {
                if (plan.Stage != "Swap")
                {
                    if (item.Observation.ActualInput != "0" || item.Observation.ActualOutput != "0" || item.Observation.PlatformFeePaid != "0")
                        return Error<SwapNextStage>("Allowance history contains contradictory economic evidence.");
                    retained.RemoveAll(effect => effect.Kind == "ApproveExact");
                    if (plan.Stage == "ApproveExact")
                        retained.Add(new("ApproveExact", quote.Input.Address, intent.Terms.GrossInput, intent.Terms.Owner.Address,
                            quote.Deployment.Executor, true));
                }
                else if (item.Observation.ActualInput == intent.Terms.GrossInput && item.Observation.PlatformFeePaid == intent.Terms.PlatformFee.Amount &&
                    UnsignedIntegerText.TryParse(item.Observation.ActualOutput, out var output) &&
                    UnsignedIntegerText.TryParse(intent.Terms.MinimumOutput, out var minimum) && output >= minimum)
                {
                    confirmedSwap = true;
                    retained.RemoveAll(effect => effect.Kind == "ApproveExact");
                    if (!intent.Terms.InputAsset.Native && RemainingAllowance(item.Observation) is { } remaining and not "0")
                        retained.Add(new("RetainedAllowance", quote.Input.Address, remaining, intent.Terms.Owner.Address, quote.Deployment.Executor, true));
                }
                else return Error<SwapNextStage>("Swap history does not prove the consented economic settlement.");
            }
            last = item;
        }
        if (confirmedSwap) return Ok(new SwapNextStage(null, true, retained));
        if (last is not null)
            return Ok(new SwapNextStage(last.Observation.Verdict == "Confirmed" ? order[Array.IndexOf(order, last.Plan.Stage) + 1] : last.Plan.Stage,
                false, retained));
        return await FirstDexStageAsync(intent, quote, order, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseRetainedResourcesAsync(SwapIntent intent, string stageId, PreparedSwapStage prepared,
        SwapStageObservation observation, CancellationToken ct = default)
        => await ReleaseAllowanceAsync(intent, stageId, prepared.Plan.Stage, observation, ct);

    /// <summary>Releases the allowance lane against a confirmed Swap/RevokeAllowance stage that carries zero-allowance evidence.</summary>
    private async Task<bool> ReleaseAllowanceAsync(SwapIntent intent, string stageId, string stage, SwapStageObservation observation,
        CancellationToken ct)
    {
        if (intent.Terms.InputAsset.Native || observation.Verdict != "Confirmed" || stage is not ("Swap" or "RevokeAllowance") ||
            observation.EvidenceJson is null) return false;
        using (var evidence = JsonDocument.Parse(observation.EvidenceJson))
            if (!evidence.RootElement.TryGetProperty("AllowanceRelease", out _)) return false;
        var resource = DexAllowance(intent, ReadDexAcceptedIntent(intent));
        return await WithDexAllowanceStoreAsync(store => store.ReleaseAsync(resource.Id, intent.OperationId, stageId, ct));
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseUnsentStageAsync(SwapIntent intent, SwapStagePlan plan, CancellationToken ct = default)
    {
        DexEvmPlan saved;
        try { saved = ReadEthereumDexPlan(intent, plan); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return false; }
        return await ReleaseUnsignedReservationAsync(saved.ReservationId, ct);
    }

    private async Task<AZOAResult<SwapNextStage>> FirstDexStageAsync(SwapIntent intent, DexEvmQuoteEvidence quote, string[] order,
        CancellationToken ct)
    {
        if (intent.Terms.InputAsset.Native) return Ok(new SwapNextStage("Swap", false, []));
        try
        {
            var snapshot = await ReadDexSnapshotAsync(quote.Deployment, ct);
            if (snapshot.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity)
                return Error<SwapNextStage>("The configured node is not the original physical network.");
            var allowance = await ReadUInt256AtBlockAsync(quote.Input.Address, "allowance(address,address)",
                [intent.Terms.Owner.Address, quote.Deployment.Executor], snapshot.Block, ct);
            var gross = EthereumDexExecutionCodec.Amount(intent.Terms.GrossInput);
            var stage = allowance == gross ? "Swap" : allowance == 0 ? "ApproveExact" : "ResetAllowance";
            return order.Contains(stage) ? Ok(new SwapNextStage(stage, false, []))
                : Error<SwapNextStage>("The finalized allowance changed after the quote and needs a stage the owner did not consent to; request a new quote.");
        }
        catch (HttpRequestException) { return AZOAResult<SwapNextStage>.FailureWithCode("Ethereum allowance state is unavailable.", AzoaErrorCodes.DependencyUnavailable); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException)
        { return Error<SwapNextStage>("Ethereum deployment or allowance evidence differs from the accepted quote."); }
    }

    private bool ValidEthereumHistory(SwapIntent intent, SwapStageHistory item)
    {
        // An operator-settled stage proves its outcome through the recorded same-call winner, not the saved candidate.
        if (SwapOperatorSettlementContract.IsOperatorSettlement(item.Observation))
            return (item.Prepared is null ? item.ExposedPlan is not null : item.ExposedPlan is null) &&
                ValidateOperatorSettlement(intent, item.Plan, item.Observation);
        if (item.Prepared is not { } prepared)
            return item.ExposedPlan is { } exposed && ValidateExposedSwapNoEffect(intent, exposed, item.Observation);
        if (item.ExposedPlan is not null) return false;
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (validated.IsError || validated.Result is not { } candidate || !candidate.TransactionIds.SequenceEqual(prepared.TransactionIds) ||
            !SwapObservationContract.IsValid(intent, prepared, item.Observation)) return false;
        if (item.Observation.FinalizedCandidate is { } winner)
        {
            var actual = ValidateSignedSwapStage(intent, prepared.Plan, winner.Candidate.SignedPayloadJson);
            if (actual.IsError || actual.Result is not { } alternate || alternate.SignedPayloadJson != winner.Candidate.SignedPayloadJson ||
                !alternate.TransactionIds.SequenceEqual(winner.Candidate.TransactionIds)) return false;
        }
        return item.Observation.Verdict != "ExpiredAbsent" || ValidDexNoEffect(intent, prepared.Plan, item.Observation);
    }

    private static string? RemainingAllowance(SwapStageObservation observation)
    {
        if (observation.EvidenceJson is null) return null;
        using var evidence = JsonDocument.Parse(observation.EvidenceJson);
        return evidence.RootElement.TryGetProperty("AllowanceAfter", out var after) && after.ValueKind == JsonValueKind.String &&
            UnsignedIntegerText.TryParse(after.GetString(), out _) ? after.GetString() : null;
    }
}
