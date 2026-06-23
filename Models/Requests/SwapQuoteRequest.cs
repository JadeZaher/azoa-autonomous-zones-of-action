using System.ComponentModel.DataAnnotations;

namespace AZOA.WebAPI.Models.Requests;

public class SwapQuoteRequest
{
    [Required]
    public string Chain { get; set; } = string.Empty;  // "algorand" | "solana"

    [Required]
    public string TokenIn { get; set; } = string.Empty;

    [Required]
    public string TokenOut { get; set; } = string.Empty;

    [Required]
    public string AmountIn { get; set; } = string.Empty;

    [Range(0, 10000)]
    public int SlippageBps { get; set; } = 50;

    /// <summary>Public key of the wallet requesting the swap (required for Jupiter v2).</summary>
    public string? WalletAddress { get; set; }
}
