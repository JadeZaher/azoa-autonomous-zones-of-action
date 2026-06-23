using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AZOA.WebAPI.Managers.Dex;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>
/// Unit tests for <see cref="TinymanDexAdapter"/>. The validation + build
/// cases are fully offline (they short-circuit before any Algod call); the
/// live quote case is config-driven and mirrors the existing SwapManager
/// integration assertion at the adapter level.
/// </summary>
public class TinymanDexAdapterTests
{
    private static IConfiguration AppConfig() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

    private static TinymanDexAdapter Build() =>
        new(AppConfig(), new LoggerFactory().CreateLogger<TinymanDexAdapter>());

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
        // Tinyman build is client-side: it returns instructions regardless of
        // the (already-validated) cached payload — no network involved.
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
    public async Task GetQuoteAsync_AlgorandTestnet_ReturnsValidQuote()
    {
        // Live: hits the Tinyman V2 ALGO/USDC pool on Algorand testnet via the
        // config-driven Algod URL. Asserts shape, not a hardcoded ratio.
        var result = await Build().GetQuoteAsync(new SwapQuoteRequest
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
        long.TryParse(dq.Quote.ExpectedAmountOut, out var outAmt).Should().BeTrue();
        outAmt.Should().BeGreaterThan(0);
        // Adapter leaves QuoteId null — SwapManager assigns it.
        dq.Quote.QuoteId.Should().BeNull();
        dq.CachePayload.Should().Contain("asset1Id");
    }
}
