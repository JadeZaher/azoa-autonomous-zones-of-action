using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private const string JupiterNativeAssetId = "SOL";
    private sealed record JupiterQuoteAssembly(DexQuoteTerms Terms, JupiterBuildPlan Build, JupiterState State,
        DurableSolanaPlan Recovery, JupiterCompilation Compiled, string ExecutionKey, uint ComputeUnits,
        ulong ComputePrice, ulong TokenRent, ulong NetworkFee, ulong ReservedRent);

    /// <inheritdoc cref="AZOA.WebAPI.Interfaces.IDexTransactionModule.QuoteSwapAsync"/>
    public async Task<AZOAResult<DexQuoteTerms>> QuoteSwapAsync(DexQuoteTerms requested, CancellationToken ct = default)
    {
        try
        {
            var result = await BuildJupiterQuoteAsync(requested, ct);
            return result.IsError || result.Result is null
                ? AZOAResult<DexQuoteTerms>.FailureWithCode(result.Message, result.Code ?? AzoaErrorCodes.DependencyUnavailable)
                : AZOAResult<DexQuoteTerms>.Success(result.Result.Terms);
        }
        catch (HttpRequestException) { return JupiterUnavailable("Jupiter network data is temporarily unavailable."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return JupiterUnavailable("Jupiter network data timed out."); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException)
        { return AZOAResult<DexQuoteTerms>.FailureWithCode("Jupiter returned invalid or unreviewed protocol evidence.", "InvalidProviderEvidence"); }
    }

    private async Task<AZOAResult<JupiterQuoteAssembly>> BuildJupiterQuoteAsync(DexQuoteTerms requested, CancellationToken ct,
        bool retainAcceptedTerms = false, string? preparationIdentity = null)
    {
        if (requested is null || requested.Version != 1 || requested.Network is null || requested.Owner is null ||
            requested.Payer is null || requested.InputAsset is null || requested.OutputAsset is null || requested.PlatformFee is null ||
            requested.Budget is null || requested.Network.ChainType != ChainType || requested.Network.Network != ActiveNetwork.ToString() ||
            requested.Owner != requested.Payer || requested.Owner.UserId != requested.ActorUserId ||
            requested.RecipientAddress != requested.Owner.Address || !SolanaAddress.IsSigner(requested.Owner.Address) ||
            !JupiterAsset(requested.InputAsset, out var inputMint) || !JupiterAsset(requested.OutputAsset, out var outputMint) ||
            inputMint == outputMint || !UInt64Text.TryParse(requested.GrossInput, out var gross) ||
            !UInt64Text.TryParse(requested.SwapInput, out var input) || input == 0 ||
            !UInt64Text.TryParse(requested.PlatformFee.Amount, out var platformFee) || (UInt128)input + platformFee != gross ||
            requested.PlatformFee.Denomination != NodeFeeSchedule.InputBaseUnitsV1 ||
            platformFee > 0 && (!SolanaAddress.IsAccount(requested.PlatformFee.TreasuryAddress) || requested.PlatformFee.TreasuryAddress == requested.Owner.Address) ||
            requested.SlippageBps is < 0 or >= 10000 ||
            !UInt64Text.TryParse(requested.Budget.MaximumNetworkFee, out var networkCap) ||
            !UInt64Text.TryParse(requested.Budget.MaximumLockedBalance, out var rentCap) ||
            !UInt64Text.TryParse(requested.Budget.MaximumCleanupFee, out var cleanupCap))
            return AZOAResult<JupiterQuoteAssembly>.FailureWithCode("Invalid Jupiter owner, denomination or exact-input budget.", AzoaErrorCodes.InvalidRequest);
        var prefix = $"AZOA:Solana:Dex:Networks:{ActiveNetwork}:";
        if (!_config.GetValue<bool>(prefix + "Enabled") ||
            !uint.TryParse(_config[prefix + "MaximumComputeUnits"], NumberStyles.None, CultureInfo.InvariantCulture, out var computeUnits) ||
            computeUnits is 0 or > 1_400_000 || !UInt64Text.TryParse(_config[prefix + "MicroLamportsPerUnit"], out var computePrice) ||
            !IsNativeHash(_config[prefix + "JupiterProgramDataHash"]) || !IsNativeHash(_config[prefix + "WhirlpoolProgramDataHash"]) ||
            !IsNativeHash(_config[prefix + "CostGuardProgramDataHash"]) || !SolanaAddress.IsAccount(_config[prefix + "CostGuardProgram"]))
            return JupiterAssemblyUnavailable("Jupiter requires explicit network readiness, bounded compute policy and reviewed executable pins.");
        var genesis = await VerifyGenesisAsync(ct);
        if (requested.Network.ProtocolIdentity != "solana:" + genesis)
            return AZOAResult<JupiterQuoteAssembly>.FailureWithCode("Jupiter network genesis differs from the requested identity.", AzoaErrorCodes.InvalidRequest);
        var accepted = retainAcceptedTerms ? JupiterAcceptedQuote.Read(requested) : null;
        if (accepted is not null && (accepted.ComputeUnits != computeUnits || accepted.ComputePrice != computePrice))
            return JupiterAssemblyUnavailable("The accepted Jupiter compute policy is no longer configured.");
        var owner = new PublicKey(requested.Owner.Address);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, new PublicKey(inputMint)).Key;
        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, new PublicKey(outputMint)).Key;
        var built = await ReadJupiterBuildAsync(inputMint, outputMint, input, owner.Key, destination, requested.SlippageBps, ct);
        if (built.IsError || built.Result is not { } build)
            return AZOAResult<JupiterQuoteAssembly>.FailureWithCode(built.Message, built.Code ?? AzoaErrorCodes.DependencyUnavailable);
        if (accepted is not null) build = accepted.Bind(build, requested);
        var preparationKey = accepted?.ExecutionKey ?? "quote:" + Guid.NewGuid().ToString("N");
        var nativeAddress = requested.InputAsset.Native || requested.OutputAsset.Native
            ? JupiterNativeAccount.Create(owner.Key, preparationKey, 1, 0).Address : null;
        var treasuryAccount = platformFee > 0 && !requested.InputAsset.Native
            ? AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(requested.PlatformFee.TreasuryAddress), new PublicKey(inputMint)).Key : null;
        var extra = new[] { owner.Key, nativeAddress, treasuryAccount }.Where(value => value is not null).Select(value => value!).ToArray();
        var recovery = await NewPlanAsync(inputMint, 0, 0, false, CanonicalJson.Hash(requested), ct);
        var state = await ReadJupiterStateAsync(build, owner.Key, source, destination, extra, checked((ulong)recovery.ContextSlot), ct);
        if (accepted is not null && (accepted.JupiterHash != state.JupiterHash || accepted.WhirlpoolHash != state.WhirlpoolHash ||
            accepted.GuardProgram != state.Guard.Program || accepted.GuardHash != state.Guard.CodeHash ||
            accepted.RentBase64 != state.Guard.RentBase64))
            return JupiterAssemblyUnavailable("Current Jupiter executable or Rent evidence differs from the accepted quote.");
        if (state.InputMint.Decimals != requested.InputAsset.Decimals || state.OutputMint.Decimals != requested.OutputAsset.Decimals)
            return AZOAResult<JupiterQuoteAssembly>.FailureWithCode("Jupiter mint decimals differ from the requested denomination.", AzoaErrorCodes.InvalidRequest);
        if (!requested.InputAsset.Native && (!state.Holdings.TryGetValue(source, out var holding) || holding.Amount < gross))
            return JupiterAssemblyUnavailable("The owner lacks the exact gross input token balance.");
        if (nativeAddress is not null && state.Snapshot.Accounts[nativeAddress].ValueKind != JsonValueKind.Null)
            return JupiterAssemblyUnavailable("The operation-owned native account already exists; request a fresh quote.");
        if (treasuryAccount is not null)
        {
            var treasury = SolanaSplAssetCodec.ReadHolding(state.Snapshot.Owned(treasuryAccount, TokenProgram.ProgramIdKey.Key));
            if (treasury.Mint != inputMint || treasury.Owner != requested.PlatformFee.TreasuryAddress || treasury.Frozen)
                return JupiterAssemblyUnavailable("The configured treasury cannot receive the input token fee.");
        }
        var rent = WttSolanaCostGuard.MinimumBalance(Convert.FromBase64String(state.Guard.RentBase64), 165);
        if (accepted is not null && rent != accepted.TokenRent)
            return JupiterAssemblyUnavailable("Current account rent differs from the accepted quote.");
        var retainedCount = state.MissingOwnerAccounts.Count(address =>
            !(requested.InputAsset.Native && address == source) && !(requested.OutputAsset.Native && address == destination));
        var reservedRent = checked(rent * (ulong)(retainedCount + (nativeAddress is null ? 0 : 1)));
        if (reservedRent > rentCap) return JupiterAssemblyUnavailable("The authorized rent cap cannot cover the exact account prerequisites.");
        var terms = requested with
        {
            ExpectedOutput = build.OutputAmount.ToString(CultureInfo.InvariantCulture),
            MinimumOutput = build.MinimumOutput.ToString(CultureInfo.InvariantCulture), Provider = "Jupiter", ProtocolVersion = "6-whirlpool/1"
        };
        var compiled = CompileJupiter(terms, build, recovery, preparationKey, computeUnits, computePrice, rent, state, preparationIdentity);
        var fee = await NativeMessageFeeAsync(compiled.Message, ct);
        if (fee > networkCap) return JupiterAssemblyUnavailable("The exact compiled message exceeds the authorized network-fee cap.");
        var ownerAccount = state.Snapshot.Accounts[owner.Key];
        if (state.Snapshot.Owned(owner.Key, SystemProgram.ProgramIdKey.Key).Length != 0 ||
            !ownerAccount.TryGetProperty("lamports", out var lamports) || lamports.ValueKind != JsonValueKind.Number || !lamports.TryGetUInt64(out var balance) ||
            (UInt128)(requested.InputAsset.Native ? gross : 0) + networkCap + reservedRent + (retainedCount > 0 ? cleanupCap : 0) > balance)
            return JupiterAssemblyUnavailable("The owner lacks the SOL budget for exact input, fees, rent and retained-account cleanup.");
        if (await MeasureSolanaComputeAsync(compiled.Message, checked((long)state.Snapshot.Slot), ct) > computeUnits)
            return JupiterAssemblyUnavailable("The exact route exceeds its configured compute ceiling.");
        var prerequisites = state.MissingOwnerAccounts
            .Where(address => !(requested.InputAsset.Native && address == source) && !(requested.OutputAsset.Native && address == destination))
            .Select(address => new DexEffectDisclosure("CreateAta", address == source ? inputMint : outputMint,
                rent.ToString(CultureInfo.InvariantCulture), owner.Key, address, true)).ToList();
        if (nativeAddress is not null)
            prerequisites.Add(new("TemporaryWrappedSol", JupiterNativeAssetId, rent.ToString(CultureInfo.InvariantCulture), owner.Key, nativeAddress, false));
        if (accepted is not null)
        {
            if (requested.Prerequisites is null || prerequisites.Any(effect => !requested.Prerequisites.Contains(effect)))
                return JupiterAssemblyUnavailable("Preparation requires an account effect absent from the accepted quote.");
            return AZOAResult<JupiterQuoteAssembly>.Success(new(requested, build, state, recovery, compiled,
                preparationKey, computeUnits, computePrice, rent, fee, reservedRent));
        }
        var quotedTerms = terms with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30), Prerequisites = prerequisites,
            ProtocolEvidence = JsonSerializer.SerializeToElement(new
            {
                format = "jupiter-whirlpool/1", pool = build.Pool, sharedAccounts = build.Route.SharedAccounts,
                whirlpoolV2 = build.Route.WhirlpoolV2, aToB = build.Route.AToB,
                slot = state.Snapshot.Slot.ToString(CultureInfo.InvariantCulture), state.BuildHash,
                state.JupiterHash, state.WhirlpoolHash, guard = state.Guard,
                nativeAccountKey = nativeAddress is null ? null : preparationKey,
                executionKey = preparationKey,
                computeUnits, computePrice = computePrice.ToString(CultureInfo.InvariantCulture),
                networkFee = fee.ToString(CultureInfo.InvariantCulture), rent = rent.ToString(CultureInfo.InvariantCulture)
            })
        };
        return AZOAResult<JupiterQuoteAssembly>.Success(new(quotedTerms, build, state, recovery, compiled,
            preparationKey, computeUnits, computePrice, rent, fee, reservedRent));
    }

    private bool JupiterAsset(DexAsset asset, out string mint)
    {
        mint = asset.Native ? JupiterNativeAccount.Mint.Key : asset.Id;
        return asset.Native ? asset.Id == JupiterNativeAssetId && asset.Decimals == 9
            : SolanaAddress.IsAccount(asset.Id) && asset.Decimals is >= 0 and <= 255;
    }

    private static AZOAResult<DexQuoteTerms> JupiterUnavailable(string message)
        => AZOAResult<DexQuoteTerms>.FailureWithCode(message, AzoaErrorCodes.DependencyUnavailable);

    private static AZOAResult<JupiterQuoteAssembly> JupiterAssemblyUnavailable(string message)
        => AZOAResult<JupiterQuoteAssembly>.FailureWithCode(message, AzoaErrorCodes.DependencyUnavailable);
}

