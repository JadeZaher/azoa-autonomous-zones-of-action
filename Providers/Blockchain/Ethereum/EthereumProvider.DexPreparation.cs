using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Model;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private sealed record DexEvmOwnerIntent(int Version, string IntentHash, string Stage, int Generation, string Permit);
    private sealed record DexEvmPlan(int Version, string ReservationId, string ProtocolIdentity, string Nonce, EthereumCall Call);

    /// <inheritdoc cref="IDexTransactionModule.PlanSwapStageAsync"/>
    public async Task<AZOAResult<SwapStagePlan>> PlanSwapStageAsync(SwapIntent intent, string stage,
        int generation, CancellationToken ct = default)
    {
        try
        {
            var quote = ReadDexAcceptedIntent(intent);
            if (generation < 0 || stage != "Swap" && !intent.Terms.Prerequisites.Any(item => item.Kind == stage && stage is "ResetAllowance" or "ApproveExact"))
                return Error<SwapStagePlan>("The requested stage is not part of the accepted Ethereum quote.");
            var hash = CanonicalJson.Hash(intent);
            var key = SwapOperationIdentity.Preparation(intent.OperationId, stage, generation, hash);
            var id = EthereumIdentity(intent.Terms.Network.ProtocolIdentity, intent.Terms.Owner.Address.ToLowerInvariant(), key);
            var prior = await WithPreparationStoreAsync(store => store.GetAsync(id, ct));
            if (prior is not null)
            {
                if (prior.State is "Canceling" or "Canceled" or "Reverted")
                    return Error<SwapStagePlan>("The original nonce is canceling or resolved without the intended effect; reconcile its saved stage.");
                return Ok(DexPlanFromReservation(intent, stage, generation, prior));
            }
            if (ReadDexDeployment() != quote.Deployment)
                return Error<SwapStagePlan>("The original accepted Ethereum deployment policy changed before preparation.");
            var snapshot = await ReadDexSnapshotAsync(quote.Deployment, ct);
            if (!CurrentDexTime(snapshot.ChainTime)) return AZOAResult<SwapStagePlan>.FailureWithCode("Finalized Ethereum evidence is not current.", AzoaErrorCodes.DependencyUnavailable);
            if (snapshot.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity || snapshot.ChainTime >= EthereumDexExecutionCodec.Amount(quote.ExecutionDeadline))
                return Error<SwapStagePlan>("The original physical network or executor authorization lifetime is no longer current.");
            if (await ReadDexTokenAsync(intent.Terms.InputAsset, snapshot, ct) != quote.Input ||
                await ReadDexTokenAsync(intent.Terms.OutputAsset, snapshot, ct) != quote.Output)
                return Error<SwapStagePlan>("The reviewed immutable token policy changed before preparation.");
            if (!intent.Terms.InputAsset.Native)
            {
                var allowance = await ReadUInt256AtBlockAsync(quote.Input.Address, "allowance(address,address)",
                    [intent.Terms.Owner.Address, quote.Deployment.Executor], snapshot.Block, ct);
                if (stage == "ApproveExact" && allowance != 0 || stage == "Swap" && allowance != EthereumDexExecutionCodec.Amount(intent.Terms.GrossInput))
                    return Error<SwapStagePlan>("The exact allowance prerequisite has not been finalized.");
                var lane = await WithDexAllowanceStoreAsync(store => store.ClaimAsync(DexAllowance(intent, quote), ct));
                if (lane is not { State: "Held" } || lane.OperationId != intent.OperationId || lane.IntentHash != hash)
                    return Error<SwapStagePlan>("Another unresolved operation owns this exact ERC20 allowance.");
            }
            var permit = stage == "Swap" ? EthereumHex.Data(SignDexPolicy(quote.Deployment, snapshot.ChainId, DexExecutionPayload(intent, quote))) : "0x";
            var ownerIntent = new DexEvmOwnerIntent(1, hash, stage, generation, permit);
            var call = DexStageCall(intent, quote, ownerIntent);
            var fees = DexStageFees(call);
            var reserved = await ReserveCallAsync(key, call.Sender, call.Target, EthereumDexExecutionCodec.Amount(call.Value),
                Convert.FromHexString(call.Data[2..]), call.Label, null, ct, call.OwnerIntentJson, fees, call.Owner);
            if (reserved.IsError || reserved.Result is null) return Error<SwapStagePlan>(reserved.Message);
            return Ok(DexPlanFromReservation(intent, stage, generation, reserved.Result));
        }
        catch (HttpRequestException) { return AZOAResult<SwapStagePlan>.FailureWithCode("Ethereum preparation data is unavailable.", AzoaErrorCodes.DependencyUnavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return AZOAResult<SwapStagePlan>.FailureWithCode("Ethereum preparation data timed out.", AzoaErrorCodes.DependencyUnavailable); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return Error<SwapStagePlan>("Ethereum preparation differs from its accepted protocol or immutable authority."); }
    }

    private SwapStagePlan DexPlanFromReservation(SwapIntent intent, string stage, int generation, EthereumPreparation row)
    {
        var call = JsonSerializer.Deserialize<EthereumCall>(row.IntentJson) ?? throw new InvalidDataException("Reserved Ethereum call is missing.");
        var saved = new DexEvmPlan(1, SurrealRecordGuid.BareId(row.Id), row.ProtocolIdentity,
            Text(BigInteger.Parse(row.Nonce, CultureInfo.InvariantCulture)), call);
        var hash = CanonicalJson.Hash(intent);
        var plan = new SwapStagePlan(stage, generation, SwapOperationIdentity.Preparation(intent.OperationId, stage, generation, hash),
            hash, [intent.Terms.Owner.Address], JsonSerializer.Serialize(Convert.ToBase64String(DexUnsigned(saved))),
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(saved)), Text(DexStageFees(call).MaximumFee), "0");
        var read = ReadEthereumDexPlan(intent, plan);
        if (row.Network != ActiveNetwork.ToString() || row.PreparationKey != plan.PreparationKey || row.CallFingerprint != DexCallFingerprint(read.Call))
            throw new InvalidDataException("Durable Ethereum reservation differs from the exact DEX call.");
        return plan;
    }

    private DexEvmPlan ReadEthereumDexPlan(SwapIntent intent, SwapStagePlan plan)
    {
        var quote = ReadDexAcceptedIntent(intent);
        if (plan is null || plan.Purpose != "Effect" || plan.CleanupConsent is not null || plan.Generation < 0 ||
            plan.IntentHash != CanonicalJson.Hash(intent) || plan.ReservedLockedBalance != "0" ||
            plan.PreparationKey != SwapOperationIdentity.Preparation(intent.OperationId, plan.Stage, plan.Generation, plan.IntentHash) ||
            plan.RequiredSignerAddresses is not { Count: 1 } || plan.RequiredSignerAddresses[0] != intent.Terms.Owner.Address)
            throw new InvalidDataException("Ethereum plan identity differs from the original operation.");
        var saved = JsonSerializer.Deserialize<DexEvmPlan>(plan.RecoveryMetadata);
        if (saved is not { Version: 1, Call: not null } || saved.Call.Owner is null || saved.Call.Context is not null ||
            saved.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity ||
            saved.ReservationId != EthereumIdentity(saved.ProtocolIdentity, intent.Terms.Owner.Address.ToLowerInvariant(), plan.PreparationKey) ||
            plan.RecoveryMetadata != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(saved)))
            throw new InvalidDataException("Ethereum plan has no exact durable reservation identity.");
        var owner = JsonSerializer.Deserialize<DexEvmOwnerIntent>(saved.Call.OwnerIntentJson);
        if (owner is not { Version: 1 } || owner.IntentHash != plan.IntentHash || owner.Stage != plan.Stage || owner.Generation != plan.Generation ||
            saved.Call != DexStageCall(intent, quote, owner) ||
            EthereumDexExecutionCodec.Amount(plan.ReservedNetworkFee) != DexStageFees(saved.Call).MaximumFee ||
            plan.UnsignedPayloadJson != JsonSerializer.Serialize(Convert.ToBase64String(DexUnsigned(saved))))
            throw new InvalidDataException("Ethereum plan changes the original permit, economics, nonce or transaction bytes.");
        return saved;
    }

    private DexEvmQuoteEvidence ReadDexAcceptedIntent(SwapIntent intent)
    {
        if (intent is null || intent.Version != 1 || intent.Terms is not { Version: 1, Owner: not null, Payer: not null,
                Network: not null, InputAsset: not null, OutputAsset: not null, PlatformFee: not null, Budget: not null, Prerequisites: not null } terms ||
            intent.QuoteHash != CanonicalJson.Hash(terms) || terms.Provider != "UniswapV3" || terms.ProtocolVersion != "classic-exact-input/1" ||
            terms.Network.ChainType != ChainType || terms.Network.Network != ActiveNetwork.ToString() || terms.Owner != terms.Payer ||
            !terms.Network.ProtocolIdentity.StartsWith("eip155:", StringComparison.Ordinal) ||
            terms.ActorUserId != terms.Owner.UserId || terms.Owner.UserId == Guid.Empty || terms.Owner.WalletId == Guid.Empty ||
            !EthereumAddress.IsNonZero(terms.Owner.Address) || !EthereumAddress.AreSame(terms.RecipientAddress, terms.Owner.Address))
            throw new InvalidDataException("The original accepted Ethereum quote is required.");
        var quote = terms.ProtocolEvidence.Deserialize<DexEvmQuoteEvidence>();
        if (quote is not { Version: 1, Deployment: not null, Input: not null, Output: not null, Costs: not null } ||
            terms.InputAsset.Native && terms.OutputAsset.Native || terms.PlatformFee.PolicyKeyVersion != quote.Deployment.PolicyKeyVersion[2..] ||
            EthereumDexExecutionCodec.Amount(quote.ExecutionDeadline) <= EthereumDexExecutionCodec.Amount(quote.ChainTime) ||
            EthereumDexExecutionCodec.Amount(quote.ExecutionDeadline) - EthereumDexExecutionCodec.Amount(quote.ChainTime) > 86400 ||
            EthereumDexExecutionCodec.Amount(terms.GrossInput) != EthereumDexExecutionCodec.Amount(terms.SwapInput) + EthereumDexExecutionCodec.Amount(terms.PlatformFee.Amount) ||
            quote.Input.Decimals != terms.InputAsset.Decimals || quote.Output.Decimals != terms.OutputAsset.Decimals ||
            !EthereumAddress.AreSame(quote.Input.Address, terms.InputAsset.Native ? quote.Deployment.WrappedNative : terms.InputAsset.Id) ||
            !EthereumAddress.AreSame(quote.Output.Address, terms.OutputAsset.Native ? quote.Deployment.WrappedNative : terms.OutputAsset.Id))
            throw new InvalidDataException("Ethereum quote evidence does not bind original tokens, fee and lifetime.");
        return quote;
    }

    private EthereumCall DexStageCall(SwapIntent intent, DexEvmQuoteEvidence quote, DexEvmOwnerIntent owner)
    {
        var terms = intent.Terms;
        var swap = owner.Stage == "Swap";
        if (!swap && (terms.InputAsset.Native || owner.Stage is not ("ResetAllowance" or "ApproveExact") || !terms.Prerequisites.Any(item => item.Kind == owner.Stage)))
            throw new InvalidDataException("Ethereum prerequisite is absent from original consent.");
        var gas = owner.Stage switch { "Swap" => quote.Costs.SwapGas, "ResetAllowance" => quote.Costs.ResetGas, _ => quote.Costs.ApproveGas };
        string data;
        if (swap)
        {
            var payload = DexExecutionPayload(intent, quote);
            if (!EthereumHex.TryParseData(owner.Permit, out var permit)) throw new InvalidDataException("Executor policy permit is missing.");
            var chain = EthereumDexExecutionCodec.Amount(terms.Network.ProtocolIdentity[7..]);
            EthereumPayloadPermit.RequireSigner(EthereumPayloadPermit.Digest("AZOA DEX", "1", chain, quote.Deployment.Executor, payload), permit, quote.Deployment.PolicySigner);
            data = EthereumAbi.EncodeCall("execute(bytes,bytes)", payload, permit);
        }
        else
        {
            if (owner.Permit != "0x") throw new InvalidDataException("Allowance prerequisite cannot carry an executor permit.");
            data = EthereumAbi.EncodeCall("approve(address,uint256)", quote.Deployment.Executor,
                owner.Stage == "ResetAllowance" ? BigInteger.Zero : EthereumDexExecutionCodec.Amount(terms.GrossInput));
        }
        var call = new EthereumCall(terms.Owner.Address.ToLowerInvariant(), swap ? quote.Deployment.Executor : quote.Input.Address,
            swap && terms.InputAsset.Native ? terms.GrossInput : "0", data, "DEX " + owner.Stage, null, gas,
            quote.Costs.PriorityFee, quote.Costs.MaxFee, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(owner)),
            new("swap", intent.OperationId, owner.Stage, owner.Generation, terms.ActorUserId, terms.Owner.WalletId, terms.TenantId, AzoaScopes.SwapSign, owner.IntentHash));
        if (DexStageFees(call).MaximumFee > EthereumDexExecutionCodec.Amount(terms.Budget.MaximumNetworkFee))
            throw new InvalidDataException("Ethereum stage fee exceeds original execution cap.");
        return call;
    }

    private static byte[] DexExecutionPayload(SwapIntent intent, DexEvmQuoteEvidence quote)
    {
        var terms = intent.Terms;
        return EthereumDexExecutionCodec.Encode(new(EthereumDexExecutionCodec.OperationIdentity(intent.OperationId), intent.QuoteHash,
            terms.Owner.Address, terms.RecipientAddress, quote.Input.Address, quote.Output.Address, quote.Pool,
            string.IsNullOrEmpty(terms.PlatformFee.TreasuryAddress) ? "0x" + new string('0', 40) : terms.PlatformFee.TreasuryAddress,
            terms.GrossInput, terms.PlatformFee.Amount, terms.MinimumOutput, quote.Deployment.PoolFee, quote.ExecutionDeadline,
            terms.PlatformFee.ScheduleVersion, terms.PlatformFee.PolicyKeyVersion, quote.Input.CodeHash[2..], quote.Output.CodeHash[2..],
            terms.InputAsset.Native, terms.OutputAsset.Native));
    }

    private static EthereumFeeAuthorization DexStageFees(EthereumCall call)
    {
        var gas = EthereumDexExecutionCodec.Amount(call.GasLimit); var max = EthereumDexExecutionCodec.Amount(call.MaxFee);
        var priority = EthereumDexExecutionCodec.Amount(call.PriorityFee);
        if (gas < 21000 || gas > 30_000_000 || priority > max) throw new InvalidDataException("Ethereum stage gas and fee are outside accepted bounds.");
        return new(gas, priority, max, gas * max, EthereumDexExecutionCodec.Amount(call.Value));
    }

    private static byte[] DexUnsigned(DexEvmPlan saved)
    {
        if (!saved.ProtocolIdentity.StartsWith("eip155:", StringComparison.Ordinal)) throw new InvalidDataException("Invalid Ethereum protocol identity.");
        var call = saved.Call;
        return new Transaction1559(EthereumDexExecutionCodec.Amount(saved.ProtocolIdentity[7..]), EthereumDexExecutionCodec.Amount(saved.Nonce),
            EthereumDexExecutionCodec.Amount(call.PriorityFee), EthereumDexExecutionCodec.Amount(call.MaxFee), EthereumDexExecutionCodec.Amount(call.GasLimit),
            call.Target, EthereumDexExecutionCodec.Amount(call.Value), call.Data, null).GetRLPEncodedRaw();
    }

    private static string DexCallFingerprint(EthereumCall call)
        => EthereumIdentity(EthereumIdentity(call.Sender, call.Target!.ToLowerInvariant(), call.Value, call.Data, "null", call.OwnerIntentJson),
            JsonSerializer.Serialize(call.Owner), JsonSerializer.Serialize(DexStageFees(call)));

    private static EthereumDexAllowance DexAllowance(SwapIntent intent, DexEvmQuoteEvidence quote)
    {
        var protocol = intent.Terms.Network.ProtocolIdentity + ":" + quote.Deployment.GenesisHash[2..];
        var owner = intent.Terms.Owner.Address.ToLowerInvariant(); var token = quote.Input.Address.ToLowerInvariant();
        var executor = quote.Deployment.Executor.ToLowerInvariant();
        return new() { Id = EthereumDexAllowanceIdentity.Create(protocol, owner, token, executor), ProtocolIdentity = protocol,
            OwnerAddress = owner, TokenAddress = token, ExecutorAddress = executor, OperationId = intent.OperationId, IntentHash = CanonicalJson.Hash(intent) };
    }

    private async Task<T> WithDexAllowanceStoreAsync<T>(Func<IEthereumDexAllowanceStore, Task<T>> action)
    {
        if (_custodyScopeFactory is null) throw new InvalidOperationException("Durable Ethereum allowance storage is unavailable.");
        using var scope = _custodyScopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IEthereumDexAllowanceStore>());
    }
}
