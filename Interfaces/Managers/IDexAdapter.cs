using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Per-chain DEX adapter. Mirrors the SDK's <c>DexAdapter</c> contract in
/// <c>sdk/azoa-wallet/src/core/types.ts</c> (one implementation per chain that
/// supports swaps, e.g. Tinyman/Algorand, Jupiter/Solana).
///
/// To add a new chain: implement this interface in
/// <c>Managers/Dex/&lt;Chain&gt;DexAdapter.cs</c> and add a single DI
/// registration — <see cref="AZOA.WebAPI.Managers.SwapManager"/>'s dispatch
/// never changes.
///
/// The quote→execute cache is cross-cutting and is owned by
/// <see cref="AZOA.WebAPI.Managers.SwapManager"/>: adapters are stateless with
/// respect to caching. <see cref="GetQuoteAsync"/> returns the quote plus an
/// opaque payload that SwapManager caches under a generated
/// <see cref="SwapQuoteResponse.QuoteId"/>; that same payload is handed back to
/// <see cref="BuildSwapTransactionAsync"/> so adapters never touch the cache or
/// the quote-id lifecycle themselves.
/// </summary>
public interface IDexAdapter
{
    /// <summary>Chain identifier this adapter serves, e.g. "algorand", "solana".</summary>
    string Chain { get; }

    /// <summary>
    /// Produce a swap quote. The returned <see cref="DexQuote.CachePayload"/> is
    /// opaque to SwapManager and is handed back verbatim to
    /// <see cref="BuildSwapTransactionAsync"/> via the shared quote cache. The
    /// adapter must NOT set <see cref="SwapQuoteResponse.QuoteId"/> — SwapManager
    /// owns that lifecycle.
    /// </summary>
    Task<AZOAResult<DexQuote>> GetQuoteAsync(SwapQuoteRequest request);

    /// <summary>
    /// Build an unsigned swap transaction for client-side signing.
    /// <paramref name="cachedQuotePayload"/> is exactly the payload this adapter
    /// returned from <see cref="GetQuoteAsync"/>; SwapManager has already
    /// validated the request and resolved it from the quote cache.
    /// </summary>
    Task<AZOAResult<SwapQuoteResponse>> BuildSwapTransactionAsync(
        SwapExecuteRequest request, string cachedQuotePayload);
}

/// <summary>
/// What an <see cref="IDexAdapter"/> returns from a quote: the wire response
/// (sans <see cref="SwapQuoteResponse.QuoteId"/>) plus the opaque payload
/// SwapManager must cache so the matching <see cref="IDexAdapter.BuildSwapTransactionAsync"/>
/// can complete the swap. Splitting these two keeps the quote-id + cache
/// lifecycle entirely in SwapManager so cross-chain behavior is uniform.
/// </summary>
public sealed class DexQuote
{
    /// <summary>The quote to return to the caller. <c>QuoteId</c> is left null;
    /// SwapManager assigns it after caching.</summary>
    public SwapQuoteResponse Quote { get; init; } = new();

    /// <summary>
    /// Adapter-private payload (e.g. the raw Jupiter quote body, or the Tinyman
    /// pool/asset descriptor) that SwapManager caches under the generated
    /// QuoteId and replays into <see cref="IDexAdapter.BuildSwapTransactionAsync"/>.
    /// </summary>
    public string CachePayload { get; init; } = string.Empty;
}
