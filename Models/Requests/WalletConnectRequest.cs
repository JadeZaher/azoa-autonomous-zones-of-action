using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Request to connect an external wallet (e.g., MetaMask, Ghost, Pera).
/// The address is provided by the browser wallet; optionally a signed message
/// can be included to verify ownership.
/// </summary>
public class WalletConnectRequest
{
    /// <summary>Chain type (e.g., "Ethereum", "Algorand", "Solana")</summary>
    public string ChainType { get; set; } = string.Empty;
    /// <summary>Wallet address from the external wallet</summary>
    public string Address { get; set; } = string.Empty;
    /// <summary>Optional public key</summary>
    public string? PublicKey { get; set; }
    /// <summary>Optional label</summary>
    public string? Label { get; set; }
    /// <summary>Signed message proving ownership (challenge from nonce)</summary>
    public string? SignedMessage { get; set; }
    /// <summary>The original message that was signed</summary>
    public string? OriginalMessage { get; set; }
    /// <summary>Whether this should be the default wallet</summary>
    public bool IsDefault { get; set; }
}
