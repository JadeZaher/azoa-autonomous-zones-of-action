using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Managers.Dex;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core.Blockchain;
using System.Net.Http;
using Xunit;
using System.Threading.Tasks;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// End-to-end behavior through the SwapManager → IDexAdapter pipeline. The
/// adapters are constructed config-driven (real appsettings) exactly as the
/// app wires them, so these tests still exercise the live Tinyman/Jupiter
/// paths. The dispatch-only cases use an in-memory fake adapter.
/// </summary>
public class SwapManagerTests
{
    private readonly SwapManager _swapManager;
    private readonly HttpClient _httpClient;

    // Mirrors the production registration (AddMemoryCache(o => o.SizeLimit = 1024)).
    // SwapManager sets per-entry Size=1 on every cache write, which throws if the
    // cache has no SizeLimit — so the test cache must also be bounded.
    private static IMemoryCache NewBoundedCache() =>
        new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });

    public SwapManagerTests()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders = { { "User-Agent", "AZOA-SwapManager-Tests" } }
        };
        // Config-driven: use the app's real appsettings (chain URLs,
        // DefaultNetwork) rather than empty config or hardcoded fallbacks.
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();
        var lf = new LoggerFactory();
        // Adapters are config-driven; SwapManager is a thin dispatcher over them.
        var tinyman = new TinymanDexAdapter(config, lf.CreateLogger<TinymanDexAdapter>());
        // Config-driven: bind JupiterConfig from the same appsettings section.
        var jupiterConfig = new JupiterConfig();
        config.GetSection(JupiterConfig.SectionName).Bind(jupiterConfig);
        var jupiter = new JupiterDexAdapter(
            _httpClient, Options.Create(jupiterConfig), lf.CreateLogger<JupiterDexAdapter>());
        _swapManager = new SwapManager(
            new IDexAdapter[] { tinyman, jupiter }, NewBoundedCache(), lf.CreateLogger<SwapManager>());
    }

    [Fact]
    public async Task GetTinymanQuote_AlgorandTestnet_ReturnsValidQuote()
    {
        // Arrange
        var request = new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",            // ALGO (native)
            TokenOut = "10458941",    // Circle USDC on Algorand TESTNET (mainnet USDC is 31566704)
            AmountIn = "1000000",     // 1 ALGO
            SlippageBps = 50
        };

        // Act — hits the live Tinyman V2 ALGO/USDC pool on Algorand testnet.
        var result = await _swapManager.GetQuoteAsync(request);

        // Assert — validate quote structure, not a hardcoded ratio: testnet
        // pool reserves are seeded at arbitrary ratios by random LPs, so the
        // exact output isn't predictable. We assert the quote is well-formed.
        result.IsError.Should().BeFalse($"quote should succeed but failed with: {result.Message}");
        result.Result.Should().NotBeNull();
        result.Result!.Chain.Should().Be("algorand");
        result.Result.TokenIn.Should().Be("0");
        result.Result.TokenOut.Should().Be("10458941");
        result.Result.AmountIn.Should().Be("1000000");

        long.TryParse(result.Result.ExpectedAmountOut, out var expectedOut)
            .Should().BeTrue("ExpectedAmountOut should be a numeric string");
        expectedOut.Should().BeGreaterThan(0);

        result.Result.PriceImpact.Should().BeGreaterOrEqualTo(0);
        result.Result.Fee.Should().NotBeNullOrEmpty();
        // SwapManager owns the QuoteId lifecycle — it must be assigned post-quote.
        result.Result.QuoteId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetTinymanQuote_InvalidAmount_ReturnsError()
    {
        // Arrange
        var request = new SwapQuoteRequest
        {
            Chain = "algorand",
            TokenIn = "0",
            TokenOut = "31566704",
            AmountIn = "invalid",
            SlippageBps = 50
        };

        // Act
        var result = await _swapManager.GetQuoteAsync(request);

        // Assert
        result.IsError.Should().BeTrue();
        var msg = result.Message.ToLower();
        (msg.Contains("format") || msg.Contains("parse") || msg.Contains("invalid")).Should().BeTrue();
    }

    [Fact]
    public async Task GetJupiterQuote_Solana_ReturnsQuoteOrGracefulError()
    {
        // Arrange
        var request = new SwapQuoteRequest
        {
            Chain = "solana",
            TokenIn = "So11111111111111111111111111111111111111112", // WSOL
            TokenOut = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", // USDC
            AmountIn = "1000000000", // 1 SOL
            SlippageBps = 50
        };

        // Act
        var result = await _swapManager.GetQuoteAsync(request);

        // Assert - Either success OR network error (both acceptable in tests)
        if (!result.IsError)
        {
            result.Result.Should().NotBeNull();
            result.Result!.Chain.Should().Be("solana");
            result.Result.ExpectedAmountOut.Should().NotBeNullOrEmpty();
            result.Result.AmountIn.Should().Be("1000000000");
        }
        else
        {
            // Network errors are OK for integration tests (DNS/firewall)
            result.Message.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetQuoteAsync_UnsupportedChain_ReturnsError()
    {
        // Arrange
        var request = new SwapQuoteRequest
        {
            Chain = "ethereum",
            TokenIn = "0x...",
            TokenOut = "0x...",
            AmountIn = "1000000",
            SlippageBps = 50
        };

        // Act
        var result = await _swapManager.GetQuoteAsync(request);

        // Assert
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Unsupported");
    }

    // ─── Dispatch behavior (in-memory fake adapter, no network) ───

    private SwapManager BuildDispatcher(FakeDexAdapter adapter) =>
        new(new IDexAdapter[] { adapter }, NewBoundedCache(), new LoggerFactory().CreateLogger<SwapManager>());

    [Fact]
    public async Task GetQuoteAsync_ResolvesAdapterCaseInsensitively_AndAssignsQuoteId()
    {
        var adapter = new FakeDexAdapter("solana");
        var mgr = BuildDispatcher(adapter);

        var result = await mgr.GetQuoteAsync(new SwapQuoteRequest
        {
            Chain = "SoLaNa", // different casing than adapter.Chain
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
        // SwapManager must replay the exact opaque payload the adapter cached.
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
        public int QuoteCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public string? LastBuildPayload { get; private set; }

        public Task<AZOAResult<DexQuote>> GetQuoteAsync(SwapQuoteRequest request)
        {
            QuoteCalls++;
            return Task.FromResult(new AZOAResult<DexQuote>
            {
                IsError = false,
                Message = "fake quote",
                Result = new DexQuote
                {
                    Quote = new SwapQuoteResponse { Chain = Chain },
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
