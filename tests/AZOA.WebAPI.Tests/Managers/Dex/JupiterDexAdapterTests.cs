using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Managers.Dex;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>
/// Offline unit tests for <see cref="JupiterDexAdapter"/> using a stub
/// HttpMessageHandler — no network. Verifies the Jupiter v2 quote/swap
/// mapping and that the adapter returns the raw body as the opaque cache
/// payload without assigning a QuoteId (SwapManager owns that).
/// </summary>
public class JupiterDexAdapterTests
{
    // Base URL ends in /v2 so the adapter's "{BaseUrl}/quote" and "{BaseUrl}/swap"
    // routes contain "/v2/quote" and "/v2/swap" — what RoutingHandler keys off.
    private static IOptions<JupiterConfig> Config() =>
        Options.Create(new JupiterConfig { BaseUrl = "https://api.jup.ag/swap/v2" });

    private static JupiterDexAdapter Build(RoutingHandler handler) =>
        new(new HttpClient(handler), Config(),
            new LoggerFactory().CreateLogger<JupiterDexAdapter>());

    [Fact]
    public void Chain_IsSolana()
    {
        Build(new RoutingHandler()).Chain.Should().Be("solana");
    }

    [Fact]
    public async Task GetQuoteAsync_Success_MapsResponse_AndReturnsRawAsCachePayload()
    {
        const string body =
            "{\"inAmount\":\"1000000000\",\"outAmount\":\"24500000\"," +
            "\"priceImpactPct\":0.12,\"routePlan\":[{\"swapInfo\":{\"label\":\"Orca\"}}]}";
        var adapter = Build(new RoutingHandler
        {
            QuoteResponse = (HttpStatusCode.OK, body)
        });

        var result = await adapter.GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "solana",
            TokenIn = "So11111111111111111111111111111111111111112",
            TokenOut = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v",
            AmountIn = "1000000000",
            SlippageBps = 50
        });

        result.IsError.Should().BeFalse(result.Message);
        var dq = result.Result!;
        dq.Quote.Chain.Should().Be("solana");
        dq.Quote.AmountIn.Should().Be("1000000000");
        dq.Quote.ExpectedAmountOut.Should().Be("24500000");
        dq.Quote.PriceImpact.Should().Be(0.12);
        dq.Quote.Fee.Should().Be("0");
        dq.Quote.Route.Should().NotBeNull();
        // Adapter must NOT assign a QuoteId — that is SwapManager's job.
        dq.Quote.QuoteId.Should().BeNull();
        // The opaque cache payload is the raw response body verbatim.
        dq.CachePayload.Should().Be(body);
        result.Message.Should().Be("Jupiter quote: 24500000");
    }

    [Fact]
    public async Task GetQuoteAsync_NonSuccess_ReturnsError()
    {
        var adapter = Build(new RoutingHandler
        {
            QuoteResponse = (HttpStatusCode.BadRequest, "no route")
        });

        var result = await adapter.GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "solana", TokenIn = "A", TokenOut = "B", AmountIn = "1", SlippageBps = 50
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Jupiter API error");
    }

    [Fact]
    public async Task BuildSwapTransactionAsync_Success_ReturnsSwapTransaction()
    {
        const string swapBody =
            "{\"swapTransaction\":\"BASE64_TX\",\"lastValidBlockHeight\":234567890}";
        var adapter = Build(new RoutingHandler
        {
            SwapResponse = (HttpStatusCode.OK, swapBody)
        });

        // cachedQuotePayload is a previously cached raw Jupiter quote body.
        var result = await adapter.BuildSwapTransactionAsync(
            new SwapExecuteRequest { Chain = "solana", QuoteId = "qid", WalletAddress = "WALLET" },
            "{\"outAmount\":\"24500000\"}");

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Chain.Should().Be("solana");
        result.Result.QuoteId.Should().Be("qid");
        result.Result.SwapTransaction.Should().Be("BASE64_TX");
        result.Result.LastValidBlockHeight.Should().Be(234567890);
        result.Message.Should().Be("Swap transaction built — sign and submit via client-side wallet");
    }

    [Fact]
    public async Task BuildSwapTransactionAsync_NonSuccess_ReturnsError()
    {
        var adapter = Build(new RoutingHandler
        {
            SwapResponse = (HttpStatusCode.InternalServerError, "boom")
        });

        var result = await adapter.BuildSwapTransactionAsync(
            new SwapExecuteRequest { Chain = "solana", QuoteId = "qid", WalletAddress = "WALLET" },
            "{\"outAmount\":\"1\"}");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Jupiter swap build error");
    }

    /// <summary>Routes /v2/quote and /v2/swap to canned responses.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) QuoteResponse { get; set; }
            = (HttpStatusCode.OK, "{}");
        public (HttpStatusCode Status, string Body) SwapResponse { get; set; }
            = (HttpStatusCode.OK, "{}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (status, body) = path.Contains("/v2/swap")
                ? SwapResponse
                : QuoteResponse;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
