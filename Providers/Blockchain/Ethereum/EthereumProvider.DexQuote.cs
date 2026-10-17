using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private sealed record DexEvmToken(string Address, string CodeHash, int Decimals, string SourceRevision);
    private sealed record DexEvmCosts(string ResetGas, string ApproveGas, string SwapGas, string CleanupGas,
        string PriorityFee, string MaxFee);
    private sealed record DexEvmQuoteEvidence(int Version, DexEvmDeployment Deployment, string BlockHash, string BlockNumber,
        string ChainTime, string ExecutionDeadline, string Pool, DexEvmToken Input, DexEvmToken Output, string Allowance, DexEvmCosts Costs);

    /// <inheritdoc cref="AZOA.WebAPI.Interfaces.IDexTransactionModule.QuoteSwapAsync"/>
    public async Task<AZOAResult<DexQuoteTerms>> QuoteSwapAsync(DexQuoteTerms requested, CancellationToken ct = default)
    {
        DexEvmDeployment deployment;
        try { deployment = ReadDexDeployment(); }
        catch (InvalidDataException) { return DexUnavailable("A complete reviewed Ethereum DEX deployment is not configured."); }
        if (ReadDexPolicyKey(deployment) is null) return DexUnavailable("The independent configured DEX policy key version is unavailable.");
        try { return await QuoteEthereumDexAsync(requested, deployment, ct); }
        catch (HttpRequestException) { return DexUnavailable("Ethereum quote data is temporarily unavailable."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DexUnavailable("Ethereum quote data timed out."); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return AZOAResult<DexQuoteTerms>.FailureWithCode("Ethereum quote evidence differs from the reviewed protocol.", "InvalidProviderEvidence"); }
    }

    private async Task<AZOAResult<DexQuoteTerms>> QuoteEthereumDexAsync(DexQuoteTerms terms, DexEvmDeployment deployment, CancellationToken ct)
    {
        if (terms is null || terms.Version != 1 || terms.Network is null || terms.Owner is null || terms.Payer is null ||
            terms.InputAsset is null || terms.OutputAsset is null || terms.PlatformFee is null || terms.Budget is null ||
            terms.Network.ChainType != ChainType || terms.Network.Network != ActiveNetwork.ToString() || terms.Owner != terms.Payer ||
            terms.Owner.UserId != terms.ActorUserId || !EthereumAddress.IsNonZero(terms.Owner.Address) ||
            !EthereumAddress.AreSame(terms.RecipientAddress, terms.Owner.Address) || terms.InputAsset.Native && terms.OutputAsset.Native ||
            terms.SlippageBps is < 0 or >= 10000 || terms.PlatformFee.Denomination != NodeFeeSchedule.InputBaseUnitsV1 || terms.PlatformFee.ScheduleVersion <= 0)
            return AZOAResult<DexQuoteTerms>.Failure("Ethereum quote requires exact owner, network, input-unit fee policy and slippage consent.");
        var gross = EthereumDexExecutionCodec.Amount(terms.GrossInput); var fee = EthereumDexExecutionCodec.Amount(terms.PlatformFee.Amount);
        var input = EthereumDexExecutionCodec.Amount(terms.SwapInput);
        if (input == 0 || input + fee != gross || gross >= (BigInteger.One << 256) ||
            fee > 0 && (!EthereumAddress.IsNonZero(terms.PlatformFee.TreasuryAddress) || EthereumAddress.AreSame(terms.PlatformFee.TreasuryAddress, terms.Owner.Address)))
            return AZOAResult<DexQuoteTerms>.Failure("Ethereum fee and exact input do not match the accepted gross amount.");
        var snapshot = await ReadDexSnapshotAsync(deployment, ct);
        if (!CurrentDexTime(snapshot.ChainTime)) return DexUnavailable("The finalized Ethereum head is outside the configured lag/skew policy.");
        if (terms.PlatformFee.PolicyKeyVersion != deployment.PolicyKeyVersion[2..].ToLowerInvariant())
            return DexUnavailable("The server fee policy and deployed independent permit key version do not match.");
        if (terms.Network.ProtocolIdentity != snapshot.ProtocolIdentity)
            return AZOAResult<DexQuoteTerms>.Failure("Ethereum quote belongs to another physical network.");
        var inputToken = await ReadDexTokenAsync(terms.InputAsset, snapshot, ct);
        var outputToken = await ReadDexTokenAsync(terms.OutputAsset, snapshot, ct);
        if (inputToken is null || outputToken is null) return DexUnavailable("One selected token lacks a reviewed immutable ERC20 policy.");
        if (EthereumAddress.AreSame(inputToken.Address, outputToken.Address))
            return AZOAResult<DexQuoteTerms>.Failure("Ethereum input and output tokens must differ.");
        var pool = await ReadDexAddressAsync(deployment.Factory, "getPool(address,address,uint24)",
            [inputToken.Address, outputToken.Address, deployment.PoolFee], snapshot.Block, ct);
        if (!EthereumAddress.IsNonZero(pool)) return DexUnavailable("No reviewed pool exists for this pair and fee tier.");
        if (!EthereumAddress.AreSame(await ReadDexAddressAsync(pool, "factory()", [], snapshot.Block, ct), deployment.Factory) ||
            await ReadUInt256AtBlockAsync(pool, "fee()", [], snapshot.Block, ct) != deployment.PoolFee)
            throw new InvalidDataException("Pool immutable factory or fee identity differs.");
        var token0 = await ReadDexAddressAsync(pool, "token0()", [], snapshot.Block, ct);
        var token1 = await ReadDexAddressAsync(pool, "token1()", [], snapshot.Block, ct);
        if (!(EthereumAddress.AreSame(token0, inputToken.Address) && EthereumAddress.AreSame(token1, outputToken.Address) ||
            EthereumAddress.AreSame(token1, inputToken.Address) && EthereumAddress.AreSame(token0, outputToken.Address)))
            throw new InvalidDataException("Pool immutable token pair differs.");
        var output = await ReadUInt256AtBlockAsync(deployment.Quoter, "quoteExactInputSingle(address,address,uint24,uint256,uint160)",
            [inputToken.Address, outputToken.Address, deployment.PoolFee, input, BigInteger.Zero], snapshot.Block, ct);
        var minimum = output * (10000 - terms.SlippageBps) / 10000;
        if (output == 0 || minimum == 0) return DexUnavailable("The pool cannot quote a positive output under the selected floor.");
        var allowance = terms.InputAsset.Native ? BigInteger.Zero : await ReadUInt256AtBlockAsync(inputToken.Address,
            "allowance(address,address)", [terms.Owner.Address, deployment.Executor], snapshot.Block, ct);
        var costs = await ReadDexCostsAsync(ct);
        if (costs is null) return DexUnavailable("Reviewed stage gas ceilings or current fee data are unavailable.");
        if (!int.TryParse(_config[$"AZOA:Ethereum:Dex:Networks:{ActiveNetwork}:ExecutionLifetimeSeconds"],
                NumberStyles.None, CultureInfo.InvariantCulture, out var lifetime) || lifetime is < 300 or > 86400)
            return DexUnavailable("An explicit bounded executor permit lifetime is not configured.");
        var networkCap = EthereumDexExecutionCodec.Amount(terms.Budget.MaximumNetworkFee);
        var cleanupCap = EthereumDexExecutionCodec.Amount(terms.Budget.MaximumCleanupFee);
        var gas = EthereumDexExecutionCodec.Amount(costs.SwapGas);
        var prerequisites = new List<DexEffectDisclosure>();
        if (!terms.InputAsset.Native && allowance != gross)
        {
            if (allowance != 0)
            {
                gas += EthereumDexExecutionCodec.Amount(costs.ResetGas);
                prerequisites.Add(new("ResetAllowance", inputToken.Address, "0", terms.Owner.Address, deployment.Executor, true));
            }
            gas += EthereumDexExecutionCodec.Amount(costs.ApproveGas);
            prerequisites.Add(new("ApproveExact", inputToken.Address, terms.GrossInput, terms.Owner.Address, deployment.Executor, true));
        }
        if (gas * EthereumDexExecutionCodec.Amount(costs.MaxFee) > networkCap ||
            !terms.InputAsset.Native && EthereumDexExecutionCodec.Amount(costs.CleanupGas) * EthereumDexExecutionCodec.Amount(costs.MaxFee) > cleanupCap)
            return DexUnavailable("The accepted fee caps do not cover the conservative prerequisite and execution ceilings.");
        var balanceResponse = await RpcReadAsync("eth_getBalance", [terms.Owner.Address, snapshot.Block], ct);
        if (balanceResponse.IsError || !balanceResponse.TryGetString(out var balanceText) || !EthereumHex.TryParseQuantity(balanceText, out var balance) ||
            balance < networkCap + cleanupCap + (terms.InputAsset.Native ? gross : 0))
            return DexUnavailable("The owner lacks the native balance reserved for exact input and protocol costs.");
        if (!terms.InputAsset.Native && await ReadUInt256AtBlockAsync(inputToken.Address, "balanceOf(address)", [terms.Owner.Address], snapshot.Block, ct) < gross)
            return DexUnavailable("The owner lacks the exact gross input balance.");
        var evidence = new DexEvmQuoteEvidence(1, deployment, snapshot.BlockHash, Text(snapshot.BlockNumber), Text(snapshot.ChainTime), Text(snapshot.ChainTime + lifetime),
            pool, inputToken, outputToken, Text(allowance), costs);
        return AZOAResult<DexQuoteTerms>.Success(terms with
        {
            Provider = "UniswapV3", ProtocolVersion = "classic-exact-input/1", ExpectedOutput = Text(output), MinimumOutput = Text(minimum),
            ProtocolEvidence = JsonSerializer.SerializeToElement(evidence), Prerequisites = prerequisites,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60)
        });
    }

    private async Task<DexEvmToken?> ReadDexTokenAsync(DexAsset asset, DexEvmSnapshot snapshot, CancellationToken ct)
    {
        var address = asset.Native && asset.Id == "ETH" ? snapshot.Deployment.WrappedNative :
            !asset.Native && EthereumAddress.TryParse(asset.Id, out var parsed) && EthereumAddress.IsNonZero(parsed) ? parsed
            : throw new InvalidDataException("Ethereum asset identity is not canonical native ETH or an explicit token.");
        var prefix = $"AZOA:Ethereum:Dex:Networks:{ActiveNetwork}:Tokens:{address.ToLowerInvariant()}:";
        var hash = asset.Native ? snapshot.Deployment.WrappedHash : _config[prefix + "CodeHash"] ?? string.Empty;
        var revision = asset.Native ? _config[$"AZOA:Ethereum:Dex:Networks:{ActiveNetwork}:WrappedNativeSourceRevision"] : _config[prefix + "SourceRevision"];
        if (!IsDexHash(hash) || string.IsNullOrWhiteSpace(revision) || !asset.Native && _config[prefix + "Standard"] != "ERC20Immutable")
            return null;
        await RequireCodeAtBlockAsync(address, hash, snapshot.Block, ct);
        var decimals = await ReadUInt256AtBlockAsync(address, "decimals()", [], snapshot.Block, ct);
        if (decimals > 255 || decimals != asset.Decimals || asset.Native && decimals != 18)
            throw new InvalidDataException("Actual token decimals differ from the input denomination.");
        return new(address, hash, (int)decimals, revision);
    }

    private async Task<DexEvmCosts?> ReadDexCostsAsync(CancellationToken ct)
    {
        var prefix = $"AZOA:Ethereum:Dex:Networks:{ActiveNetwork}:";
        string? Gas(string name)
        {
            var text = _config[prefix + name] ?? string.Empty;
            if (!UnsignedIntegerText.TryParse(text, out var amount) || amount < 21000 || amount > 30_000_000) return null;
            return text;
        }
        var reset = Gas("ResetGasLimit"); var approve = Gas("ApproveGasLimit"); var swap = Gas("SwapGasLimit"); var cleanup = Gas("CleanupGasLimit");
        if (reset is null || approve is null || swap is null || cleanup is null) return null;
        var fees = await GetFeeDataAsync(ct);
        if (fees.IsError || fees.Result is null) return null;
        return new(reset, approve, swap, cleanup,
            Text(fees.Result.MaxPriorityFeePerGas), Text(fees.Result.MaxFeePerGas));
    }

    private static string Text(BigInteger amount) => amount.ToString(CultureInfo.InvariantCulture);
    private static AZOAResult<DexQuoteTerms> DexUnavailable(string detail)
        => AZOAResult<DexQuoteTerms>.FailureWithCode(detail, AzoaErrorCodes.DependencyUnavailable);
}
