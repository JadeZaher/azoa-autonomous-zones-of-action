using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IDexTransactionModule.DexCapability"/>
    public DexCapability DexCapability { get; } = new("Jupiter", "6-whirlpool/1", ExactInput: true, ExactOutput: false,
        NativeInput: true, NativeOutput: true, SelfRecipientOnly: true,
        ["Jupiter Build API single 100% Orca Whirlpool hop only; no split, multihop, RFQ, tip or Jupiter platform fee.",
         "Standard SPL token program mints only; Token-2022, transfer hooks and transfer fees are refused.",
         "Native SOL uses a dedicated operation-owned wrapped account; an existing wrapped SOL account is never closed.",
         "The recipient is the owner; a created output account remains disclosed rent until separately consented cleanup."],
        CleanupSemantics: [SwapCleanupContract.CloseCurrentZeroHolding]);

    /// <inheritdoc cref="IDexTransactionModule.DescribeDexNetworkAsync"/>
    public async Task<AZOAResult<DexNetwork>> DescribeDexNetworkAsync(CancellationToken ct = default)
    {
        try { return Ok(new DexNetwork(ChainType, ActiveNetwork.ToString(), "solana:" + await VerifyGenesisAsync(ct))); }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        { return AZOAResult<DexNetwork>.FailureWithCode("Solana genesis identity is unavailable or differs from the bound network.", AzoaErrorCodes.DependencyUnavailable); }
    }

    /// <inheritdoc cref="IDexTransactionModule.GetNextSwapStageAsync"/>
    public Task<AZOAResult<SwapNextStage>> GetNextSwapStageAsync(SwapIntent intent,
        IReadOnlyList<SwapStageHistory> history, CancellationToken ct = default)
    {
        try
        {
            var generation = 0;
            JupiterSettlement? completed = null;
            foreach (var item in history)
            {
                ct.ThrowIfCancellationRequested();
                if (completed is not null || item.Plan.Generation != generation || item.Plan.Stage != "Swap" ||
                    item.Plan.Purpose != "Effect" || !ValidJupiterHistory(intent, item))
                    return Task.FromResult(AZOAResult<SwapNextStage>.Failure("Jupiter history is not an exact terminal preparation lineage."));
                generation = checked(generation + 1);
                if (item.Observation.Verdict == "Confirmed")
                {
                    using var evidence = JsonDocument.Parse(item.Observation.EvidenceJson!);
                    if (evidence.RootElement.GetProperty("format").GetString() != "jupiter-settlement/1" ||
                        evidence.RootElement.GetProperty("PlanHash").GetString() != CanonicalJson.Hash(item.Plan))
                        throw new InvalidDataException("Jupiter settlement belongs to another immutable preparation.");
                    completed = evidence.RootElement.GetProperty("Settlement").Deserialize<JupiterSettlement>()
                        ?? throw new InvalidDataException("Jupiter settlement receipt is missing.");
                    if (completed.Input.ToString(CultureInfo.InvariantCulture) != intent.Terms.GrossInput ||
                        completed.PlatformFee.ToString(CultureInfo.InvariantCulture) != intent.Terms.PlatformFee.Amount ||
                        completed.Output < UInt64Text.Parse(intent.Terms.MinimumOutput) ||
                        completed.Input.ToString(CultureInfo.InvariantCulture) != item.Observation.ActualInput ||
                        completed.Output.ToString(CultureInfo.InvariantCulture) != item.Observation.ActualOutput ||
                        completed.PlatformFee.ToString(CultureInfo.InvariantCulture) != item.Observation.PlatformFeePaid ||
                        completed.RetainedRent.ToString(CultureInfo.InvariantCulture) != item.Observation.LockedBalance ||
                        completed.RetainedEffects is null || completed.RetainedEffects.Select(effect => effect.DestinationAddress)
                            .Distinct(StringComparer.Ordinal).Count() != completed.RetainedEffects.Count)
                        throw new InvalidDataException("Jupiter settlement contradicts its exact immutable economic terms.");
                    ulong retained = 0;
                    foreach (var effect in completed.RetainedEffects)
                    {
                        if (effect.Kind != "CreateAta" || !effect.RetainedOnSwapFailure || effect.OwnerAddress != intent.Terms.Owner.Address ||
                            !UInt64Text.TryParse(effect.Amount, out var amount) || !intent.Terms.Prerequisites.Any(original =>
                                original.Kind == effect.Kind && original.AssetId == effect.AssetId && original.OwnerAddress == effect.OwnerAddress &&
                                original.DestinationAddress == effect.DestinationAddress && UInt64Text.TryParse(original.Amount, out var reserved) && amount <= reserved))
                            throw new InvalidDataException("Jupiter retained effects were not part of the original consent.");
                        retained = checked(retained + amount);
                    }
                    if (retained != completed.RetainedRent) throw new InvalidDataException("Jupiter retained rent attribution is inconsistent.");
                }
            }
            return Task.FromResult(AZOAResult<SwapNextStage>.Success(completed is null
                ? new("Swap", false, []) : new(null, true, completed.RetainedEffects)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or
            OverflowException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return Task.FromResult(AZOAResult<SwapNextStage>.Failure("Jupiter stage history lacks exact terminal evidence.")); }
    }

    private bool ValidJupiterHistory(SwapIntent intent, SwapStageHistory item)
    {
        if (!SwapObservationContract.IsTerminal(item.Observation.Verdict)) return false;
        if (item.Prepared is not { } prepared)
            return item.ExposedPlan is { } exposed && ValidateExposedSwapNoEffect(intent, exposed, item.Observation);
        if (item.ExposedPlan is not null) return false;
        var valid = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (valid.IsError || valid.Result is not { } candidate || !candidate.TransactionIds.SequenceEqual(prepared.TransactionIds) ||
            candidate.SignedPayloadJson != prepared.SignedPayloadJson || !SwapObservationContract.IsValid(intent, prepared, item.Observation)) return false;
        if (item.Observation.Verdict == "FailedOnChain")
        {
            using var proof = JsonDocument.Parse(item.Observation.EvidenceJson!);
            return proof.RootElement.GetProperty("format").GetString() == "jupiter-revert/1" &&
                proof.RootElement.GetProperty("PlanHash").GetString() == CanonicalJson.Hash(prepared.Plan) &&
                proof.RootElement.GetProperty("Error").ValueKind != JsonValueKind.Null;
        }
        return item.Observation.Verdict != "ExpiredAbsent" ||
            ValidateExposedSwapNoEffect(intent, prepared.Plan, item.Observation with { TransactionIds = [] });
    }
}
