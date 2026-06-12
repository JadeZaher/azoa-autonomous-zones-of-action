namespace OASIS.WebAPI.Models.Responses;

public class SwapQuoteResponse
{
    public string Chain { get; set; } = string.Empty;
    public string TokenIn { get; set; } = string.Empty;
    public string TokenOut { get; set; } = string.Empty;
    public string AmountIn { get; set; } = string.Empty;
    public string ExpectedAmountOut { get; set; } = string.Empty;
    public double PriceImpact { get; set; }
    public string Fee { get; set; } = "0";
    public object? Route { get; set; }
    public object? Raw { get; set; }

    /// <summary>Unique quote identifier for downstream swap execution (Jupiter v2).</summary>
    public string? QuoteId { get; set; }

    /// <summary>Base64-encoded unsigned swap transaction for client-side signing (Jupiter v2 /swap).</summary>
    public string? SwapTransaction { get; set; }

    /// <summary>Last valid block height for the swap transaction (Solana).</summary>
    public long? LastValidBlockHeight { get; set; }

    /// <summary>Human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// True when the quote could not be produced for a known *environmental*
    /// reason (no liquidity on the requested pair, upstream DEX unreachable,
    /// etc.) — i.e. the server is healthy but cannot serve a real number.
    /// The response is still 200 so frontend test panels can render this as
    /// an expected-skip rather than a red failure. <c>UnavailableReason</c>
    /// carries the machine-readable cause; <c>Message</c> the human-readable.
    /// </summary>
    public bool Unavailable { get; set; }

    /// <summary>
    /// Machine-readable identifier for an <c>Unavailable</c> quote. Stable
    /// strings: <c>"no_pool"</c> (no on-chain liquidity for the asset pair),
    /// <c>"upstream_unreachable"</c> (DNS/network/timeout against the
    /// off-chain quote API).
    /// </summary>
    public string? UnavailableReason { get; set; }
}
