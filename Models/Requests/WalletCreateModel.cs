namespace AZOA.WebAPI.Models.Requests;

/// <summary>Registers a connected External wallet; Platform wallets come only from custody generation.</summary>
public class WalletCreateModel
{
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
}
