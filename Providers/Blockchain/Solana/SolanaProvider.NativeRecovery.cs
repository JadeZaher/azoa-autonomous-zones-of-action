using System.Buffers.Binary;
using System.Text.Json;
using System.Numerics;
using System.Globalization;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : INativeRecoveryModule
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NativeNextStage>> GetRecoveryNextStageAsync(NativeOperationIntent intent,
        IReadOnlyList<NativeStageHistory> history, string mode, CancellationToken ct = default)
    {
        if (intent.Kind == "Recovery" || !TrySolanaNativeIntent(intent, out var input) || !NativeOperationAuthorization.ValidQuote(intent, false)
            || mode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority"))
            return Error<NativeNextStage>("Invalid original-budget recovery request.");
        var quote = JsonSerializer.Deserialize<NativeSolanaQuote>(intent.Quote!.PlanJson)!;
        var stage = mode == "FinalizeAuthority" ? "RemoveAuthority" : "CloseBuffer";
        if (stage == "CloseBuffer" && quote.FallbackCleanup is null)
            return Error<NativeNextStage>("Original quote contains no authorized cleanup reserve.");
        var terminal = history.LastOrDefault();
        var complete = terminal?.Prepared.Stage == stage && terminal.Observation.Verdict == "Confirmed";
        var originalHistory = stage == "CloseBuffer" ? history.TakeWhile(item => item.Prepared.Stage != stage).ToArray()
            : complete ? history.Take(history.Count - 1).ToArray() : history.ToArray();
        var recovery = intent with { Kind = "Recovery", Input = JsonSerializer.SerializeToElement(new NativeRecoveryInput(intent,
            originalHistory, mode, input.Payer)) };
        try
        {
            await PlanSolanaRecoveryAsync(recovery, input, ct, !complete);
            foreach (var item in history.Skip(originalHistory.Length))
            {
                if (item.Prepared.Stage != stage || item.Observation.Verdict is not ("Confirmed" or "FailedOnChain" or "ExpiredNoEffect"))
                    return Error<NativeNextStage>("Recovery history contains an unresolved or unrelated stage.");
                var observed = await ObserveNativeStageAsync(intent, item.Prepared, ct);
                if (observed.IsError || observed.Result?.Verdict != item.Observation.Verdict)
                    return Error<NativeNextStage>("Recovery receipt lacks authoritative evidence.");
            }
        }
        catch (ArgumentException exception) { return Error<NativeNextStage>(exception.Message); }
        var reserved = stage == "CloseBuffer" ? quote.FallbackCleanup : quote.Stages.SingleOrDefault(item => item.Name == stage);
        if (reserved is null) return Error<NativeNextStage>("Recovery stage is absent from the original quote.");
        return complete ? Ok(new NativeNextStage(null, true, terminal!.Observation.ResourceId, terminal.Observation.EvidenceJson,
            mode == "FinalizeAuthority" ? "Succeeded" : mode == "AbortCleanup" ? "AbortedClean" : "FailedClean"))
            : Ok(new NativeNextStage(stage, false, Disposition: stage == "CloseBuffer" ? "CleanupRequired" : "AuthorityFinalizationRequired",
                Reservation: new(reserved.Fee, reserved.Rent, "0")));
    }

    private async Task<NativeSolanaQuote> PlanSolanaRecoveryAsync(NativeOperationIntent intent, SolanaNativeInput input, CancellationToken ct,
        bool requireCurrentState = true)
    {
        var recovery = intent.Input.Deserialize<NativeRecoveryInput>(NativeSolanaJson)!;
        var original = recovery.OriginalIntent;
        if (!TrySolanaNativeIntent(original, out var source) || !NativeOperationAuthorization.ValidQuote(original, false)
            || source.Operation is not ("Create" or "Upgrade"))
            throw new ArgumentException("Recovery requires the original quoted loader operation.");
        var quote = JsonSerializer.Deserialize<NativeSolanaQuote>(original.Quote!.PlanJson)!;
        var cursor = 0;
        var cleanupSeen = false;
        foreach (var item in recovery.ResolvedHistory)
        {
            var cleanup = quote.FallbackCleanup?.Name == item.Prepared.Stage;
            if ((!cleanup && (cleanupSeen || cursor >= quote.Stages.Count || item.Prepared.Stage != quote.Stages[cursor].Name))
                || item.Observation.Verdict is not ("Confirmed" or "FailedOnChain" or "ExpiredNoEffect"))
                throw new ArgumentException("Original recovery history is not an ordered resolved prefix.");
            var observed = await ObserveNativeStageAsync(original, item.Prepared, ct);
            if (observed.IsError || observed.Result?.Verdict != item.Observation.Verdict)
                throw new ArgumentException("Every original wire must have authoritative final evidence before recovery.");
            if (cleanup)
            {
                cleanupSeen = true;
                if (item.Observation.Verdict == "Confirmed") throw new ArgumentException("Original buffer cleanup already completed.");
            }
            else if (item.Observation.Verdict == "Confirmed") cursor++;
        }
        var deployed = recovery.ResolvedHistory.Any(item => item.Observation.Verdict == "Confirmed"
            && item.Prepared.Stage is "Deploy" or "Upgrade");
        var authority = new PublicKey(source.Authority);
        if (recovery.Mode == "FinalizeAuthority")
        {
            if (!deployed || !source.Immutable || quote.ExpectedProgramHash is null
                || recovery.ResolvedHistory.Any(item => item.Prepared.Stage == "RemoveAuthority" && item.Observation.Verdict == "Confirmed"))
                throw new ArgumentException("Only unfinished originally requested authority finalization can be recovered.");
            if (requireCurrentState) await RequireRecoveryProgramAuthorityAsync(quote, source.Authority, ct);
            return new(quote.Resource, [NativeStage("RemoveAuthority", 0,
                SolanaNativeLoader.RemoveAuthority(new PublicKey(quote.Resource), authority))], quote.ExpectedProgramHash);
        }
        if (deployed || !recovery.ResolvedHistory.Any(item => item.Prepared.Stage == "CreateBuffer" && item.Observation.Verdict == "Confirmed"))
            throw new ArgumentException("Buffer cleanup requires original confirmed funding and no deployment effect.");
        var buffer = SolanaNativeLoader.Seeded(authority, SolanaNativeLoader.Seed(original.OperationKey, "buffer"));
        if (requireCurrentState) await RequireRecoveryBufferAsync(buffer.Key, source.Authority, ct);
        var recipient = source.RentRecipient ?? source.Payer;
        if (recipient == buffer.Key) throw new ArgumentException("A buffer cannot receive its own cleanup refund.");
        return new(buffer.Key, [NativeStage("CloseBuffer", 0,
            SolanaNativeLoader.CloseBuffer(buffer, new PublicKey(recipient), authority))],
            Disclosure: new("CloseBuffer", recipient));
    }

    private async Task RequireRecoveryBufferAsync(string buffer, string authority, CancellationToken ct)
    {
        var account = await ReadNativeAccountAsync(buffer, ct) ?? throw new ArgumentException("Original buffer is absent; reconcile its disposition.");
        if (account.Executable || account.Owner != SolanaNativeLoader.Program.Key || account.Data.Length < SolanaNativeLoader.BufferHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(account.Data) != 1 || account.Data[4] != 1
            || new PublicKey(account.Data.AsSpan(5, 32).ToArray()).Key != authority)
            throw new ArgumentException("Buffer loader state or current authority differs from the original operation.");
    }

    private static bool ProvesBufferRefund(NativeSolanaQuote quote, SolanaNativeInput input, NativeStageAccounting accounting, string bufferAddress)
    {
        var recipient = input.Operation == "Recovery" ? quote.Disclosure?.RefundDestination : quote.Disclosure?.CleanupRefundDestination;
        if (recipient is null || recipient == bufferAddress) return false;
        var buffer = accounting.NativeBalances.SingleOrDefault(item => item.Address == bufferAddress);
        var destination = accounting.NativeBalances.SingleOrDefault(item => item.Address == recipient);
        if (buffer is null || destination is null) return false;
        var rent = BigInteger.Parse(buffer.BeforeBaseUnits, CultureInfo.InvariantCulture);
        var refund = BigInteger.Parse(destination.AfterBaseUnits, CultureInfo.InvariantCulture)
            - BigInteger.Parse(destination.BeforeBaseUnits, CultureInfo.InvariantCulture);
        if (recipient == input.Payer) refund += BigInteger.Parse(accounting.NetworkFeePaid, CultureInfo.InvariantCulture);
        return rent > 0 && buffer.AfterBaseUnits == "0" && refund == rent;
    }

    private async Task RequireRecoveryProgramAuthorityAsync(NativeSolanaQuote quote, string authority, CancellationToken ct)
    {
        var data = await RequireProgramHashAsync(quote.Resource, quote.ExpectedProgramHash!, ct);
        if (data.Data[12] != 1 || new PublicKey(data.Data.AsSpan(13, 32).ToArray()).Key != authority)
            throw new ArgumentException("Current program authority differs from the original operation.");
    }
}
