using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Request to transfer assets from a platform wallet to another address.
/// Requires the wallet's passphrase to authorize.
/// </summary>
public class WalletTransferRequest
{
    /// <summary>Source wallet ID</summary>
    public Guid SourceWalletId { get; set; }
    /// <summary>Destination address</summary>
    public string DestinationAddress { get; set; } = string.Empty;
    /// <summary>Amount to transfer (string for precision)</summary>
    public string Amount { get; set; } = string.Empty;
    /// <summary>Token/asset ID (null for native currency)</summary>
    public string? TokenId { get; set; }
}
