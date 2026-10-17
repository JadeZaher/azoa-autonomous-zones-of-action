using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IDexTransactionModule.PlanSwapCleanupAsync"/>
    public async Task<AZOAResult<SwapStagePlan>> PlanSwapCleanupAsync(SwapIntent intent, IReadOnlyList<SwapStageHistory> history,
        SwapCleanupConsent consent, string stage, int generation, CancellationToken ct = default)
    {
        try
        {
            var quote = ReadDexAcceptedIntent(intent);
            if (stage != "RevokeAllowance" || generation < 0 || consent is null || !ValidRevokeConsent(intent, consent) ||
                history.Count == 0 || history.Any(item => item.Plan.Purpose is not ("Effect" or "Cleanup")))
                return Error<SwapStagePlan>("Allowance cleanup requires its own explicit revocation consent and terminal original history.");
            var lineage = await GetNextSwapStageAsync(intent, history.Where(item => item.Plan.Purpose == "Effect").ToArray(), ct);
            if (lineage.IsError) return Error<SwapStagePlan>(lineage.Message);
            foreach (var earlier in history.Where(item => item.Plan.Purpose == "Cleanup"))
                if (!ValidEthereumHistory(intent, earlier) || earlier.Plan.CleanupConsent != consent || earlier.Plan.Generation >= generation ||
                    earlier.Observation.Verdict is not ("FailedOnChain" or "ExpiredAbsent"))
                    return Error<SwapStagePlan>("An earlier allowance cleanup is unresolved, confirmed or differently authorized.");
            var hash = CanonicalJson.Hash(intent);
            var key = SwapOperationIdentity.CleanupPreparation(intent.OperationId, stage, generation, hash, consent);
            var prior = await WithPreparationStoreAsync(store => store.GetAsync(
                EthereumIdentity(intent.Terms.Network.ProtocolIdentity, intent.Terms.Owner.Address.ToLowerInvariant(), key), ct));
            if (prior is not null)
                return prior.State is "Canceling" or "Canceled" or "Reverted" || ReleasedReservation(prior.State)
                    ? Error<SwapStagePlan>("The cleanup nonce is canceling or resolved without the intended effect; reconcile its saved stage.")
                    : Ok(DexPlanFromReservation(intent, stage, generation, prior, consent));
            var resource = DexAllowance(intent, quote);
            var holder = await WithDexAllowanceStoreAsync(store => store.GetAsync(resource.Id, ct));
            if (holder is not { State: "Held" } || holder.OperationId != intent.OperationId || holder.IntentHash != hash)
                return Error<SwapStagePlan>("This operation does not own the exact unreleased allowance resource.");
            var snapshot = await ReadDexSnapshotAsync(quote.Deployment, ct);
            if (!CurrentDexTime(snapshot.ChainTime))
                return AZOAResult<SwapStagePlan>.FailureWithCode("Finalized Ethereum evidence is not current.", AzoaErrorCodes.DependencyUnavailable);
            if (snapshot.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity)
                return Error<SwapStagePlan>("Allowance cleanup belongs to another physical network.");
            await RequireCodeAtBlockAsync(quote.Input.Address, quote.Input.CodeHash, snapshot.Block, ct);
            var fees = await GetFeeDataAsync(ct);
            var cap = EthereumDexExecutionCodec.Amount(consent.MaximumNetworkFee);
            var gas = EthereumDexExecutionCodec.Amount(quote.Costs.CleanupGas);
            if (fees.IsError || fees.Result is null || gas == 0)
                return AZOAResult<SwapStagePlan>.FailureWithCode("Current Ethereum fee data is unavailable.", AzoaErrorCodes.DependencyUnavailable);
            var maximum = BigInteger.Min(cap / gas, fees.Result.MaxFeePerGas * 2);
            if (maximum < fees.Result.MaxFeePerGas || fees.Result.MaxPriorityFeePerGas > maximum)
                return Error<SwapStagePlan>("Current Ethereum fees exceed the separately accepted cleanup cap.");
            var owner = new DexEvmCleanupIntent(1, hash, stage, generation, CanonicalJson.Hash(consent), quote.Costs.CleanupGas,
                Text(fees.Result.MaxPriorityFeePerGas), Text(maximum));
            var call = DexCleanupCall(intent, quote, consent, owner);
            var authorization = DexStageFees(call);
            var balance = await RpcReadAsync("eth_getBalance", [intent.Terms.Owner.Address, snapshot.Block], ct);
            if (balance.IsError || !balance.TryGetString(out var balanceText) || !EthereumHex.TryParseQuantity(balanceText, out var available) ||
                available < authorization.MaximumFee)
                return AZOAResult<SwapStagePlan>.FailureWithCode("The owner lacks the native balance reserved for cleanup fees.", AzoaErrorCodes.DependencyUnavailable);
            var reserved = await ReserveCallAsync(key, call.Sender, call.Target, BigInteger.Zero, Convert.FromHexString(call.Data[2..]),
                call.Label, null, ct, call.OwnerIntentJson, authorization, call.Owner);
            if (reserved.IsError || reserved.Result is null) return Error<SwapStagePlan>(reserved.Message);
            return Ok(DexPlanFromReservation(intent, stage, generation, reserved.Result, consent));
        }
        catch (HttpRequestException) { return AZOAResult<SwapStagePlan>.FailureWithCode("Ethereum cleanup data is unavailable.", AzoaErrorCodes.DependencyUnavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return AZOAResult<SwapStagePlan>.FailureWithCode("Ethereum cleanup data timed out.", AzoaErrorCodes.DependencyUnavailable); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return Error<SwapStagePlan>("Ethereum cleanup differs from its accepted protocol, token pin or separate consent."); }
    }
}
