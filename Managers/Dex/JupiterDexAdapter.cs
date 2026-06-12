using System.Net.Http.Json;
using System.Text.Json;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers.Dex;

/// <summary>
/// Jupiter v2 (Solana) DEX adapter. Mirrors the SDK's <c>JupiterAdapter</c>
/// (<c>sdk/oasis-wallet/src/dex/jupiter.ts</c>).
///
/// Quoting calls Jupiter's <c>/v2/quote</c>; execution calls <c>/v2/swap</c>
/// with the cached raw quote body. The <see cref="HttpClient"/> is injected as
/// a typed client so its timeout + User-Agent are configured in Program.cs
/// (the config that previously lived on the SwapManager typed client). API
/// key / base URL are read from <see cref="IConfiguration"/>.
/// </summary>
public class JupiterDexAdapter : IDexAdapter
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<JupiterDexAdapter> _logger;

    public string Chain => "solana";

    public JupiterDexAdapter(HttpClient httpClient, IConfiguration config, ILogger<JupiterDexAdapter> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<OASISResult<DexQuote>> GetQuoteAsync(SwapQuoteRequest req)
    {
        var apiKey = _config.GetValue<string>("Blockchain:Jupiter:ApiKey") ?? "";
        var baseUrl = _config.GetValue<string>("Blockchain:Jupiter:BaseUrl") ?? "https://quote-api.jup.ag";

        var url = $"{baseUrl}/v2/quote?" +
                  $"inputMint={Uri.EscapeDataString(req.TokenIn)}" +
                  $"&outputMint={Uri.EscapeDataString(req.TokenOut)}" +
                  $"&amount={req.AmountIn}" +
                  $"&slippageBps={req.SlippageBps}" +
                  $"&mode=ExactIn";

        using var requestMsg = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            requestMsg.Headers.Add("x-api-key", apiKey);

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

            var quote = new SwapQuoteResponse
            {
                Chain = "solana",
                TokenIn = req.TokenIn,
                TokenOut = req.TokenOut,
                AmountIn = root.GetProperty("inAmount").GetString() ?? req.AmountIn,
                ExpectedAmountOut = outAmount,
                PriceImpact = root.TryGetProperty("priceImpactPct", out var pip) ? pip.GetDouble() : 0,
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
                    PriceImpact = 0,
                    Fee = "0",
                    Unavailable = true,
                    UnavailableReason = "upstream_unreachable",
                    Message = $"Jupiter (quote-api.jup.ag) unreachable from this network: {ex.Message}"
                },
                CachePayload = string.Empty
            }, "Jupiter upstream unreachable (env condition; not a server fault).");
        }
    }

    public async Task<OASISResult<SwapQuoteResponse>> BuildSwapTransactionAsync(
        SwapExecuteRequest req, string cachedQuotePayload)
    {
        // SwapManager has already validated the request and resolved the cached
        // raw Jupiter quote body (passed here as cachedQuotePayload).
        var cachedRaw = cachedQuotePayload;

        var apiKey = _config.GetValue<string>("Blockchain:Jupiter:ApiKey") ?? "";
        var baseUrl = _config.GetValue<string>("Blockchain:Jupiter:BaseUrl") ?? "https://quote-api.jup.ag";

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

        var url = $"{baseUrl}/v2/swap";
        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(swapPayload)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            requestMsg.Headers.Add("x-api-key", apiKey);

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

    // ─── Result helpers ───

    private static OASISResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private static OASISResult<T> Error<T>(string message, Exception? ex = null)
        => new() { IsError = true, Message = message, Exception = ex };
}
