using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IDexTransactionModule.PlanSwapStageAsync"/>
    public async Task<AZOAResult<SwapStagePlan>> PlanSwapStageAsync(SwapIntent intent, string stage,
        int generation, CancellationToken ct = default)
    {
        if (stage is not ("OptIn" or "Swap") || generation < 0)
            return AZOAResult<SwapStagePlan>.Failure("Unknown Tinyman preparation stage.");
        var fresh = await QuoteSwapAsync(intent.Terms, ct);
        if (fresh.IsError || fresh.Result is null)
            return AZOAResult<SwapStagePlan>.FailureWithCode(fresh.Message, fresh.Code ?? AzoaErrorCodes.DependencyUnavailable);
        var original = intent.Terms.ProtocolEvidence;
        var current = fresh.Result.ProtocolEvidence;
        foreach (var propertyName in new[] { "format", "validator", "pool", "approvalSha256", "clearStateSha256", "asset1", "asset2" })
            if (!original.TryGetProperty(propertyName, out var expected) || !current.TryGetProperty(propertyName, out var actual) ||
                expected.ValueKind != JsonValueKind.String || actual.GetString() != expected.GetString())
                return AZOAResult<SwapStagePlan>.Failure("The saved quote's protocol deployment changed.");
        var needsOptIn = fresh.Result.Prerequisites.Any(effect => effect.Kind == "OptIn");
        if ((stage == "OptIn") != needsOptIn)
            return AZOAResult<SwapStagePlan>.Failure("Current output holding requires a different preparation stage.");
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new JsonException("Algod preparation parameters are missing.");
        if (parameters.LastRound <= 0 || parameters.MinFee <= 0 || parameters.Fee < 0 ||
            intent.Terms.Network.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return AZOAResult<SwapStagePlan>.Failure("Preparation fee, round or genesis evidence is invalid.");
        var intentHash = CanonicalJson.Hash(intent);
        var key = SwapOperationIdentity.Preparation(intent.OperationId, stage, generation, intentHash);
        var wire = new TinymanWireParameters(stage, key, intent.Terms.Owner.Address, intent.Terms.PlatformFee.TreasuryAddress,
            UInt64Text.Parse(original.GetProperty("validator").GetString()!), UInt64Text.Parse(intent.Terms.InputAsset.Id),
            UInt64Text.Parse(intent.Terms.OutputAsset.Id), UInt64Text.Parse(intent.Terms.GrossInput), UInt64Text.Parse(intent.Terms.PlatformFee.Amount),
            UInt64Text.Parse(intent.Terms.MinimumOutput), (ulong)parameters.LastRound, checked((ulong)parameters.LastRound + 1000),
            parameters.GenesisHashB64!, parameters.GenesisId ?? "", (ulong)parameters.MinFee, (ulong)parameters.MinFee,
            checked((ulong)parameters.MinFee * 2));
        var initial = TinymanSwapWire.Build(wire);
        var regularFee = wire.MinimumFee;
        var applicationFee = wire.ApplicationFee;
        foreach (var transaction in initial)
        {
            var byteFee = checked((ulong)parameters.Fee * ((ulong)Encoder.EncodeToMsgPackOrdered(transaction).Length + 88UL));
            if (transaction is ApplicationCallTransaction) applicationFee = Math.Max(applicationFee, checked(byteFee + wire.MinimumFee));
            else regularFee = Math.Max(regularFee, byteFee);
        }
        wire = wire with { RegularFee = regularFee, ApplicationFee = applicationFee };
        var transactions = TinymanSwapWire.Build(wire);
        var totalFee = transactions.Aggregate(0UL, (sum, transaction) => checked(sum + transaction.Fee!.Value));
        var rent = stage == "OptIn" ? 100000UL : 0UL;
        if (!UInt64Text.TryParse(intent.Terms.Budget.MaximumNetworkFee, out var feeCap) || totalFee > feeCap ||
            !UInt64Text.TryParse(intent.Terms.Budget.MaximumLockedBalance, out var rentCap) || rent > rentCap)
            return AZOAResult<SwapStagePlan>.Failure("The exact transaction fees or opt-in rent exceed the accepted caps.");
        var plan = new SwapStagePlan(stage, generation, key, intentHash, [intent.Terms.Owner.Address],
            TinymanUnsignedPayload(transactions), CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire)),
            totalFee.ToString(CultureInfo.InvariantCulture), rent.ToString(CultureInfo.InvariantCulture));
        return AZOAResult<SwapStagePlan>.Success(plan);
    }

    /// <inheritdoc cref="IDexTransactionModule.SignSwapStageAsync"/>
    public async Task<AZOAResult<PreparedSwapStage>> SignSwapStageAsync(SwapIntent intent, SwapStagePlan plan,
        IReadOnlyList<SwapSigningAuthority> authorities, CancellationToken ct = default)
    {
        var wire = ReadTinymanPlan(intent, plan);
        if (authorities.Count != 1 || authorities[0].Participant != intent.Terms.Owner)
            return AZOAResult<PreparedSwapStage>.Failure("The exact asset owner must authorize Tinyman custody signing.");
        var context = authorities[0].Context;
        var expectedTenant = plan.Purpose == "Cleanup" ? plan.CleanupConsent!.TenantId : intent.Terms.TenantId;
        if (context.IsPlatform || !context.IsResolvableUserContext || context.UserId != intent.Terms.ActorUserId ||
            context.WalletId != intent.Terms.Owner.WalletId || context.ChainType != ChainType ||
            context.Scope != AzoaScopes.SwapSign || context.ActingTenantId != (expectedTenant ?? Guid.Empty) ||
            (expectedTenant.HasValue && context.GrantorUserId != intent.Terms.ActorUserId))
            return AZOAResult<PreparedSwapStage>.Failure("Tinyman signing context does not match the original participant and scope.");
        var envelopes = new List<string>();
        foreach (var transaction in TinymanSwapWire.Build(wire))
        {
            ct.ThrowIfCancellationRequested();
            var signed = await SignViaCustodyAsync(Encoder.EncodeToMsgPackOrdered(transaction), "prepare exact Tinyman swap stage", context);
            if (signed.IsError || signed.Result is null) return AZOAResult<PreparedSwapStage>.Failure(signed.Message);
            envelopes.Add(Convert.ToBase64String(signed.Result));
        }
        return ValidateSignedSwapStage(intent, plan, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(envelopes)));
    }

    /// <inheritdoc cref="IDexTransactionModule.ValidateSignedSwapStage"/>
    public AZOAResult<PreparedSwapStage> ValidateSignedSwapStage(SwapIntent intent, SwapStagePlan plan, string signedPayloadJson)
    {
        try
        {
            var wire = ReadTinymanPlan(intent, plan);
            using var document = JsonDocument.Parse(signedPayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 3)
                return AZOAResult<PreparedSwapStage>.Failure("Tinyman requires the exact admitted signed envelope list.");
            var envelopes = document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
            var signed = TinymanSwapWire.ValidateSigned(wire, envelopes);
            return AZOAResult<PreparedSwapStage>.Success(new(plan, signed.Select(item => item.Tx.TxID()).ToArray(),
                CanonicalJson.Canonicalize(document.RootElement)));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return AZOAResult<PreparedSwapStage>.Failure("Signed Tinyman bytes do not match the saved plan and owner signature.");
        }
    }

    private TinymanWireParameters ReadTinymanPlan(SwapIntent intent, SwapStagePlan plan)
    {
        using var recovery = JsonDocument.Parse(plan.RecoveryMetadata);
        if (CanonicalJson.Canonicalize(recovery.RootElement) != plan.RecoveryMetadata)
            throw new InvalidOperationException("Tinyman recovery metadata is not canonical.");
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(plan.RecoveryMetadata)
            ?? throw new InvalidOperationException("Tinyman recovery metadata is missing.");
        var terms = intent.Terms;
        var cleanup = plan.Purpose == "Cleanup" && plan.Stage == "CloseOptIn" && plan.CleanupConsent is { } consent &&
            SwapCleanupContract.IsValid(intent, consent);
        var key = cleanup ? SwapOperationIdentity.CleanupPreparation(intent.OperationId, plan.Stage, plan.Generation, plan.IntentHash, plan.CleanupConsent!)
            : SwapOperationIdentity.Preparation(intent.OperationId, plan.Stage, plan.Generation, plan.IntentHash);
        if (terms.Network.ChainType != ChainType || terms.Network.Network != ActiveNetwork.ToString() ||
            (!cleanup && (plan.Purpose != "Effect" || plan.CleanupConsent is not null || plan.Stage == "CloseOptIn")) ||
            plan.IntentHash != CanonicalJson.Hash(intent) || plan.Generation < 0 ||
            plan.PreparationKey != key ||
            wire.Stage != plan.Stage || wire.PreparationKey != plan.PreparationKey || wire.Owner != terms.Owner.Address ||
            wire.Treasury != terms.PlatformFee.TreasuryAddress || terms.Network.ProtocolIdentity != "algorand:" + wire.GenesisHash ||
            wire.Validator.ToString(CultureInfo.InvariantCulture) != terms.ProtocolEvidence.GetProperty("validator").GetString() ||
            wire.InputAsset.ToString(CultureInfo.InvariantCulture) != terms.InputAsset.Id ||
            wire.OutputAsset.ToString(CultureInfo.InvariantCulture) != terms.OutputAsset.Id ||
            wire.GrossInput.ToString(CultureInfo.InvariantCulture) != terms.GrossInput ||
            wire.PlatformFee.ToString(CultureInfo.InvariantCulture) != terms.PlatformFee.Amount ||
            wire.MinimumOutput.ToString(CultureInfo.InvariantCulture) != terms.MinimumOutput ||
            !plan.RequiredSignerAddresses.SequenceEqual(new[] { terms.Owner.Address }))
            throw new InvalidOperationException("Tinyman plan differs from its immutable quote and participant.");
        var transactions = TinymanSwapWire.Build(wire);
        var totalFee = transactions.Aggregate(0UL, (sum, transaction) => checked(sum + transaction.Fee!.Value));
        if (plan.UnsignedPayloadJson != TinymanUnsignedPayload(transactions) ||
            plan.ReservedNetworkFee != totalFee.ToString(CultureInfo.InvariantCulture) ||
            plan.ReservedLockedBalance != (plan.Stage == "OptIn" ? "100000" : "0") ||
            !UInt64Text.TryParse(cleanup ? plan.CleanupConsent!.MaximumNetworkFee : terms.Budget.MaximumNetworkFee, out var cap) || totalFee > cap ||
            !UInt64Text.TryParse(terms.Budget.MaximumLockedBalance, out var rentCap) ||
            (plan.Stage == "OptIn" && rentCap < 100000))
            throw new InvalidOperationException("Tinyman plan bytes or reservation changed.");
        return wire;
    }

    private static string TinymanUnsignedPayload(IReadOnlyList<Transaction> transactions)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            format = "algorand-group/1", transactions = transactions.Select(transaction =>
                Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction))).ToArray(),
        }));
}
