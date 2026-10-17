using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record JupiterSavedPlan(int Version, JupiterBuildPlan Build, JupiterRpcSnapshot Snapshot,
        IReadOnlyList<SolanaLookupTableSnapshot> Tables, DurableSolanaPlan Recovery, WttSolanaGuard Guard,
        string Source, string Destination, string ExecutionKey, string NetworkFee, string ReservedRent);

    /// <inheritdoc cref="IDexTransactionModule.PlanSwapStageAsync"/>
    public async Task<AZOAResult<SwapStagePlan>> PlanSwapStageAsync(SwapIntent intent, string stage,
        int generation, CancellationToken ct = default)
    {
        if (stage != "Swap" || generation < 0 || intent.Version != 1 ||
            intent.QuoteHash != CanonicalJson.Hash(intent.Terms))
            return AZOAResult<SwapStagePlan>.Failure("Jupiter preparation requires the original accepted swap intent.");
        try
        {
            var hash = CanonicalJson.Hash(intent);
            var key = SwapOperationIdentity.Preparation(intent.OperationId, stage, generation, hash);
            var assembled = await BuildJupiterQuoteAsync(intent.Terms, ct, retainAcceptedTerms: true, preparationIdentity: key);
            if (assembled.IsError || assembled.Result is not { } value)
                return AZOAResult<SwapStagePlan>.FailureWithCode(assembled.Message, assembled.Code ?? AzoaErrorCodes.DependencyUnavailable);
            var saved = new JupiterSavedPlan(1, value.Build, value.State.Snapshot, value.State.LookupTables,
                value.Recovery, value.State.Guard, value.State.Source, value.State.Destination, value.ExecutionKey,
                value.NetworkFee.ToString(CultureInfo.InvariantCulture), value.ReservedRent.ToString(CultureInfo.InvariantCulture));
            var plan = new SwapStagePlan(stage, generation, key, hash, [intent.Terms.Owner.Address],
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(Convert.ToBase64String(value.Compiled.Message))),
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(saved)), saved.NetworkFee, saved.ReservedRent);
            _ = ReadJupiterPlan(intent, plan);
            return AZOAResult<SwapStagePlan>.Success(plan);
        }
        catch (HttpRequestException)
        { return AZOAResult<SwapStagePlan>.FailureWithCode("Jupiter preparation data is temporarily unavailable.", AzoaErrorCodes.DependencyUnavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return AZOAResult<SwapStagePlan>.FailureWithCode("Jupiter preparation data timed out.", AzoaErrorCodes.DependencyUnavailable); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException)
        { return AZOAResult<SwapStagePlan>.Failure("Jupiter preparation does not match the accepted protocol evidence."); }
    }

    /// <inheritdoc cref="IDexTransactionModule.SignSwapStageAsync"/>
    public async Task<AZOAResult<PreparedSwapStage>> SignSwapStageAsync(SwapIntent intent, SwapStagePlan plan,
        IReadOnlyList<SwapSigningAuthority> authorities, CancellationToken ct = default)
    {
        var (saved, compiled) = ReadJupiterPlan(intent, plan);
        if (authorities.Count != 1 || authorities[0].Participant != intent.Terms.Owner)
            return AZOAResult<PreparedSwapStage>.Failure("The exact Jupiter asset owner must authorize custody signing.");
        var context = authorities[0].Context;
        var signingTenant = plan.Purpose == "Cleanup" ? plan.CleanupConsent!.TenantId : intent.Terms.TenantId;
        if (context.IsPlatform || !context.IsResolvableUserContext || context.UserId != intent.Terms.ActorUserId ||
            context.WalletId != intent.Terms.Owner.WalletId || context.ChainType != ChainType ||
            context.Scope != AzoaScopes.SwapSign || context.ActingTenantId != (signingTenant ?? Guid.Empty) ||
            signingTenant.HasValue && context.GrantorUserId != intent.Terms.ActorUserId)
            return AZOAResult<PreparedSwapStage>.Failure("Jupiter signing context differs from the original participant and scope.");
        await VerifyGenesisAsync(ct);
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.ValueKind != JsonValueKind.Number || !height.TryGetInt64(out var currentHeight) || currentHeight < 0)
            throw new JsonException("Finalized Solana block height is missing.");
        if (currentHeight > saved.Recovery.LastValidBlockHeight)
            return AZOAResult<PreparedSwapStage>.Failure("The original Jupiter blockhash expired; reconcile before requesting new custody.");
        if (await NativeMessageFeeAsync(compiled.Message, ct) > UInt64Text.Parse(saved.NetworkFee))
            return AZOAResult<PreparedSwapStage>.Failure("The exact Jupiter message fee now exceeds its saved reservation.");
        await RequireCurrentLookupResolutionAsync(compiled.Message, compiled.LookupTables, ct);
        var signed = await SignDurableAsync(compiled.Message,
            new Dictionary<string, SigningContext> { [intent.Terms.Owner.Address] = context }, ct);
        return signed.IsError || signed.Result is null
            ? AZOAResult<PreparedSwapStage>.Failure(signed.Message)
            : ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(Convert.ToBase64String(signed.Result)));
    }

    /// <inheritdoc cref="IDexTransactionModule.ValidateSignedSwapStage"/>
    public AZOAResult<PreparedSwapStage> ValidateSignedSwapStage(SwapIntent intent, SwapStagePlan plan, string signedPayloadJson)
    {
        try
        {
            var (_, compiled) = ReadJupiterPlan(intent, plan);
            using var payload = JsonDocument.Parse(signedPayloadJson);
            if (payload.RootElement.ValueKind != JsonValueKind.String || payload.RootElement.GetString() is not { Length: <= 1644 } encoded)
                throw new InvalidDataException("Jupiter expects one bounded signed transaction.");
            var wire = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(wire) != encoded) throw new InvalidDataException("Jupiter wire encoding is not canonical.");
            var id = SolanaMessageEnvelope.VerifyWire(wire, compiled.Message);
            return AZOAResult<PreparedSwapStage>.Success(new(plan, [id], CanonicalJson.Canonicalize(payload.RootElement)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return AZOAResult<PreparedSwapStage>.Failure("Signed Jupiter bytes differ from the saved plan and real owner signature."); }
    }

    private (JupiterSavedPlan Saved, JupiterCompilation Compiled) ReadJupiterPlan(SwapIntent intent, SwapStagePlan plan)
    {
        if (plan.Purpose == "Cleanup") return ReadJupiterCleanupPlan(intent, plan);
        var terms = intent.Terms;
        if (intent.Version != 1 || intent.QuoteHash != CanonicalJson.Hash(terms) || plan.Stage != "Swap" || plan.Generation < 0 ||
            plan.Purpose != "Effect" || plan.CleanupConsent is not null || plan.IntentHash != CanonicalJson.Hash(intent) ||
            plan.PreparationKey != SwapOperationIdentity.Preparation(intent.OperationId, plan.Stage, plan.Generation, plan.IntentHash) ||
            !plan.RequiredSignerAddresses.SequenceEqual([terms.Owner.Address]) || terms.Network.ChainType != ChainType ||
            terms.Network.Network != ActiveNetwork.ToString() || terms.Network.ProtocolIdentity != "solana:" + ExpectedGenesis)
            throw new InvalidDataException("Jupiter saved plan changed its immutable operation, owner or network.");
        using var document = JsonDocument.Parse(plan.RecoveryMetadata);
        if (CanonicalJson.Canonicalize(document.RootElement) != plan.RecoveryMetadata)
            throw new InvalidDataException("Jupiter recovery metadata is not canonical.");
        var saved = JsonSerializer.Deserialize<JupiterSavedPlan>(plan.RecoveryMetadata)
            ?? throw new InvalidDataException("Jupiter recovery metadata is missing.");
        if (saved.Build is null || saved.Recovery is null || saved.Snapshot is null || saved.Snapshot.Accounts is null ||
            saved.Guard is null || saved.Tables is null || saved.Tables.Any(table => table is null) ||
            saved.Build.Route is null || saved.Build.Swap is null || saved.Build.Swap.Data is null ||
            saved.Build.Swap.Accounts is null || saved.Build.Setup is null || saved.Build.Compute is null ||
            saved.Build.LookupAddresses is null || saved.Source is null || saved.Destination is null || saved.ExecutionKey is null)
            throw new InvalidDataException("Jupiter recovery metadata omits required plan evidence.");
        var pins = JupiterAcceptedQuote.Read(terms);
        if (!JupiterAsset(terms.InputAsset, out var inputMint) || !JupiterAsset(terms.OutputAsset, out var outputMint) ||
            saved.Build.InputMint != inputMint || saved.Build.OutputMint != outputMint ||
            saved.Version != 1 || CanonicalJson.Hash(pins.Bind(saved.Build, terms)) != CanonicalJson.Hash(saved.Build) ||
            saved.Recovery.Genesis != ExpectedGenesis || saved.Recovery.IntentHash != CanonicalJson.Hash(terms) ||
            saved.Recovery.ContextSlot < 0 || saved.Recovery.LastValidBlockHeight <= 0 ||
            saved.Snapshot.Slot < (ulong)saved.Recovery.ContextSlot ||
            saved.Guard.Program != pins.GuardProgram || saved.Guard.CodeHash != pins.GuardHash || saved.Guard.RentBase64 != pins.RentBase64 ||
            pins.ExecutionKey != saved.ExecutionKey ||
            saved.NetworkFee != plan.ReservedNetworkFee || saved.ReservedRent != plan.ReservedLockedBalance ||
            !UInt64Text.TryParse(saved.NetworkFee, out var fee) || fee > UInt64Text.Parse(terms.Budget.MaximumNetworkFee) ||
            !UInt64Text.TryParse(saved.ReservedRent, out var reserved) || reserved > UInt64Text.Parse(terms.Budget.MaximumLockedBalance))
            throw new InvalidDataException("Jupiter recovery evidence differs from the accepted protocol and budget.");
        var state = ValidateJupiterState(saved.Build, terms.Owner.Address, saved.Source, saved.Destination,
            saved.Snapshot, saved.Tables, saved.Guard, pins.JupiterHash, pins.WhirlpoolHash);
        var compiled = CompileJupiter(terms, saved.Build, saved.Recovery, saved.ExecutionKey,
            pins.ComputeUnits, pins.ComputePrice, pins.TokenRent, state, plan.PreparationKey);
        var retained = state.MissingOwnerAccounts.Count(address =>
            !(terms.InputAsset.Native && address == state.Source) && !(terms.OutputAsset.Native && address == state.Destination));
        var requiredEffects = state.MissingOwnerAccounts
            .Where(address => !(terms.InputAsset.Native && address == state.Source) && !(terms.OutputAsset.Native && address == state.Destination))
            .Select(address => new DexEffectDisclosure("CreateAta", address == state.Source ? inputMint : outputMint,
                pins.TokenRent.ToString(CultureInfo.InvariantCulture), terms.Owner.Address, address, true)).ToList();
        if (compiled.NativeAccount is { } native)
            requiredEffects.Add(new("TemporaryWrappedSol", JupiterNativeAssetId, pins.TokenRent.ToString(CultureInfo.InvariantCulture),
                terms.Owner.Address, native.Address, false));
        if (terms.Prerequisites is null || requiredEffects.Any(effect => !terms.Prerequisites.Contains(effect)) ||
            state.InputMint.Decimals != terms.InputAsset.Decimals || state.OutputMint.Decimals != terms.OutputAsset.Decimals ||
            reserved != checked(pins.TokenRent * (ulong)(retained + (compiled.NativeAccount is null ? 0 : 1))) ||
            plan.UnsignedPayloadJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(Convert.ToBase64String(compiled.Message))))
            throw new InvalidDataException("Jupiter saved message or reservation differs from its reconstructed plan.");
        return (saved, compiled);
    }
}

