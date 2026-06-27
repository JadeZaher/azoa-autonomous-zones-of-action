using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>
/// Jupiter v2 (Solana) DEX adapter. Mirrors the SDK's <c>JupiterAdapter</c>
/// (<c>sdk/azoa-wallet/src/dex/jupiter.ts</c>).
///
/// Quoting calls Jupiter's <c>/quote</c>; execution calls <c>/swap</c>
/// with the cached raw quote body. The <see cref="HttpClient"/> is injected as
/// a typed client so its timeout + User-Agent are configured in Program.cs
/// (the config that previously lived on the SwapManager typed client). API
/// key / base URL are bound from <see cref="JupiterConfig"/> via
/// <see cref="IOptions{TOptions}"/>.
/// </summary>
public class JupiterDexAdapter : IDexAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<JupiterDexAdapter> _logger;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public string Chain => "solana";

    public JupiterDexAdapter(HttpClient httpClient, IOptions<JupiterConfig> config, ILogger<JupiterDexAdapter> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var cfg = config.Value;
        _apiKey = cfg.ApiKey ?? "";
        // Align with the SDK's JupiterAdapter (api.jup.ag/swap/v2 + /quote + /swap).
        _baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? "https://api.jup.ag/swap/v2" : cfg.BaseUrl;
    }

    public async Task<AZOAResult<DexQuote>> GetQuoteAsync(SwapQuoteRequest req)
    {
        var url = $"{_baseUrl}/quote?" +
                  $"inputMint={Uri.EscapeDataString(req.TokenIn)}" +
                  $"&outputMint={Uri.EscapeDataString(req.TokenOut)}" +
                  $"&amount={req.AmountIn}" +
                  $"&slippageBps={req.SlippageBps}" +
                  $"&mode=ExactIn";

        using var requestMsg = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            requestMsg.Headers.Add("x-api-key", _apiKey);

        try
        {
            var response = await _httpClient.SendAsync(requestMsg);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jupiter API {Status}: {Body}", (int)response.StatusCode, body);
                return Error<DexQuote>(
                    $"Jupiter API error ({response.StatusCode}): " +
                    $"{(string.IsNullOrWhiteSpace(body) ? "No route found — check token mints and liquidity" : body[..Math.Min(300, body.Length)])}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var outAmount = root.GetProperty("outAmount").GetString() ?? "0";

            // ExpectedAmountOut is the pre-slippage expected output (standardized
            // across adapters); MinAmountOut is the slippage floor. Jupiter
            // returns the floor as otherAmountThreshold; if absent, derive it
            // from the requested slippage so both fields are always populated.
            var minAmountOut = root.TryGetProperty("otherAmountThreshold", out var oat)
                ? oat.GetString() ?? Amounts.ComputeSlippageFloor(outAmount, req.SlippageBps)
                : Amounts.ComputeSlippageFloor(outAmount, req.SlippageBps);

            var quote = new SwapQuoteResponse
            {
                Chain = "solana",
                TokenIn = req.TokenIn,
                TokenOut = req.TokenOut,
                AmountIn = root.GetProperty("inAmount").GetString() ?? req.AmountIn,
                ExpectedAmountOut = outAmount,
                MinAmountOut = minAmountOut,
                PriceImpact = ParsePriceImpact(root),
                Fee = "0",
                Route = root.TryGetProperty("routePlan", out var rp) ? JsonSerializer.Deserialize<object>(rp.GetRawText()) : null,
                Raw = JsonSerializer.Deserialize<object>(body)
            };

            // Cache the raw quote response for later swap execution
            // (SwapManager owns the QuoteId + cache lifecycle).
            return Ok(new DexQuote { Quote = quote, CachePayload = body },
                $"Jupiter quote: {quote.ExpectedAmountOut}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Jupiter upstream unreachable");
            return Ok(new DexQuote
            {
                Quote = new SwapQuoteResponse
                {
                    Chain = "solana",
                    TokenIn = req.TokenIn,
                    TokenOut = req.TokenOut,
                    AmountIn = req.AmountIn,
                    ExpectedAmountOut = "0",
                    MinAmountOut = "0",
                    PriceImpact = 0,
                    Fee = "0",
                    Unavailable = true,
                    UnavailableReason = "upstream_unreachable",
                    Message = $"Jupiter ({_baseUrl}) unreachable from this network: {ex.Message}"
                },
                CachePayload = string.Empty
            }, "Jupiter upstream unreachable (env condition; not a server fault).");
        }
    }

    public async Task<AZOAResult<SwapQuoteResponse>> BuildSwapTransactionAsync(
        SwapExecuteRequest req, string cachedQuotePayload)
    {
        // SwapManager has already validated the request and resolved the cached
        // raw Jupiter quote body (passed here as cachedQuotePayload).
        var cachedRaw = cachedQuotePayload;

        // Parse the cached quote to build the swap request
        using var quoteDoc = JsonDocument.Parse(cachedRaw);

        var swapPayload = new
        {
            quoteResponse = JsonSerializer.Deserialize<object>(cachedRaw),
            userPublicKey = req.WalletAddress,
            wrapAndUnwrapSol = true,
            dynamicComputeUnitLimit = true,
            prioritizationFeeLamports = "auto"
        };

        var url = $"{_baseUrl}/swap";
        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(swapPayload)
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            requestMsg.Headers.Add("x-api-key", _apiKey);

        try
        {
            var response = await _httpClient.SendAsync(requestMsg);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jupiter /swap error {Status}: {Body}", (int)response.StatusCode, body);
                return Error<SwapQuoteResponse>($"Jupiter swap build error ({response.StatusCode}): {body[..Math.Min(300, body.Length)]}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var result = new SwapQuoteResponse
            {
                Chain = "solana",
                TokenIn = req.QuoteId,
                TokenOut = req.WalletAddress,
                AmountIn = "0",
                ExpectedAmountOut = "0",
                QuoteId = req.QuoteId,
                SwapTransaction = root.GetProperty("swapTransaction").GetString() ?? "",
                LastValidBlockHeight = root.TryGetProperty("lastValidBlockHeight", out var lvbh) ? lvbh.GetInt64() : null,
                Raw = JsonSerializer.Deserialize<object>(body)
            };

            _logger.LogInformation("Jupiter swap tx built for {Wallet}", req.WalletAddress);
            return Ok(result, "Swap transaction built — sign and submit via client-side wallet");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Jupiter /swap request failed");
            return Error<SwapQuoteResponse>($"Jupiter /swap unreachable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Jupiter returns <c>priceImpactPct</c> as a JSON STRING (not a number),
    /// so a bare GetDouble() throws. Tolerate string or number and parse with
    /// invariant culture; default to 0 when absent/unparseable.
    /// </summary>
    private static double ParsePriceImpact(JsonElement root)
    {
        if (!root.TryGetProperty("priceImpactPct", out var pip))
            return 0;

        return pip.ValueKind switch
        {
            JsonValueKind.Number => pip.GetDouble(),
            JsonValueKind.String when double.TryParse(
                pip.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        };
    }

    // ─── Result helpers ───

    private static AZOAResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private static AZOAResult<T> Error<T>(string message, Exception? ex = null)
        => new() { IsError = true, Message = message, Exception = ex };
}
