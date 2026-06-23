using System.ComponentModel.DataAnnotations;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Request to execute a swap using a previously obtained quote.
/// </summary>
public class SwapExecuteRequest
{
    /// <summary>Chain identifier: "algorand" or "solana".</summary>
    [Required]
    public string Chain { get; set; } = string.Empty;

    /// <summary>The QuoteId returned by GET /api/swap/quote.</summary>
    [Required]
    public string QuoteId { get; set; } = string.Empty;

    /// <summary>Public key of the wallet that will sign the transaction.</summary>
    [Required]
    public string WalletAddress { get; set; } = string.Empty;
}
