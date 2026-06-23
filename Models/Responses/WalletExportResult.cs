namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of exporting a platform-generated wallet.
/// Only available for WalletType.Platform wallets.
/// </summary>
public class WalletExportResult
{
    public Guid WalletId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? PublicKey { get; set; }

    /// <summary>Decrypted private key (hex-encoded). Handle with care!</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Decrypted seed phrase / mnemonic, if available.</summary>
    public string? SeedPhrase { get; set; }
}
