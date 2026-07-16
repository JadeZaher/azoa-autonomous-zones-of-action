using System.Net;
using FluentAssertions;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Dex;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>
/// Unit tests for <see cref="TinymanDexAdapter"/>. The quote path uses a local
/// Algod handler so the AMM math is exercised without public testnet uptime.
/// </summary>
public class TinymanDexAdapterTests
{
    private static IConfiguration AppConfig(string algodUrl = "http://algod.test/") =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Testnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Testnet:NodeUrl"] = algodUrl
            })
            .Build();

    private static TinymanDexAdapter Build(HttpMessageHandler? handler = null) =>
        new(AppConfig(), new LoggerFactory().CreateLogger<TinymanDexAdapter>(), handler);

    [Fact]
    public void Chain_IsAlgorand()
    {
        Build().Chain.Should().Be("algorand");
    }

    [Fact]
    public async Task GetQuoteAsync_InvalidTokenIn_ReturnsError()
    {
        var result = await Build().GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand", TokenIn = "not-a-number", TokenOut = "10458941", AmountIn = "1000000"
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid Algorand asset ID for TokenIn");
    }

    [Fact]
    public async Task GetQuoteAsync_InvalidTokenOut_ReturnsError()
    {
        var result = await Build().GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand", TokenIn = "0", TokenOut = "xyz", AmountIn = "1000000"
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid Algorand asset ID for TokenOut");
    }

    [Fact]
    public async Task GetQuoteAsync_InvalidAmount_ReturnsError()
    {
        var result = await Build().GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand", TokenIn = "0", TokenOut = "10458941", AmountIn = "invalid"
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid amount");
    }

    [Fact]
    public async Task BuildSwapTransactionAsync_ReturnsClientSideInstructions()
    {
        var result = await Build().BuildSwapTransactionAsync(
            new SwapExecuteRequest { Chain = "algorand", QuoteId = "qid", WalletAddress = "WALLET" },
            "{\"asset1Id\":0,\"asset2Id\":10458941}");

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Chain.Should().Be("algorand");
        result.Result.QuoteId.Should().Be("qid");
        result.Result.TokenOut.Should().Be("WALLET");
        result.Result.Message.Should().Contain("client-side");
        result.Message.Should().Be("Tinyman swap parameters ready for client-side construction");
    }

    [Fact]
    public async Task GetQuoteAsync_LocalPoolReserves_ReturnsValidQuote()
    {
        var handler = new RoutingHandler
        {
            AccountResponse = (HttpStatusCode.OK, PoolStateJson(
                asset1Reserves: 2_500_000_000,
                asset2Reserves: 100_000_000_000))
        };

        var result = await Build(handler).GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",
            TokenOut = "10458941",
            AmountIn = "1000000",
            SlippageBps = 50
        });

        result.IsError.Should().BeFalse($"quote should succeed but failed with: {result.Message}");
        var dq = result.Result!;
        dq.Quote.Chain.Should().Be("algorand");
        dq.Quote.TokenIn.Should().Be("0");
        dq.Quote.TokenOut.Should().Be("10458941");
        dq.Quote.AmountIn.Should().Be("1000000");
        dq.Quote.ExpectedAmountOut.Should().Be("24924");
        dq.Quote.MinAmountOut.Should().Be("24799");
        dq.Quote.Fee.Should().Be("3000");
        dq.Quote.QuoteId.Should().BeNull("SwapManager assigns QuoteId after the adapter returns");
        dq.CachePayload.Should().Contain("asset1Id");
        handler.LastPath.Should().StartWith("/v2/accounts/");
    }

    [Fact]
    public async Task GetQuoteAsync_MissingPool_ReturnsUnavailableQuote()
    {
        var handler = new RoutingHandler
        {
            AccountResponse = (HttpStatusCode.NotFound, "missing")
        };

        var result = await Build(handler).GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",
            TokenOut = "10458941",
            AmountIn = "1000000",
            SlippageBps = 50
        });

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Quote.Unavailable.Should().BeTrue();
        result.Result.Quote.UnavailableReason.Should().Be("no_pool");
        result.Result.CachePayload.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQuoteAsync_AlgodTimeout_ReturnsUnavailableQuote()
    {
        var handler = new RoutingHandler
        {
            Exception = new TaskCanceledException("simulated algod timeout")
        };

        var result = await Build(handler).GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",
            TokenOut = "10458941",
            AmountIn = "1000000",
            SlippageBps = 50
        });

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Quote.Unavailable.Should().BeTrue();
        result.Result.Quote.UnavailableReason.Should().Be("no_pool");
    }

    private static string PoolStateJson(ulong asset1Reserves, ulong asset2Reserves)
    {
        var asset1Key = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("asset_1_reserves"));
        var asset2Key = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("asset_2_reserves"));
        return $$"""
            {
              "apps-local-state": [
                {
                  "id": {{TinymanV2PoolLocator.TestnetValidatorAppId}},
                  "key-value": [
                    { "key": "{{asset1Key}}", "value": { "type": 2, "uint": {{asset1Reserves}} } },
                    { "key": "{{asset2Key}}", "value": { "type": 2, "uint": {{asset2Reserves}} } }
                  ]
                }
              ]
            }
            """;
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) AccountResponse { get; set; }
            = (HttpStatusCode.OK, "{}");
        public Exception? Exception { get; set; }
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            if (Exception is not null)
                return Task.FromException<HttpResponseMessage>(Exception);

            return Task.FromResult(new HttpResponseMessage(AccountResponse.Status)
            {
                Content = new StringContent(AccountResponse.Body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
