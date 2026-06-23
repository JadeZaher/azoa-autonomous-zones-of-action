using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Request to generate a new wallet on the platform for a given chain.
/// </summary>
public class WalletGenerateRequest
{
    /// <summary>Chain to generate the wallet for (e.g., "Algorand", "Solana", "Ethereum")</summary>
    public string ChainType { get; set; } = string.Empty;
    /// <summary>Optional label for the wallet</summary>
    public string? Label { get; set; }
    /// <summary>Whether this should be the default wallet</summary>
    public bool IsDefault { get; set; }
}
