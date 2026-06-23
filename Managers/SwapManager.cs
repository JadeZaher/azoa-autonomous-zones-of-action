using Microsoft.Extensions.Caching.Memory;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Thin swap dispatcher. Validates the request, resolves the per-chain
/// <see cref="IDexAdapter"/> (Tinyman/Algorand, Jupiter/Solana, …) by
/// <see cref="SwapQuoteRequest.Chain"/>, and delegates. All chain-specific
/// HTTP / AMM math / pool discovery lives in the adapters.
///
/// SwapManager owns the cross-cutting quote→execute cache and the
/// <see cref="SwapQuoteResponse.QuoteId"/> lifecycle so behavior is uniform
/// across chains: an adapter returns a <see cref="DexQuote"/> (quote + opaque
/// payload); SwapManager assigns the QuoteId, caches the payload, and replays
/// it into the adapter on execute.
///
/// Adding a new chain = add one <see cref="IDexAdapter"/> implementation + one
/// DI registration. This dispatcher never changes.
/// </summary>
public class SwapManager : ISwapManager
{
    private readonly IReadOnlyDictionary<string, IDexAdapter> _adapters;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SwapManager> _logger;

    // Quote cache entries are valid for ~2 min after insert (absolute expiration),
    // matching the prior static-Dictionary behavior. IMemoryCache is thread-safe;
    // no external lock is needed. SizeLimit + per-entry Size=1 enforced in Program.cs
    // via AddMemoryCache(o => o.SizeLimit = …).
    private static readonly TimeSpan QuoteCacheDuration = TimeSpan.FromMinutes(2);

    public SwapManager(IEnumerable<IDexAdapter> adapters, IMemoryCache cache, ILogger<SwapManager> logger)
    {
        // Resolve adapters by chain, case-insensitive (mirrors the chain switch
        // that used request.Chain.ToLowerInvariant()).
        _adapters = adapters.ToDictionary(a => a.Chain, StringComparer.OrdinalIgnoreCase);
        _cache = cache;
        _logger = logger;
    }

    public async Task<AZOAResult<SwapQuoteResponse>> GetQuoteAsync(SwapQuoteRequest request)
    {
        try
        {
            if (!_adapters.TryGetValue(request.Chain, out var adapter))
                return new AZOAResult<SwapQuoteResponse> { IsError = true, Message = "Unsupported chain" };

            if (request.SlippageBps is < 0 or > 10000)
                return new AZOAResult<SwapQuoteResponse> { IsError = true, Message = "SlippageBps must be between 0 and 10000" };

            var quoteResult = await adapter.GetQuoteAsync(request);
            if (quoteResult.IsError || quoteResult.Result is null)
                return new AZOAResult<SwapQuoteResponse>
                {
                    IsError = true,
                    Message = quoteResult.Message,
                    Exception = quoteResult.Exception
                };

            var dexQuote = quoteResult.Result;
            var quote = dexQuote.Quote;

            // Unavailable quotes have no payload to cache or replay.
            if (!quote.Unavailable)
            {
                quote.QuoteId = Guid.NewGuid().ToString("N");
                CacheQuote(quote.QuoteId, request.Chain, dexQuote.CachePayload);
            }

            return new AZOAResult<SwapQuoteResponse>
            {
                IsError = false,
                Result = quote,
                Message = quoteResult.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swap quote failed for {Chain}", request.Chain);
            return new AZOAResult<SwapQuoteResponse> { IsError = true, Message = ex.Message, Exception = ex };
        }
    }

    public async Task<AZOAResult<SwapQuoteResponse>> GetSwapTransactionAsync(SwapExecuteRequest request, string? clientIdempotencyKey = null)
    {
        // clientIdempotencyKey is accepted for forward-compat/audit only: this
        // path returns an UNSIGNED tx (client signs + broadcasts), so there is
        // no server-side irreversible effect to dedupe. Intentionally not used.
        _ = clientIdempotencyKey;
        try
        {
            if (string.IsNullOrWhiteSpace(request.QuoteId))
                return Error<SwapQuoteResponse>("QuoteId is required");

            if (string.IsNullOrWhiteSpace(request.WalletAddress))
                return Error<SwapQuoteResponse>("WalletAddress is required for swap execution");

            if (!_adapters.TryGetValue(request.Chain, out var adapter))
                return Error<SwapQuoteResponse>($"Unsupported chain: {request.Chain}");

            if (!TryGetCachedQuote(request.QuoteId, out var cachedChain, out var cachedPayload))
                return Error<SwapQuoteResponse>("Quote expired or not found. Request a new quote first.");

            // A quote is bound to the chain it was produced on; replaying it
            // under a different chain would let a Solana quote drive an Algorand
            // execution (and vice-versa).
            if (!string.Equals(cachedChain, request.Chain, StringComparison.OrdinalIgnoreCase))
                return Error<SwapQuoteResponse>("Quote chain does not match the requested chain.");

            return await adapter.BuildSwapTransactionAsync(request, cachedPayload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swap transaction build failed for {Chain}", request.Chain);
            return Error<SwapQuoteResponse>(ex.Message, ex);
        }
    }

    // ─── Quote Cache (IMemoryCache, thread-safe, bounded via SizeLimit in DI) ───

    private void CacheQuote(string quoteId, string chain, string rawPayload)
    {
        var cacheKey = QuoteCacheKey(quoteId);
        var entry = new CachedQuote { Chain = chain, RawPayload = rawPayload };

        using var cacheEntry = _cache.CreateEntry(cacheKey);
        cacheEntry.Value = entry;
        cacheEntry.Size = 1;
        cacheEntry.AbsoluteExpirationRelativeToNow = QuoteCacheDuration;
    }

    private bool TryGetCachedQuote(string quoteId, out string chain, out string rawPayload)
    {
        if (_cache.TryGetValue(QuoteCacheKey(quoteId), out CachedQuote? cached) && cached is not null)
        {
            chain = cached.Chain;
            rawPayload = cached.RawPayload;
            return true;
        }
        chain = "";
        rawPayload = "";
        return false;
    }

    private static string QuoteCacheKey(string quoteId) => $"swap:quote:{quoteId}";

    private sealed class CachedQuote
    {
        public string Chain { get; set; } = "";
        public string RawPayload { get; set; } = "";
    }

    // ─── Result helpers ───

    private static AZOAResult<T> Error<T>(string message, Exception? ex = null)
        => new() { IsError = true, Message = message, Exception = ex };
}
