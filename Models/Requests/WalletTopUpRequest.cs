namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Request to top-up (faucet-fund) a platform wallet with test tokens.
/// Only honored on dev / test networks — never on mainnet.
/// </summary>
public class WalletTopUpRequest
{
    /// <summary>
    /// Amount of native test tokens to dispense (e.g., ALGO).
    /// Optional — falls back to the configured default (Blockchain:Faucet:DefaultAmount).
    /// </summary>
    public decimal? Amount { get; set; }
}
