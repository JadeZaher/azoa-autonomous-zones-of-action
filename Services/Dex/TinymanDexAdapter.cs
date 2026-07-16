using System.Numerics;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>
/// Tinyman V2 (Algorand) DEX adapter. Mirrors the SDK's
/// <c>TinymanAdapter</c> (<c>sdk/azoa-wallet/src/dex/tinyman.ts</c>).
///
/// Quoting uses the constant-product AMM against real pool reserves read from
/// Algod; the pool account is derived deterministically via
/// <see cref="TinymanV2PoolLocator"/> (no indexer scan / pair→pool registry).
/// All network selection and node URLs come from <see cref="IConfiguration"/>.
/// </summary>
public class TinymanDexAdapter : IDexAdapter
{
    private readonly IConfiguration _config;
    private readonly ILogger<TinymanDexAdapter> _logger;
    private readonly HttpMessageHandler? _httpMessageHandler;

    public string Chain => "algorand";

    public TinymanDexAdapter(
        IConfiguration config,
        ILogger<TinymanDexAdapter> logger,
        HttpMessageHandler? httpMessageHandler = null)
    {
        _config = config;
        _logger = logger;
        _httpMessageHandler = httpMessageHandler;
    }

    public async Task<AZOAResult<DexQuote>> GetQuoteAsync(SwapQuoteRequest req)
    {
        var feeBps = 30; // Tinyman V2 0.3%

        if (!uint.TryParse(req.TokenIn, out var asset1Id))
            return Error<DexQuote>($"Invalid Algorand asset ID for TokenIn: {req.TokenIn}");
        if (!uint.TryParse(req.TokenOut, out var asset2Id))
            return Error<DexQuote>($"Invalid Algorand asset ID for TokenOut: {req.TokenOut}");
        if (!ulong.TryParse(req.AmountIn, out var amountInMicro))
            return Error<DexQuote>($"Invalid amount: {req.AmountIn}");

        if (req.SlippageBps is < 0 or > 10000)
            return Error<DexQuote>($"SlippageBps must be between 0 and 10000: {req.SlippageBps}");

        try
        {
            // Fetch real pool reserves from Algod
            var algodUrl = GetAlgodUrl();
            var (reserveIn, reserveOut) = await FetchTinymanPoolReservesAsync(algodUrl, asset1Id, asset2Id);

            if (reserveIn == 0 || reserveOut == 0)
            {
                return Ok(new DexQuote
                {
                    Quote = new SwapQuoteResponse
                    {
                        Chain = "algorand",
                        TokenIn = req.TokenIn,
                        TokenOut = req.TokenOut,
                        AmountIn = req.AmountIn,
                        ExpectedAmountOut = "0",
                        MinAmountOut = "0",
                        PriceImpact = 0,
                        Fee = "0",
                        Unavailable = true,
                        UnavailableReason = "no_pool",
                        Message = $"No Tinyman V2 pool for {asset1Id}/{asset2Id} on the configured Algorand network."
                    },
                    CachePayload = string.Empty
                }, $"No Tinyman V2 pool for {asset1Id}/{asset2Id} (env condition; not a server fault).");
            }

            // AMM Constant Product: x * y = k. Intermediate products
            // (amountIn * reserveOut) can exceed ulong range, so the whole
            // computation runs in UInt128 and the results are range-checked
            // before narrowing back to ulong.
            UInt128 amountIn128 = amountInMicro;
            UInt128 reserveIn128 = reserveIn;
            UInt128 reserveOut128 = reserveOut;
            UInt128 feeMultiplier = 10000UL - (uint)feeBps;

            var amountInWithFee = amountIn128 * feeMultiplier / 10000UL;

            var numerator = amountInWithFee * reserveOut128;
            var denominator = reserveIn128 + amountInWithFee;
            var amountOut128 = numerator / denominator;

            // Apply slippage to derive the minimum acceptable output floor.
            UInt128 slippageMultiplier = 10000UL - (uint)req.SlippageBps;
            var minAmountOut128 = amountOut128 * slippageMultiplier / 10000UL;

            // The 30bps fee actually charged on the input (input minus the
            // post-fee input), not the prior ~0 round-trip remainder.
            var feeMicro128 = amountIn128 - amountInWithFee;

            // Price impact uses the pre-fee marginal vs effective rate; the
            // products are UInt128 to avoid overflow, then narrowed to double.
            var idealOut = amountIn128 * reserveOut128;
            var effectiveOut = reserveIn128 * amountOut128;
            var priceImpactPct = (denominator > 0 && effectiveOut > 0)
                ? Math.Abs(((double)idealOut - (double)effectiveOut) / (double)effectiveOut * 100)
                : 0;

            if (amountOut128 > ulong.MaxValue || feeMicro128 > ulong.MaxValue || minAmountOut128 > ulong.MaxValue)
                return Error<DexQuote>("Swap amounts exceed supported range for this pool");

            var amountOut = (ulong)amountOut128;
            var minAmountOut = (ulong)minAmountOut128;
            var feeMicro = (ulong)feeMicro128;

            var quote = new SwapQuoteResponse
            {
                Chain = "algorand",
                TokenIn = req.TokenIn,
                TokenOut = req.TokenOut,
                AmountIn = req.AmountIn,
                ExpectedAmountOut = amountOut.ToString(),
                MinAmountOut = minAmountOut.ToString(),
                PriceImpact = priceImpactPct,
                Fee = feeMicro.ToString(),
                Raw = new
                {
                    poolReserveIn = reserveIn.ToString(),
                    poolReserveOut = reserveOut.ToString(),
                    amountInWithFee = amountInWithFee.ToString(),
                    amountOut = amountOut.ToString(),
                    minAmountOut = minAmountOut.ToString(),
                    priceImpactPct,
                    feeBps,
                    method = "tinyman_v2_constant_product_amm"
                }
            };

            // Pool info for execution — cached by SwapManager under the QuoteId.
            var cachePayload = JsonSerializer.Serialize(new
            {
                asset1Id,
                asset2Id,
                asset1IsAlgo = asset1Id == 0,
                asset2IsAlgo = asset2Id == 0
            });

            return Ok(new DexQuote { Quote = quote, CachePayload = cachePayload },
                $"Tinyman quote: {quote.ExpectedAmountOut}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tinyman quote failed for {Asset1}/{Asset2}", asset1Id, asset2Id);
            return Error<DexQuote>($"Tinyman quote error: {ex.Message}", ex);
        }
    }

    public Task<AZOAResult<SwapQuoteResponse>> BuildSwapTransactionAsync(
        SwapExecuteRequest req, string cachedQuotePayload)
    {
        // Tinyman V2 requires client-side application call transactions (app opt-in + swap).
        // We provide the pool info for the client to construct the tx group.
        // SwapManager has already validated the request and resolved the cached
        // quote payload (passed as cachedQuotePayload) before delegating here.
        var result = new SwapQuoteResponse
        {
            Chain = "algorand",
            QuoteId = req.QuoteId,
            TokenIn = req.QuoteId, // used as identifier
            TokenOut = req.WalletAddress,
            Message = "Tinyman V2 swap requires client-side ABI-compatible tx group construction. " +
                      "Use the pool reserves from the quote response with algosdk to build: " +
                      "1) app_opt_in (if not opted in), 2) swap (app call to Tinyman pool), 3) fee payment. " +
                      "See: https://docs.tinyman.org/developer/contracts/v2"
        };

        return Task.FromResult(Ok(result, "Tinyman swap parameters ready for client-side construction"));
    }

    // ─── Tinyman Pool Reserves via Algod ───

    private async Task<(ulong reserveIn, ulong reserveOut)> FetchTinymanPoolReservesAsync(
        string algodUrl, uint assetInId, uint assetOutId)
    {
        // Tinyman V2 pools are stateless logicsig accounts whose address is
        // fully derived from the validator app id + the sorted asset pair —
        // there is no indexer scan or pair→pool registry (the prior approach
        // wrongly assumed the validator app held such a registry). We compute
        // the pool address, then read its reserves from its local state under
        // the validator app. Tinyman keys reserves by sorted order:
        // asset_1 = max(assetIn, assetOut), asset_2 = min.
        var validatorAppId = GetTinymanValidatorAppId();
        var poolAddress = TinymanV2PoolLocator.GetPoolAddress(validatorAppId, assetInId, assetOutId);

        using var client = CreateAlgodClient(algodUrl);
        client.DefaultRequestHeaders.Add("User-Agent", "AZOA-SwapManager/1.0");

        string response;
        try
        {
            response = await client.GetStringAsync($"/v2/accounts/{poolAddress}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Tinyman pool account {Pool} not found on Algod", poolAddress);
            return (0, 0);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Tinyman pool account {Pool} lookup timed out on Algod", poolAddress);
            return (0, 0);
        }

        using var doc = JsonDocument.Parse(response);
        if (!doc.RootElement.TryGetProperty("apps-local-state", out var appsLocalState))
            return (0, 0);

        JsonElement? poolState = null;
        foreach (var appState in appsLocalState.EnumerateArray())
        {
            if (appState.TryGetProperty("id", out var idProp) &&
                idProp.GetUInt64() == validatorAppId)
            {
                poolState = appState;
                break;
            }
        }

        if (poolState is null || !poolState.Value.TryGetProperty("key-value", out var kvs))
            return (0, 0);

        ulong asset1Reserves = 0, asset2Reserves = 0;
        foreach (var kv in kvs.EnumerateArray())
        {
            var keyName = System.Text.Encoding.ASCII.GetString(
                Convert.FromBase64String(kv.GetProperty("key").GetString() ?? ""));
            if (keyName is not ("asset_1_reserves" or "asset_2_reserves"))
                continue;

            var v = kv.GetProperty("value");
            ulong amount = v.GetProperty("type").GetInt32() == 1
                ? AZOA.WebAPI.Helpers.Encoding.ReadBigEndianU64Loose(Convert.FromBase64String(v.GetProperty("bytes").GetString() ?? ""))
                : v.GetProperty("uint").GetUInt64();

            if (keyName == "asset_1_reserves") asset1Reserves = amount;
            else asset2Reserves = amount;
        }

        if (asset1Reserves == 0 || asset2Reserves == 0)
            return (0, 0);

        _logger.LogInformation(
            "Tinyman pool {Pool}: asset_1_reserves={R1} asset_2_reserves={R2}",
            poolAddress, asset1Reserves, asset2Reserves);

        // asset_1 = max(in,out), asset_2 = min — map back to caller's (in,out).
        bool inIsAsset1 = Math.Max(assetInId, assetOutId) == assetInId;
        return inIsAsset1
            ? (asset1Reserves, asset2Reserves)
            : (asset2Reserves, asset1Reserves);
    }


    // ─── Configuration Helpers ───

    private HttpClient CreateAlgodClient(string algodUrl)
    {
        var client = _httpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(_httpMessageHandler, disposeHandler: false);

        client.BaseAddress = new Uri(algodUrl);
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    private string GetAlgodUrl()
    {
        var chains = _config.GetSection("Blockchain:Chains").Get<List<BlockchainChainConfig>>()
                     ?? new List<BlockchainChainConfig>();
        var algo = chains.FirstOrDefault(c =>
            c.ChainType.Equals("Algorand", StringComparison.OrdinalIgnoreCase));

        var defaultNetwork = _config.GetValue<string>("Blockchain:DefaultNetwork") ?? "Devnet";
        var networkConfig = defaultNetwork.ToLowerInvariant() switch
        {
            "mainnet" => algo?.Mainnet,
            "testnet" => algo?.Testnet,
            _ => algo?.Devnet
        };

        return networkConfig?.NodeUrl ?? "https://testnet-api.algonode.cloud";
    }

    /// <summary>
    /// Config-driven Tinyman V2 validator (factory) app id: mainnet uses the
    /// mainnet contract, every other network (testnet/devnet → testnet
    /// endpoints) uses the testnet one.
    /// </summary>
    private ulong GetTinymanValidatorAppId()
    {
        var network = _config.GetValue<string>("Blockchain:DefaultNetwork") ?? "Devnet";
        return network.Equals("Mainnet", StringComparison.OrdinalIgnoreCase)
            ? TinymanV2PoolLocator.MainnetValidatorAppId
            : TinymanV2PoolLocator.TestnetValidatorAppId;
    }

    // ─── Result helpers ───

    private static AZOAResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private static AZOAResult<T> Error<T>(string message, Exception? ex = null)
        => new() { IsError = true, Message = message, Exception = ex };
}
