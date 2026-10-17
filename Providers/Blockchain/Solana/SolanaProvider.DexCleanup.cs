using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record JupiterCleanupPlan(int Version, SwapStagePlan Allocation, JupiterRpcSnapshot Snapshot,
        DurableSolanaPlan Recovery, string Account, string NetworkFee);

    /// <inheritdoc cref="IDexTransactionModule.PlanSwapCleanupAsync"/>
    public async Task<AZOAResult<SwapStagePlan>> PlanSwapCleanupAsync(SwapIntent intent,
        IReadOnlyList<SwapStageHistory> history, SwapCleanupConsent consent, string stage, int generation,
        CancellationToken ct = default)
    {
        if (stage != "CloseAta" || generation < 0 || consent.Semantics != SwapCleanupContract.CloseCurrentZeroHolding ||
            !SwapCleanupContract.IsValid(intent, consent) ||
            history.Count == 0 || history.Any(item => item.Plan.Purpose is not ("Effect" or "Cleanup")))
            return AZOAResult<SwapStagePlan>.Failure("Cleanup requires exact owner consent and terminal original history.");
        var effects = history.Where(item => item.Plan.Purpose == "Effect").ToArray();
        var next = await GetNextSwapStageAsync(intent, effects, ct);
        if (next.IsError || next.Result is not { Complete: true })
            return AZOAResult<SwapStagePlan>.Failure("Jupiter cleanup requires the original confirmed settlement.");
        var allocation = effects.Single(item => item.Observation.Verdict == "Confirmed");
        if (allocation.Prepared is not { } original)
            return AZOAResult<SwapStagePlan>.Failure("Cleanup requires the original actual owner signature.");
        foreach (var prior in history.Where(item => item.Plan.Purpose == "Cleanup"))
            if (!ValidJupiterHistory(intent, prior) || prior.Plan.CleanupConsent != consent || prior.Plan.Generation >= generation ||
                prior.Observation.Verdict is not ("FailedOnChain" or "ExpiredAbsent"))
                return AZOAResult<SwapStagePlan>.Failure("A prior cleanup is unresolved, completed or differently authorized.");
        var owner = new PublicKey(intent.Terms.Owner.Address);
        var account = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, new PublicKey(consent.AssetId)).Key;
        if (!next.Result.RetainedEffects.Any(effect => effect.Kind == "CreateAta" && effect.DestinationAddress == account &&
            effect.AssetId == consent.AssetId && effect.OwnerAddress == owner.Key))
            return AZOAResult<SwapStagePlan>.Failure("The original settlement did not create this retained owner account.");
        var observed = await ObserveSwapStageAsync(intent, original, ct);
        if (observed.IsError || observed.Result?.Verdict != "Confirmed")
            return AZOAResult<SwapStagePlan>.Failure("Authoritative original settlement is not currently available.");
        var pins = JupiterAcceptedQuote.Read(intent.Terms);
        var guard = await ReadSolanaCostGuardAsync($"AZOA:Solana:Dex:Networks:{ActiveNetwork}:", ct);
        if (guard.Program != pins.GuardProgram || guard.CodeHash != pins.GuardHash)
            return AZOAResult<SwapStagePlan>.Failure("The immutable original cleanup guard is unavailable.");
        var hash = CanonicalJson.Hash(intent);
        var key = SwapOperationIdentity.CleanupPreparation(intent.OperationId, stage, generation, hash, consent);
        var recovery = await NewPlanAsync(consent.AssetId, 0, 0, false, hash, ct);
        var addresses = new[] { account, owner.Key, SysVars.ClockKey.Key };
        var response = await DurableRpcAsync("getMultipleAccounts", [addresses,
            new { encoding = "base64", commitment = "finalized", minContextSlot = recovery.ContextSlot }], ct);
        var snapshot = JupiterRpcSnapshot.Read(response, addresses, checked((ulong)recovery.ContextSlot), SysVars.ClockKey.Key);
        RequireJupiterCleanupHolding(snapshot, account, consent.AssetId, owner.Key);
        var instructions = JupiterCleanupWire.Build(intent, consent, stage, generation, key, account);
        var message = CompileDurable(recovery, owner, instructions);
        var fee = await NativeMessageFeeAsync(message, ct);
        if (fee > UInt64Text.Parse(consent.MaximumNetworkFee) ||
            snapshot.Accounts[owner.Key].GetProperty("lamports").GetUInt64() < fee)
            return AZOAResult<SwapStagePlan>.Failure("Cleanup exceeds its separately accepted fee cap or spendable balance.");
        if (await MeasureSolanaComputeAsync(message, checked((long)snapshot.Slot), ct) > pins.ComputeUnits)
            return AZOAResult<SwapStagePlan>.Failure("Cleanup exceeds the original compute ceiling.");
        var saved = new JupiterCleanupPlan(1, original.Plan, snapshot, recovery, account, fee.ToString(CultureInfo.InvariantCulture));
        var plan = new SwapStagePlan(stage, generation, key, hash, [owner.Key],
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(Convert.ToBase64String(message))),
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(saved)), saved.NetworkFee, "0", "Cleanup", consent);
        _ = ReadJupiterCleanupPlan(intent, plan);
        return AZOAResult<SwapStagePlan>.Success(plan);
    }

    private (JupiterSavedPlan Saved, JupiterCompilation Compiled) ReadJupiterCleanupPlan(SwapIntent intent, SwapStagePlan plan)
    {
        if (plan.Purpose != "Cleanup" || plan.CleanupConsent is not { } consent || !SwapCleanupContract.IsValid(intent, consent) ||
            plan.IntentHash != CanonicalJson.Hash(intent) || plan.ReservedLockedBalance != "0" ||
            !plan.RequiredSignerAddresses.SequenceEqual([intent.Terms.Owner.Address]))
            throw new InvalidDataException("Cleanup changed its original actor, intent or separate consent.");
        using var json = JsonDocument.Parse(plan.RecoveryMetadata);
        if (CanonicalJson.Canonicalize(json.RootElement) != plan.RecoveryMetadata)
            throw new InvalidDataException("Cleanup recovery evidence is not canonical.");
        var saved = json.RootElement.Deserialize<JupiterCleanupPlan>();
        if (saved is null || saved.Version != 1 || saved.Allocation is null || saved.Allocation.Purpose != "Effect" ||
            saved.Snapshot?.Accounts is null || saved.Recovery is null || string.IsNullOrEmpty(saved.Account) ||
            saved.NetworkFee != plan.ReservedNetworkFee || !UInt64Text.TryParse(saved.NetworkFee, out var fee) ||
            fee > UInt64Text.Parse(consent.MaximumNetworkFee) || saved.Recovery.Genesis != ExpectedGenesis ||
            saved.Recovery.IntentHash != plan.IntentHash || saved.Recovery.ContextSlot < 0 || saved.Recovery.LastValidBlockHeight <= 0 ||
            saved.Snapshot.Slot < (ulong)saved.Recovery.ContextSlot || saved.Recovery.Mint != consent.AssetId)
            throw new InvalidDataException("Cleanup recovery evidence changed its network, identity or costs.");
        var (original, _) = ReadJupiterPlan(intent, saved.Allocation);
        var rebuilt = JupiterRpcSnapshot.Read(JsonSerializer.SerializeToElement(new
        {
            context = new { slot = saved.Snapshot.Slot }, value = saved.Snapshot.Accounts.Values.ToArray()
        }), saved.Snapshot.Accounts.Keys.ToArray(), saved.Snapshot.Slot, SysVars.ClockKey.Key);
        if (rebuilt.ChainTimestamp != saved.Snapshot.ChainTimestamp)
            throw new InvalidDataException("Cleanup Clock evidence changed.");
        RequireJupiterCleanupHolding(saved.Snapshot, saved.Account, consent.AssetId, intent.Terms.Owner.Address);
        var instructions = JupiterCleanupWire.Build(intent, consent, plan.Stage, plan.Generation, plan.PreparationKey, saved.Account);
        var message = CompileDurable(saved.Recovery, new PublicKey(intent.Terms.Owner.Address), instructions);
        if (plan.UnsignedPayloadJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(Convert.ToBase64String(message))))
            throw new InvalidDataException("Cleanup message differs from its exact saved instructions.");
        var expected = instructions.Select(JupiterCompiledMessage.FromInstruction).ToArray();
        JupiterCompiledMessage.Validate(message, [], intent.Terms.Owner.Address, expected);
        return (original with { Recovery = saved.Recovery, NetworkFee = saved.NetworkFee, ReservedRent = "0", Tables = [] },
            new(message, [], expected, null));
    }

    private static void RequireJupiterCleanupHolding(JupiterRpcSnapshot snapshot, string account, string mint, string owner)
    {
        var data = snapshot.Owned(account, TokenProgram.ProgramIdKey.Key);
        JupiterZeroHoldingGuard.RequireEmpty(data, snapshot.Accounts[account].GetProperty("lamports").GetUInt64(), mint, owner);
        if (snapshot.Owned(owner, SystemProgram.ProgramIdKey.Key).Length != 0)
            throw new InvalidDataException("Cleanup requires the original system-owned fee payer.");
    }
}
