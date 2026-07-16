using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// End-to-end behavior through the SwapManager -> IDexAdapter pipeline.
/// Manager behavior uses in-memory adapters; network behavior belongs to
/// adapter-level tests.
/// </summary>
public class SwapManagerTests
{
    private readonly SwapManager _swapManager;

    private static IMemoryCache NewBoundedCache() =>
        new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });

    public SwapManagerTests()
    {
        var lf = new LoggerFactory();
        _swapManager = new SwapManager(
            new IDexAdapter[] { new FakeDexAdapter("algorand"), new FakeDexAdapter("solana") },
            NewBoundedCache(),
            lf.CreateLogger<SwapManager>());
    }

    [Fact]
    public async Task GetTinymanQuote_Algorand_DispatchesToAdapterAndAssignsQuoteId()
    {
        var request = new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",
            TokenOut = "10458941",
            AmountIn = "1000000",
            SlippageBps = 50
        };

        var result = await _swapManager.GetQuoteAsync(request);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNull();
        result.Result!.Chain.Should().Be("algorand");
        result.Result.TokenIn.Should().Be(request.TokenIn);
        result.Result.TokenOut.Should().Be(request.TokenOut);
        result.Result.AmountIn.Should().Be(request.AmountIn);
        result.Result.ExpectedAmountOut.Should().Be("42");
        result.Result.Fee.Should().Be("1");
        result.Result.QuoteId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetQuoteAsync_AdapterError_PropagatesError()
    {
        var adapter = new FakeDexAdapter("algorand")
        {
            QuoteError = "invalid amount"
        };
        var mgr = BuildDispatcher(adapter);

        var result = await mgr.GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",
            TokenOut = "31566704",
            AmountIn = "invalid",
            SlippageBps = 50
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("invalid amount");
        adapter.QuoteCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetJupiterQuote_Solana_DispatchesToAdapterAndAssignsQuoteId()
    {
        var request = new SwapQuoteRequest
        {
            Chain = "solana",
            TokenIn = "So11111111111111111111111111111111111111112",
            TokenOut = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v",
            AmountIn = "1000000000",
            SlippageBps = 50
        };

        var result = await _swapManager.GetQuoteAsync(request);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNull();
        result.Result!.Chain.Should().Be("solana");
        result.Result.TokenIn.Should().Be(request.TokenIn);
        result.Result.TokenOut.Should().Be(request.TokenOut);
        result.Result.AmountIn.Should().Be(request.AmountIn);
        result.Result.ExpectedAmountOut.Should().Be("42");
        result.Result.QuoteId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetQuoteAsync_UnsupportedChain_ReturnsError()
    {
        var request = new SwapQuoteRequest
        {
            Chain = "ethereum",
            TokenIn = "0x...",
            TokenOut = "0x...",
            AmountIn = "1000000",
            SlippageBps = 50
        };

        var result = await _swapManager.GetQuoteAsync(request);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Unsupported");
    }

    private static SwapManager BuildDispatcher(params IDexAdapter[] adapters) =>
        new(adapters, NewBoundedCache(), new LoggerFactory().CreateLogger<SwapManager>());

    [Fact]
    public async Task GetQuoteAsync_ResolvesAdapterCaseInsensitively_AndAssignsQuoteId()
    {
        var adapter = new FakeDexAdapter("solana");
        var mgr = BuildDispatcher(adapter);

        var result = await mgr.GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "SoLaNa",
            TokenIn = "A",
            TokenOut = "B",
            AmountIn = "100"
        });

        result.IsError.Should().BeFalse(result.Message);
        adapter.QuoteCalls.Should().Be(1);
        result.Result!.QuoteId.Should().NotBeNullOrEmpty(
            "SwapManager owns the QuoteId lifecycle, not the adapter");
        result.Message.Should().Be("fake quote");
    }

    [Fact]
    public async Task Quote_Then_Execute_RoundTripsCachePayloadThroughSwapManager()
    {
        var adapter = new FakeDexAdapter("solana") { CachePayloadToReturn = "OPAQUE-123" };
        var mgr = BuildDispatcher(adapter);

        var quote = await mgr.GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "solana", TokenIn = "A", TokenOut = "B", AmountIn = "100"
        });
        quote.IsError.Should().BeFalse(quote.Message);
        var quoteId = quote.Result!.QuoteId!;

        var exec = await mgr.GetSwapTransactionAsync(new SwapExecuteRequest
        {
            Chain = "solana", QuoteId = quoteId, WalletAddress = "WALLET"
        });

        exec.IsError.Should().BeFalse(exec.Message);
        adapter.LastBuildPayload.Should().Be("OPAQUE-123");
        adapter.BuildCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetSwapTransactionAsync_MissingQuoteId_ReturnsError()
    {
        var mgr = BuildDispatcher(new FakeDexAdapter("solana"));

        var result = await mgr.GetSwapTransactionAsync(new SwapExecuteRequest
        {
            Chain = "solana", QuoteId = "", WalletAddress = "WALLET"
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("QuoteId is required");
    }

    [Fact]
    public async Task GetSwapTransactionAsync_MissingWalletAddress_ReturnsError()
    {
        var mgr = BuildDispatcher(new FakeDexAdapter("solana"));

        var result = await mgr.GetSwapTransactionAsync(new SwapExecuteRequest
        {
            Chain = "solana", QuoteId = "abc", WalletAddress = ""
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("WalletAddress is required for swap execution");
    }

    [Fact]
    public async Task GetSwapTransactionAsync_UnsupportedChain_ReturnsError()
    {
        var mgr = BuildDispatcher(new FakeDexAdapter("solana"));

        var result = await mgr.GetSwapTransactionAsync(new SwapExecuteRequest
        {
            Chain = "bitcoin", QuoteId = "abc", WalletAddress = "WALLET"
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Unsupported chain: bitcoin");
    }

    [Fact]
    public async Task GetSwapTransactionAsync_QuoteNotCached_ReturnsExpiredError()
    {
        var mgr = BuildDispatcher(new FakeDexAdapter("solana"));

        var result = await mgr.GetSwapTransactionAsync(new SwapExecuteRequest
        {
            Chain = "solana", QuoteId = "never-cached", WalletAddress = "WALLET"
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Quote expired or not found. Request a new quote first.");
    }

    /// <summary>Minimal in-memory IDexAdapter for dispatch-only assertions.</summary>
    private sealed class FakeDexAdapter : IDexAdapter
    {
        public FakeDexAdapter(string chain) => Chain = chain;

        public string Chain { get; }
        public string CachePayloadToReturn { get; set; } = "payload";
        public string? QuoteError { get; set; }
        public int QuoteCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public string? LastBuildPayload { get; private set; }

        public Task<AZOAResult<DexQuote>> GetQuoteAsync(SwapQuoteRequest request)
        {
            QuoteCalls++;
            if (QuoteError is not null)
            {
                return Task.FromResult(new AZOAResult<DexQuote>
                {
                    IsError = true,
                    Message = QuoteError
                });
            }

            return Task.FromResult(new AZOAResult<DexQuote>
            {
                IsError = false,
                Message = "fake quote",
                Result = new DexQuote
                {
                    Quote = new SwapQuoteResponse
                    {
                        Chain = Chain,
                        TokenIn = request.TokenIn,
                        TokenOut = request.TokenOut,
                        AmountIn = request.AmountIn,
                        ExpectedAmountOut = "42",
                        Fee = "1"
                    },
                    CachePayload = CachePayloadToReturn
                }
            });
        }

        public Task<AZOAResult<SwapQuoteResponse>> BuildSwapTransactionAsync(
            SwapExecuteRequest request, string cachedQuotePayload)
        {
            BuildCalls++;
            LastBuildPayload = cachedQuotePayload;
            return Task.FromResult(new AZOAResult<SwapQuoteResponse>
            {
                IsError = false,
                Message = "fake build",
                Result = new SwapQuoteResponse { Chain = Chain, QuoteId = request.QuoteId }
            });
        }
    }
}
